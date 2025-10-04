# WebRTC File Stream Implementation Plan (csharp/server & csharp/client)

## Overview
- Target: play a local video file over WebRTC from csharp/server to csharp/client using SIPSorcery and OpenCV.
- Scope: local LAN/loopback, host ICE only, VP8 video without audio for initial release.
- Approach: build in small increments, verify after each milestone, keep artifacts runnable on Windows first and extend later.

## Milestones
| Stage | Goal | Key Artifacts | Validation |
| --- | --- | --- | --- |
| 0 | Tooling ready | ffmpeg, .NET 8 SDK | fmpeg -version, dotnet --info |
| 1 | .NET projects scaffolded | csharp/server, csharp/client | dotnet restore in each project |
| 2 | Dependencies resolved | Updated .csproj files | Restore succeeds, runtime DLLs located |
| 3 | Server HTTP skeleton | Minimal API with /offer stub | dotnet run responds to health check |
| 4 | Server WebRTC sender | FFmpeg video source, SDP answer | Manual SDP exchange returns answer |
| 5 | Client signaling shell | Offer/answer exchange implemented | Logs show PC state: connected |
| 6 | Client video rendering | I420->BGR via OpenCV, render window | Live playback visible |
| 7 | Cross-platform polish | RID-aware packaging, docs | dotnet publish -r on target OS |
| 8 | Regression checklist | Test matrix, tuning notes | All regression tasks executed |

## Stage 0: Prerequisites
### Tasks
- Install ffmpeg on Windows (winget install Gyan.FFmpeg or choco install ffmpeg).
- Confirm ffmpeg availability with fmpeg -version.
- Install .NET 8 SDK on Windows (winget install Microsoft.DotNet.SDK.8).
- Install .NET 8 SDK on macOS (brew install --cask dotnet-sdk@8) or use the official installer if Homebrew lacks versioned formulae.
- Verify with dotnet --info.
- Decide initial sample file path and store in notes.
### Validation
- Both commands return versions without errors.
- Sample video file accessible by absolute path.

## Stage 1: Project Scaffolding
### Tasks
- In repo root, create directories csharp/server and csharp/client if missing.
- Run dotnet new console -n server -o csharp/server.
- Run dotnet new console -n client -o csharp/client.
- Add shared solution file later if useful (optional at this stage).
### Validation
- dotnet restore inside each directory succeeds.
- Template Program.cs builds with dotnet run (prints "Hello World!").

## Stage 2: Dependency Setup
### Tasks
- Edit csharp/server/server.csproj to include packages: SIPSorcery, SIPSorceryMedia.FFmpeg.
- Edit csharp/client/client.csproj to include packages: SIPSorcery, OpenCvSharp4, OpenCvSharp4.runtime.win (initial RID: win-x64).
- Consider Microsoft.Extensions.Hosting only if later required.
- Run dotnet restore in each project to download packages.
- Confirm native OpenCV DLLs appear under csharp/client/bin/.../runtimes/win-x64/native.
### Validation
- Restore produces no errors.
- dotnet run still works (even if minimal code) proving assemblies load.

## Stage 3: Server HTTP Skeleton
### Tasks
- Replace csharp/server/Program.cs with minimal API hosting http://127.0.0.1:8080.
- Implement /health GET endpoint returning simple text for quick checks.
- Stub /offer POST endpoint that echoes request body for now.
- Add logging for incoming requests.
### Validation
- Run dotnet run in csharp/server.
- Invoke-RestMethod http://127.0.0.1:8080/health returns expected string.
- POST to /offer returns request echo (using PowerShell Invoke-RestMethod -Method Post).

## Stage 4: Server WebRTC Sender
### Tasks
- Instantiate RTCPeerConnection with host-only ICE and subscribe to state/ICE events for diagnostics.
- Create an FFmpegFileSource using WEBRTC_FILE (fallback to sample.mp4), configure VP8 1280x720@30, and feed its encoded samples into the peer connection.
- Parse the incoming SDP offer, set it as the remote description, generate the answer, and apply setLocalDescription before streaming.
- Implement cleanup so FFmpeg and the peer connection dispose when the session ends or fails.
### Validation
- Start the server with WEBRTC_FILE pointing at a valid mp4; confirm logs show FFmpeg startup.
- POST a captured SDP offer (from client Stage 5) and ensure an SDP answer is produced without exceptions.
- Check server logs for ICE/state transitions and absence of unhandled errors.

### Tasks
- Implement RTCPeerConnection in csharp/client/Program.cs (no rendering yet).
- Create offer, set local description.
- POST offer SDP to http://127.0.0.1:8080/offer using HttpClient.
- Apply answer to peer connection.
- Hook OnConnectionStateChange to log transitions.
### Validation
- Run server (Stage 4) and client.
- Console logs show PC state: connected (or connecting then connected).
- Any ICE or SDP errors resolved.

## Stage 6: Client Video Rendering
### Tasks
- Subscribe to OnVideoFrameReceived.
- Validate pixelFormat is I420; log warning for others.
- Convert I420 buffer to BGR Mat (helper function). Reuse Mats where possible.
- Display frame via Cv2.ImShow("WebRTC-Client", mat) and call Cv2.WaitKey(1).
- Handle graceful shutdown on Ctrl+C (optional: catch Console.CancelKeyPress).
### Validation
- Client window opens showing video playback with acceptable latency.
- No rapid exceptions logged; process exits cleanly on Ctrl+C.

## Stage 7: Cross-Platform Adjustments
### Tasks
- Update client project to include conditional OpenCV runtime packages based on RID (e.g., use ItemGroup with Condition for win-x64, linux-x64, osx-x64).
- Document required ffmpeg installation commands per OS.
- Add README note on setting WEBRTC_FILE for each shell.
- Test dotnet publish -c Release -r win-x64 for both server and client.
- If possible, smoke test on Linux/macOS containers or hosts.
### Validation
- Publish outputs contain native dependencies for target RID.
- On second OS, playback works following same instructions.

## Stage 8: Regression and Tuning
### Tasks
- Compile checklist covering: different video files, resolution changes (720p vs 480p), missing env var fallback, ffmpeg not found error handling.
- Measure CPU usage, adjust encoding parameters if high (e.g., lower resolution or fps).
- Document troubleshooting steps (I420 mismatch, ffmpeg path, firewall).
- Consider backlog items: audio support, dynamic constraints, multi-client handling.
### Validation
- Checklist executed with notes stored (e.g., under docs/verification-log.md).
- Known issues documented for future work.

## Deliverables
- Updated source in csharp/server and csharp/client following staged plan.
- Plan tracking document (this file) committed for reference.
- Optional logs or scripts created during validation stored under scripts/ or docs/ folders.

## Progress Log
- 2025-10-04 Stage 0: ffmpeg 8.0 verified; .NET 8.0.414 available.
- 2025-10-04 Stage 1: Created csharp solution with Server/Client console apps, baseline run prints Hello World.

- 2025-10-04 Stage 2: Added SIPSorcery/SIPSorceryMedia.FFmpeg to server and SIPSorcery/OpenCvSharp4 (win runtime) to client; solution restore successful.
- 2025-10-04 Stage 3: Implemented server HTTP skeleton with /health and /offer echo; verified via Invoke-RestMethod.
- 2025-10-04 Stage 4: Wired RTCPeerConnection + FFmpegFileSource to return SDP answers; added cleanup logging.
- 2025-10-04 Stage 3: Implemented server HTTP skeleton with /health and /offer echo; verified via Invoke-RestMethod.
