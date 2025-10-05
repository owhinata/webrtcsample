using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Extensions.Logging;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace Server;

class Program
{
    // Example: @"C:\\ffmpeg-7.1.1-full_build-shared\\bin";
    private const string ffmpegLibFullPath = null;

    private static readonly string Mp4Path = Path.Combine(
        AppContext.BaseDirectory,
        "media",
        "video.mp4"
    );

    private const int TEST_PATTERN_FRAMES_PER_SECOND = 30;

    private static Microsoft.Extensions.Logging.ILogger _logger = NullLogger.Instance;

    private static int _frameCount = 0;
    private static DateTime _startTime;

    static async Task Main(string[] args)
    {
        ConfigureLogging();
        await BuildAndRunAsync().ConfigureAwait(false);
    }

    private static void ConfigureLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .CreateLogger();

        Log.Information("WebRTC Test Pattern Server Demo");

        var factory = new SerilogLoggerFactory(Log.Logger);
        SIPSorcery.LogFactory.Set(factory);
        _logger = factory.CreateLogger<Program>();
    }

    private static async Task BuildAndRunAsync()
    {
        var builder = WebApplication.CreateBuilder();

        builder.Host.UseSerilog();
        builder.Services.AddLogging(loggingBuilder =>
        {
            loggingBuilder.AddSerilog(dispose: true);
        });

        var app = builder.Build();
        ConfigureRequestPipeline(app);

        await app.RunAsync().ConfigureAwait(false);
    }

    private static void ConfigureRequestPipeline(WebApplication app)
    {
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.MapPost("/offer", HandleOfferAsync);
    }

    private static async Task<IResult> HandleOfferAsync(HttpRequest request)
    {
        var sdpOffer = await ReadRequestBodyAsync(request).ConfigureAwait(false);
        _logger.LogInformation("Received SDP Offer:\n{Sdp}", sdpOffer);

        var peerConnection = await CreatePeerConnectionAsync().ConfigureAwait(false);

        var result = peerConnection.setRemoteDescription(
            new RTCSessionDescriptionInit { sdp = sdpOffer, type = RTCSdpType.offer }
        );

        if (result != SetDescriptionResultEnum.OK)
        {
            _logger.LogError("Failed to set remote description: {Result}", result);
            return Results.BadRequest(result.ToString());
        }

        var answerSdp = peerConnection.createAnswer();
        await peerConnection.setLocalDescription(answerSdp).ConfigureAwait(false);

        _logger.LogInformation("Returning answer SDP:\n{Sdp}", answerSdp.sdp);

        return Results.Text(peerConnection.localDescription.sdp.ToString());
    }

    private static async Task<string> ReadRequestBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body);
        return await reader.ReadToEndAsync().ConfigureAwait(false);
    }

    private static Task<RTCPeerConnection> CreatePeerConnectionAsync()
    {
        var peerConnection = new RTCPeerConnection(null);

        InitialiseMediaFramework();
        var videoSource = CreateVideoSource();
        var audioSource = CreateAudioSource();

        if (videoSource is FFmpegFileSource fileSource)
        {
            AttachFileSourceTracks(peerConnection, fileSource);
            RegisterFileSourceEvents(peerConnection, fileSource);
        }
        else
        {
            AttachVideoTrack(peerConnection, videoSource);

            if (audioSource != null)
            {
                AttachAudioTrack(peerConnection, audioSource);
                RegisterPeerConnectionEvents(
                    peerConnection,
                    audioSource,
                    videoSource
                );
            }
        }

        return Task.FromResult(peerConnection);
    }

    private static void InitialiseMediaFramework()
    {
        FFmpegInit.Initialise(null, ffmpegLibFullPath, _logger);
    }

    private static IVideoSource CreateVideoSource()
    {
        if (File.Exists(Mp4Path))
        {
            _logger.LogInformation(
                "MP4 file found at {Path}, using file source.",
                Mp4Path
            );
            var fileSource = new FFmpegFileSource(Mp4Path, true, new AudioEncoder());
            fileSource.RestrictFormats(format =>
                format.Codec == VideoCodecsEnum.H264
                || format.Codec == VideoCodecsEnum.VP8
            );
            return fileSource;
        }

        _logger.LogInformation("MP4 file not found, using test pattern source.");
        var testPatternSource = new VideoTestPatternSource(new FFmpegVideoEncoder());
        testPatternSource.RestrictFormats(format =>
            format.Codec == VideoCodecsEnum.H264
            || format.Codec == VideoCodecsEnum.VP8
        );
        testPatternSource.SetFrameRate(TEST_PATTERN_FRAMES_PER_SECOND);
        testPatternSource.OnVideoSourceRawSample += MeasureTestPatternSourceFrameRate;
        return testPatternSource;
    }

    private static IAudioSource? CreateAudioSource()
    {
        if (File.Exists(Mp4Path))
        {
            return null;
        }

        return new AudioExtrasSource(
            new AudioEncoder(includeOpus: false),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.Music }
        );
    }

    private static void AttachVideoTrack(
        RTCPeerConnection peerConnection,
        IVideoSource videoSource
    )
    {
        MediaStreamTrack track = new(
            videoSource.GetVideoSourceFormats(),
            MediaStreamStatusEnum.SendOnly
        );
        peerConnection.addTrack(track);

        videoSource.OnVideoSourceEncodedSample += peerConnection.SendVideo;
        peerConnection.OnVideoFormatsNegotiated += formats =>
            videoSource.SetVideoSourceFormat(formats.First());
    }

    private static void AttachAudioTrack(
        RTCPeerConnection peerConnection,
        IAudioSource audioSource
    )
    {
        audioSource.OnAudioSourceEncodedSample += peerConnection.SendAudio;

        MediaStreamTrack audioTrack = new(
            audioSource.GetAudioSourceFormats(),
            MediaStreamStatusEnum.SendOnly
        );
        peerConnection.addTrack(audioTrack);

        peerConnection.OnAudioFormatsNegotiated += audioFormats =>
            audioSource.SetAudioSourceFormat(audioFormats.First());
    }

    private static void RegisterPeerConnectionEvents(
        RTCPeerConnection peerConnection,
        IAudioSource audioSource,
        IVideoSource videoSource
    )
    {
        peerConnection.onconnectionstatechange += async state =>
        {
            _logger.LogDebug("Peer connection state change to {State}.", state);

            switch (state)
            {
                case RTCPeerConnectionState.failed:
                    peerConnection.Close("ice disconnection");
                    return;

                case RTCPeerConnectionState.closed:
                    await audioSource.CloseAudio().ConfigureAwait(false);
                    await videoSource.CloseVideo().ConfigureAwait(false);
                    if (videoSource is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                    return;

                case RTCPeerConnectionState.connected:
                    await audioSource.StartAudio().ConfigureAwait(false);
                    await videoSource.StartVideo().ConfigureAwait(false);
                    return;
            }
        };

        peerConnection.oniceconnectionstatechange += state =>
            _logger.LogDebug("ICE connection state change to {State}.", state);

        peerConnection.onsignalingstatechange += () =>
        {
            switch (peerConnection.signalingState)
            {
                case RTCSignalingState.have_local_offer:
                    _logger.LogDebug(
                        "Local SDP set, type {Type}.",
                        peerConnection.localDescription.type
                    );
                    _logger.LogDebug(peerConnection.localDescription.sdp.ToString());
                    break;

                case RTCSignalingState.have_remote_offer:
                    _logger.LogDebug(
                        "Remote SDP set, type {Type}.",
                        peerConnection.remoteDescription.type
                    );
                    _logger.LogDebug(peerConnection.remoteDescription.sdp.ToString());
                    break;
            }
        };
    }

    private static void AttachFileSourceTracks(
        RTCPeerConnection peerConnection,
        FFmpegFileSource fileSource
    )
    {
        MediaStreamTrack videoTrack = new(
            fileSource.GetVideoSourceFormats(),
            MediaStreamStatusEnum.SendOnly
        );
        peerConnection.addTrack(videoTrack);

        MediaStreamTrack audioTrack = new(
            fileSource.GetAudioSourceFormats(),
            MediaStreamStatusEnum.SendOnly
        );
        peerConnection.addTrack(audioTrack);

        fileSource.OnVideoSourceEncodedSample += peerConnection.SendVideo;
        fileSource.OnAudioSourceEncodedSample += peerConnection.SendAudio;

        peerConnection.OnVideoFormatsNegotiated += formats =>
            fileSource.SetVideoSourceFormat(formats.First());
        peerConnection.OnAudioFormatsNegotiated += formats =>
            fileSource.SetAudioSourceFormat(formats.First());
    }

    private static void RegisterFileSourceEvents(
        RTCPeerConnection peerConnection,
        FFmpegFileSource fileSource
    )
    {
        peerConnection.onconnectionstatechange += async state =>
        {
            _logger.LogDebug("Peer connection state change to {State}.", state);

            switch (state)
            {
                case RTCPeerConnectionState.failed:
                    peerConnection.Close("ice disconnection");
                    return;

                case RTCPeerConnectionState.closed:
                case RTCPeerConnectionState.disconnected:
                    await fileSource.CloseVideo().ConfigureAwait(false);
                    await fileSource.CloseAudio().ConfigureAwait(false);
                    return;

                case RTCPeerConnectionState.connected:
                    await fileSource.StartVideo().ConfigureAwait(false);
                    await fileSource.StartAudio().ConfigureAwait(false);
                    return;
            }
        };

        peerConnection.oniceconnectionstatechange += state =>
            _logger.LogDebug("ICE connection state change to {State}.", state);

        peerConnection.onsignalingstatechange += () =>
        {
            switch (peerConnection.signalingState)
            {
                case RTCSignalingState.have_local_offer:
                    _logger.LogDebug(
                        "Local SDP set, type {Type}.",
                        peerConnection.localDescription.type
                    );
                    _logger.LogDebug(peerConnection.localDescription.sdp.ToString());
                    break;

                case RTCSignalingState.have_remote_offer:
                    _logger.LogDebug(
                        "Remote SDP set, type {Type}.",
                        peerConnection.remoteDescription.type
                    );
                    _logger.LogDebug(peerConnection.remoteDescription.sdp.ToString());
                    break;
            }
        };
    }

    private static void MeasureTestPatternSourceFrameRate(
        uint durationMilliseconds,
        int width,
        int height,
        byte[] sample,
        VideoPixelFormatsEnum pixelFormat
    )
    {
        if (_startTime == DateTime.MinValue)
        {
            _startTime = DateTime.Now;
        }

        _frameCount++;

        if (DateTime.Now.Subtract(_startTime).TotalSeconds > 5)
        {
            double fps = _frameCount / DateTime.Now.Subtract(_startTime).TotalSeconds;
            _logger.LogDebug($"Frame rate {fps:0.##}fps.");
            _startTime = DateTime.Now;
            _frameCount = 0;
        }
    }
}
