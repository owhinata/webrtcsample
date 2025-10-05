using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
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
        Console.WriteLine("WebRTC Client Test Console");

        logger = AddConsoleLogger();

        var webSocketServer = new WebSocketServer(IPAddress.Any, WebSocketPort);
        webSocketServer.AddWebSocketService<WebRTCWebSocketPeer>("/", peer => peer.CreatePeerConnection = CreatePeerConnection);
        webSocketServer.Start();

        Console.WriteLine($"Waiting for WebSocket connections on ws://0.0.0.0:{WebSocketPort}");
        Console.WriteLine("Press Ctrl+C to exit.");

        Cv2.NamedWindow(WindowName, WindowFlags.AutoSize);

        using var exitCts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            exitCts.Cancel();
        };

        try
        {
            while (!exitCts.IsCancellationRequested)
            {
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
                    exitCts.Cancel();
                }
            }
        }
        finally
        {
            webSocketServer.Stop();

            lock (FrameSync)
            {
                latestFrame?.Dispose();
                latestFrame = null;
                framePending = false;
            }

            Cv2.DestroyAllWindows();
        }

        Console.WriteLine("Client shutting down.");
    }

    private static Task<RTCPeerConnection> CreatePeerConnection()
    {
        var peerConnection = new RTCPeerConnection();

        var videoEndPoint = new Vp8NetVideoEncoderEndPoint();

        videoEndPoint.OnVideoSinkDecodedSampleFaster += rawImage =>
        {
            var (buffer, width, height, matType, conversion) = ConvertRawImage(rawImage);
            if (buffer != null)
            {
                UpdateFrame(buffer, width, height, matType, conversion);
            }
        };

       videoEndPoint.OnVideoSinkDecodedSample += (byte[] bmp, uint width, uint height, int stride, VideoPixelFormatsEnum pixelFormat) =>
        {
            if (pixelFormat == VideoPixelFormatsEnum.Rgb)
            {
                logger.LogDebug("Decoded RGB frame {Width}x{Height} stride={Stride}.", width, height, stride);
                UpdateFrame(bmp, (int)width, (int)height, MatType.CV_8UC3, ColorConversionCodes.RGB2BGR);
            }
            else if (pixelFormat == VideoPixelFormatsEnum.Bgr)
            {
                logger.LogDebug("Decoded BGR frame {Width}x{Height} stride={Stride}.", width, height, stride);
                UpdateFrame(bmp, (int)width, (int)height, MatType.CV_8UC3, null);
            }
            else
            {
                logger.LogWarning("Unhandled decoded sample pixel format {PixelFormat} (stride={Stride}).", pixelFormat, stride);
            }
        };

        MediaStreamTrack videoTrack = new(videoEndPoint.GetVideoSinkFormats(), MediaStreamStatusEnum.RecvOnly);
        peerConnection.addTrack(videoTrack);

        peerConnection.OnVideoFrameReceived += (rep, ts, frame, pixelFmt) =>
        {
            logger.LogDebug($"Video frame received {frame.Length} bytes from {rep}.");
            videoEndPoint.GotVideoFrame(rep, ts, frame, pixelFmt);
        };

        peerConnection.OnVideoFormatsNegotiated += formats =>
        {
            var format = formats.First();
            videoEndPoint.SetVideoSinkFormat(format);
            logger.LogInformation($"Negotiated video format {format.FormatID}:{format.Codec}.");
        };

        peerConnection.onconnectionstatechange += state =>
        {
            logger.LogDebug($"Peer connection state change to {state}.");
        };

        peerConnection.oniceconnectionstatechange += state =>
        {
            logger.LogDebug($"ICE connection state change to {state}.");
        };

        return Task.FromResult(peerConnection);
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

            return rawImage.PixelFormat switch
            {
                VideoPixelFormatsEnum.Rgb => (buffer, width, height, MatType.CV_8UC3, ColorConversionCodes.RGB2BGR),
                VideoPixelFormatsEnum.Bgr => (buffer, width, height, MatType.CV_8UC3, null),
                _ => (LogUnhandled(rawImage.PixelFormat, stride), 0, 0, MatType.CV_8UC3, null)
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
        logger.LogWarning("Unhandled raw image pixel format {PixelFormat} (stride={Stride}).", pixelFormat, stride);
        return null;
    }

    private static void UpdateFrame(byte[] buffer, int width, int height, MatType matType, ColorConversionCodes? conversion)
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
