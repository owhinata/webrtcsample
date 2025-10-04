# Platform Progress Summary

## Windows
### Stage 0 - Tooling
- Confirmed .NET SDK 8.0.414 and ffmpeg 8.0 availability.
```powershell
dotnet --info
ffmpeg -version
```
> Note: If `dotnet` or `ffmpeg` are missing, install them and retry the checks.
> Windows (PowerShell):
```powershell
winget install Microsoft.DotNet.SDK.8
winget install Gyan.FFmpeg
```
> Linux (Debian/Ubuntu):
```bash
sudo apt update
sudo apt install -y dotnet-sdk-8.0 ffmpeg
```
> macOS (Homebrew):
```bash
brew install --cask dotnet-sdk@8
brew install ffmpeg
```

### Stage 1 - Project Scaffolding
- Created csharp solution layout and validated templates.
```powershell
New-Item -ItemType Directory -Path "csharp" -Force
dotnet new console -n Server -o "csharp/Server"
dotnet new console -n Client -o "csharp/Client"
dotnet new sln -n webrtcsample -o "csharp"
dotnet sln "csharp/webrtcsample.sln" add "csharp/Server/Server.csproj"
dotnet sln "csharp/webrtcsample.sln" add "csharp/Client/Client.csproj"
dotnet restore "csharp/webrtcsample.sln"
dotnet run --project "csharp/Server/Server.csproj"
dotnet run --project "csharp/Client/Client.csproj"
```

### Stage 2 - Dependencies
- Added WebRTC/OpenCV package references and verified restore succeeds.
- Configured OS-specific OpenCV runtime packages in `csharp/Client/Client.csproj` so native binaries load on Windows/Linux/macOS.
```powershell
dotnet add "csharp/Server/Server.csproj" package SIPSorcery
dotnet add "csharp/Server/Server.csproj" package SIPSorceryMedia.FFmpeg
dotnet add "csharp/Client/Client.csproj" package SIPSorcery
dotnet add "csharp/Client/Client.csproj" package OpenCvSharp4
dotnet add "csharp/Client/Client.csproj" package OpenCvSharp4.runtime.win
dotnet restore "csharp/webrtcsample.sln"
```
#### Tips: Check for package updates
```powershell
dotnet list "csharp/Server/Server.csproj" package --outdated
dotnet list "csharp/Client/Client.csproj" package --outdated
```

### Stage 3 - Server Skeleton
- Introduced minimal HTTP host with `/health` and `/offer` echo endpoint; health verified via local request.
```powershell
dotnet build "csharp/webrtcsample.sln"
$server = Start-Process -FilePath "dotnet" -ArgumentList "run --project `"csharp/Server/Server.csproj`" --no-build" -WorkingDirectory "$PWD" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 3
Invoke-RestMethod -Uri "http://127.0.0.1:8080/health"
if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id; $server.WaitForExit() }
```

### Stage 4 - WebRTC Sender
- Connected FFmpegFileSource to RTCPeerConnection and return SDP answers with cleanup when sessions end.
```powershell
$Env:WEBRTC_FILE = "C:\\video\\sample.mp4"  # adjust to your mp4 path
dotnet build "csharp/webrtcsample.sln"
$server = Start-Process -FilePath "dotnet" -ArgumentList "run --project `"csharp/Server/Server.csproj`" --no-build" -WorkingDirectory "$PWD" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 3
$offer = Get-Content "offer.sdp" -Raw  # SDP captured from client Stage 5
$answer = Invoke-RestMethod -Uri "http://127.0.0.1:8080/offer" -Method Post -ContentType "application/sdp" -Body $offer
if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id; $server.WaitForExit() }
```

### Stage 5 - Client Signaling
- Built client RTCPeerConnection offer/answer flow; verified server returns SDP answer and connection reaches connected state.
```powershell
$serverJob = Start-Job { param($wd,$video) $Env:WEBRTC_FILE = $video; Set-Location $wd; dotnet run --project "csharp/Server/Server.csproj" --no-build } -ArgumentList (Get-Location),(Resolve-Path "sample.mp4").Path
Start-Sleep -Seconds 3
dotnet run --project "csharp/Client/Client.csproj" --no-build
Stop-Job $serverJob
Receive-Job $serverJob
```


### Stage 6 - Video Rendering
- Decoded VP8 samples with `Vp8Codec` and displayed I420 frames via OpenCV; verified ICE/connection states reach `connected`.
```powershell
$serverJob = Start-Job { param($wd,$video) $Env:WEBRTC_FILE = $video; Set-Location $wd; dotnet run --project "csharp/Server/Server.csproj" --no-build } -ArgumentList (Get-Location),(Resolve-Path "sample.mp4").Path
Start-Sleep -Seconds 3
$clientJob = Start-Job { param($wd) Set-Location $wd; dotnet run --project "csharp/Client/Client.csproj" --no-build } -ArgumentList (Get-Location)
Wait-Job $clientJob -Timeout 15 | Out-Null
Stop-Job $clientJob
Stop-Job $serverJob
Receive-Job $clientJob
Receive-Job $serverJob
```
### Formatting Pass
- Normalized encoding, newlines, and tabs across tracked files; confirmed results.
```powershell
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$targets = @(
    "web_rtc_file_stream_plan.md",
    "web_rtc_file_stream_cross_platform_sipsorcery_open_cv.md",
    "csharp/webrtcsample.sln",
    "csharp/Server/Server.csproj",
    "csharp/Client/Client.csproj",
    "csharp/Server/Program.cs",
    "csharp/Client/Program.cs",
    "platform_progress.md"
)
foreach ($path in $targets) {
    if (Test-Path $path) {
        $text = [IO.File]::ReadAllText($path)
        $text = $text -replace "`r`n", "`n"
        $text = $text -replace "`t", "    "
        [IO.File]::WriteAllText($path, $text, $utf8NoBom)
    }
}
Write-Host "CRLF present in plan:" ([IO.File]::ReadAllText("web_rtc_file_stream_plan.md") -match "`r`n")
Write-Host "Tabs present in platform progress:" ([IO.File]::ReadAllText("platform_progress.md") -match "`t")
```


## Linux
- Pending: Tooling installation and validation steps have not been executed yet.

## macOS
- Pending: Tooling installation and validation steps have not been executed yet.
