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
using SIPSorceryMedia.Abstractions;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.FFmpeg;

namespace Server;

class Program
{
    private const string ffmpegLibFullPath = null; //@"C:\ffmpeg-7.1.1-full_build-shared\bin";

    private const string STUN_URL = "stun:stun.cloudflare.com";
    private const int TEST_PATTERN_FRAMES_PER_SECOND = 30;

    private static Microsoft.Extensions.Logging.ILogger _logger = NullLogger.Instance;

    private static int _frameCount = 0;
    private static DateTime _startTime;

    static async Task Main(string[] args)
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

        var builder = WebApplication.CreateBuilder();

        builder.Host.UseSerilog();

        builder.Services.AddLogging(builder =>
        {
            builder.AddSerilog(dispose: true);
        });

        var app = builder.Build();

        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapPost("/offer", async (HttpRequest request) =>
        {
            string sdpOffer;
            using (var reader = new StreamReader(request.Body))
            {
                sdpOffer = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            _logger.LogInformation("Received SDP Offer:\n{Sdp}", sdpOffer);

            var pc = await CreatePeerConnection();

            var result = pc.setRemoteDescription(new RTCSessionDescriptionInit { sdp = sdpOffer, type = RTCSdpType.offer });

            if(result == SetDescriptionResultEnum.OK)
            {
                var answerSdp = pc.createAnswer();

                await pc.setLocalDescription(answerSdp);

                _logger.LogInformation("Returning answer SDP:\n{Sdp}", answerSdp.sdp);

                return Results.Text(pc.localDescription.sdp.ToString());
            }
            else
            {
                _logger.LogError("Failed to set remote description: {Result}", result);
                return Results.BadRequest(result.ToString());
            }
        });

        await app.RunAsync();
    }

    private static Task<RTCPeerConnection> CreatePeerConnection()
    {
        var pc = new RTCPeerConnection(null);

        SIPSorceryMedia.FFmpeg.FFmpegInit.Initialise(SIPSorceryMedia.FFmpeg.FfmpegLogLevelEnum.AV_LOG_VERBOSE, ffmpegLibFullPath, _logger);
        var testPatternSource = new VideoTestPatternSource(new FFmpegVideoEncoder());
        testPatternSource.SetFrameRate(TEST_PATTERN_FRAMES_PER_SECOND);

        MediaStreamTrack track = new MediaStreamTrack(testPatternSource.GetVideoSourceFormats(), MediaStreamStatusEnum.SendOnly);
        pc.addTrack(track);

        //testPatternSource.OnVideoSourceRawSample += videoEndPoint.ExternalVideoSourceRawSample;
        testPatternSource.OnVideoSourceRawSample += MesasureTestPatternSourceFrameRate;
        testPatternSource.OnVideoSourceEncodedSample += pc.SendVideo;
        pc.OnVideoFormatsNegotiated += (formats) => testPatternSource.SetVideoSourceFormat(formats.First());

        AudioExtrasSource audioSource = new AudioExtrasSource(new AudioEncoder(includeOpus: false), new AudioSourceOptions { AudioSource = AudioSourcesEnum.Music });
        audioSource.OnAudioSourceEncodedSample += pc.SendAudio;

        MediaStreamTrack audioTrack = new MediaStreamTrack(audioSource.GetAudioSourceFormats(), MediaStreamStatusEnum.SendOnly);
        pc.addTrack(audioTrack);
        pc.OnAudioFormatsNegotiated += (audioFormats) => audioSource.SetAudioSourceFormat(audioFormats.First());

        pc.onconnectionstatechange += async (state) =>
        {
            _logger.LogDebug($"Peer connection state change to {state}.");

            if (state == RTCPeerConnectionState.failed)
            {
                pc.Close("ice disconnection");
            }
            else if (state == RTCPeerConnectionState.closed)
            {
                await audioSource.CloseAudio();
                await testPatternSource.CloseVideo();
                testPatternSource.Dispose();
            }
            else if (state == RTCPeerConnectionState.connected)
            {
                await audioSource.StartAudio();
                await testPatternSource.StartVideo();
            }
        };

        // Diagnostics.
        //pc.OnReceiveReport += (re, media, rr) => logger.LogDebug($"RTCP Receive for {media} from {re}\n{rr.GetDebugSummary()}");
        //pc.OnSendReport += (media, sr) => logger.LogDebug($"RTCP Send for {media}\n{sr.GetDebugSummary()}");
        //pc.GetRtpChannel().OnStunMessageReceived += (msg, ep, isRelay) => logger.LogDebug($"STUN {msg.Header.MessageType} received from {ep}.");
        pc.oniceconnectionstatechange += (state) => _logger.LogDebug($"ICE connection state change to {state}.");
        pc.onsignalingstatechange += () =>
        {
            if (pc.signalingState == RTCSignalingState.have_local_offer)
            {
                _logger.LogDebug($"Local SDP set, type {pc.localDescription.type}.");
                _logger.LogDebug(pc.localDescription.sdp.ToString());
            }
            else if (pc.signalingState == RTCSignalingState.have_remote_offer)
            {
                _logger.LogDebug($"Remote SDP set, type {pc.remoteDescription.type}.");
                _logger.LogDebug(pc.remoteDescription.sdp.ToString());
            }
        };

        return Task.FromResult(pc);
    }

    private static void MesasureTestPatternSourceFrameRate(uint durationMilliseconds, int width, int height, byte[] sample, VideoPixelFormatsEnum pixelFormat)
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

    /// <summary>
    ///  Adds a console logger. Can be omitted if internal SIPSorcery debug and warning messages are not required.
    /// </summary>
    private static Microsoft.Extensions.Logging.ILogger AddConsoleLogger()
    {
        var seriLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .MinimumLevel.Is(Serilog.Events.LogEventLevel.Debug)
            .WriteTo.Console()
            .CreateLogger();
        var factory = new SerilogLoggerFactory(seriLogger);
        SIPSorcery.LogFactory.Set(factory);
        return factory.CreateLogger<Program>();
    }
}
