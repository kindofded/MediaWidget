using System.Drawing.Drawing2D;

namespace MediaWidget;

/// <summary>Small popup with a mute button and a volume slider, shown above the widget's volume button.</summary>
internal sealed class VolumeFlyout : Form
{
    private const TextFormatFlags GlyphFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
        TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter;

    private readonly string _iconFamily;
    private Theme _theme;
    private float _scale = 1;
    private Rectangle _muteRect, _trackRect, _sliderHitRect, _textRect, _captionRect;
    private Font? _iconFont, _textFont, _captionFont;
    private IVolumeControl? _volume;
    private float _level;
    private bool _muted, _dragging, _muteHover, _thumbHover;

    public VolumeFlyout(Theme theme, string iconFamily)
    {
        _theme = theme;
        _iconFamily = iconFamily;
        Text = "Media Widget Volume";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    /// <summary>Raised with the new level and mute state whenever the flyout changes them.</summary>
    public event Action<float, bool>? VolumeChanged;

    /// <summary>When the flyout last closed; lets the widget ignore the click that closed it.</summary>
    public DateTime LastClosedUtc { get; private set; }

    public void SetTheme(Theme theme)
    {
        _theme = theme;
        Invalidate();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    private int S(float value) => (int)MathF.Round(value * _scale);

    /// <summary>Shows the flyout for <paramref name="volume"/>, which it owns until it closes.</summary>
    public void ShowAt(Rectangle anchor, float scale, IVolumeControl volume, TaskbarEdge edge)
    {
        _volume?.Dispose();
        _volume = volume;
        _scale = scale;
        BuildLayout();
        volume.TryRead(out _level, out _muted);

        // Open away from the taskbar, like the Windows flyouts do.
        var screen = Screen.FromRectangle(anchor).Bounds;
        int gap = S(8);
        int x = edge switch
        {
            TaskbarEdge.Left => anchor.Right + gap,
            TaskbarEdge.Right => anchor.Left - Width - gap,
            _ => anchor.Left + anchor.Width / 2 - Width / 2,
        };
        int y = edge switch
        {
            TaskbarEdge.Top => anchor.Bottom + gap,
            TaskbarEdge.Bottom => anchor.Top - Height - gap,
            _ => anchor.Top + anchor.Height / 2 - Height / 2,
        };
        Location = new Point(
            Math.Clamp(x, screen.Left + gap, screen.Right - Width - gap),
            Math.Clamp(y, screen.Top + gap, screen.Bottom - Height - gap));

        Show();
        Activate();
        Native.SetForegroundWindow(Handle);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int corner = Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        _dragging = false;
        Capture = false;
        LastClosedUtc = DateTime.UtcNow;
        Hide();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (!Visible)
        {
            _volume?.Dispose();
            _volume = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _volume?.Dispose();
        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        int width = S(272), height = S(64);
        _muteRect = new Rectangle(S(8), (height - S(36)) / 2, S(36), S(36));
        _textRect = new Rectangle(width - S(46), S(22), S(36), S(32));
        int trackLeft = _muteRect.Right + S(14), trackRight = _textRect.Left - S(12);
        _captionRect = new Rectangle(trackLeft - S(2), S(8), width - trackLeft - S(8), S(16));
        _trackRect = new Rectangle(trackLeft, S(38) - S(2), trackRight - trackLeft, Math.Max(2, S(4)));
        _sliderHitRect = Rectangle.FromLTRB(trackLeft - S(10), S(24), trackRight + S(10), height);

        _iconFont?.Dispose();
        _textFont?.Dispose();
        _captionFont?.Dispose();
        _iconFont = new Font(_iconFamily, 16 * _scale, GraphicsUnit.Pixel);
        _textFont = new Font("Segoe UI Semibold", 13 * _scale, GraphicsUnit.Pixel);
        _captionFont = new Font("Segoe UI", 12 * _scale, GraphicsUnit.Pixel);
        Size = new Size(width, height);
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaintBackground(PaintEventArgs e) { }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_theme.Background);

        if (_muteHover)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Drawing.RoundedRect(_muteRect, S(4));
            using var hover = new SolidBrush(_theme.HoverBackground);
            g.FillPath(hover, path);
            g.SmoothingMode = SmoothingMode.None;
        }
        Drawing.Glyph(g, AudioVolume.Glyph(_level, _muted), _iconFont!, _muteRect, _theme.Foreground);
        TextRenderer.DrawText(g, _volume?.Name ?? "", _captionFont, _captionRect, _theme.ForegroundDim,
            GlyphFlags & ~TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);

        float fraction = _muted ? 0 : _level;
        int thumbX = _trackRect.Left + (int)MathF.Round(_trackRect.Width * fraction);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var track = new SolidBrush(_theme.Track))
        using (var trackPath = Drawing.RoundedRect(_trackRect, _trackRect.Height / 2))
            g.FillPath(track, trackPath);
        if (thumbX > _trackRect.Left)
        {
            var filled = new Rectangle(_trackRect.Left, _trackRect.Top, thumbX - _trackRect.Left, _trackRect.Height);
            using var accent = new SolidBrush(_theme.Accent);
            using var filledPath = Drawing.RoundedRect(filled, Math.Min(_trackRect.Height / 2, filled.Width / 2));
            g.FillPath(accent, filledPath);
        }

        // Windows 11 slider thumb: neutral outer disc with an accent dot that grows on hover.
        int outer = S(20), inner = _thumbHover || _dragging ? S(14) : S(11);
        int cy = _trackRect.Top + _trackRect.Height / 2;
        using (var ring = new SolidBrush(_theme.IsLight ? Color.White : Color.FromArgb(69, 69, 69)))
            g.FillEllipse(ring, thumbX - outer / 2, cy - outer / 2, outer, outer);
        if (_theme.IsLight)
        {
            using var border = new Pen(Color.FromArgb(40, 0, 0, 0));
            g.DrawEllipse(border, thumbX - outer / 2, cy - outer / 2, outer, outer);
        }
        using (var dot = new SolidBrush(_theme.Accent))
            g.FillEllipse(dot, thumbX - inner / 2f, cy - inner / 2f, inner, inner);
        g.SmoothingMode = SmoothingMode.None;

        string percent = ((int)MathF.Round(_level * 100)).ToString();
        TextRenderer.DrawText(g, percent, _textFont, _textRect, _muted ? _theme.ForegroundDim : _theme.Foreground,
            GlyphFlags & ~TextFormatFlags.HorizontalCenter | TextFormatFlags.Right);
    }

    // ---------------------------------------------------------------- input

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left)
            return;
        if (_muteRect.Contains(e.Location))
        {
            _volume?.ToggleMute();
            Reload();
        }
        else if (_sliderHitRect.Contains(e.Location))
        {
            _dragging = true;
            Capture = true;
            SetFromX(e.X);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            SetFromX(e.X);
            return;
        }
        bool muteHover = _muteRect.Contains(e.Location), thumbHover = _sliderHitRect.Contains(e.Location);
        if (muteHover != _muteHover || thumbHover != _thumbHover)
        {
            _muteHover = muteHover;
            _thumbHover = thumbHover;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = false;
        Capture = false;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _muteHover = _thumbHover = false;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _volume?.Step(e.Delta / 120f * 0.02f);
        Reload();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.KeyCode)
        {
            case Keys.Escape:
                Hide();
                break;
            case Keys.Left or Keys.Down:
                _volume?.Step(-0.02f);
                Reload();
                break;
            case Keys.Right or Keys.Up:
                _volume?.Step(0.02f);
                Reload();
                break;
        }
    }

    private void SetFromX(int x)
    {
        float level = MathF.Round(Math.Clamp((x - _trackRect.Left) / (float)_trackRect.Width, 0f, 1f) * 100) / 100;
        if (level == _level && !_muted)
            return;
        _volume?.Set(level);
        Reload();
    }

    private void Reload()
    {
        if (_volume is null || !_volume.TryRead(out _level, out _muted))
            return;
        Invalidate();
        VolumeChanged?.Invoke(_level, _muted);
    }
}
