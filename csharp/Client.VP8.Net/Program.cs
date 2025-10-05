using System;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Serilog;
using Serilog.Extensions.Logging;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using Vpx.Net;
using WebSocketSharp.Server;
using MicrosoftLogger = Microsoft.Extensions.Logging.ILogger;

namespace Client.VP8.Net;

class Program
{
    private const int WebSocketPort = 8081;
    private const string WindowName = "WebRTC Client";

    private static readonly object FrameSync = new();

    private static MicrosoftLogger logger = NullLogger.Instance;
    private static Mat? latestFrame;
    private static bool framePending;
    private static bool firstFrameLogged;

    static void Main(string[] args)
    {
        Run();
    }

    private static void Run()
    {
        Console.WriteLine("WebRTC Client Test Console");

        logger = AddConsoleLogger();

        var webSocketServer = StartWebSocketServer();
        Cv2.NamedWindow(WindowName, WindowFlags.AutoSize);

        using var exitCts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitCts.Cancel();
        };

        try
        {
            RunDisplayLoop(exitCts);
        }
        finally
        {
            webSocketServer.Stop();
            ResetFrameState();
            Cv2.DestroyAllWindows();
        }

        Console.WriteLine("Client shutting down.");
    }

    private static WebSocketServer StartWebSocketServer()
    {
        var webSocketServer = new WebSocketServer(IPAddress.Any, WebSocketPort);
        webSocketServer.AddWebSocketService<WebRTCWebSocketPeer>(
            "/",
            peer => peer.CreatePeerConnection = CreatePeerConnection
        );
        webSocketServer.Start();

        Console.WriteLine(
            $"Waiting for WebSocket connections on ws://0.0.0.0:{WebSocketPort}"
        );
        Console.WriteLine("Press Ctrl+C to exit.");

        return webSocketServer;
    }

    private static void RunDisplayLoop(CancellationTokenSource exitCts)
    {
        while (!exitCts.IsCancellationRequested)
        {
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

            var key = Cv2.WaitKey(10);
            if (IsExitKey(key))
            {
                exitCts.Cancel();
                break;
            }

            Thread.Sleep(10);
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

    private static Task<RTCPeerConnection> CreatePeerConnection()
    {
        var peerConnection = new RTCPeerConnection();

        var videoEndPoint = CreateVideoEndPoint();
        ConfigureVideoPipeline(peerConnection, videoEndPoint);
        RegisterConnectionDiagnostics(peerConnection);

        return Task.FromResult(peerConnection);
    }

    private static Vp8NetVideoEncoderEndPoint CreateVideoEndPoint()
    {
        var videoEndPoint = new Vp8NetVideoEncoderEndPoint();
        AttachVideoFrameHandlers(videoEndPoint);
        return videoEndPoint;
    }

    private static void AttachVideoFrameHandlers(
        Vp8NetVideoEncoderEndPoint videoEndPoint
    )
    {
        videoEndPoint.OnVideoSinkDecodedSampleFaster += ProcessFastDecodedSample;
        videoEndPoint.OnVideoSinkDecodedSample += ProcessDecodedSample;
    }

    private static void ConfigureVideoPipeline(
        RTCPeerConnection peerConnection,
        Vp8NetVideoEncoderEndPoint videoEndPoint
    )
    {
        MediaStreamTrack videoTrack = new(
            videoEndPoint.GetVideoSinkFormats(),
            MediaStreamStatusEnum.RecvOnly
        );
        peerConnection.addTrack(videoTrack);

        peerConnection.OnVideoFrameReceived += (rep, ts, frame, pixelFmt) =>
        {
            logger.LogDebug(
                "Video frame received {Length} bytes from {Remote}.",
                frame.Length,
                rep
            );
            videoEndPoint.GotVideoFrame(rep, ts, frame, pixelFmt);
        };

        peerConnection.OnVideoFormatsNegotiated += formats =>
        {
            var format = formats.First();
            videoEndPoint.SetVideoSinkFormat(format);
            logger.LogInformation(
                "Negotiated video format {FormatId}:{Codec}.",
                format.FormatID,
                format.Codec
            );
        };
    }

    private static void RegisterConnectionDiagnostics(
        RTCPeerConnection peerConnection
    )
    {
        peerConnection.onconnectionstatechange += state =>
        {
            logger.LogDebug("Peer connection state change to {State}.", state);
        };

        peerConnection.oniceconnectionstatechange += state =>
        {
            logger.LogDebug("ICE connection state change to {State}.", state);
        };
    }

    private static void ProcessFastDecodedSample(RawImage rawImage)
    {
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
        switch (pixelFormat)
        {
            case VideoPixelFormatsEnum.Rgb:
                logger.LogDebug(
                    "Decoded RGB frame {Width}x{Height} stride={Stride}.",
                    width,
                    height,
                    stride
                );
                UpdateFrame(
                    buffer,
                    (int)width,
                    (int)height,
                    MatType.CV_8UC3,
                    ColorConversionCodes.RGB2BGR
                );
                break;

            case VideoPixelFormatsEnum.Bgr:
                logger.LogDebug(
                    "Decoded BGR frame {Width}x{Height} stride={Stride}.",
                    width,
                    height,
                    stride
                );
                UpdateFrame(buffer, (int)width, (int)height, MatType.CV_8UC3, null);
                break;

            default:
                logger.LogWarning(
                    "Unhandled decoded sample pixel format {PixelFormat} (stride={Stride}).",
                    pixelFormat,
                    stride
                );
                break;
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

            return rawImage.PixelFormat switch
            {
                VideoPixelFormatsEnum.Rgb => (
                    buffer,
                    width,
                    height,
                    MatType.CV_8UC3,
                    ColorConversionCodes.RGB2BGR
                ),
                VideoPixelFormatsEnum.Bgr => (
                    buffer,
                    width,
                    height,
                    MatType.CV_8UC3,
                    null
                ),
                _ => (
                    LogUnhandled(rawImage.PixelFormat, stride),
                    0,
                    0,
                    MatType.CV_8UC3,
                    null
                ),
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to convert raw image frame.");
            return (null, 0, 0, MatType.CV_8UC3, null);
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
            if (width <= 0 || height <= 0)
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

    private static MicrosoftLogger AddConsoleLogger()
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
