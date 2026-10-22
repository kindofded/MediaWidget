using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace MediaWidget;

/// <summary>Something whose volume the widget can change: one app's audio, or the whole system.</summary>
internal interface IVolumeControl : IDisposable
{
    /// <summary>What this adjusts, e.g. "Spotify" or "System volume".</summary>
    string Name { get; }
    bool TryRead(out float level, out bool muted);
    void Set(float level);
    void Step(float delta);
    void ToggleMute();
}

internal static class AudioVolume
{
    public static string Glyph(float level, bool muted) =>
        muted ? "\uE74F" : level < 0.01f ? "\uE992" : level < 0.34f ? "\uE993" : level < 0.67f ? "\uE994" : "\uE995";

    /// <summary>Volume of the given media app, or system volume when that app has no audio session.</summary>
    public static IVolumeControl For(string? appId) =>
        (string.IsNullOrEmpty(appId) ? null : AppVolume.TryCreate(appId)) ?? (IVolumeControl)new SystemVolume();
}

/// <summary>Master volume of the default playback device.</summary>
internal sealed class SystemVolume : IVolumeControl
{
    public string Name => "System volume";

    public bool TryRead(out float level, out bool muted)
    {
        float l = 0;
        bool m = false;
        bool ok = TryUse(v =>
        {
            v.GetMasterVolumeLevelScalar(out l);
            v.GetMute(out m);
        });
        level = l;
        muted = m;
        return ok;
    }

    public void Set(float level) => TryUse(v =>
    {
        level = Math.Clamp(level, 0f, 1f);
        v.SetMute(level <= 0f, IntPtr.Zero);
        v.SetMasterVolumeLevelScalar(level, IntPtr.Zero);
    });

    public void Step(float delta) => TryUse(v =>
    {
        v.GetMasterVolumeLevelScalar(out float level);
        if (delta > 0)
            v.SetMute(false, IntPtr.Zero);
        v.SetMasterVolumeLevelScalar(Math.Clamp(level + delta, 0f, 1f), IntPtr.Zero);
    });

    public void ToggleMute() => TryUse(v =>
    {
        v.GetMute(out bool muted);
        v.SetMute(!muted, IntPtr.Zero);
    });

    public void Dispose() { }

    private static bool TryUse(Action<CoreAudio.IAudioEndpointVolume> action)
    {
        CoreAudio.IMMDeviceEnumerator? enumerator = null;
        CoreAudio.IMMDevice? device = null;
        object? endpoint = null;
        try
        {
            enumerator = CoreAudio.CreateEnumerator();
            enumerator.GetDefaultAudioEndpoint(CoreAudio.eRender, CoreAudio.eMultimedia, out device);
            var iid = typeof(CoreAudio.IAudioEndpointVolume).GUID;
            device.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out endpoint);
            action((CoreAudio.IAudioEndpointVolume)endpoint);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            return false;
        }
        finally
        {
            CoreAudio.Release(endpoint, device, enumerator);
        }
    }
}

/// <summary>
/// Per-app volume, as shown in the Windows volume mixer. An app can have several audio sessions
/// (one per output device or audio process), so all matching sessions are changed together.
/// </summary>
internal sealed class AppVolume : IVolumeControl
{
    // AUMIDs that don't resemble the process name.
    private static readonly Dictionary<string, string> KnownProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["308046B0AF4A39CB"] = "firefox",
        ["6F193CCC56814779"] = "firefox",
    };

    private readonly List<CoreAudio.ISimpleAudioVolume> _sessions;

    private AppVolume(string name, List<CoreAudio.ISimpleAudioVolume> sessions)
    {
        Name = name;
        _sessions = sessions;
    }

    public string Name { get; }

    public static AppVolume? TryCreate(string appId)
    {
        var matches = CreateMatcher(appId);
        var sessions = new List<CoreAudio.ISimpleAudioVolume>();
        string? name = null;

        CoreAudio.IMMDeviceEnumerator? enumerator = null;
        CoreAudio.IMMDeviceCollection? devices = null;
        try
        {
            enumerator = CoreAudio.CreateEnumerator();
            enumerator.EnumAudioEndpoints(CoreAudio.eRender, CoreAudio.DEVICE_STATE_ACTIVE, out devices);
            devices.GetCount(out uint deviceCount);

            for (uint d = 0; d < deviceCount; d++)
            {
                CoreAudio.IMMDevice? device = null;
                object? manager = null;
                CoreAudio.IAudioSessionEnumerator? list = null;
                try
                {
                    devices.Item(d, out device);
                    var iid = typeof(CoreAudio.IAudioSessionManager2).GUID;
                    device.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out manager);
                    ((CoreAudio.IAudioSessionManager2)manager).GetSessionEnumerator(out list);
                    list.GetCount(out int sessionCount);

                    for (int s = 0; s < sessionCount; s++)
                    {
                        list.GetSession(s, out var control);
                        control.GetProcessId(out uint pid);
                        if (pid != 0 && matches(pid))
                        {
                            sessions.Add((CoreAudio.ISimpleAudioVolume)control);
                            name ??= FriendlyName(pid);
                        }
                        else
                        {
                            Marshal.ReleaseComObject(control);
                        }
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                }
                finally
                {
                    CoreAudio.Release(list, manager, device);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
        }
        finally
        {
            CoreAudio.Release(devices, enumerator);
        }

        return sessions.Count > 0 ? new AppVolume(name ?? appId, sessions) : null;
    }

    public bool TryRead(out float level, out bool muted)
    {
        level = 0;
        muted = false;
        try
        {
            _sessions[0].GetMasterVolume(out level);
            _sessions[0].GetMute(out muted);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }

    public void Set(float level)
    {
        level = Math.Clamp(level, 0f, 1f);
        ForEach(s =>
        {
            s.SetMute(level <= 0f, IntPtr.Zero);
            s.SetMasterVolume(level, IntPtr.Zero);
        });
    }

    public void Step(float delta)
    {
        if (TryRead(out float level, out _))
            Set(level + delta);
    }

    public void ToggleMute()
    {
        if (TryRead(out _, out bool muted))
            ForEach(s => s.SetMute(!muted, IntPtr.Zero));
    }

    public void Dispose()
    {
        foreach (var session in _sessions)
            Marshal.ReleaseComObject(session);
        _sessions.Clear();
    }

    private void ForEach(Action<CoreAudio.ISimpleAudioVolume> action)
    {
        foreach (var session in _sessions)
        {
            try { action(session); }
            catch (COMException) { /* the session ended */ }
        }
    }

    /// <summary>Packaged (Store) apps are matched by package family; everything else by process name.</summary>
    private static Func<uint, bool> CreateMatcher(string appId)
    {
        int bang = appId.IndexOf('!');
        if (bang > 0)
        {
            string family = appId[..bang];
            return pid => string.Equals(PackageFamilyName(pid), family, StringComparison.OrdinalIgnoreCase);
        }

        string name = appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(appId)
            : appId.Split('.')[0];
        name = KnownProcessNames.GetValueOrDefault(name, name);
        return pid => string.Equals(ProcessName(pid), name, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string FriendlyName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            string? description = process.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(description) ? process.ProcessName : description;
        }
        catch
        {
            return ProcessName(pid) ?? "App";
        }
    }

    private static string? PackageFamilyName(uint pid)
    {
        IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return null;
        try
        {
            uint length = 256;
            var buffer = new StringBuilder((int)length);
            return Native.GetPackageFamilyName(process, ref length, buffer) == 0 ? buffer.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }
}

/// <summary>Windows Core Audio COM interfaces. Only the methods we call have real signatures.</summary>
internal static class CoreAudio
{
    public const int eRender = 0;
    public const int eMultimedia = 1;
    public const int DEVICE_STATE_ACTIVE = 1;
    public const int CLSCTX_ALL = 23;

    public static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumerator();

    public static void Release(params object?[] objects)
    {
        foreach (var o in objects)
            if (o is not null && Marshal.IsComObject(o))
                Marshal.ReleaseComObject(o);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        void EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        void GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        void GetCount(out uint count);
        void Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object endpoint);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);
        void UnregisterControlChangeNotify(IntPtr notify);
        void GetChannelCount(out uint count);
        void SetMasterVolumeLevel(float levelDb, IntPtr context);
        void SetMasterVolumeLevelScalar(float level, IntPtr context);
        void GetMasterVolumeLevel(out float levelDb);
        void GetMasterVolumeLevelScalar(out float level);
        void SetChannelVolumeLevel(uint channel, float levelDb, IntPtr context);
        void SetChannelVolumeLevelScalar(uint channel, float level, IntPtr context);
        void GetChannelVolumeLevel(uint channel, out float levelDb);
        void GetChannelVolumeLevelScalar(uint channel, out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionManager2
    {
        void GetAudioSessionControl(); // vtable slot only
        void GetSimpleAudioVolume(); // vtable slot only
        void GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEnumerator
    {
        void GetCount(out int count);
        void GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl2
    {
        // IAudioSessionControl (vtable slots only)
        void GetState();
        void GetDisplayName();
        void SetDisplayName();
        void GetIconPath();
        void SetIconPath();
        void GetGroupingParam();
        void SetGroupingParam();
        void RegisterAudioSessionNotification();
        void UnregisterAudioSessionNotification();
        // IAudioSessionControl2
        void GetSessionIdentifier();
        void GetSessionInstanceIdentifier();
        [PreserveSig] int GetProcessId(out uint pid); // returns a success code for multi-process sessions
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISimpleAudioVolume
    {
        void SetMasterVolume(float level, IntPtr context);
        void GetMasterVolume(out float level);
        void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, IntPtr context);
        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
