using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.Encoders.Codecs;

const string DefaultOfferUrl = "http://127.0.0.1:8080/offer";
var offerUrl = Environment.GetEnvironmentVariable("WEBRTC_OFFER_URL") ?? DefaultOfferUrl;

var rtcConfig = new RTCConfiguration { iceServers = null };
using var pc = new RTCPeerConnection(rtcConfig);
using var vp8Decoder = new Vp8Codec();
vp8Decoder.InitialiseDecoder();

Cv2.NamedWindow("WebRTC-Client", WindowFlags.AutoSize);

var frameLock = new object();
var frameSize = (width: 0, height: 0);
Mat? yuvMat = null;
Mat? bgrMat = null;
var firstFrameLogged = false;

pc.onconnectionstatechange += state => Console.WriteLine($"Connection state: {state}");
pc.oniceconnectionstatechange += state => Console.WriteLine($"ICE connection state: {state}");
pc.onicecandidate += candidate =>
{
    if (candidate != null)
    {
        Console.WriteLine($"ICE candidate gathered: {candidate.candidate}");
    }
};
pc.OnVideoFormatsNegotiated += formats => Console.WriteLine("Video formats negotiated: " + string.Join(", ", formats));
pc.OnRtpPacketReceived += (remote, media, rtp) =>
{
    if (media == SDPMediaTypesEnum.video)
    {
        Console.WriteLine($"RTP packet received: pt={rtp.Header.PayloadType}, payload={rtp.Payload?.Length ?? 0}");
    }
};
pc.OnVideoFrameReceived += (remoteEndPoint, timestamp, buffer, videoFormat) =>
{
    Console.WriteLine($"OnVideoFrameReceived invoked: buffer={buffer?.Length ?? 0}, formatID={videoFormat.FormatID}, codec={videoFormat.Codec}");
    ProcessEncodedSample(buffer ?? Array.Empty<byte>());
};
pc.OnVideoFrameReceivedByIndex += (trackIndex, remoteEndPoint, timestamp, buffer, videoFormat) =>
{
    Console.WriteLine($"OnVideoFrameReceivedByIndex invoked: track={trackIndex}, buffer={buffer?.Length ?? 0}, formatID={videoFormat.FormatID}, codec={videoFormat.Codec}");
    ProcessEncodedSample(buffer ?? Array.Empty<byte>());
};

void ProcessEncodedSample(byte[] buffer)
{
    try
    {
        if (buffer.Length == 0)
        {
            Console.WriteLine("Encoded sample empty.");
            return;
        }

        lock (frameLock)
        {
            uint width;
            uint height;
            var decodedFrames = vp8Decoder.Decode(buffer, buffer.Length, out width, out height);
            if (decodedFrames == null || decodedFrames.Count == 0 || width == 0 || height == 0)
            {
                Console.WriteLine("Decoder returned no frames.");
                return;
            }

            foreach (var frame in decodedFrames)
            {
                var w = (int)width;
                var h = (int)height;
                var expectedLength = h * w * 3 / 2;
                if (frame.Length != expectedLength)
                {
                    Console.WriteLine($"Unexpected frame length {frame.Length} for {w}x{h} I420 (expected {expectedLength}).");
                    continue;
                }

                if (frameSize.width != w || frameSize.height != h || yuvMat == null || bgrMat == null)
                {
                    yuvMat?.Dispose();
                    bgrMat?.Dispose();
                    frameSize = (w, h);
                    yuvMat = new Mat(h * 3 / 2, w, MatType.CV_8UC1);
                    bgrMat = new Mat(h, w, MatType.CV_8UC3);
                    Console.WriteLine($"Allocated buffers for {w}x{h}");
                }

                System.Runtime.InteropServices.Marshal.Copy(frame, 0, yuvMat.Data, frame.Length);
                Cv2.CvtColor(yuvMat, bgrMat, ColorConversionCodes.YUV2BGR_I420);
                Cv2.ImShow("WebRTC-Client", bgrMat);
                Cv2.WaitKey(1);

                if (!firstFrameLogged)
                {
                    Console.WriteLine($"Displaying first frame {w}x{h}");
                    firstFrameLogged = true;
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Video frame processing error: {ex.Message}");
    }
}

// Request inbound video from the server (RecvOnly track).
var videoFormats = new List<VideoFormat>
{
    new VideoFormat(VideoCodecsEnum.VP8, 96, 90000, null)
};
var recvOnlyTrack = new MediaStreamTrack(videoFormats, MediaStreamStatusEnum.RecvOnly);
pc.addTrack(recvOnlyTrack);

Console.WriteLine($"Posting offer to {offerUrl}");
var offer = pc.createOffer(null);
await pc.setLocalDescription(offer);
Console.WriteLine("Local SDP offer created.");

using var http = new HttpClient();
var request = new StringContent(offer.sdp ?? string.Empty, Encoding.UTF8, "application/sdp");
var response = await http.PostAsync(offerUrl, request);
response.EnsureSuccessStatusCode();
var answerSdp = await response.Content.ReadAsStringAsync();
Console.WriteLine("Received SDP answer from server.");

var answer = new RTCSessionDescriptionInit { type = RTCSdpType.answer, sdp = answerSdp };
var setRemoteResult = pc.setRemoteDescription(answer);
if (setRemoteResult != SetDescriptionResultEnum.OK)
{
    throw new ApplicationException($"Failed to set remote description: {setRemoteResult}");
}
Console.WriteLine("Remote SDP answer applied.");

var exitEvent = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    exitEvent.Set();
};

Console.WriteLine("Receiving media... Press Ctrl+C to exit.");
exitEvent.Wait();

pc.Close("normal");
lock (frameLock)
{
    yuvMat?.Dispose();
    bgrMat?.Dispose();
}
Cv2.DestroyAllWindows();
Console.WriteLine("Peer connection closed.");