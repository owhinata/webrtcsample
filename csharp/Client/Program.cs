using System;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
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

    private static readonly object FrameSync = new();
    private const string? ffmpegLibFullPath = null; // @"C:\\ffmpeg-7.1.1-full_build-shared\\bin";

    private static ILogger logger = NullLogger.Instance;
    private static Mat? latestFrame;
    private static bool framePending;
    private static bool firstFrameLogged;
    private static FFmpegVideoEndPoint? videoEndPoint;

    static async Task Main(string[] args)
    {
        logger = AddConsoleLogger();

        FFmpegInit.Initialise(null, ffmpegLibFullPath, logger);

        var offerUrl = Environment.GetEnvironmentVariable("WEBRTC_OFFER_URL") ?? DefaultOfferUrl;

        using var peerConnection = await CreatePeerConnection().ConfigureAwait(false);

        var offer = peerConnection.createOffer();
        await peerConnection.setLocalDescription(offer).ConfigureAwait(false);

        using var http = new HttpClient();
        var request = new StringContent(offer.sdp ?? string.Empty, Encoding.UTF8, "application/sdp");

        logger.LogInformation("Posting SDP offer to {OfferUrl}.", offerUrl);
        var response = await http.PostAsync(offerUrl, request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Server returned failure status {StatusCode}", response.StatusCode);
            return;
        }

        var answerSdp = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        logger.LogInformation("Received SDP answer:\n{Sdp}", answerSdp);

        var sdp = SDP.ParseSDPDescription(answerSdp);
        if (sdp == null)
        {
            logger.LogError("Failed to parse SDP answer.");
            return;
        }

        var setResult = peerConnection.SetRemoteDescription(SdpType.answer, sdp);
        if (setResult != SetDescriptionResultEnum.OK)
        {
            logger.LogError("Failed to set remote description: {Result}.", setResult);
            return;
        }

        logger.LogInformation("Remote description applied. ICE negotiation in progress.");

        Cv2.NamedWindow(WindowName, WindowFlags.AutoSize);

        using var exitCts = new CancellationTokenSource();
        var shutdownTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitCts.Cancel();
            shutdownTcs.TrySetResult();
        };

        peerConnection.onconnectionstatechange += state =>
        {
            logger.LogDebug("Peer connection state changed to {State}.", state);
            if (state == RTCPeerConnectionState.failed || state == RTCPeerConnectionState.closed)
            {
                shutdownTcs.TrySetResult();
            }
        };

        peerConnection.oniceconnectionstatechange += state =>
        {
            logger.LogDebug("ICE connection state changed to {State}.", state);
        };

        try
        {
            while (!exitCts.IsCancellationRequested)
            {
                if (shutdownTcs.Task.IsCompleted)
                {
                    exitCts.Cancel();
                    break;
                }

                Mat? frameToShow = null;

                lock (FrameSync)
                {
                    if (framePending && latestFrame != null)
                    {
                        frameToShow = latestFrame.Clone();
                        framePending = false;
                    }
                }

                if (frameToShow != null)
                {
                    Cv2.ImShow(WindowName, frameToShow);
                    frameToShow.Dispose();

                    if (!firstFrameLogged)
                    {
                        logger.LogInformation("Displaying first frame.");
                        firstFrameLogged = true;
                    }
                }

                var key = Cv2.WaitKey(10);
                if (key == 27 || key == 'q')
                {
                    shutdownTcs.TrySetResult();
                    exitCts.Cancel();
                }
            }

            await shutdownTcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }

        if (videoEndPoint != null)
        {
            await videoEndPoint.CloseVideoSink().ConfigureAwait(false);
        }

        lock (FrameSync)
        {
            latestFrame?.Dispose();
            latestFrame = null;
            framePending = false;
        }

        Cv2.DestroyAllWindows();

        logger.LogInformation("Client exiting.");
    }

    private static async Task<RTCPeerConnection> CreatePeerConnection()
    {
        var peerConnection = new RTCPeerConnection();

        videoEndPoint = new FFmpegVideoEndPoint();
        videoEndPoint.RestrictFormats(format => format.Codec == VideoCodecsEnum.VP8);

        videoEndPoint.OnVideoSinkDecodedSampleFaster += rawImage =>
        {
            logger.LogDebug("Decoded fast frame format={Format} {Width}x{Height} stride={Stride}.", rawImage.PixelFormat, rawImage.Width, rawImage.Height, rawImage.Stride);
            var (buffer, width, height, matType, conversion) = ConvertRawImage(rawImage);
            if (buffer != null)
            {
                UpdateFrame(buffer, width, height, matType, conversion);
            }
        };

        videoEndPoint.OnVideoSinkDecodedSample += (byte[] buffer, uint width, uint height, int stride, VideoPixelFormatsEnum pixelFormat) =>
        {
            if (pixelFormat == VideoPixelFormatsEnum.Rgb)
            {
                logger.LogDebug("Decoded RGB frame {Width}x{Height} stride={Stride}.", width, height, stride);
                UpdateFrame(buffer, (int)width, (int)height, MatType.CV_8UC3, ColorConversionCodes.RGB2BGR);
            }
            else if (pixelFormat == VideoPixelFormatsEnum.Bgr)
            {
                logger.LogDebug("Decoded BGR frame {Width}x{Height} stride={Stride}.", width, height, stride);
                UpdateFrame(buffer, (int)width, (int)height, MatType.CV_8UC3, null);
            }
            else
            {
                logger.LogWarning("Unhandled decoded sample pixel format {PixelFormat} (stride={Stride}).", pixelFormat, stride);
            }
        };

        MediaStreamTrack videoTrack = new(videoEndPoint.GetVideoSinkFormats(), MediaStreamStatusEnum.RecvOnly);
        peerConnection.addTrack(videoTrack);

        peerConnection.OnVideoFormatsNegotiated += formats =>
        {
            var format = formats.First();
            videoEndPoint.SetVideoSinkFormat(format);
            logger.LogInformation("Negotiated video format {FormatId}:{Codec}.", format.FormatID, format.Codec);
        };

        peerConnection.OnVideoFrameReceived += videoEndPoint.GotVideoFrame;

        await videoEndPoint.StartVideoSink().ConfigureAwait(false);

        return peerConnection;
    }

    private static (byte[]? buffer, int width, int height, MatType matType, ColorConversionCodes? conversion) ConvertRawImage(RawImage rawImage)
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
            using var yuv = Mat.FromPixelData(height + height / 2, width, MatType.CV_8UC1, buffer);
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
        logger.LogWarning("Unhandled raw image pixel format {PixelFormat} (stride={Stride}).", pixelFormat, stride);
        return null;
    }

    private static void UpdateFrame(byte[] buffer, int width, int height, MatType matType, ColorConversionCodes? conversion)
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
