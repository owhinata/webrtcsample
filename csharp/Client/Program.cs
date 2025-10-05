using System;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Serilog;
using Serilog.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Client;

class Program
{
    private const string DefaultOfferUrl = "https://localhost:5443/offer";
    private const string WindowName = "WebRTC Client";
    private const int DisplayIntervalMs = 15;

    // Example: @"C:\\ffmpeg-7.1.1-full_build-shared\\bin";
    private const string? ffmpegLibFullPath = null;

    private static readonly object FrameSync = new();

    private static ILogger logger = NullLogger.Instance;
    private static Mat? latestFrame;
    private static bool framePending;
    private static bool firstFrameLogged;
    private static FFmpegVideoEndPoint? videoEndPoint;

    private static int frameCount = 0;
    private static DateTime startTime = DateTime.MinValue;

    static async Task Main(string[] args)
    {
        await RunAsync().ConfigureAwait(false);
    }

    private static async Task RunAsync()
    {
        logger = AddConsoleLogger();
        FFmpegInit.Initialise(null, ffmpegLibFullPath, logger);

        var offerUrl = GetOfferUrl();

        using var peerConnection = await CreatePeerConnection().ConfigureAwait(false);
        var offerCompleted = await TryCompleteOfferAnswerAsync(
                peerConnection,
                offerUrl
            )
            .ConfigureAwait(false);

        if (!offerCompleted)
        {
            return;
        }

        Cv2.NamedWindow(WindowName, WindowFlags.AutoSize);

        using var exitCts = new CancellationTokenSource();
        var shutdownTcs = CreateShutdownSignal(exitCts, peerConnection);

        RunDisplayLoop(exitCts, shutdownTcs);
        await shutdownTcs.Task.ConfigureAwait(false);

        await CloseVideoSinkAsync().ConfigureAwait(false);
        ResetFrameState();
        Cv2.DestroyAllWindows();

        logger.LogInformation("Client exiting.");
    }

    private static string GetOfferUrl() =>
        Environment.GetEnvironmentVariable("WEBRTC_OFFER_URL") ?? DefaultOfferUrl;

    private static async Task<bool> TryCompleteOfferAnswerAsync(
        RTCPeerConnection peerConnection,
        string offerUrl
    )
    {
        var offer = peerConnection.createOffer();
        await peerConnection.setLocalDescription(offer).ConfigureAwait(false);

        using var http = new HttpClient();
        var request = new StringContent(
            offer.sdp ?? string.Empty,
            Encoding.UTF8,
            "application/sdp"
        );

        logger.LogInformation("Posting SDP offer to {OfferUrl}.", offerUrl);
        var response = await http.PostAsync(offerUrl, request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Server returned failure status {StatusCode}",
                response.StatusCode
            );
            return false;
        }

        var answerSdp = await response
            .Content.ReadAsStringAsync()
            .ConfigureAwait(false);
        logger.LogInformation("Received SDP answer:\n{Sdp}", answerSdp);

        var sdp = SDP.ParseSDPDescription(answerSdp);
        if (sdp == null)
        {
            logger.LogError("Failed to parse SDP answer.");
            return false;
        }

        var setResult = peerConnection.SetRemoteDescription(SdpType.answer, sdp);
        if (setResult != SetDescriptionResultEnum.OK)
        {
            logger.LogError("Failed to set remote description: {Result}.", setResult);
            return false;
        }

        logger.LogInformation(
            "Remote description applied. ICE negotiation in progress."
        );

        return true;
    }

    private static TaskCompletionSource CreateShutdownSignal(
        CancellationTokenSource exitCts,
        RTCPeerConnection peerConnection
    )
    {
        var shutdownTcs = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitCts.Cancel();
            shutdownTcs.TrySetResult();
        };

        peerConnection.onconnectionstatechange += state =>
        {
            logger.LogDebug("Peer connection state changed to {State}.", state);
            if (
                state == RTCPeerConnectionState.failed
                || state == RTCPeerConnectionState.closed
            )
            {
                shutdownTcs.TrySetResult();
            }
        };

        peerConnection.oniceconnectionstatechange += state =>
        {
            logger.LogDebug("ICE connection state changed to {State}.", state);
        };

        return shutdownTcs;
    }

    private static void RunDisplayLoop(
        CancellationTokenSource exitCts,
        TaskCompletionSource shutdownTcs
    )
    {
        while (!exitCts.IsCancellationRequested)
        {
            if (shutdownTcs.Task.IsCompleted)
            {
                exitCts.Cancel();
                break;
            }

            using var frameToShow = DequeueFrame();
            if (frameToShow != null)
            {
                Cv2.ImShow(WindowName, frameToShow);

                if (!firstFrameLogged)
                {
                    logger.LogInformation("Displaying first frame.");
                    firstFrameLogged = true;
                }
            }

            var key = Cv2.WaitKey(DisplayIntervalMs);
            if (IsExitKey(key))
            {
                shutdownTcs.TrySetResult();
                exitCts.Cancel();
                break;
            }

            Thread.Sleep(DisplayIntervalMs);
        }
    }

    private static Mat? DequeueFrame()
    {
        lock (FrameSync)
        {
            if (!framePending || latestFrame == null)
            {
                return null;
            }

            framePending = false;
            return latestFrame.Clone();
        }
    }

    private static bool IsExitKey(int key) => key is 27 or 'q' or 'Q';

    private static async Task<RTCPeerConnection> CreatePeerConnection()
    {
        var peerConnection = new RTCPeerConnection();

        videoEndPoint = CreateVideoEndPoint();
        ConfigureVideoPipeline(peerConnection, videoEndPoint);

        await videoEndPoint.StartVideoSink().ConfigureAwait(false);

        return peerConnection;
    }

    private static FFmpegVideoEndPoint CreateVideoEndPoint()
    {
        var endPoint = new FFmpegVideoEndPoint();
        var codec = GetVideoCodec();
        endPoint.RestrictFormats(format => format.Codec == codec);

        AttachVideoFrameHandlers(endPoint);

        return endPoint;
    }

    private static VideoCodecsEnum GetVideoCodec()
    {
        var codecEnv = Environment.GetEnvironmentVariable("WEBRTC_VIDEO_CODEC");
        logger.LogInformation(
            "WEBRTC_VIDEO_CODEC environment variable: {CodecEnv}",
            codecEnv ?? "(not set)"
        );

        if (string.IsNullOrEmpty(codecEnv))
        {
            logger.LogInformation("Using default codec: H264");
            return VideoCodecsEnum.H264;
        }

        var codec = codecEnv.ToUpperInvariant() switch
        {
            "VP8" => VideoCodecsEnum.VP8,
            "H264" => VideoCodecsEnum.H264,
            _ => VideoCodecsEnum.H264,
        };

        logger.LogInformation("Selected codec: {Codec}", codec);
        return codec;
    }

    private static void AttachVideoFrameHandlers(FFmpegVideoEndPoint endPoint)
    {
        endPoint.OnVideoSinkDecodedSampleFaster += ProcessFastDecodedSample;
        endPoint.OnVideoSinkDecodedSample += ProcessDecodedSample;
    }

    private static void ProcessFastDecodedSample(RawImage rawImage)
    {
        if (startTime == DateTime.MinValue)
        {
            startTime = DateTime.Now;
        }

        frameCount++;

        if (DateTime.Now.Subtract(startTime).TotalSeconds >= 5)
        {
            double fps = frameCount / DateTime.Now.Subtract(startTime).TotalSeconds;
            logger.LogDebug(
                "Decoded fast frame format={Format} {Width}x{Height} stride={Stride}, Frame rate {Fps:0.##}fps.",
                rawImage.PixelFormat,
                rawImage.Width,
                rawImage.Height,
                rawImage.Stride,
                fps
            );
            startTime = DateTime.Now;
            frameCount = 0;
        }

        var (buffer, width, height, matType, conversion) = ConvertRawImage(rawImage);
        if (buffer != null)
        {
            UpdateFrame(buffer, width, height, matType, conversion);
        }
    }

    private static void ProcessDecodedSample(
        byte[] buffer,
        uint width,
        uint height,
        int stride,
        VideoPixelFormatsEnum pixelFormat
    )
    {
        if (startTime == DateTime.MinValue)
        {
            startTime = DateTime.Now;
        }

        frameCount++;

        bool shouldLog = DateTime.Now.Subtract(startTime).TotalSeconds >= 5;

        if (shouldLog)
        {
            double fps = frameCount / DateTime.Now.Subtract(startTime).TotalSeconds;

            switch (pixelFormat)
            {
                case VideoPixelFormatsEnum.Rgb:
                    logger.LogDebug(
                        "Decoded RGB frame {Width}x{Height} stride={Stride}, Frame rate {Fps:0.##}fps.",
                        width,
                        height,
                        stride,
                        fps
                    );
                    break;

                case VideoPixelFormatsEnum.Bgr:
                    logger.LogDebug(
                        "Decoded BGR frame {Width}x{Height} stride={Stride}, Frame rate {Fps:0.##}fps.",
                        width,
                        height,
                        stride,
                        fps
                    );
                    break;

                default:
                    logger.LogWarning(
                        "Unhandled decoded sample pixel format {PixelFormat} (stride={Stride}).",
                        pixelFormat,
                        stride
                    );
                    break;
            }

            startTime = DateTime.Now;
            frameCount = 0;
        }

        switch (pixelFormat)
        {
            case VideoPixelFormatsEnum.Rgb:
                UpdateFrame(
                    buffer,
                    (int)width,
                    (int)height,
                    MatType.CV_8UC3,
                    ColorConversionCodes.RGB2BGR
                );
                break;

            case VideoPixelFormatsEnum.Bgr:
                UpdateFrame(buffer, (int)width, (int)height, MatType.CV_8UC3, null);
                break;
        }
    }

    private static void ConfigureVideoPipeline(
        RTCPeerConnection peerConnection,
        FFmpegVideoEndPoint endPoint
    )
    {
        MediaStreamTrack videoTrack = new(
            endPoint.GetVideoSinkFormats(),
            MediaStreamStatusEnum.RecvOnly
        );
        peerConnection.addTrack(videoTrack);

        peerConnection.OnVideoFormatsNegotiated += formats =>
        {
            var format = formats.First();
            endPoint.SetVideoSinkFormat(format);
            logger.LogInformation(
                "Negotiated video format {FormatId}:{Codec}.",
                format.FormatID,
                format.Codec
            );
        };

        peerConnection.OnVideoFrameReceived += endPoint.GotVideoFrame;
    }

    private static async Task CloseVideoSinkAsync()
    {
        if (videoEndPoint != null)
        {
            await videoEndPoint.CloseVideoSink().ConfigureAwait(false);
        }
    }

    private static void ResetFrameState()
    {
        lock (FrameSync)
        {
            latestFrame?.Dispose();
            latestFrame = null;
            framePending = false;
            firstFrameLogged = false;
        }
    }

    private static (
        byte[]? buffer,
        int width,
        int height,
        MatType matType,
        ColorConversionCodes? conversion
    ) ConvertRawImage(RawImage rawImage)
    {
        try
        {
            var width = (int)rawImage.Width;
            var height = (int)rawImage.Height;
            var stride = rawImage.Stride;

            if (width <= 0 || height <= 0 || stride <= 0)
            {
                return (null, 0, 0, MatType.CV_8UC3, null);
            }

            var bufferLength = stride * height;
            var buffer = new byte[bufferLength];
            Marshal.Copy(rawImage.Sample, buffer, 0, bufferLength);

            byte[]? targetBuffer = buffer;
            MatType matType = MatType.CV_8UC3;
            ColorConversionCodes? conversion = null;

            switch (rawImage.PixelFormat)
            {
                case VideoPixelFormatsEnum.Rgb:
                    conversion = ColorConversionCodes.RGB2BGR;
                    break;
                case VideoPixelFormatsEnum.Bgr:
                    break;
                case VideoPixelFormatsEnum.I420:
                    targetBuffer = ConvertI420ToBgr(buffer, width, height);
                    break;
                default:
                    targetBuffer = LogUnhandled(rawImage.PixelFormat, stride);
                    break;
            }

            if (targetBuffer == null)
            {
                return (null, 0, 0, MatType.CV_8UC3, null);
            }

            return (targetBuffer, width, height, matType, conversion);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to convert raw image frame.");
            return (null, 0, 0, MatType.CV_8UC3, null);
        }
    }

    private static byte[]? ConvertI420ToBgr(byte[] buffer, int width, int height)
    {
        try
        {
            using var yuv = Mat.FromPixelData(
                height + height / 2,
                width,
                MatType.CV_8UC1,
                buffer
            );
            using var bgr = new Mat();
            Cv2.CvtColor(yuv, bgr, ColorConversionCodes.YUV2BGR_I420);
            var output = new byte[width * height * 3];
            Marshal.Copy(bgr.Data, output, 0, output.Length);
            return output;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to convert I420 buffer to BGR.");
            return null;
        }
    }

    private static byte[]? LogUnhandled(VideoPixelFormatsEnum pixelFormat, int stride)
    {
        logger.LogWarning(
            "Unhandled raw image pixel format {PixelFormat} (stride={Stride}).",
            pixelFormat,
            stride
        );
        return null;
    }

    private static void UpdateFrame(
        byte[] buffer,
        int width,
        int height,
        MatType matType,
        ColorConversionCodes? conversion
    )
    {
        try
        {
            if (buffer == null || width <= 0 || height <= 0)
            {
                return;
            }

            var mat = new Mat(height, width, matType);
            Marshal.Copy(buffer, 0, mat.Data, buffer.Length);

            if (conversion.HasValue)
            {
                Cv2.CvtColor(mat, mat, conversion.Value);
            }

            lock (FrameSync)
            {
                latestFrame?.Dispose();
                latestFrame = mat;
                framePending = true;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update video frame.");
        }
    }

    private static ILogger AddConsoleLogger()
    {
        var seriLogger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();
        var factory = new SerilogLoggerFactory(seriLogger);
        Log.Logger = seriLogger;
        return factory.CreateLogger<Program>();
    }
}
