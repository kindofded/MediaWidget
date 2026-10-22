using Interop.UIAutomationClient;

namespace MediaWidget;

internal enum TaskbarEdge { Bottom, Top, Left, Right }

/// <summary>Taskbar items the widget must not cover. Any of them may be missing.</summary>
internal readonly record struct TaskbarAnchors(Rectangle? Widgets, Rectangle? Start, Rectangle? Tray);

/// <summary>Locates the taskbar and the things on it that the widget must not cover.</summary>
internal static class TaskbarInfo
{
    private const int UIA_AutomationIdPropertyId = 30011;

    private static CUIAutomation? _automation;

    public static IntPtr Handle => Native.FindWindow("Shell_TrayWnd", null);

    public static Rectangle? Bounds => GetRect(Handle);

    /// <summary>Which screen edge the taskbar is on (Windows 11 allows all four since the 2026 update).</summary>
    public static TaskbarEdge Edge
    {
        get
        {
            if (Bounds is not { } taskbar)
                return TaskbarEdge.Bottom;
            var screen = Screen.FromRectangle(taskbar).Bounds;
            if (taskbar.Height > taskbar.Width)
                return taskbar.Left - screen.Left <= screen.Right - taskbar.Right ? TaskbarEdge.Left : TaskbarEdge.Right;
            return taskbar.Top - screen.Top <= screen.Bottom - taskbar.Bottom ? TaskbarEdge.Top : TaskbarEdge.Bottom;
        }
    }

    /// <summary>
    /// Finds the Widgets (weather) button, the Start button and the system tray. The Windows 11 taskbar is drawn
    /// by XAML without child windows, so UI Automation is the only way to locate these. Blocks on Explorer, so
    /// call it off the UI thread.
    /// </summary>
    public static TaskbarAnchors FindAnchors()
    {
        var taskbar = Handle;
        if (taskbar == IntPtr.Zero)
            return default;
        try
        {
            _automation ??= new CUIAutomation();
            var root = _automation.ElementFromHandle(taskbar);

            Rectangle? Find(string automationId) =>
                ToRectangle(root.FindFirst(TreeScope.TreeScope_Descendants, ById(automationId)));

            Rectangle? tray = GetRect(Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null));
            var trayItems = root.FindAll(TreeScope.TreeScope_Descendants,
                _automation.CreateOrCondition(ById("SystemTrayIcon"), ById("NotifyItemIcon")));
            for (int i = 0; i < (trayItems?.Length ?? 0); i++)
            {
                if (ToRectangle(trayItems!.GetElement(i)) is { } item)
                    tray = tray is { } t ? Rectangle.Union(t, item) : item;
            }

            return new TaskbarAnchors(Find("WidgetsButton"), Find("StartButton"), tray);
        }
        catch
        {
            return default;
        }
    }

    private static IUIAutomationCondition ById(string automationId) =>
        _automation!.CreatePropertyCondition(UIA_AutomationIdPropertyId, automationId);

    private static Rectangle? ToRectangle(IUIAutomationElement? element)
    {
        if (element is null)
            return null;
        var r = element.CurrentBoundingRectangle;
        return r.right > r.left && r.bottom > r.top ? Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom) : null;
    }

    private static Rectangle? GetRect(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top
            ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
            : null;
}
