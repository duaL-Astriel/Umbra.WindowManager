using System;
using Dalamud.Interface.Windowing;

namespace Umbra.WindowManager.Services.WindowManager;

public class TrackedWindow
{
    private readonly WeakReference<IWindow> windowRef;

    public TrackedWindow(IWindow window)
    {
        this.windowRef = new WeakReference<IWindow>(window);
        this.WindowName = window.WindowName;
        this.CleanTitle = WindowInfoHelper.GetCleanTitle(window.WindowName);
        this.Namespace = window.Namespace ?? string.Empty;
    }

    public string WindowName { get; }
    public string CleanTitle { get; }
    public string Id => WindowInfoHelper.GetWindowId(this.WindowName);
    public string Namespace { get; set; }
    public bool IsMinimized { get; set; }
    public string? DockGroupKey { get; set; }

    public bool TryGetWindow(out IWindow window) => this.windowRef.TryGetTarget(out window!);

    public bool IsOpen
    {
        get => this.TryGetWindow(out var w) && w.IsOpen;
        set
        {
            if (this.TryGetWindow(out var w))
                w.IsOpen = value;
        }
    }

    public bool IsFocused => this.TryGetWindow(out var w) && w.IsFocused;

    public void BringToFront()
    {
        if (this.TryGetWindow(out var w))
        {
            w.BringToFront();
            w.RequestFocus = true;
        }
    }
}
