using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Media;

namespace MediaWidget;

internal sealed class WidgetForm : Form
{
    private enum Btn { None, Shuffle, Previous, PlayPause, Next, Repeat, Volume }

    private const int AppBarMessage = Native.WM_APP + 1;
    private const TextFormatFlags TextFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
        TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.PreserveGraphicsClipping;
    private const TextFormatFlags GlyphFlags = TextFlags | TextFormatFlags.HorizontalCenter;

    private readonly Settings _settings = Settings.Load();
    private readonly MediaController _media = new();
    private readonly System.Windows.Forms.Timer _progressTimer = new();
    private readonly System.Windows.Forms.Timer _marqueeTimer = new();
    private readonly System.Windows.Forms.Timer _anchorTimer = new() { Interval = 60_000 };
    private readonly System.Windows.Forms.Timer _topmostTimer = new() { Interval = 100 };
    private readonly NotifyIcon _tray = new();
    private readonly VolumeFlyout _volumeFlyout;
    private readonly List<(Btn Id, Rectangle Bounds)> _buttons = [];
    private readonly StringBuilder _classBuffer = new(256);
    private readonly string _iconFamily = ResolveIconFamily();

    private Native.WinEventDelegate? _winEventProc;
    private IntPtr _winEventHook;
    private uint _taskbarCreatedMessage;
    private bool _fullscreen;
    private TaskbarAnchors _anchors;
    private TaskbarEdge _edge;
    private ToolTip? _toolTip;

    private enum LyricsState { Off, Loading, Ready, NotFound, Unavailable }
    // Show each line a touch early; it reads as better synced than switching exactly on the timestamp.
    private static readonly TimeSpan LyricLead = TimeSpan.FromMilliseconds(150);
    private readonly System.Windows.Forms.Timer _lyricsTimer = new();
    private LyricsState _lyricsState;
    private Lyrics? _lyrics;
    private int _lyricIndex = -1;
    private CancellationTokenSource? _lyricsCancellation;
    private LyricsCard? _lyricsCard;

    // Long lyric lines scroll sideways so the whole line is readable before the next one starts.
    private readonly System.Windows.Forms.Timer _lyricScrollTimer = new() { Interval = 33 };
    private int _lyricOverflow;
    private float _lyricOffset;
    private double _lyricHoldMs, _lyricTravelMs, _lyricElapsedMs;
    private DateTime _lyricScrollResumed;

    private Theme _baseTheme = Theme.Load();
    private Theme _theme;
    private Theme _themeFrom, _themeTo;
    private DateTime _themeBlendStart;
    private readonly System.Windows.Forms.Timer _themeTimer = new() { Interval = 16 };
    private Color? _albumColor;
    private Bitmap? _colorSourceArt;
    private float _scale = 1;
    private Rectangle _artRect, _titleRect, _artistRect, _progressRect, _seekRect, _progressArea;
    private bool _seekHover, _seeking;
    private int _seekX;
    private Font _titleFont = null!, _artistFont = null!, _iconFont = null!;

    private Btn _hover, _pressed;
    private bool _mouseDown, _dragging;
    private Point _dragStartCursor, _dragStartLocation;

    private int _titleWidth, _marqueeOffset;
    private bool _marqueeScrolling;

    private float _volume = 1;
    private bool _muted;

    public WidgetForm()
    {
        Text = "Media Widget";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _theme = _themeFrom = _themeTo = _baseTheme;
        BackColor = _theme.Background;
        _themeTimer.Tick += OnThemeBlendTick;
        _lyricsTimer.Tick += (_, _) => UpdateLyricLine();
        _lyricScrollTimer.Tick += OnLyricScrollTick;

        _tray.Text = "Media Widget";
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                ShowMenu();
        };
        UpdateTrayIcon();
        _tray.Visible = true;

        _volumeFlyout = new VolumeFlyout(_theme, _iconFamily);
        _volumeFlyout.VolumeChanged += SetVolumeIcon;

        _progressTimer.Tick += (_, _) => InvalidateProgress();
        _marqueeTimer.Tick += OnMarqueeTick;
        // The weather widget resizes as its text changes, so re-check where it ends now and then.
        _anchorTimer.Tick += (_, _) => RefreshTaskbarAnchors();
        _topmostTimer.Tick += (_, _) =>
        {
            if (!IsTaskbar(Native.GetForegroundWindow()))
                _topmostTimer.Stop();
            else if (IsCoveredByTaskbar())
                AssertTopmost();
        };
        _media.MediaChanged += OnMediaChanged;
        _media.PlaybackChanged += OnPlaybackChanged;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    private string DisplayTitle => !_media.HasSession ? "Nothing playing"
        : _media.Title.Length > 0 ? _media.Title : "Unknown title";

    private int S(float value) => (int)MathF.Round(value * _scale);

    // ---------------------------------------------------------------- lifecycle

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        int corner = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // Registering as an (zero-size) app bar gets us shell notifications for fullscreen apps and taskbar moves.
        RegisterAppBar();
        _taskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");

        // The taskbar pushes itself above us when it's clicked; re-assert topmost when the foreground changes.
        _winEventProc = OnForegroundChanged;
        _winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

        Theme.AllowDarkMenus(Handle);
        AttachToTaskbar();
        _edge = TaskbarInfo.Edge;
        BuildLayout();
        RefreshTaskbarAnchors();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RefreshVolume();
        try
        {
            await _media.InitAsync();
        }
        catch
        {
            // Media session API unavailable; the widget stays in its "Nothing playing" state.
        }
        UpdateVisibility();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_winEventHook != IntPtr.Zero)
            Native.UnhookWinEvent(_winEventHook);
        UnregisterAppBar();
        base.OnHandleDestroyed(e);
    }

    /// <summary>True once the user quits (or Windows shuts down), as opposed to the window being destroyed by Explorer.</summary>
    public bool QuitRequested { get; private set; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        QuitRequested = true;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _progressTimer.Dispose();
            _marqueeTimer.Dispose();
            _anchorTimer.Dispose();
            _topmostTimer.Dispose();
            _themeTimer.Dispose();
            _lyricsTimer.Dispose();
            _lyricScrollTimer.Dispose();
            _lyricsCancellation?.Cancel();
            _lyricsCard?.Dispose();
            _tray.Visible = false;
            _tray.Icon?.Dispose();
            _tray.Dispose();
            _toolTip?.Dispose();
            _volumeFlyout.Dispose();
            _media.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        // Showing a ShowInTaskbar=false form makes WinForms re-assign its own hidden owner window.
        if (Visible)
            AttachToTaskbar();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        BuildLayout();
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEACTIVATE:
                m.Result = Native.MA_NOACTIVATE;
                return;

            case AppBarMessage:
                if ((int)m.WParam == Native.ABN_FULLSCREENAPP)
                {
                    _fullscreen = m.LParam != IntPtr.Zero && !IsShellWindow(Native.GetForegroundWindow());
                    UpdateVisibility();
                }
                else if ((int)m.WParam == Native.ABN_POSCHANGED)
                {
                    RefreshTaskbarAnchors();
                }
                return;

            case Native.WM_SETTINGCHANGE:
            case Native.WM_DWMCOLORIZATIONCOLORCHANGED:
                _baseTheme = Theme.Load();
                ApplyTheme(animate: false);
                UpdateTrayIcon();
                RefreshTaskbarAnchors();
                Invalidate();
                break;

            case Native.WM_DISPLAYCHANGE:
                RefreshTaskbarAnchors();
                break;

            default:
                if (_taskbarCreatedMessage != 0 && m.Msg == (int)_taskbarCreatedMessage)
                {
                    UnregisterAppBar();
                    RegisterAppBar();
                    AttachToTaskbar();
                    RefreshTaskbarAnchors();
                    AssertTopmost();
                }
                break;
        }
        base.WndProc(ref m);
    }

    // ---------------------------------------------------------------- layout and position

    private bool IsVertical => _edge is TaskbarEdge.Left or TaskbarEdge.Right;

    private void BuildLayout()
    {
        _scale = DeviceDpi / 96f;
        int pad = S(6), art = S(32), button = S(32), gap = S(2);
        int width, height;

        _buttons.Clear();
        var order = new List<Btn>();
        if (_settings.ShowShuffleRepeat) order.Add(Btn.Shuffle);
        order.AddRange([Btn.Previous, Btn.PlayPause, Btn.Next]);
        if (_settings.ShowShuffleRepeat) order.Add(Btn.Repeat);
        order.Add(Btn.Volume);

        if (IsVertical)
        {
            // Side taskbars are about one icon wide: art, progress bar, then the buttons in a column.
            // The title and artist move into a tooltip.
            width = S(44);
            _artRect = new Rectangle((width - art) / 2, pad, art, art);
            _titleRect = _artistRect = Rectangle.Empty;
            _progressRect = new Rectangle(_artRect.X, _artRect.Bottom + S(6), art, Math.Max(1, S(2)));
            _progressArea = Rectangle.FromLTRB(0, _artRect.Bottom + S(1), width, _artRect.Bottom + S(12));
            _seekRect = _progressArea;

            int y = _progressArea.Bottom + S(2);
            foreach (var id in order)
            {
                _buttons.Add((id, new Rectangle((width - button) / 2, y, button, button)));
                y += button + gap;
            }
            height = y - gap + pad;
        }
        else
        {
            height = S(44);
            _artRect = new Rectangle(pad, (height - art) / 2 - S(1), art, art);
            int textX = _artRect.Right + S(8), textWidth = S(150);
            _titleRect = new Rectangle(textX, _artRect.Top - S(1), textWidth, S(18));
            _artistRect = new Rectangle(textX, _artRect.Top + S(16), textWidth, S(17));

            int x = textX + textWidth + S(6);
            foreach (var id in order)
            {
                _buttons.Add((id, new Rectangle(x, _artRect.Top, button, button)));
                x += button + gap;
            }
            width = x - gap + pad;
            _progressRect = new Rectangle(pad, height - S(4), width - pad * 2, Math.Max(1, S(2)));
            _seekRect = Rectangle.FromLTRB(0, _artRect.Bottom, width, height);
            _progressArea = Rectangle.FromLTRB(0, height - S(11), width, height);
        }

        _titleFont?.Dispose();
        _artistFont?.Dispose();
        _iconFont?.Dispose();
        _titleFont = new Font("Segoe UI Semibold", 13 * _scale, GraphicsUnit.Pixel);
        _artistFont = new Font("Segoe UI", 12 * _scale, GraphicsUnit.Pixel);
        _iconFont = new Font(_iconFamily, 16 * _scale, GraphicsUnit.Pixel);

        _media.ArtSize = art;
        Size = new Size(width, height);
        MeasureTitle();
        UpdateToolTip();
        if (!_dragging)
            Reposition();
        UpdateProgressTimer();
        Invalidate();
    }

    private void UpdateToolTip()
    {
        if (!IsVertical)
        {
            _toolTip?.SetToolTip(this, null);
            return;
        }
        _toolTip ??= new ToolTip { InitialDelay = 400 };
        string text = _media.Artist.Length > 0 ? $"{DisplayTitle}\n{_media.Artist}" : DisplayTitle;
        _toolTip.SetToolTip(this, text);
    }

    private void Reposition()
    {
        if (!IsHandleCreated || _dragging)
            return;

        if (!_settings.SnapToTaskbar)
        {
            var saved = new Rectangle(_settings.X, _settings.Y, Width, Height);
            if (Screen.AllScreens.Any(s => s.Bounds.IntersectsWith(saved)))
            {
                Location = saved.Location;
                return;
            }
        }

        if (TaskbarInfo.Bounds is not { } taskbar)
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
            Location = new Point(area.Left + S(8), area.Bottom - Height - S(8));
            return;
        }

        // Work along the taskbar's long axis so the same logic covers horizontal and vertical taskbars.
        bool vertical = IsVertical;
        int Near(Rectangle r) => vertical ? r.Top : r.Left;
        int Far(Rectangle r) => vertical ? r.Bottom : r.Right;
        Rectangle? Inside(Rectangle? r) => r is { } v && v.IntersectsWith(taskbar) ? v : null;

        int axisStart = Near(taskbar), axisEnd = Far(taskbar), middle = (axisStart + axisEnd) / 2;
        int length = vertical ? Height : Width;
        var widgets = Inside(_anchors.Widgets);
        var start = Inside(_anchors.Start);
        var tray = Inside(_anchors.Tray);

        // With left- or top-aligned icons, Start sits at the beginning, so that end is taken.
        bool startSideTaken = start is { } s && Near(s) - axisStart < (axisEnd - axisStart) / 4;

        int position;
        if (_settings.DockRight || startSideTaken)
        {
            int end = axisEnd;
            if (tray is { } t && Near(t) > middle)
                end = Math.Min(end, Near(t));
            if (widgets is { } w && Near(w) > middle)
                end = Math.Min(end, Near(w));
            position = end - length - S(8);
        }
        else
        {
            int begin = axisStart;
            if (widgets is { } w && Far(w) < middle)
                begin = Math.Max(begin, Far(w));
            position = begin + S(8);
        }

        Location = vertical
            ? new Point(taskbar.Left + (taskbar.Width - Width) / 2, position)
            : new Point(position, taskbar.Top + (taskbar.Height - Height) / 2);
    }

    private async void RefreshTaskbarAnchors()
    {
        if (_settings.SnapToTaskbar)
        {
            _anchors = await Task.Run(TaskbarInfo.FindAnchors);
            _anchorTimer.Start();
        }
        else
        {
            _anchorTimer.Stop();
        }

        var edge = TaskbarInfo.Edge;
        if (edge != _edge)
        {
            _edge = edge;
            BuildLayout();
        }
        else
        {
            Reposition();
        }
    }

    private void AssertTopmost()
    {
        if (Visible)
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    private void UpdateVisibility()
    {
        bool show = !_fullscreen && (_media.HasSession || !_settings.HideWhenIdle);
        if (show != Visible)
        {
            Visible = show;
            if (show)
            {
                Reposition();
                AssertTopmost();
                RestartMarquee();
            }
            else
            {
                _marqueeTimer.Stop();
                _lyricsCard?.Hide();
            }
        }
        UpdateProgressTimer();
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Leave Start, Search and other shell flyouts on top of us.
        if (GetClass(hwnd) is "Windows.UI.Core.CoreWindow" or "Xaml_WindowedPopupClass")
            return;
        AssertTopmost();

        // While it's the active window the taskbar keeps raising itself above us, often just after this
        // event fires, so keep checking until something else is activated.
        if (IsTaskbar(hwnd))
            _topmostTimer.Start();
    }

    /// <summary>
    /// Makes the taskbar our owner window. Windows always keeps owned windows directly above their owner and
    /// raises them together, so the taskbar can no longer cover us (no flicker, and we stay visible when Search
    /// or Start activates the taskbar). If Explorer restarts we lose the owner; TaskbarCreated re-attaches.
    /// </summary>
    private void AttachToTaskbar()
    {
        var taskbar = TaskbarInfo.Handle;
        if (taskbar != IntPtr.Zero && Native.GetWindow(Handle, Native.GW_OWNER) != taskbar)
            Native.SetWindowLongPtr(Handle, Native.GWLP_HWNDPARENT, taskbar);
    }

    private bool IsTaskbar(IntPtr hwnd) => GetClass(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private bool IsCoveredByTaskbar()
    {
        var taskbar = TaskbarInfo.Handle;
        for (var above = Native.GetWindow(Handle, Native.GW_HWNDPREV); above != IntPtr.Zero; above = Native.GetWindow(above, Native.GW_HWNDPREV))
        {
            if (above == taskbar)
                return true;
        }
        return false;
    }

    private void RegisterAppBar()
    {
        var data = new Native.APPBARDATA
        {
            cbSize = Marshal.SizeOf<Native.APPBARDATA>(),
            hWnd = Handle,
            uCallbackMessage = AppBarMessage,
        };
        Native.SHAppBarMessage(Native.ABM_NEW, ref data);
    }

    private void UnregisterAppBar()
    {
        var data = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = Handle };
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref data);
    }

    private bool IsShellWindow(IntPtr hwnd) =>
        GetClass(hwnd) is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    private string GetClass(IntPtr hwnd)
    {
        _classBuffer.Clear();
        Native.GetClassName(hwnd, _classBuffer, _classBuffer.Capacity);
        return _classBuffer.ToString();
    }

    // ---------------------------------------------------------------- media updates

    private void OnMediaChanged(bool textChanged)
    {
        if (!ReferenceEquals(_media.Art, _colorSourceArt))
        {
            _colorSourceArt = _media.Art;
            _albumColor = _media.AlbumColor;
            ApplyTheme(animate: true);
        }
        if (textChanged)
        {
            MeasureTitle();
            RestartMarquee();
            RefreshVolume();
            UpdateToolTip();
            LoadLyrics();
        }
        Invalidate();
        UpdateVisibility();
    }

    private void OnPlaybackChanged()
    {
        Invalidate();
        UpdateVisibility();
        UpdateLyricLine();
    }

    // ---------------------------------------------------------------- lyrics

    private async void LoadLyrics()
    {
        _lyricsCancellation?.Cancel();
        _lyricsCancellation = null;
        _lyrics = null;
        _lyricIndex = -1;
        _lyricsTimer.Stop();
        StartLyricScroll();

        if (!_settings.ShowLyrics || !_media.HasSession || _media.Title.Length == 0)
        {
            _lyricsState = LyricsState.Off;
            RefreshLyrics();
            return;
        }

        var cancellation = _lyricsCancellation = new CancellationTokenSource();
        _lyricsState = LyricsState.Loading;
        RefreshLyrics();

        // Network hiccups (VPN reconnects, a slow response) are common; retry a couple of times before giving up.
        const int attempts = 3;
        string title = _media.Title, artist = _media.Artist;
        Lyrics? found = null;
        var state = LyricsState.Unavailable;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                found = await LyricsService.FindAsync(title, artist, _media.Duration, cancellation.Token);
                state = found is null ? LyricsState.NotFound : LyricsState.Ready;
                break;
            }
            catch (Exception ex)
            {
                if (cancellation.IsCancellationRequested)
                    return;
                LyricsService.LogFailure(title, artist, ex);
                // Only network problems are worth retrying; anything else (bad data) would just fail again.
                if (ex is not (HttpRequestException or TaskCanceledException))
                    break;
                if (attempt == attempts)
                    break;
                try { await Task.Delay(TimeSpan.FromSeconds(3 * attempt), cancellation.Token); }
                catch (TaskCanceledException) { return; }
            }
        }
        if (cancellation.IsCancellationRequested)
            return;
        // Never show lyrics for a song that's no longer playing.
        if (title != _media.Title || artist != _media.Artist)
        {
            LoadLyrics();
            return;
        }

        _lyrics = found;
        _lyricsState = state;
        UpdateLyricLine();
    }

    /// <summary>Picks the line for the current position and schedules a wake-up for exactly the next line.</summary>
    private void UpdateLyricLine()
    {
        _lyricsTimer.Stop();
        if (_lyrics is not { } lyrics)
        {
            RefreshLyrics();
            return;
        }

        var position = _media.CurrentPosition + LyricLead;
        int index = lyrics.IndexAt(position);
        if (index != _lyricIndex)
        {
            _lyricIndex = index;
            StartLyricScroll();
            RefreshLyrics();
        }
        else
        {
            PauseOrResumeLyricScroll();
        }

        if (_media.IsPlaying && index + 1 < lyrics.Lines.Count)
        {
            double wait = (lyrics.Lines[index + 1].Time - position).TotalMilliseconds;
            _lyricsTimer.Interval = (int)Math.Clamp(wait + 10, 15, 60_000);
            _lyricsTimer.Start();
        }
    }

    /// <summary>
    /// If the new line is too wide, plans a scroll that holds the start briefly, then slides to the end so it's
    /// fully shown shortly before the next line. Speed adapts to the time available, within readable limits.
    /// </summary>
    private void StartLyricScroll()
    {
        _lyricScrollTimer.Stop();
        _lyricOffset = 0;
        _lyricElapsedMs = 0;
        _lyricOverflow = 0;

        if (CurrentLyric is not { } text || _artistRect.Width == 0)
            return;
        _lyricOverflow = TextRenderer.MeasureText(text, _artistFont, Size.Empty, TextFlags).Width - _artistRect.Width;
        if (_lyricOverflow <= 0)
            return;

        double available = 5000;
        if (_lyrics is { } lyrics && _lyricIndex + 1 < lyrics.Lines.Count)
            available = (lyrics.Lines[_lyricIndex + 1].Time - (_media.CurrentPosition + LyricLead)).TotalMilliseconds;

        _lyricHoldMs = Math.Clamp(available * 0.15, 250, 800);
        const double endHoldMs = 400;
        double fastest = _lyricOverflow / (160 * _scale) * 1000, slowest = _lyricOverflow / (30 * _scale) * 1000;
        _lyricTravelMs = Math.Clamp(available - _lyricHoldMs - endHoldMs, fastest, slowest);

        PauseOrResumeLyricScroll();
    }

    private void PauseOrResumeLyricScroll()
    {
        bool shouldRun = _lyricOverflow > 0 && _media.IsPlaying && Visible && _lyricElapsedMs < _lyricHoldMs + _lyricTravelMs;
        if (shouldRun && !_lyricScrollTimer.Enabled)
        {
            _lyricScrollResumed = DateTime.UtcNow - TimeSpan.FromMilliseconds(_lyricElapsedMs);
            _lyricScrollTimer.Start();
        }
        else if (!shouldRun && _lyricScrollTimer.Enabled)
        {
            _lyricScrollTimer.Stop();
        }
    }

    private void OnLyricScrollTick(object? sender, EventArgs e)
    {
        _lyricElapsedMs = (DateTime.UtcNow - _lyricScrollResumed).TotalMilliseconds;
        double t = Math.Clamp((_lyricElapsedMs - _lyricHoldMs) / _lyricTravelMs, 0, 1);
        double eased = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
        _lyricOffset = (float)(_lyricOverflow * eased);
        Invalidate(_artistRect);
        if (t >= 1)
            _lyricScrollTimer.Stop();
    }

    // The album art and the lyric/artist line open the lyrics card.
    private bool IsLyricsTarget(Point p) => _settings.ShowLyrics && (_artRect.Contains(p) || _artistRect.Contains(p));

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_lyricsCard is { Visible: true } card)
            card.ShowNear(Bounds, _edge, _scale);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_lyricsCard is { Visible: true } card)
            card.ShowNear(Bounds, _edge, _scale);
    }

    private string? CurrentLyric =>
        _settings.ShowLyrics && _lyrics is { } lyrics && _lyricIndex >= 0
            ? lyrics.Lines[_lyricIndex].Text is { Length: > 0 } text ? text : "♪"
            : null;

    private void RefreshLyrics()
    {
        Invalidate(_artistRect);
        if (_lyricsCard is { Visible: true } card)
        {
            card.SetContent(_lyrics, _lyricIndex, _lyricsState switch
            {
                LyricsState.Loading => "Looking for lyrics…",
                LyricsState.NotFound => "No synced lyrics for this song",
                LyricsState.Unavailable => "Couldn't reach the lyrics service. Click to try again",
                LyricsState.Off => "Nothing playing",
                _ => null,
            });
        }
    }

    private void ToggleLyricsCard()
    {
        if (_lyricsCard is { Visible: true })
        {
            _lyricsCard.Hide();
            return;
        }
        if (_lyricsCard is null)
        {
            _lyricsCard = new LyricsCard(_themeTo) { Owner = this };
            _lyricsCard.SeekRequested += time =>
            {
                _media.SeekTo(time);
                UpdateLyricLine();
            };
            _lyricsCard.MessageClicked += () =>
            {
                if (_lyricsState == LyricsState.Unavailable)
                    LoadLyrics();
            };
        }
        _lyricsCard.ShowNear(Bounds, _edge, _scale);
        if (_lyricsState == LyricsState.Unavailable)
            LoadLyrics();
        else
            RefreshLyrics();
    }

    private void UpdateProgressTimer()
    {
        bool run = Visible && _settings.ShowProgress && _media.IsPlaying && _media.Duration > TimeSpan.Zero;
        if (!run)
        {
            _progressTimer.Stop();
            return;
        }

        // Tick about once per pixel of progress, but never faster than 4 Hz.
        int interval = (int)Math.Clamp(_media.Duration.TotalMilliseconds / Math.Max(1, _progressRect.Width), 250, 1000);
        if (_progressTimer.Interval != interval)
            _progressTimer.Interval = interval;
        _progressTimer.Start();
    }

    private void RefreshVolume()
    {
        using var volume = AudioVolume.For(_media.AppId);
        if (volume.TryRead(out float level, out bool muted))
            SetVolumeIcon(level, muted);
    }

    private void SetVolumeIcon(float level, bool muted)
    {
        if (Math.Abs(level - _volume) > 0.001f || muted != _muted)
        {
            _volume = level;
            _muted = muted;
            InvalidateButton(Btn.Volume);
        }
    }

    // ---------------------------------------------------------------- title marquee

    private void MeasureTitle() =>
        _titleWidth = TextRenderer.MeasureText(DisplayTitle, _titleFont, Size.Empty, TextFlags).Width;

    private void RestartMarquee()
    {
        _marqueeTimer.Stop();
        _marqueeOffset = 0;
        _marqueeScrolling = false;
        if (Visible && _titleRect.Width > 0 && _titleWidth > _titleRect.Width)
        {
            _marqueeTimer.Interval = 2000;
            _marqueeTimer.Start();
        }
        Invalidate(_titleRect);
    }

    private void OnMarqueeTick(object? sender, EventArgs e)
    {
        if (!_marqueeScrolling)
        {
            _marqueeScrolling = true;
            _marqueeTimer.Interval = 33;
        }

        _marqueeOffset += Math.Max(1, S(1));
        if (_marqueeOffset >= _titleWidth + S(32))
        {
            _marqueeOffset = 0;
            _marqueeScrolling = false;
            if (_settings.ScrollOnce)
                _marqueeTimer.Stop();
            else
                _marqueeTimer.Interval = 3000;
        }
        Invalidate(_titleRect);
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var clip = e.ClipRectangle;
        if (_theme.Background == _theme.BackgroundEnd)
        {
            g.Clear(_theme.Background);
        }
        else
        {
            using var gradient = new LinearGradientBrush(ClientRectangle, _theme.Background, _theme.BackgroundEnd,
                IsVertical ? LinearGradientMode.Vertical : LinearGradientMode.Horizontal);
            g.FillRectangle(gradient, ClientRectangle);
        }

        if (clip.IntersectsWith(_artRect))
            DrawArt(g);
        if (clip.IntersectsWith(_titleRect) || clip.IntersectsWith(_artistRect))
            DrawText(g);
        foreach (var (id, bounds) in _buttons)
            if (clip.IntersectsWith(bounds))
                DrawButton(g, id, bounds);
        if (_settings.ShowProgress && clip.IntersectsWith(_progressArea))
            DrawProgress(g);
    }

    private void DrawArt(Graphics g)
    {
        if (_media.Art is { } art)
        {
            if (art.Width == _artRect.Width && art.Height == _artRect.Height)
            {
                g.DrawImageUnscaled(art, _artRect.Location);
            }
            else
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(art, _artRect);
            }
            return;
        }

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Drawing.RoundedRect(_artRect, Math.Max(2, S(4))))
        using (var brush = new SolidBrush(_theme.HoverBackground))
            g.FillPath(brush, path);
        g.SmoothingMode = SmoothingMode.None;
        Drawing.Glyph(g, "\uE8D6", _iconFont, _artRect, _theme.ForegroundDim);
    }

    private void DrawText(Graphics g)
    {
        string title = DisplayTitle;
        var state = g.Save();
        g.SetClip(_titleRect);
        if (!_marqueeScrolling)
        {
            TextRenderer.DrawText(g, title, _titleFont, _titleRect, _theme.Foreground, TextFlags | TextFormatFlags.EndEllipsis);
        }
        else
        {
            var r = new Rectangle(_titleRect.X - _marqueeOffset, _titleRect.Y, _titleWidth, _titleRect.Height);
            TextRenderer.DrawText(g, title, _titleFont, r, _theme.Foreground, TextFlags);
            r.X += _titleWidth + S(32);
            TextRenderer.DrawText(g, title, _titleFont, r, _theme.Foreground, TextFlags);
            DrawEdgeFades(g, _titleRect, left: _marqueeOffset > 0, right: true);
        }
        g.Restore(state);

        // Priority: seek preview time, then the current lyric line (in the accent colour), then the artist.
        string? lyric = SeekActive ? null : CurrentLyric;
        if (lyric is not null && _lyricOverflow > 0 && _lyricOffset > 0)
        {
            var lyricState = g.Save();
            g.SetClip(_artistRect);
            var r = new Rectangle(_artistRect.X - (int)MathF.Round(_lyricOffset), _artistRect.Y,
                _artistRect.Width + _lyricOverflow, _artistRect.Height);
            TextRenderer.DrawText(g, lyric, _artistFont, r, _theme.Accent, TextFlags);
            DrawEdgeFades(g, _artistRect, left: true, right: _lyricOffset < _lyricOverflow - 0.5f);
            g.Restore(lyricState);
            return;
        }

        string subtitle = SeekActive
            ? $"{FormatTime(_media.Duration * SeekFraction(_seekX))} / {FormatTime(_media.Duration)}"
            : lyric ?? _media.Artist;
        TextRenderer.DrawText(g, subtitle, _artistFont, _artistRect, lyric is null ? _theme.ForegroundDim : _theme.Accent,
            TextFlags | TextFormatFlags.EndEllipsis);
    }

    /// <summary>
    /// Fades text out at the clipped edges of <paramref name="area"/> by painting background-coloured gradients
    /// over it (GDI text can't be drawn with transparency). The colour is sampled from the background gradient
    /// at each edge so the fade blends in when album colours are on.
    /// </summary>
    private void DrawEdgeFades(Graphics g, Rectangle area, bool left, bool right)
    {
        int width = Math.Min(S(16), area.Width / 4);
        if (width <= 0)
            return;

        void Fade(int x, bool fadeToRight)
        {
            var solid = BackgroundAt(fadeToRight ? x : x + width);
            var clear = Color.FromArgb(0, solid);
            // The brush is a pixel wider than the fill so GDI+ doesn't wrap a sliver of the far colour at the edge.
            var brushArea = new RectangleF(x - 1, area.Y, width + 2, area.Height);
            using var brush = new LinearGradientBrush(brushArea, fadeToRight ? solid : clear, fadeToRight ? clear : solid, 0f);
            g.FillRectangle(brush, x, area.Y, width, area.Height);
        }

        if (left)
            Fade(area.Left, fadeToRight: true);
        if (right)
            Fade(area.Right - width, fadeToRight: false);
    }

    private Color BackgroundAt(int x)
    {
        if (_theme.Background == _theme.BackgroundEnd || Width <= 0)
            return _theme.Background;
        return ColorMath.Lerp(_theme.Background, _theme.BackgroundEnd, Math.Clamp(x / (float)Width, 0f, 1f));
    }

    private void DrawButton(Graphics g, Btn id, Rectangle bounds)
    {
        bool enabled = IsEnabled(id);
        bool hovered = enabled && id == _hover;

        if (id == Btn.PlayPause)
        {
            int d = S(30);
            var circle = new Rectangle(bounds.X + (bounds.Width - d) / 2, bounds.Y + (bounds.Height - d) / 2, d, d);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(!enabled ? _theme.ForegroundDisabled : hovered ? _theme.PlayHover : _theme.Foreground))
                g.FillEllipse(brush, circle);
            DrawTransportGlyph(g, id, circle, _theme.Background);
            return;
        }

        if (hovered)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Drawing.RoundedRect(Rectangle.Inflate(bounds, -S(1), -S(1)), Math.Max(2, S(4))))
            using (var brush = new SolidBrush(_theme.HoverBackground))
                g.FillPath(brush, path);
            g.SmoothingMode = SmoothingMode.None;
        }

        Color color = !enabled ? _theme.ForegroundDisabled : IsActive(id) ? _theme.Accent : _theme.Foreground;
        if (id is Btn.Previous or Btn.Next)
            DrawTransportGlyph(g, id, bounds, color);
        else
            Drawing.Glyph(g, Glyph(id), _iconFont, bounds, color);
    }

    // Solid shapes; the icon fonts only ship outlined play/pause/skip glyphs.
    private void DrawTransportGlyph(Graphics g, Btn id, Rectangle bounds, Color color)
    {
        float s = 11 * _scale, half = s / 2;
        float cx = bounds.X + bounds.Width / 2f, cy = bounds.Y + bounds.Height / 2f;
        float bar = s * 0.18f;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        switch (id)
        {
            case Btn.PlayPause when _media.IsPlaying:
                float width = s * 0.28f, gap = s * 0.28f;
                g.FillRectangle(brush, cx - gap / 2 - width, cy - half, width, s);
                g.FillRectangle(brush, cx + gap / 2, cy - half, width, s);
                break;
            case Btn.PlayPause:
                g.FillPolygon(brush, new PointF[] { new(cx - s * 0.35f, cy - half), new(cx - s * 0.35f, cy + half), new(cx + s * 0.55f, cy) });
                break;
            case Btn.Previous:
                g.FillRectangle(brush, cx - half, cy - half, bar, s);
                g.FillPolygon(brush, new PointF[] { new(cx + half, cy - half), new(cx + half, cy + half), new(cx - half + bar, cy) });
                break;
            case Btn.Next:
                g.FillRectangle(brush, cx + half - bar, cy - half, bar, s);
                g.FillPolygon(brush, new PointF[] { new(cx - half, cy - half), new(cx - half, cy + half), new(cx + half - bar, cy) });
                break;
        }
        g.SmoothingMode = SmoothingMode.None;
    }

    private void DrawProgress(Graphics g)
    {
        bool active = SeekActive;
        // On hover the bar thickens and centres in the hover strip so the thumb fits inside the window.
        int thick = Math.Max(2, S(4));
        var bar = active
            ? new Rectangle(_progressRect.X, _progressArea.Top + (_progressArea.Height - thick) / 2, _progressRect.Width, thick)
            : _progressRect;

        using (var track = new SolidBrush(_theme.Track))
            g.FillRectangle(track, bar);
        if (_media.Duration <= TimeSpan.Zero)
            return;

        double fraction = _seeking ? SeekFraction(_seekX) : Math.Clamp(_media.CurrentPosition / _media.Duration, 0, 1);
        int filledX = bar.X + (int)Math.Round(bar.Width * fraction);

        if (active && !_seeking && _seekX > filledX)
        {
            using var preview = new SolidBrush(Color.FromArgb(110, _theme.ProgressFill));
            g.FillRectangle(preview, filledX, bar.Y, Math.Min(_seekX, bar.Right) - filledX, bar.Height);
        }

        using var fill = new SolidBrush(_theme.ProgressFill);
        if (filledX > bar.X)
            g.FillRectangle(fill, bar.X, bar.Y, filledX - bar.X, bar.Height);

        if (active)
        {
            int d = S(10);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillEllipse(fill, filledX - d / 2f, bar.Y + bar.Height / 2f - d / 2f, d, d);
            g.SmoothingMode = SmoothingMode.None;
        }
    }

    private bool CanSeek => _settings.ShowProgress && _media.CanSeek && _media.Duration > TimeSpan.Zero;

    private bool SeekActive => (_seekHover || _seeking) && CanSeek;

    private double SeekFraction(int x) => Math.Clamp((x - _progressRect.X) / (double)_progressRect.Width, 0, 1);

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private void InvalidateProgress() =>
        Invalidate(SeekActive ? _progressArea : Rectangle.Inflate(_progressRect, 0, 1));

    private void SetSeekHover(bool hover, int x)
    {
        if (hover == _seekHover && (!hover || x == _seekX))
            return;
        _seekHover = hover;
        _seekX = x;
        Invalidate(_progressArea);
        Invalidate(_artistRect);
    }

    private bool IsEnabled(Btn id) => id switch
    {
        Btn.Shuffle => _media.CanShuffle,
        Btn.Previous => _media.CanPrevious,
        Btn.PlayPause => _media.CanPlayPause,
        Btn.Next => _media.CanNext,
        Btn.Repeat => _media.CanRepeat,
        Btn.Volume => true,
        _ => false,
    };

    private bool IsActive(Btn id) => id switch
    {
        Btn.Shuffle => _media.Shuffle == true,
        Btn.Repeat => _media.Repeat is MediaPlaybackAutoRepeatMode.List or MediaPlaybackAutoRepeatMode.Track,
        _ => false,
    };

    private string Glyph(Btn id) => id switch
    {
        Btn.Shuffle => "\uE8B1",
        Btn.Repeat => _media.Repeat == MediaPlaybackAutoRepeatMode.Track ? "\uE8ED" : "\uE8EE",
        Btn.Volume => AudioVolume.Glyph(_volume, _muted),
        _ => "",
    };

    private void InvalidateButton(Btn id)
    {
        foreach (var (buttonId, bounds) in _buttons)
            if (buttonId == id)
                Invalidate(bounds);
    }

    // ---------------------------------------------------------------- input

    private Btn HitTest(Point p)
    {
        foreach (var (id, bounds) in _buttons)
            if (bounds.Contains(p))
                return id;
        return Btn.None;
    }

    private void SetHover(Btn id)
    {
        if (id == _hover)
            return;
        InvalidateButton(_hover);
        _hover = id;
        InvalidateButton(id);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        RefreshVolume();
        if (!_marqueeTimer.Enabled && _titleRect.Width > 0 && _titleWidth > _titleRect.Width)
        {
            _marqueeScrolling = true;
            _marqueeTimer.Interval = 33;
            _marqueeTimer.Start();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHover(Btn.None);
        if (!_seeking)
            SetSeekHover(false, 0);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        _mouseDown = true;
        _dragging = false;
        _pressed = HitTest(e.Location);
        _dragStartCursor = Cursor.Position;
        _dragStartLocation = Location;
        Capture = true;

        if (_pressed == Btn.None && CanSeek && _seekRect.Contains(e.Location))
        {
            _seeking = true;
            _seekX = e.X;
            Invalidate(_progressArea);
            Invalidate(_artistRect);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_seeking)
        {
            _seekX = e.X;
            Invalidate(_progressArea);
            Invalidate(_artistRect);
            return;
        }
        if (_mouseDown && _pressed == Btn.None)
        {
            var cursor = Cursor.Position;
            int dx = cursor.X - _dragStartCursor.X, dy = cursor.Y - _dragStartCursor.Y;
            if (!_dragging && (Math.Abs(dx) > SystemInformation.DragSize.Width || Math.Abs(dy) > SystemInformation.DragSize.Height))
                _dragging = true;
            if (_dragging)
            {
                Location = new Point(_dragStartLocation.X + dx, _dragStartLocation.Y + dy);
                return;
            }
        }
        var hit = HitTest(e.Location);
        SetHover(hit);
        SetSeekHover(hit == Btn.None && _seekRect.Contains(e.Location), e.X);
        bool clickable = (_seekHover && CanSeek) || (hit == Btn.None && IsLyricsTarget(e.Location));
        var pointer = clickable ? Cursors.Hand : Cursors.Default;
        if (Cursor != pointer)
            Cursor = pointer;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            ShowMenu();
            return;
        }
        if (e.Button != MouseButtons.Left || !_mouseDown)
            return;

        _mouseDown = false;
        Capture = false;

        if (_seeking)
        {
            _seeking = false;
            _media.Seek(SeekFraction(e.X));
            SetSeekHover(ClientRectangle.Contains(e.Location) && _seekRect.Contains(e.Location), e.X);
            Invalidate(_progressArea);
            Invalidate(_artistRect);
            return;
        }

        if (_dragging)
        {
            _dragging = false;
            _settings.SnapToTaskbar = false;
            _settings.X = Left;
            _settings.Y = Top;
            _settings.Save();
            return;
        }

        var hit = HitTest(e.Location);
        if (hit != Btn.None && hit == _pressed)
            Execute(hit);
        else if (hit == Btn.None && _pressed == Btn.None && IsLyricsTarget(e.Location))
            ToggleLyricsCard();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        using var volume = AudioVolume.For(_media.AppId);
        volume.Step(e.Delta / 120f * 0.02f);
        if (volume.TryRead(out float level, out bool muted))
            SetVolumeIcon(level, muted);
    }

    private void Execute(Btn id)
    {
        if (!IsEnabled(id))
            return;
        switch (id)
        {
            case Btn.Shuffle: _media.ToggleShuffle(); break;
            case Btn.Previous: _media.Previous(); break;
            case Btn.PlayPause: _media.TogglePlayPause(); break;
            case Btn.Next: _media.Next(); break;
            case Btn.Repeat: _media.CycleRepeat(); break;
            case Btn.Volume: ToggleVolumeFlyout(); break;
        }
    }

    // ---------------------------------------------------------------- menu, tray, theme

    private void ShowMenu()
    {
        using var menu = new PopupMenu();

        var position = menu.AddSubmenu("Position");
        position.Add(IsVertical ? "Top of taskbar" : "Left side of taskbar", () => DockToTaskbar(right: false),
            _settings.SnapToTaskbar && !_settings.DockRight, radio: true);
        position.Add(IsVertical ? "Bottom of taskbar" : "Right side of taskbar", () => DockToTaskbar(right: true),
            _settings.SnapToTaskbar && _settings.DockRight, radio: true);
        position.Add("Where I drag it", () =>
        {
            _settings.SnapToTaskbar = false;
            _settings.X = Left;
            _settings.Y = Top;
            RefreshTaskbarAnchors();
        }, !_settings.SnapToTaskbar, radio: true);

        menu.AddSeparator();
        menu.Add("Show shuffle and repeat", () =>
        {
            _settings.ShowShuffleRepeat = !_settings.ShowShuffleRepeat;
            BuildLayout();
        }, _settings.ShowShuffleRepeat);
        menu.Add("Show lyrics", () =>
        {
            _settings.ShowLyrics = !_settings.ShowLyrics;
            if (!_settings.ShowLyrics)
                _lyricsCard?.Hide();
            LoadLyrics();
        }, _settings.ShowLyrics);
        menu.Add("Colors from album art", () =>
        {
            _settings.AlbumColors = !_settings.AlbumColors;
            ApplyTheme(animate: true);
        }, _settings.AlbumColors);
        menu.Add("Show progress bar", () =>
        {
            _settings.ShowProgress = !_settings.ShowProgress;
            UpdateProgressTimer();
            Invalidate();
        }, _settings.ShowProgress);
        menu.Add("Scroll title only once", () =>
        {
            _settings.ScrollOnce = !_settings.ScrollOnce;
            RestartMarquee();
        }, _settings.ScrollOnce);
        menu.Add("Hide when nothing is playing", () =>
        {
            _settings.HideWhenIdle = !_settings.HideWhenIdle;
            UpdateVisibility();
        }, _settings.HideWhenIdle);

        menu.AddSeparator();
        menu.Add("Start with Windows", () => Settings.StartWithWindows = !Settings.StartWithWindows, Settings.StartWithWindows);
        menu.AddSeparator();
        menu.Add("Quit", Close);

        menu.Show(Handle);
        _settings.Save();
    }

    private void DockToTaskbar(bool right)
    {
        _settings.SnapToTaskbar = true;
        _settings.DockRight = right;
        RefreshTaskbarAnchors();
    }

    private void ToggleVolumeFlyout()
    {
        if (_volumeFlyout.Visible)
        {
            _volumeFlyout.Hide();
            return;
        }
        // Clicking the button while the flyout is open closes the flyout first; don't reopen it straight away.
        if ((DateTime.UtcNow - _volumeFlyout.LastClosedUtc).TotalMilliseconds < 300)
            return;
        var bounds = _buttons.First(b => b.Id == Btn.Volume).Bounds;
        _volumeFlyout.ShowAt(RectangleToScreen(bounds), _scale, AudioVolume.For(_media.AppId), _edge);
    }

    private void ApplyTheme(bool animate)
    {
        var target = _settings.AlbumColors && _albumColor is { } album ? _baseTheme.Tinted(album) : _baseTheme;
        _volumeFlyout.SetTheme(target);
        _lyricsCard?.SetTheme(target);

        if (!animate || !Visible)
        {
            _themeTimer.Stop();
            _theme = _themeFrom = _themeTo = target;
            BackColor = target.Background;
            Invalidate();
            return;
        }

        _themeFrom = _theme;
        _themeTo = target;
        _themeBlendStart = DateTime.UtcNow;
        _themeTimer.Start();
    }

    // Cross-fades colours over 400 ms when the track (and so the album colour) changes.
    private void OnThemeBlendTick(object? sender, EventArgs e)
    {
        float t = Math.Clamp((float)(DateTime.UtcNow - _themeBlendStart).TotalMilliseconds / 400f, 0f, 1f);
        float eased = 1 - (1 - t) * (1 - t) * (1 - t);
        _theme = t >= 1 ? _themeTo : Theme.Lerp(_themeFrom, _themeTo, eased);
        BackColor = _theme.Background;
        Invalidate();
        if (t >= 1)
            _themeTimer.Stop();
    }

    private void UpdateTrayIcon()
    {
        int size = SystemInformation.SmallIconSize.Width;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        using (var font = new Font(_iconFamily, size * 0.8f, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(_theme.Foreground))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawString("\uE8D6", font, brush, new RectangleF(0, 0, size, size), format);
        }

        IntPtr handle = bitmap.GetHicon();
        var old = _tray.Icon;
        using (var temp = Icon.FromHandle(handle))
            _tray.Icon = (Icon)temp.Clone();
        Native.DestroyIcon(handle);
        old?.Dispose();
    }

    private static string ResolveIconFamily()
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            using var font = new Font(name, 10);
            if (font.Name == name)
                return name;
        }
        return "Segoe MDL2 Assets";
    }
}
