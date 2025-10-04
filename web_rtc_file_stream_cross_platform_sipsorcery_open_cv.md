# WebRTC File Stream (Cross‑platform) — SIPSorcery + OpenCV

最小構成で「動画ファイル → WebRTC（同一PC内） → C#クライアント(OpenCV再生)」を実装します。

- 開発OS: **Windows**（主）
- 併せて **Linux/macOS** でも動作可能
- セキュリティ: ローカル学習用（自己署名、ICE=host のみ）
- ライブラリ: **SIPSorcery**（WebRTC）、**SIPSorceryMedia.FFmpeg**（ファイル入力/エンコード）、**OpenCvSharp4**（再生）
- .NET: **.NET 8**（Console）

---

## 1) プロジェクト構成

```
webrtc-local
├─ server/            # ファイル→WebRTC 送出
│  ├─ Program.cs
│  └─ server.csproj
└─ client/            # 受信→OpenCV再生
   ├─ Program.cs
   └─ client.csproj
```

---

## 2) 事前準備

### 共通（FFmpeg）
- **Windows**: `winget install Gyan.FFmpeg` もしくは `choco install ffmpeg`
- **Ubuntu/Debian**: `sudo apt update && sudo apt install -y ffmpeg`
- **macOS**: `brew install ffmpeg`

> `ffmpeg` が PATH に通っている前提（SIPSorceryMedia.FFmpeg は外部プロセスとして ffmpeg を起動）

### .NET SDK
- .NET 8 SDK をインストール（Windows: `winget install Microsoft.DotNet.SDK.8`）

### OpenCV ランタイム（クライアント側）
OpenCvSharp4 は NuGet の **runtime** パッケージで各OSへ必要なバイナリを展開します。
- Windows: `OpenCvSharp4.runtime.win`
- Linux: `OpenCvSharp4.runtime.ubuntu.20.04`（Ubuntu 22.04でも可）
- macOS: `OpenCvSharp4.runtime.osx`

> OSごとに `runtime` を条件付き参照（`RuntimeIdentifier`）するか、開発OSに合わせて一旦固定でOK。学習目的なら最初は Windows のみで開始→後で条件分岐に。

---

## 3) サーバ（server/）

### server.csproj
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SIPSorcery" Version="6.*" />
    <PackageReference Include="SIPSorceryMedia.FFmpeg" Version="6.*" />
    <PackageReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
```

### Program.cs（Minimal API, /offer に SDP(Offer) を投げると Answer を返す）
```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using SIPSorcery.Net;
using SIPSorceryMedia.FFmpeg;
using SIPSorceryMedia.Abstractions;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// 動画ファイルパス（絶対/相対OK）
// 例: Windows:  C:\\video\\sample.mp4
//     Linux:    /home/user/video/sample.mp4
//     macOS:    /Users/user/video/sample.mp4
string videoPath = Environment.GetEnvironmentVariable("WEBRTC_FILE") ?? "sample.mp4";

// FFmpeg 入力（読み取り専用）を作る関数
(FFmpegVideoSource source, Task startTask) CreateVideoSource(string path)
{
    var ff = new FFmpegVideoSource(new FFmpegVideoEncoder());
    // 例: 1280x720 / 30fps / VP8 エンコード側は SIPSorcery 側で実施
    ff.SetSource(path);
    // start は別スレッドで開始
    var t = Task.Run(() => ff.StartVideo());
    return (ff, t);
}

app.MapPost("/offer", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body, Encoding.UTF8);
    var offerSdp = await reader.ReadToEndAsync();

    var pc = new RTCPeerConnection(new RTCConfiguration
    {
        iceServers = null // host候補のみ
    });

    // 映像トラック追加
    var (videoSrc, startTask) = CreateVideoSource(videoPath);
    var videoFormat = new VideoFormat(VideoCodecsEnum.VP8, 1280, 720, 30);
    var videoTrack = new MediaStreamTrack(videoSrc.GetVideoSourceFormats(), MediaStreamStatusEnum.SendOnly);
    pc.addTrack(videoTrack);
    await videoSrc.SetVideoSourceFormat(videoFormat);
    videoSrc.OnVideoSourceEncodedSample += pc.SendVideo;

    // 接続状態のログ
    pc.OnConnectionStateChange += (state) => Console.WriteLine($"PC state: {state}");
    pc.OnIceCandidate += (cand) => Console.WriteLine($"ICE: {cand?.candidate}");

    // Remote(Offer) 設定 → Answer 生成
    await pc.setRemoteDescription(new RTCSessionDescriptionInit
    {
        type = RTCSdpType.offer,
        sdp = offerSdp
    });
    var answer = pc.createAnswer();
    await pc.setLocalDescription(answer);

    // FFmpeg 開始（未開始なら）
    _ = startTask;

    return Results.Text(answer.sdp, "application/sdp", Encoding.UTF8);
});

app.Run("http://127.0.0.1:8080");
```

> **メモ**
> - `FFmpegVideoSource` は外部 ffmpeg を呼び出し raw/encoded サンプルを供給します。
> - 最初は VP8/720p/30fps 固定で OK。重ければ 854x480 や 640x360 に下げる。
> - 複数クライアント配信や音声は後で拡張可能。

---

## 4) クライアント（client/）

### client.csproj
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <!-- Windows で実行する場合の例（他OSは適宜差し替え） -->
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="SIPSorcery" Version="6.*" />
    <PackageReference Include="OpenCvSharp4" Version="4.*" />
    <PackageReference Include="OpenCvSharp4.runtime.win" Version="4.*" />
  </ItemGroup>
</Project>
```

> **Linux/macOS** では上の `runtime.win` をそれぞれ `runtime.ubuntu.20.04` / `runtime.osx` に変更。マルチターゲットにする場合は `RuntimeIdentifiers` を用いて OS ごとに条件付け可能。

### Program.cs（Offer→/offer POST→Answer 受領→映像受信→OpenCV表示）
```csharp
using OpenCvSharp;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using System.Net.Http;
using System.Text;

static Mat I420ToBgrMat(byte[] i420, int width, int height)
{
    // I420 は (H * 3/2, W) の1chとして扱い、YUV2BGR_I420 で変換
    using var yuv = new Mat(height * 3 / 2, width, MatType.CV_8UC1, i420);
    var bgr = new Mat();
    Cv2.CvtColor(yuv, bgr, ColorConversionCodes.YUV2BGR_I420);
    return bgr;
}

var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = null });

// 受信フレームハンドラ
pc.OnVideoFrameReceived += (timestamp90K, width, height, stride, pixelFormat, buffer) =>
{
    try
    {
        if (pixelFormat != VideoPixelFormatsEnum.I420)
        {
            Console.WriteLine($"Unsupported format: {pixelFormat}");
            return;
        }
        // buffer は I420 プレーンの連結データ想定
        using var frame = I420ToBgrMat(buffer, (int)width, (int)height);
        Cv2.ImShow("WebRTC-Client", frame);
        Cv2.WaitKey(1);
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.Message);
    }
};

pc.OnConnectionStateChange += (state) => Console.WriteLine($"PC state: {state}");

// Offer 生成
var offer = pc.createOffer(null);
await pc.setLocalDescription(offer);

// サーバへ POST
var http = new HttpClient();
var sdp = new StringContent(offer.sdp, Encoding.UTF8, "application/sdp");
var answerStr = await (await http.PostAsync("http://127.0.0.1:8080/offer", sdp)).Content.ReadAsStringAsync();

await pc.setRemoteDescription(new RTCSessionDescriptionInit
{
    type = RTCSdpType.answer,
    sdp = answerStr
});

Console.WriteLine("Receiving... Press Ctrl+C to exit.");
await Task.Delay(Timeout.InfiniteTimeSpan);
```

> **メモ**
> - `OnVideoFrameReceived` は I420 前提。OpenCV で BGR に変換して表示。
> - 表示遅延/チラつきが気になる時は `Mat` の再利用や `Cv2.WaitKey(1)` のチューニングを検討。

---

## 5) ビルド & 実行

```bash
# ルートで
mkdir -p webrtc-local && cd webrtc-local

# Server
mkdir server && cd server
dotnet new console -n server -o .
# 上の server.csproj と Program.cs で差し替え
dotnet restore
# 動画ファイルパスを環境変数で指定（省略時 sample.mp4）
# Windows (PowerShell)
$Env:WEBRTC_FILE = "C:\\video\\sample.mp4"
# Linux/macOS (bash)
# export WEBRTC_FILE=/home/user/video/sample.mp4

dotnet run
```

別ターミナルで:

```bash
# Client
cd ../
mkdir client && cd client
dotnet new console -n client -o .
# 上の client.csproj と Program.cs で差し替え

# Windows で実行例
dotnet restore
# 実行
dotnet run
```

---

## 6) 動かなかったときのチェックリスト
- `ffmpeg` が PATH にあるか（`ffmpeg -version`）
- サーバ側コンソールに `PC state: connected` が出るか
- クライアントで `Unsupported format` が出ていないか（I420を期待）
- 画面が真っ黒: 入力動画の解像度/フレームレートが大きすぎる → 720p/30fpsへ、または `FFmpegVideoSource` のソース設定を見直し
- Windows の OpenCvSharp ランタイム DLL が見つからない → `OpenCvSharp4.runtime.win` を参照、`runtimes/win-x64/native` に DLL が展開されているか

---

## 7) クロスプラットフォーム対応のポイント
- **OpenCvSharp ランタイム**: OSに合わせて `runtime.*` を切り替え（`RuntimeIdentifiers` で複数指定して `dotnet publish -r`）
- **発行**:
  - Windows: `dotnet publish -c Release -r win-x64 --self-contained false`
  - Linux:   `dotnet publish -c Release -r linux-x64 --self-contained false`
  - macOS:   `dotnet publish -c Release -r osx-x64 --self-contained false`
- **権限**: Linux/macOS で `ffmpeg` 実行権限、実行パス

---

## 8) 拡張アイデア
- **音声(Opus)** 追加（`FFmpegAudioSource` → `pc.SendAudio`）
- **解像度/ビットレート** をコマンドライン引数で可変化
- **複数クライアント**（接続ごとに `RTCPeerConnection`／ファンアウト）
- **GStreamer版サーバ**（webrtcbin）や **Janus** へ差し替えて比較学習
- **ファイル→ループ再生**、**シーク**、**一時停止** の制御

---

## 9) 既知の注意点
- 生I420→BGR 変換コストがボトルネックになり得る（最適化するならメモリ再利用・並列化）
- 高FPS・低遅延は CPU/GPU 負荷次第で限界あり（最初は 720p/30fps を上限に）
- SIPSorcery の API 変更に追従が必要な場合あり（NuGet 更新時はリリースノート要確認）

---

これで Windows を主環境にしつつ、Linux/macOS へも持っていけます。まずはそのままコピペで動作確認してください。問題が出たら、ログと現象を教えてください。

