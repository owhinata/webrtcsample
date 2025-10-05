# WebRTC Sample

WebRTC video streaming samples using C# and SIPSorcery with H.264 and VP8 codec support.

## Overview

This repository contains WebRTC client and server implementations demonstrating real-time video streaming with multiple codec support.

### Features

- **Multi-codec support**: H.264 and VP8 video codecs
- **C# Server**: Streams video using FFmpeg or test pattern generation
- **C# Clients**:
  - FFmpeg-based client with configurable codec selection
  - VP8.Net-based client with native VP8 decoding
- **Performance monitoring**: Frame rate and throughput metrics
- **MP4 file streaming**: Supports streaming from local MP4 files

## Project Structure

```
webrtcsample/
├── csharp/
│   ├── Server/          # WebRTC server (H.264/VP8)
│   ├── Client/          # FFmpeg-based client (H.264/VP8)
│   └── Client.VP8.Net/  # VP8.Net-based client (VP8 only)
└── VP8.Net/             # VP8 codec library (submodule)
```

## Requirements

- .NET 8.0 SDK
- FFmpeg (for H.264 support)
- OpenCV (for video display)

## Building

Build all projects:

```bash
dotnet build csharp
```

Build individual projects:

```bash
dotnet build csharp/Server
dotnet build csharp/Client
dotnet build csharp/Client.VP8.Net
```

## Running

### Server

The server supports both H.264 and VP8 codecs. It will automatically use an MP4 file if found at `csharp/Server/media/video.mp4`, otherwise it generates a test pattern.

```bash
dotnet run --project csharp/Server
```

The server listens on `https://localhost:5443` by default.

### Client (FFmpeg-based)

#### Using H.264 (default):

```bash
dotnet run --project csharp/Client
```

#### Using VP8:

Using launch profile:
```bash
dotnet run --project csharp/Client --launch-profile "Client (VP8)"
```

Or using environment variable:
```bash
WEBRTC_VIDEO_CODEC=VP8 dotnet run --project csharp/Client --no-launch-profile
```

#### Available launch profiles:
- `Client (H264)` - Use H.264 codec (default)
- `Client (VP8)` - Use VP8 codec

### Client.VP8.Net

VP8-only client using native VP8.Net decoder:

```bash
dotnet run --project csharp/Client.VP8.Net
```

This client uses WebSocket on port `8081` for signaling.

## Configuration

### Server

- MP4 file path: `csharp/Server/media/video.mp4`
- Test pattern frame rate: 30 fps (configurable in `Program.cs`)

### Client

Environment variables:
- `WEBRTC_VIDEO_CODEC`: Set to `VP8` or `H264` to select codec
- `WEBRTC_OFFER_URL`: Server offer endpoint (default: `https://localhost:5443/offer`)

## Performance Metrics

Both clients display performance metrics every 5 seconds:
- Frame rate (fps)
- Throughput (kbps) - Client.VP8.Net only
- Frame dimensions and pixel format

## License

See [LICENSE](LICENSE) file for details.

## Dependencies

- [SIPSorcery](https://github.com/sipsorcery-org/sipsorcery) - WebRTC implementation
- [SIPSorceryMedia.FFmpeg](https://github.com/sipsorcery-org/SIPSorceryMedia.FFmpeg) - FFmpeg bindings
- [VP8.Net](https://github.com/sipsorcery-org/VP8.Net) - VP8 codec implementation
- [OpenCvSharp](https://github.com/shimat/opencvsharp) - OpenCV bindings for .NET
