using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders;
using SIPSorceryMedia.FFmpeg;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

var app = builder.Build();

var ffmpegRuntime = new FfmpegRuntime(app.Logger);
if (!ffmpegRuntime.EnsureInitialised(out var initMessage))
{
    app.Logger.LogWarning("FFmpeg initialisation deferred: {Message}", initMessage);
}

app.MapGet("/health", () => Results.Text("ok", "text/plain", Encoding.UTF8));
app.MapPost("/offer", (Func<HttpContext, Task<IResult>>)(context => HandleOfferAsync(context, ffmpegRuntime, app.Logger)));

app.Run("http://127.0.0.1:8080");
static async Task<IResult> HandleOfferAsync(HttpContext context, FfmpegRuntime ffmpegRuntime, ILogger logger)
{
    var sessionId = Guid.NewGuid().ToString();
    logger.LogInformation("Received offer for session {SessionId}", sessionId);

    if (!ffmpegRuntime.EnsureInitialised(out var ffmpegError))
    {
        logger.LogError("FFmpeg initialisation failed: {Message}", ffmpegError);
        return Results.Problem(detail: ffmpegError, statusCode: StatusCodes.Status500InternalServerError, title: "FFmpeg initialisation failed");
    }

    string offerSdp;
    using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true))
    {
        offerSdp = await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    if (string.IsNullOrWhiteSpace(offerSdp))
    {
        const string message = "SDP offer body was empty.";
        logger.LogWarning(message);
        return Results.Problem(detail: message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid offer");
    }

    if (!TryResolveVideoPath(out var videoPath, out var videoError))
    {
        logger.LogError("Video path resolution failed: {Message}", videoError);
        return Results.Problem(detail: videoError, statusCode: StatusCodes.Status500InternalServerError, title: "Video file not found");
    }

    RTCPeerConnection? pc = null;
    FFmpegFileSource? source = null;
    EncodedSampleDelegate? encodedHandler = null;
    SourceErrorDelegate? errorHandler = null;
    var cleanupState = 0;

    async Task CleanupAsync(string reason)
    {
        if (Interlocked.Exchange(ref cleanupState, 1) != 0)
        {
            return;
        }

        logger.LogInformation("Cleaning up session {SessionId}: {Reason}", sessionId, reason);

        if (source != null)
        {
            if (encodedHandler != null)
            {
                source.OnVideoSourceEncodedSample -= encodedHandler;
            }

            if (errorHandler != null)
            {
                source.OnVideoSourceError -= errorHandler;
            }

            try
            {
                await source.CloseVideo().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "CloseVideo threw for session {SessionId}", sessionId);
            }

            try
            {
                await source.Close().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Close threw for session {SessionId}", sessionId);
            }

            source.Dispose();
        }

        pc?.Close(reason);
    }

    try
    {
        logger.LogInformation("Initialising FFmpeg source for {VideoPath}", videoPath);

        // Create source WITHOUT setting format to get raw samples
        source = new FFmpegFileSource(videoPath, true, null, 0, true);

        var availableFormats = source.GetVideoSourceFormats();
        if (availableFormats == null || availableFormats.Count == 0)
        {
            const string message = "FFmpeg source returned no video formats.";
            logger.LogError(message);
            await CleanupAsync("no-formats").ConfigureAwait(false);
            return Results.Problem(detail: message, statusCode: StatusCodes.Status500InternalServerError, title: "FFmpeg error");
        }

        logger.LogInformation("Available formats ({SessionId}): {Formats}", sessionId, string.Join(", ", availableFormats.Select(f => f.Codec)));

        // Use VP8 format directly from FFmpeg (it has built-in VP8 encoder)
        var vp8Format = availableFormats.First(f => f.Codec == VideoCodecsEnum.VP8);
        source.SetVideoSourceFormat(vp8Format);
        logger.LogInformation("Set VP8 format from source ({SessionId})", sessionId);

        var vp8OutputFormat = new VideoFormat(VideoCodecsEnum.VP8, 96, 90000, null);

        pc = new RTCPeerConnection(new RTCConfiguration { iceServers = null });

        pc.onicecandidate += candidate =>
        {
            if (candidate != null)
            {
                logger.LogInformation("ICE candidate ({SessionId}): {Candidate}", sessionId, candidate.candidate);
            }
        };

        pc.oniceconnectionstatechange += state =>
        {
            logger.LogInformation("ICE state ({SessionId}): {State}", sessionId, state);
        };

        var videoStarted = false;
        pc.onconnectionstatechange += state =>
        {
            logger.LogInformation("Peer state {SessionId}: {State}", sessionId, state);
            if (state == RTCPeerConnectionState.connected && !videoStarted)
            {
                videoStarted = true;
                // Start video when connection is established
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Add a small delay to ensure connection is fully established
                        await Task.Delay(100).ConfigureAwait(false);
                        logger.LogInformation("Starting FFmpeg video source ({SessionId})", sessionId);
                        await source.StartVideo().ConfigureAwait(false);
                        logger.LogInformation("FFmpeg source completed for session {SessionId}", sessionId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "FFmpeg source failed for session {SessionId}", sessionId);
                    }
                    finally
                    {
                        await CleanupAsync("video-source-stopped").ConfigureAwait(false);
                    }
                });
            }
            else if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.disconnected or RTCPeerConnectionState.closed)
            {
                _ = CleanupAsync($"pc-{state}");
            }
        };

        pc.OnVideoFormatsNegotiated += formats =>
        {
            logger.LogInformation("Negotiated formats ({SessionId}): {Formats}", sessionId, string.Join(", ", formats.Select(f => f.FormatID)));
        };

        var track = new MediaStreamTrack(new List<VideoFormat> { vp8OutputFormat }, MediaStreamStatusEnum.SendOnly);
        pc.addTrack(track);

        // Handle encoded VP8 samples from FFmpeg
        encodedHandler = (duration, sample) =>
        {
            if (sample == null || sample.Length == 0)
            {
                return;
            }

            pc.SendVideo(duration, sample);
            logger.LogInformation("Sent VP8 sample ({SessionId}) length={Length} duration={Duration}", sessionId, sample.Length, duration);
        };
        source.OnVideoSourceEncodedSample += encodedHandler;
        logger.LogInformation("Registered OnVideoSourceEncodedSample handler ({SessionId})", sessionId);

        errorHandler = message => logger.LogError("FFmpeg source error ({SessionId}): {Message}", sessionId, message);
        source.OnVideoSourceError += errorHandler;

        var remoteDescription = new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = offerSdp
        };

        var remoteResult = pc.setRemoteDescription(remoteDescription);
        if (remoteResult != SetDescriptionResultEnum.OK)
        {
            logger.LogError("Failed to set remote description: {Result}", remoteResult);
            await CleanupAsync("set-remote-failed").ConfigureAwait(false);
            return Results.Problem(detail: $"Failed to set remote description: {remoteResult}", statusCode: StatusCodes.Status500InternalServerError, title: "SDP failure");
        }

        var answer = pc.createAnswer(new RTCAnswerOptions());
        await pc.setLocalDescription(answer).ConfigureAwait(false);

        var answerSdp = answer.sdp ?? string.Empty;
        logger.LogInformation("Returning answer for session {SessionId} with length {Length}", sessionId, answerSdp.Length);
        return Results.Text(answerSdp, "application/sdp", Encoding.UTF8);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Unhandled exception for session {SessionId}", sessionId);
        await CleanupAsync("exception").ConfigureAwait(false);
        return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError, title: "Offer handling failed");
    }
}
static bool TryResolveVideoPath(out string fullPath, out string? error)
{
    var envPath = Environment.GetEnvironmentVariable("WEBRTC_FILE");
    var candidate = string.IsNullOrWhiteSpace(envPath) ? "sample.mp4" : envPath.Trim();
    candidate = Environment.ExpandEnvironmentVariables(candidate);

    if (Path.IsPathRooted(candidate))
    {
        if (File.Exists(candidate))
        {
            fullPath = candidate;
            error = null;
            return true;
        }

        fullPath = string.Empty;
        error = $"Video file not found at {candidate}";
        return false;
    }

    var roots = new[]
    {
        Directory.GetCurrentDirectory(),
        AppContext.BaseDirectory,
        Path.Combine(AppContext.BaseDirectory, ".."),
        Path.Combine(AppContext.BaseDirectory, "..", ".."),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..")
    };

    foreach (var root in roots)
    {
        try
        {
            var probe = Path.GetFullPath(candidate, root);
            if (File.Exists(probe))
            {
                fullPath = probe;
                error = null;
                return true;
            }
        }
        catch (Exception)
        {
        }
    }

    fullPath = string.Empty;
    error = $"Video file '{candidate}' was not found. Set WEBRTC_FILE to a valid mp4.";
    return false;
}
sealed class FfmpegRuntime
{
    private readonly ILogger _logger;
    private int _initialised;

    public FfmpegRuntime(ILogger logger)
    {
        _logger = logger;
    }

    public bool EnsureInitialised(out string? error)
    {
        if (Volatile.Read(ref _initialised) == 1)
        {
            error = null;
            return true;
        }

        var libPath = ResolveCandidatePath();
        if (libPath == null)
        {
            error = "Unable to locate FFmpeg shared libraries. Set WEBRTC_FFMPEG_DIR or add binaries to PATH.";
            return false;
        }

        try
        {
            FFmpegInit.Initialise(null, libPath, _logger);
            Interlocked.Exchange(ref _initialised, 1);
            _logger.LogInformation("Initialised FFmpeg binaries from {Path}", libPath);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FFmpeg initialisation failed for path {Path}", libPath);
            error = ex.Message;
            return false;
        }
    }

    private static string? ResolveCandidatePath()
    {
        var envCandidates = new[]
        {
            Environment.GetEnvironmentVariable("WEBRTC_FFMPEG_DIR"),
            Environment.GetEnvironmentVariable("WEBRTC_FFMPEG_PATH"),
            Environment.GetEnvironmentVariable("FFMPEG_BIN_PATH"),
            Environment.GetEnvironmentVariable("FFMPEG_DIR"),
            Environment.GetEnvironmentVariable("FFMPEG_ROOT")
        };

        foreach (var candidate in envCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var expanded = Environment.ExpandEnvironmentVariables(candidate);
            if (Directory.Exists(expanded))
            {
                return Path.GetFullPath(expanded);
            }
        }

        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in pathEntries)
        {
            try
            {
                if (!Directory.Exists(entry))
                {
                    continue;
                }

                if (Directory.EnumerateFiles(entry, "avcodec*.dll", SearchOption.TopDirectoryOnly).Any())
                {
                    return Path.GetFullPath(entry);
                }
            }
            catch (Exception)
            {
            }
        }

        var searchRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            Directory.GetParent(Directory.GetCurrentDirectory())?.FullName,
            Directory.GetParent(AppContext.BaseDirectory)?.FullName
        };

        foreach (var root in searchRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            try
            {
                foreach (var candidateDir in Directory.EnumerateDirectories(root!, "ffmpeg-*", SearchOption.TopDirectoryOnly))
                {
                    var binDir = Path.Combine(candidateDir, "bin");
                    if (Directory.Exists(binDir) && Directory.EnumerateFiles(binDir, "avcodec*.dll", SearchOption.TopDirectoryOnly).Any())
                    {
                        return Path.GetFullPath(binDir);
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        return null;
    }
}
