using Interop.UIAutomationClient;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MediaWidget;

internal enum TaskbarEdge { Bottom, Top, Left, Right }

/// <summary>
/// Taskbar items the widget must not cover. <c>Apps</c> spans Start, Search and the app buttons, and grows as apps
/// open. Any of them may be missing. <c>IconsAtStart</c> is the left/top (rather than centred) icon alignment.
/// </summary>
internal readonly record struct TaskbarAnchors(Rectangle? Widgets, Rectangle? Apps, Rectangle? Tray, bool IconsAtStart);

/// <summary>Locates the taskbar and the things on it that the widget must not cover.</summary>
internal static class TaskbarInfo
{
    private const int UIA_BoundingRectanglePropertyId = 30001;
    private const int UIA_AutomationIdPropertyId = 30011;

    private static readonly Lock Gate = new();
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
    /// Finds the Widgets (weather) button, the Start/app buttons and the system tray. The Windows 11 taskbar is drawn
    /// by XAML without child windows, so UI Automation is the only way to locate these. Blocks on Explorer, so
    /// call it off the UI thread.
    /// </summary>
    public static TaskbarAnchors FindAnchors()
    {
        var taskbar = Handle;
        if (taskbar == IntPtr.Zero)
            return default;

        // UI Automation objects hold memory (here and in Explorer) that the GC can't see, so it rarely gets round to
        // freeing them; release everything a lookup creates as soon as it's done.
        var created = new HashSet<object>(ReferenceEqualityComparer.Instance);
        T? Track<T>(T? value) where T : class
        {
            if (value is not null)
                created.Add(value);
            return value;
        }

        // Lookups can overlap (several are queued while the taskbar animates) and may get the same shared objects.
        lock (Gate)
        try
        {
            var automation = _automation ??= new CUIAutomation();
            var root = Track(automation.ElementFromHandle(taskbar))!;

            IUIAutomationCondition ById(string automationId) =>
                Track(automation.CreatePropertyCondition(UIA_AutomationIdPropertyId, automationId))!;
            IUIAutomationElement? Find(string automationId) =>
                Track(root.FindFirst(TreeScope.TreeScope_Descendants, ById(automationId)));

            Rectangle? tray = GetRect(Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null));
            var trayItems = Track(root.FindAll(TreeScope.TreeScope_Descendants,
                Track(automation.CreateOrCondition(ById("SystemTrayIcon"), ById("NotifyItemIcon")))));
            for (int i = 0; i < (trayItems?.Length ?? 0); i++)
            {
                if (ToRectangle(Track(trayItems!.GetElement(i))) is { } item)
                    tray = tray is { } t ? Rectangle.Union(t, item) : item;
            }

            // Union of everything in the Start button's row except the Widgets button. The row
            // (TaskbarFrameRepeater) isn't a control element, so searches in the default control view skip it.
            Rectangle? FindApps(IUIAutomationElement start)
            {
                var row = Track(Track(automation.RawViewWalker)!.GetParentElement(start));
                if (row is null)
                    return null;

                // One cross-process call for all buttons instead of two per button.
                var cache = Track(automation.CreateCacheRequest())!;
                cache.TreeFilter = Track(automation.RawViewCondition);
                cache.AddProperty(UIA_BoundingRectanglePropertyId);
                cache.AddProperty(UIA_AutomationIdPropertyId);
                var buttons = Track(row.FindAllBuildCache(TreeScope.TreeScope_Children, Track(automation.CreateTrueCondition()), cache));

                Rectangle? apps = null;
                for (int i = 0; i < (buttons?.Length ?? 0); i++)
                {
                    var button = Track(buttons!.GetElement(i))!;
                    if (button.CachedAutomationId != "WidgetsButton" && ToRectangle(button.CachedBoundingRectangle) is { } r)
                        apps = apps is { } a ? Rectangle.Union(a, r) : r;
                }
                return apps;
            }

            var start = Find("StartButton");
            // Positions can't tell the alignment apart: centred icons stop being centred once they fill up to the tray.
            bool iconsAtStart = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl", 1) is 0;
            var apps = start is null ? null : FindApps(start) ?? ToRectangle(start);
            return new TaskbarAnchors(ToRectangle(Find("WidgetsButton")), apps, tray, iconsAtStart);
        }
        catch
        {
            return default;
        }
        finally
        {
            foreach (var value in created)
                Marshal.FinalReleaseComObject(value);
        }
    }

    private static Rectangle? ToRectangle(IUIAutomationElement? element) =>
        element is null ? null : ToRectangle(element.CurrentBoundingRectangle);

    private static Rectangle? ToRectangle(tagRECT r) =>
        r.right > r.left && r.bottom > r.top ? Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom) : null;

    private static Rectangle? GetRect(IntPtr hwnd) =>
        hwnd != IntPtr.Zero && Native.GetWindowRect(hwnd, out var r) && r.Right > r.Left && r.Bottom > r.Top
            ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)
            : null;
}
