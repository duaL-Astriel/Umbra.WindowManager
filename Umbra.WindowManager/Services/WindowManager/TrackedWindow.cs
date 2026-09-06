using System;
using System.Diagnostics.CodeAnalysis;
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
        this.Id = WindowInfoHelper.GetWindowId(window.WindowName);
        this.Namespace = window.Namespace ?? string.Empty;
    }

    public string WindowName { get; }
    public string CleanTitle { get; }
    public string Id { get; }
    public string DisplayTitle => !string.IsNullOrWhiteSpace(this.CleanTitle)
        ? this.CleanTitle
        : (!string.IsNullOrWhiteSpace(this.Id) ? this.Id : this.WindowName);
    public string Namespace { get; set; }
    public bool IsMinimized { get; set; }
    public string? DockGroupKey { get; set; }

    /// <summary>
    /// Internal name of the Dalamud plugin that owns this window, resolved by
    /// <see cref="DalamudWindowTracker"/> during discovery. <c>null</c> until resolved.
    /// </summary>
    public string? PluginInternalName { get; set; }

    /// <summary>
    /// Raw bytes of the owning plugin's icon (e.g. <c>images/icon.png</c>), if one was found on disk.
    /// Shared by reference across all windows of the same plugin. <c>null</c> when no icon is available;
    /// consumers should fall back to a text monogram.
    /// </summary>
    public byte[]? IconBytes { get; set; }

    public bool TryGetWindow([NotNullWhen(true)] out IWindow? window) => this.windowRef.TryGetTarget(out window);

    public bool IsEligibleWindow =>
        this.TryGetWindow(out var w)
        && !w.Flags.HasFlag(Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoTitleBar)
        && !w.Flags.HasFlag(Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoDecoration)
        && !w.Flags.HasFlag(Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoInputs)
        && !w.IsClickthrough;

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
