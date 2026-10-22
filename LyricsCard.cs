using System.Drawing.Drawing2D;

namespace MediaWidget;

/// <summary>
/// Karaoke-style card next to the widget: the current lyric line large in the middle, previous and upcoming
/// lines dimmed around it. Scrolls smoothly on line changes; clicking a line seeks to it.
/// </summary>
internal sealed class LyricsCard : Form
{
    private const TextFormatFlags LineFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;

    private readonly System.Windows.Forms.Timer _scrollTimer = new() { Interval = 16 };
    private Theme _theme;
    private float _scale = 1;
    private Font? _currentFont, _otherFont;
    private Lyrics? _lyrics;
    private int _index = -1;
    private string? _message;
    private float _scroll;
    private float _scrollFrom;
    private DateTime _scrollStart;

    public LyricsCard(Theme theme)
    {
        _theme = theme;
        Text = "Media Widget Lyrics";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        _scrollTimer.Tick += OnScrollTick;
    }

    /// <summary>Raised when the user clicks a lyric line.</summary>
    public event Action<TimeSpan>? SeekRequested;

    /// <summary>Raised when the user clicks a status message (e.g. to retry after an error).</summary>
    public event Action? MessageClicked;

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

    private int S(float value) => (int)MathF.Round(value * _scale);

    private int LineHeight => S(26);

    public void SetTheme(Theme theme)
    {
        _theme = theme;
        Invalidate();
    }

    /// <summary>Updates what's shown. <paramref name="message"/> (e.g. "Looking for lyrics…") replaces the lines when set.</summary>
    public void SetContent(Lyrics? lyrics, int index, string? message)
    {
        bool sameSong = ReferenceEquals(lyrics, _lyrics);
        // Animate single-line steps; jumps (seeks, new songs) snap straight there.
        if (sameSong && index == _index + 1 && Visible)
        {
            _scrollFrom = _scroll + 1;
            _scrollStart = DateTime.UtcNow;
            _scrollTimer.Start();
        }
        else if (!sameSong || index != _index)
        {
            _scrollTimer.Stop();
            _scroll = 0;
        }

        _lyrics = lyrics;
        _index = index;
        _message = message;
        Invalidate();
    }

    public void ShowNear(Rectangle widget, TaskbarEdge edge, float scale)
    {
        if (Math.Abs(scale - _scale) > 0.001f || _currentFont is null)
        {
            _scale = scale;
            _currentFont?.Dispose();
            _otherFont?.Dispose();
            _currentFont = new Font("Segoe UI Semibold", 16 * _scale, GraphicsUnit.Pixel);
            _otherFont = new Font("Segoe UI", 13 * _scale, GraphicsUnit.Pixel);
        }

        int width = edge is TaskbarEdge.Left or TaskbarEdge.Right ? S(360) : Math.Max(widget.Width, S(360));
        int height = LineHeight * 5 + S(16);
        Size = new Size(width, height);

        int gap = S(8);
        int x = edge switch
        {
            TaskbarEdge.Left => widget.Right + gap,
            TaskbarEdge.Right => widget.Left - width - gap,
            _ => widget.Left,
        };
        int y = edge switch
        {
            TaskbarEdge.Top => widget.Bottom + gap,
            TaskbarEdge.Bottom => widget.Top - height - gap,
            _ => widget.Top,
        };
        var screen = Screen.FromRectangle(widget).Bounds;
        Location = new Point(
            Math.Clamp(x, screen.Left + gap, screen.Right - width - gap),
            Math.Clamp(y, screen.Top + gap, screen.Bottom - height - gap));

        if (!Visible)
            Show();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int corner = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE)
        {
            m.Result = Native.MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scrollTimer.Dispose();
            _currentFont?.Dispose();
            _otherFont?.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnScrollTick(object? sender, EventArgs e)
    {
        float t = Math.Clamp((float)(DateTime.UtcNow - _scrollStart).TotalMilliseconds / 250f, 0f, 1f);
        float eased = 1 - (1 - t) * (1 - t) * (1 - t);
        _scroll = _scrollFrom * (1 - eased);
        Invalidate();
        if (t >= 1)
            _scrollTimer.Stop();
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using (var background = new LinearGradientBrush(ClientRectangle, _theme.Background, _theme.BackgroundEnd, LinearGradientMode.Vertical))
            g.FillRectangle(background, ClientRectangle);
        if (_currentFont is null || _otherFont is null)
            return;

        var content = Rectangle.Inflate(ClientRectangle, -S(16), 0);
        if (_message is not null || _lyrics is null)
        {
            TextRenderer.DrawText(g, _message ?? "", _otherFont, content, _theme.ForegroundDim, LineFlags);
            return;
        }

        int middle = Height / 2;
        for (int i = _index - 3; i <= _index + 3; i++)
        {
            if (i < -1 || i >= _lyrics.Lines.Count)
                continue;
            float offset = i - _index + _scroll;
            var bounds = new Rectangle(content.X, (int)(middle + offset * LineHeight - LineHeight / 2f), content.Width, LineHeight);
            if (bounds.Bottom < 0 || bounds.Top > Height)
                continue;

            bool current = i == _index;
            float distance = Math.Min(Math.Abs(offset), 3f);
            var color = current
                ? _theme.Foreground
                : ColorMath.Lerp(_theme.ForegroundDim, _theme.Background, Math.Clamp((distance - 1) * 0.35f, 0f, 0.7f));
            TextRenderer.DrawText(g, LineText(i), current ? _currentFont : _otherFont, bounds, color, LineFlags);
        }
    }

    // Index -1 is the intro before the first line; empty lines are instrumental breaks.
    private string LineText(int index) =>
        index < 0 || _lyrics is null || _lyrics.Lines[index].Text.Length == 0 ? "♪" : _lyrics.Lines[index].Text;

    // ---------------------------------------------------------------- input

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left)
            return;
        if (_message is not null)
        {
            MessageClicked?.Invoke();
            return;
        }
        if (_lyrics is null)
            return;
        int clicked = _index + (int)MathF.Round((e.Y - Height / 2f) / LineHeight);
        if (clicked >= 0 && clicked < _lyrics.Lines.Count)
            SeekRequested?.Invoke(_lyrics.Lines[clicked].Time);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = (_lyrics is not null && _message is null) || _message?.Contains("try again") == true ? Cursors.Hand : Cursors.Default;
    }
}
