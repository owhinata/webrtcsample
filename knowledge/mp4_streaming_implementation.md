# csharp/Server を MP4 配信対応に変更する方法

## 概要

`csharp/Server` を、MP4ファイルがあればそれを配信し、なければテストパターンを配信するように変更します。`examples/WebRTCMp4Source` のサンプルコードを参考に実装します。

---

## 変更内容

### 1. 定数の追加

Program.cs の定数部分に MP4 ファイルパスを追加:

```csharp
class Program
{
    // Example: @"C:\\ffmpeg-7.1.1-full_build-shared\\bin";
    private const string ffmpegLibFullPath = null;

    // 追加: MP4ファイルのパス
    private const string MP4_PATH = @"media/video.mp4";

    private const int TEST_PATTERN_FRAMES_PER_SECOND = 30;

    private static Microsoft.Extensions.Logging.ILogger _logger = NullLogger.Instance;
```

### 2. ビデオソース作成ロジックの変更

`CreateVideoSource()` メソッドを、ファイルの有無で分岐するように変更:

**変更前:**
```csharp
private static VideoTestPatternSource CreateVideoSource()
{
    var videoSource = new VideoTestPatternSource(new FFmpegVideoEncoder());
    videoSource.SetFrameRate(TEST_PATTERN_FRAMES_PER_SECOND);
    videoSource.OnVideoSourceRawSample += MeasureTestPatternSourceFrameRate;
    return videoSource;
}
```

**変更後:**
```csharp
private static IVideoSource CreateVideoSource()
{
    if (File.Exists(MP4_PATH))
    {
        _logger.LogInformation("MP4 file found at {Path}, using file source.", MP4_PATH);
        var fileSource = new FFmpegFileSource(MP4_PATH, true, new AudioEncoder());
        fileSource.RestrictFormats(x => x.Codec == VideoCodecsEnum.VP8);
        return fileSource;
    }
    else
    {
        _logger.LogInformation("MP4 file not found, using test pattern source.");
        var testPatternSource = new VideoTestPatternSource(new FFmpegVideoEncoder());
        testPatternSource.SetFrameRate(TEST_PATTERN_FRAMES_PER_SECOND);
        testPatternSource.OnVideoSourceRawSample += MeasureTestPatternSourceFrameRate;
        return testPatternSource;
    }
}
```

### 3. オーディオソース作成ロジックの変更

MP4ファイル使用時はオーディオもファイルから取得するため、`CreateAudioSource()` も変更:

**変更前:**
```csharp
private static AudioExtrasSource CreateAudioSource()
{
    return new AudioExtrasSource(
        new AudioEncoder(includeOpus: false),
        new AudioSourceOptions { AudioSource = AudioSourcesEnum.Music }
    );
}
```

**変更後:**
```csharp
private static IAudioSource? CreateAudioSource()
{
    if (File.Exists(MP4_PATH))
    {
        // MP4ファイル使用時はオーディオもファイルから取得するため null
        return null;
    }
    else
    {
        return new AudioExtrasSource(
            new AudioEncoder(includeOpus: false),
            new AudioSourceOptions { AudioSource = AudioSourcesEnum.Music }
        );
    }
}
```

### 4. `CreatePeerConnectionAsync()` の変更

ビデオソースとオーディオソースの型を変更し、ファイルソースの場合の処理を追加:

**変更前:**
```csharp
private static Task<RTCPeerConnection> CreatePeerConnectionAsync()
{
    var peerConnection = new RTCPeerConnection(null);

    InitialiseMediaFramework();
    var videoSource = CreateVideoSource();
    var audioSource = CreateAudioSource();

    AttachVideoTrack(peerConnection, videoSource);
    AttachAudioTrack(peerConnection, audioSource);
    RegisterPeerConnectionEvents(peerConnection, audioSource, videoSource);

    return Task.FromResult(peerConnection);
}
```

**変更後:**
```csharp
private static Task<RTCPeerConnection> CreatePeerConnectionAsync()
{
    var peerConnection = new RTCPeerConnection(null);

    InitialiseMediaFramework();
    var videoSource = CreateVideoSource();
    var audioSource = CreateAudioSource();

    // ファイルソースの場合、videoSourceがオーディオも提供する
    if (videoSource is FFmpegFileSource fileSource)
    {
        AttachFileSourceTracks(peerConnection, fileSource);
        RegisterFileSourceEvents(peerConnection, fileSource);
    }
    else if (videoSource is VideoTestPatternSource testPatternSource && audioSource != null)
    {
        AttachVideoTrack(peerConnection, testPatternSource);
        AttachAudioTrack(peerConnection, audioSource);
        RegisterPeerConnectionEvents(peerConnection, audioSource, testPatternSource);
    }

    return Task.FromResult(peerConnection);
}
```

### 5. ファイルソース用のメソッドを追加

```csharp
private static void AttachFileSourceTracks(
    RTCPeerConnection peerConnection,
    FFmpegFileSource fileSource
)
{
    MediaStreamTrack videoTrack = new(
        fileSource.GetVideoSourceFormats(),
        MediaStreamStatusEnum.SendOnly
    );
    peerConnection.addTrack(videoTrack);

    MediaStreamTrack audioTrack = new(
        fileSource.GetAudioSourceFormats(),
        MediaStreamStatusEnum.SendOnly
    );
    peerConnection.addTrack(audioTrack);

    fileSource.OnVideoSourceEncodedSample += peerConnection.SendVideo;
    fileSource.OnAudioSourceEncodedSample += peerConnection.SendAudio;

    peerConnection.OnVideoFormatsNegotiated += formats =>
        fileSource.SetVideoSourceFormat(formats.First());
    peerConnection.OnAudioFormatsNegotiated += formats =>
        fileSource.SetAudioSourceFormat(formats.First());
}

private static void RegisterFileSourceEvents(
    RTCPeerConnection peerConnection,
    FFmpegFileSource fileSource
)
{
    peerConnection.onconnectionstatechange += async state =>
    {
        _logger.LogDebug("Peer connection state change to {State}.", state);

        switch (state)
        {
            case RTCPeerConnectionState.failed:
                peerConnection.Close("ice disconnection");
                return;

            case RTCPeerConnectionState.closed:
            case RTCPeerConnectionState.disconnected:
                await fileSource.CloseVideo().ConfigureAwait(false);
                await fileSource.CloseAudio().ConfigureAwait(false);
                return;

            case RTCPeerConnectionState.connected:
                await fileSource.StartVideo().ConfigureAwait(false);
                await fileSource.StartAudio().ConfigureAwait(false);
                return;
        }
    };

    peerConnection.oniceconnectionstatechange += state =>
        _logger.LogDebug("ICE connection state change to {State}.", state);

    peerConnection.onsignalingstatechange += () =>
    {
        switch (peerConnection.signalingState)
        {
            case RTCSignalingState.have_local_offer:
                _logger.LogDebug(
                    "Local SDP set, type {Type}.",
                    peerConnection.localDescription.type
                );
                _logger.LogDebug(peerConnection.localDescription.sdp.ToString());
                break;

            case RTCSignalingState.have_remote_offer:
                _logger.LogDebug(
                    "Remote SDP set, type {Type}.",
                    peerConnection.remoteDescription.type
                );
                _logger.LogDebug(peerConnection.remoteDescription.sdp.ToString());
                break;
        }
    };
}
```

### 6. 既存メソッドの型変更

`AttachVideoTrack` と `RegisterPeerConnectionEvents` の引数型を変更:

**変更前:**
```csharp
private static void AttachVideoTrack(
    RTCPeerConnection peerConnection,
    VideoTestPatternSource videoSource
)
```

**変更後:**
```csharp
private static void AttachVideoTrack(
    RTCPeerConnection peerConnection,
    IVideoSource videoSource
)
```

**変更前:**
```csharp
private static void RegisterPeerConnectionEvents(
    RTCPeerConnection peerConnection,
    AudioExtrasSource audioSource,
    VideoTestPatternSource videoSource
)
```

**変更後:**
```csharp
private static void RegisterPeerConnectionEvents(
    RTCPeerConnection peerConnection,
    IAudioSource audioSource,
    IVideoSource videoSource
)
```

### 7. メディアファイルの配置

`csharp/Server` ディレクトリに `media` フォルダを作成し、MP4ファイルを配置:

```
csharp/Server/
├── Program.cs
├── Server.csproj
└── media/
    └── video.mp4
```

### 8. csproj ファイルの変更

メディアファイルを出力ディレクトリにコピーする設定を追加:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <!-- 既存の設定 -->

  <ItemGroup>
    <Content Include="media\**\*">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>

</Project>
```

---

## 動作確認

### 1. MP4ファイルがある場合

```bash
# media/video.mp4 を配置
mkdir csharp/Server/media
cp /path/to/your/video.mp4 csharp/Server/media/video.mp4

# サーバー起動
dotnet run --project csharp/Server
```

ログに以下が表示されます:
```
MP4 file found at media/video.mp4, using file source.
```

### 2. MP4ファイルがない場合

```bash
# media ディレクトリを削除またはファイルを削除
rm -rf csharp/Server/media

# サーバー起動
dotnet run --project csharp/Server
```

ログに以下が表示されます:
```
MP4 file not found, using test pattern source.
```

---

## まとめ

この変更により、`csharp/Server` は以下のように動作します:

1. **MP4ファイルがある場合**: `FFmpegFileSource` を使用してMP4をVP8に変換して配信
2. **MP4ファイルがない場合**: 従来通り `VideoTestPatternSource` でテストパターンを配信

主な変更点:
- ビデオソース/オーディオソースを抽象型(`IVideoSource`/`IAudioSource`)に変更
- ファイル存在チェックで分岐処理を追加
- ファイルソース専用のトラック接続・イベント登録メソッドを追加
