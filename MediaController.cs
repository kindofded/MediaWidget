using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace MediaWidget;

/// <summary>
/// Tracks the system's current media session (the one shown in the Windows media flyout).
/// Purely event-driven; all state is updated and events are raised on the UI thread.
/// </summary>
internal sealed class MediaController : IDisposable
{
    private readonly TypedEventHandler<Session, MediaPropertiesChangedEventArgs> _onMediaChanged;
    private readonly TypedEventHandler<Session, PlaybackInfoChangedEventArgs> _onPlaybackChanged;
    private readonly TypedEventHandler<Session, TimelinePropertiesChangedEventArgs> _onTimelineChanged;

    private SynchronizationContext? _ui;
    private SessionManager? _manager;
    private Session? _session;
    private int _mediaVersion;
    // Title/artist as of the last MediaChanged event. Compared against this (not the Title property, which an
    // update superseded mid-load may already have set) so a song change is never reported as "unchanged".
    private string _announcedText = "\0";
    private TimeSpan _startTime;
    private TimeSpan _position;
    private DateTimeOffset _positionStamp;
    private double _rate = 1;

    public MediaController()
    {
        _onMediaChanged = (_, _) => Post(() => _ = RefreshMediaAsync());
        _onPlaybackChanged = (_, _) => Post(RefreshPlayback);
        _onTimelineChanged = (_, _) => Post(RefreshTimeline);
    }

    /// <summary>Raised when title/artist/art change. The argument is true when the text changed.</summary>
    public event Action<bool>? MediaChanged;
    public event Action? PlaybackChanged;

    public int ArtSize { get; set; } = 64;

    public bool HasSession => _session is not null;

    /// <summary>App User Model ID of the playing app, e.g. "Spotify.exe" or "MSEdge".</summary>
    public string AppId { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public Bitmap? Art { get; private set; }

    /// <summary>Most prominent colour of the full-size cover, or null for greyscale covers.</summary>
    public Color? AlbumColor { get; private set; }

    public bool IsPlaying { get; private set; }
    public bool? Shuffle { get; private set; }
    public MediaPlaybackAutoRepeatMode? Repeat { get; private set; }
    public bool CanPrevious { get; private set; }
    public bool CanNext { get; private set; }
    public bool CanPlayPause { get; private set; }
    public bool CanShuffle { get; private set; }
    public bool CanRepeat { get; private set; }
    public bool CanSeek { get; private set; }
    public TimeSpan Duration { get; private set; }

    public TimeSpan CurrentPosition
    {
        get
        {
            var position = _position;
            if (IsPlaying)
                position += (DateTimeOffset.Now - _positionStamp) * _rate;
            if (position < TimeSpan.Zero)
                return TimeSpan.Zero;
            return Duration > TimeSpan.Zero && position > Duration ? Duration : position;
        }
    }

    public async Task InitAsync()
    {
        _ui = SynchronizationContext.Current;
        _manager = await SessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => Post(AttachCurrentSession);
        AttachCurrentSession();
    }

    public void TogglePlayPause() => Send(s => s.TryTogglePlayPauseAsync());
    public void Next() => Send(s => s.TrySkipNextAsync());
    public void Previous() => Send(s => s.TrySkipPreviousAsync());
    public void ToggleShuffle() => Send(s => s.TryChangeShuffleActiveAsync(!(Shuffle ?? false)));

    public void CycleRepeat()
    {
        var next = Repeat switch
        {
            MediaPlaybackAutoRepeatMode.List => MediaPlaybackAutoRepeatMode.Track,
            MediaPlaybackAutoRepeatMode.Track => MediaPlaybackAutoRepeatMode.None,
            _ => MediaPlaybackAutoRepeatMode.List,
        };
        Send(s => s.TryChangeAutoRepeatModeAsync(next));
    }

    public void SeekTo(TimeSpan position)
    {
        if (Duration > TimeSpan.Zero)
            Seek(position / Duration);
    }

    public void Seek(double fraction)
    {
        if (!CanSeek || Duration <= TimeSpan.Zero)
            return;
        var offset = Duration * Math.Clamp(fraction, 0, 1);
        var target = _startTime + offset;
        Send(s => s.TryChangePlaybackPositionAsync(target.Ticks));

        // Players confirm seeks with a delay; move now so the bar doesn't snap back in the meantime.
        _position = offset;
        _positionStamp = DateTimeOffset.Now;
    }

    public void Dispose()
    {
        DetachSession();
        Art?.Dispose();
        Art = null;
    }

    private void Post(Action action)
    {
        if (_ui is null)
            action();
        else
            _ui.Post(_ => action(), null);
    }

    private async void Send(Func<Session, IAsyncOperation<bool>> command)
    {
        if (_session is not { } session)
            return;
        try { await command(session); }
        catch { /* session went away mid-call */ }
    }

    private void AttachCurrentSession()
    {
        Session? next = null;
        try { next = _manager?.GetCurrentSession(); }
        catch { }

        DetachSession();
        _session = next;
        try { AppId = next?.SourceAppUserModelId ?? ""; }
        catch { AppId = ""; }
        if (next is not null)
        {
            next.MediaPropertiesChanged += _onMediaChanged;
            next.PlaybackInfoChanged += _onPlaybackChanged;
            next.TimelinePropertiesChanged += _onTimelineChanged;
        }

        RefreshTimeline();
        RefreshPlayback();
        _ = RefreshMediaAsync();
    }

    private void DetachSession()
    {
        if (_session is null)
            return;
        try
        {
            _session.MediaPropertiesChanged -= _onMediaChanged;
            _session.PlaybackInfoChanged -= _onPlaybackChanged;
            _session.TimelinePropertiesChanged -= _onTimelineChanged;
        }
        catch { }
        _session = null;
    }

    private void RefreshPlayback()
    {
        bool wasPlaying = IsPlaying;
        var position = CurrentPosition;

        GlobalSystemMediaTransportControlsSessionPlaybackInfo? info = null;
        try { info = _session?.GetPlaybackInfo(); }
        catch { }

        var controls = info?.Controls;
        IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        Shuffle = info?.IsShuffleActive;
        Repeat = info?.AutoRepeatMode;
        _rate = info?.PlaybackRate is > 0 and var rate ? rate : 1;
        CanPrevious = controls?.IsPreviousEnabled ?? false;
        CanNext = controls?.IsNextEnabled ?? false;
        CanPlayPause = controls is not null && (controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled);
        CanShuffle = controls?.IsShuffleEnabled ?? false;
        CanRepeat = controls?.IsRepeatEnabled ?? false;
        CanSeek = controls?.IsPlaybackPositionEnabled ?? false;

        // Some players don't push a timeline update on pause/resume; re-anchor so interpolation stays correct.
        if (wasPlaying != IsPlaying)
        {
            _position = position;
            _positionStamp = DateTimeOffset.Now;
        }

        PlaybackChanged?.Invoke();
    }

    private void RefreshTimeline()
    {
        GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline = null;
        try { timeline = _session?.GetTimelineProperties(); }
        catch { }

        if (timeline is null)
        {
            _startTime = _position = Duration = TimeSpan.Zero;
        }
        else
        {
            _startTime = timeline.StartTime;
            Duration = timeline.EndTime - timeline.StartTime;
            _position = timeline.Position - timeline.StartTime;
        }
        _positionStamp = timeline is null || timeline.LastUpdatedTime.Year < 2000
            ? DateTimeOffset.Now
            : timeline.LastUpdatedTime;

        PlaybackChanged?.Invoke();
    }

    private async Task RefreshMediaAsync()
    {
        int version = ++_mediaVersion;
        var session = _session;

        GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
        if (session is not null)
        {
            try { props = await session.TryGetMediaPropertiesAsync(); }
            catch { }
        }
        if (version != _mediaVersion)
            return;

        string title = props?.Title ?? "";
        string artist = string.IsNullOrEmpty(props?.Artist) ? props?.AlbumArtist ?? "" : props.Artist;
        Title = title;
        Artist = artist;

        var (art, color) = props?.Thumbnail is { } thumbnail ? await LoadArtAsync(thumbnail, ArtSize) : (null, null);
        if (version != _mediaVersion)
        {
            art?.Dispose();
            return;
        }

        string text = title + "\n" + artist;
        bool textChanged = text != _announcedText;
        _announcedText = text;

        // Players often send the text first and the thumbnail in a second update; keep the old art until then.
        if (art is not null || textChanged)
        {
            Art?.Dispose();
            Art = art;
            AlbumColor = color;
        }

        MediaChanged?.Invoke(textChanged);
    }

    private static async Task<(Bitmap? Art, Color? Color)> LoadArtAsync(IRandomAccessStreamReference reference, int size)
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var stream = await reference.OpenReadAsync())
            using (var managed = stream.AsStreamForRead())
                await managed.CopyToAsync(buffer);
            buffer.Position = 0;

            using var source = Image.FromStream(buffer, false, false);
            int side = Math.Min(source.Width, source.Height);
            var crop = new Rectangle((source.Width - side) / 2, (source.Height - side) / 2, side, side);

            Color? color = null;
            try
            {
                if (source is Bitmap full)
                {
                    color = ColorMath.Dominant(full, crop);
                }
                else
                {
                    using var copy = new Bitmap(source);
                    color = ColorMath.Dominant(copy, crop);
                }
            }
            catch (ArgumentException)
            {
            }

            using var scaled = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, size, size), crop, GraphicsUnit.Pixel);
            }

            var rounded = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(rounded))
            using (var brush = new TextureBrush(scaled))
            using (var path = Drawing.RoundedRect(new Rectangle(0, 0, size, size), Math.Max(2, size / 8)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillPath(brush, path);
            }
            return (rounded, color);
        }
        catch
        {
            return (null, null);
        }
    }
}
