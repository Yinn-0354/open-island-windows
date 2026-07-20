using Windows.Media.Control;
using Windows.Storage.Streams;

namespace OpenIsland.App.Services;

/// <summary>
/// 当前正在播放的媒体信息快照。字段全给默认值（string.Empty / null），避免调用方
/// 到处写可空判断压不住的警告。
/// </summary>
public sealed class NowPlayingInfo
{
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;

    /// <summary>封面缩略图原始字节（一般是 ~150px JPEG）；没有封面时为 null。</summary>
    public byte[]? ThumbnailBytes { get; init; }

    public bool IsPlaying { get; init; }
}

/// <summary>
/// 读 Windows SMTC（System Media Transport Controls）的当前播放信息 —— 任何支持 SMTC 上报
/// 的播放器（网易云音乐、Spotify、浏览器等）都能读到，不绑定具体某个播放器的 AUMID。
///
/// 事件驱动，不轮询：订阅 SessionManager 的 SessionsChanged（谁成了"当前活动会话"变了）
/// 和当前会话的 MediaPropertiesChanged / PlaybackInfoChanged（同一会话内曲目/播放状态变了）。
/// 每次变化都重新 TryGetMediaPropertiesAsync() 取一份全量快照，逻辑简单也不会有增量状态
/// 不一致的问题。
///
/// WinRT 回调可能落在非 UI 线程上；这里直接原样 Invoke NowPlayingChanged，不做线程切换——
/// 跟 UpdateService.UpdateAvailable 的既有约定一致，切回 UI 线程是调用方（ViewModel）的责任。
/// </summary>
public sealed class NowPlayingService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;

    /// <summary>当前播放信息变化时触发；没有任何活动会话时传 null（什么都没在播、什么都没连接）。</summary>
    public event EventHandler<NowPlayingInfo?>? NowPlayingChanged;

    /// <summary>订阅 SMTC 会话变化，开始工作。SMTC 服务不可用（部分机器/精简系统）时静默失败，
    /// 表现为"一直没有正在播放"而不是让 App 崩。幂等：重复调用直接返回，不会重复订阅 SessionsChanged。
    /// （媒体按钮图标也要看播放状态，所以 NowPlayingService 跟波浪开关解耦了 —— 开关重新打开时
    /// VM 会再 Start 一次，这里必须幂等避免重复订阅导致回调被调多次。）</summary>
    public async void Start()
    {
        if (_manager != null) return;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
            AttachToCurrentSession();
        }
        catch
        {
            // 没有 SMTC 服务 / 系统不支持：什么都不做，NowPlayingChanged 永远不会 raise，
            // 相当于"没有正在播放"。
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => AttachToCurrentSession();

    /// <summary>切到（可能变了的）当前活动会话，重新挂订阅并推一次最新快照。</summary>
    private void AttachToCurrentSession()
    {
        try
        {
            DetachCurrentSession();

            _currentSession = _manager?.GetCurrentSession();
            if (_currentSession == null)
            {
                NowPlayingChanged?.Invoke(this, null);
                return;
            }

            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _ = RefreshAsync();
        }
        catch
        {
            NowPlayingChanged?.Invoke(this, null);
        }
    }

    private void DetachCurrentSession()
    {
        if (_currentSession == null) return;
        try
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        catch { /* 会话可能已失效，忽略 */ }
        _currentSession = null;
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => _ = RefreshAsync();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => _ = RefreshAsync();

    /// <summary>取当前会话的曲目/播放状态/封面，组装成一份快照并 raise 出去。</summary>
    private async Task RefreshAsync()
    {
        var session = _currentSession;
        if (session == null)
        {
            NowPlayingChanged?.Invoke(this, null);
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var playback = session.GetPlaybackInfo();
            bool isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            byte[]? thumbnail = props?.Thumbnail != null ? await ReadThumbnailAsync(props.Thumbnail) : null;

            var info = new NowPlayingInfo
            {
                Title = props?.Title ?? string.Empty,
                Artist = props?.Artist ?? string.Empty,
                ThumbnailBytes = thumbnail,
                IsPlaying = isPlaying,
            };
            NowPlayingChanged?.Invoke(this, info);
        }
        catch
        {
            NowPlayingChanged?.Invoke(this, null);
        }
    }

    /// <summary>把封面的 RandomAccessStreamReference 读成 byte[]：OpenReadAsync 拿流，
    /// IBuffer.Length 读出大小，LoadAsync 一次读满，ReadBytes 转成托管字节数组。</summary>
    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            using var reader = new DataReader(stream);
            uint size = (uint)stream.Size;
            if (size == 0) return null;

            uint loaded = await reader.LoadAsync(size);
            var bytes = new byte[loaded];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        try
        {
            DetachCurrentSession();
            if (_manager != null)
                _manager.SessionsChanged -= OnSessionsChanged;
        }
        catch { /* 释放阶段忽略异常 */ }
        _manager = null;
    }
}
