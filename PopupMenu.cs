namespace MediaWidget;

/// <summary>
/// Native Win32 popup menu. Windows 11 draws these itself (rounded corners, Fluent styling, dark mode),
/// which looks far more current than a WinForms ContextMenuStrip and costs nothing to keep around.
/// </summary>
internal sealed class PopupMenu : IDisposable
{
    private readonly IntPtr _handle = Native.CreatePopupMenu();
    private readonly List<Action> _commands;

    public PopupMenu() : this([]) { }

    private PopupMenu(List<Action> commands) => _commands = commands;

    public void Add(string text, Action action, bool isChecked = false, bool radio = false)
    {
        _commands.Add(action);
        var item = new Native.MENUITEMINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MENUITEMINFO>(),
            fMask = Native.MIIM_ID | Native.MIIM_STRING | Native.MIIM_STATE | Native.MIIM_FTYPE,
            fType = radio ? Native.MFT_RADIOCHECK : 0,
            fState = isChecked ? Native.MFS_CHECKED : 0,
            wID = (uint)_commands.Count,
            dwTypeData = text,
        };
        Native.InsertMenuItem(_handle, uint.MaxValue, true, ref item);
    }

    public void AddSeparator() => Native.AppendMenu(_handle, Native.MF_SEPARATOR, UIntPtr.Zero, null);

    public PopupMenu AddSubmenu(string text)
    {
        var child = new PopupMenu(_commands);
        Native.AppendMenu(_handle, Native.MF_POPUP, (UIntPtr)(ulong)child._handle, text);
        return child;
    }

    /// <summary>Shows the menu at the cursor and runs the chosen command after it closes.</summary>
    public void Show(IntPtr owner)
    {
        var cursor = Cursor.Position;
        var screen = Screen.FromPoint(cursor).Bounds;
        uint flags = Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON | Native.TPM_NONOTIFY
            | (cursor.Y > screen.Top + screen.Height / 2 ? Native.TPM_BOTTOMALIGN : 0);

        // Without the foreground, the menu won't dismiss when clicking elsewhere.
        Native.SetForegroundWindow(owner);
        int command = Native.TrackPopupMenuEx(_handle, flags, cursor.X, cursor.Y, owner, IntPtr.Zero);
        Native.PostMessage(owner, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);

        if (command > 0 && command <= _commands.Count)
            _commands[command - 1]();
    }

    public void Dispose()
    {
        // Destroying the root menu also destroys its submenus.
        Native.DestroyMenu(_handle);
    }
}
