using Microsoft.Win32;

namespace MediaWidget;

internal sealed class Settings
{
    private const string KeyPath = @"Software\MediaWidget";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MediaWidget";

    public bool SnapToTaskbar { get; set; } = true;
    public bool DockRight { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public bool ShowProgress { get; set; } = true;
    public bool ShowShuffleRepeat { get; set; } = true;
    public bool ScrollOnce { get; set; } = true;
    public bool HideWhenIdle { get; set; }
    public bool AlbumColors { get; set; } = true;
    public bool ShowLyrics { get; set; }

    public static Settings Load()
    {
        var settings = new Settings();
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        if (key is null)
            return settings;

        settings.SnapToTaskbar = GetBool(key, nameof(SnapToTaskbar), settings.SnapToTaskbar);
        settings.DockRight = GetBool(key, nameof(DockRight), settings.DockRight);
        settings.X = GetInt(key, nameof(X), settings.X);
        settings.Y = GetInt(key, nameof(Y), settings.Y);
        settings.ShowProgress = GetBool(key, nameof(ShowProgress), settings.ShowProgress);
        settings.ShowShuffleRepeat = GetBool(key, nameof(ShowShuffleRepeat), settings.ShowShuffleRepeat);
        settings.ScrollOnce = GetBool(key, nameof(ScrollOnce), settings.ScrollOnce);
        settings.HideWhenIdle = GetBool(key, nameof(HideWhenIdle), settings.HideWhenIdle);
        settings.AlbumColors = GetBool(key, nameof(AlbumColors), settings.AlbumColors);
        settings.ShowLyrics = GetBool(key, nameof(ShowLyrics), settings.ShowLyrics);
        return settings;
    }

    public void Save()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(nameof(SnapToTaskbar), SnapToTaskbar ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(nameof(DockRight), DockRight ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(nameof(X), X, RegistryValueKind.DWord);
        key.SetValue(nameof(Y), Y, RegistryValueKind.DWord);
        key.SetValue(nameof(ShowProgress), ShowProgress ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(nameof(ShowShuffleRepeat), ShowShuffleRepeat ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(nameof(ScrollOnce), ScrollOnce ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(nameof(HideWhenIdle), HideWhenIdle ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(nameof(AlbumColors), AlbumColors ? 1 : 0, RegistryValueKind.DWord);
        key.SetValue(nameof(ShowLyrics), ShowLyrics ? 1 : 0, RegistryValueKind.DWord);
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (value)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValueName, false);
        }
    }

    private static bool GetBool(RegistryKey key, string name, bool fallback) =>
        key.GetValue(name) is int value ? value != 0 : fallback;

    private static int GetInt(RegistryKey key, string name, int fallback) =>
        key.GetValue(name) is int value ? value : fallback;
}
