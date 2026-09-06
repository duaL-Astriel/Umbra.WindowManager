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

    public bool IsEligibleWindow => this.IsManageable;

    /// <summary>
    /// Whether the underlying window passes its <see cref="IWindow.DrawConditions"/>.
    /// Returns <c>false</c> if draw conditions fail or throw an exception.
    /// </summary>
    public bool PassesDrawConditions
    {
        get
        {
            if (!this.TryGetWindow(out var w)) return false;
            try
            {
                return w.DrawConditions();
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsOpen
    {
        get => this.TryGetWindow(out var w) && w.IsOpen && this.PassesDrawConditions;
        set
        {
            if (this.TryGetWindow(out var w))
                w.IsOpen = value;
        }
    }

    /// <summary>
    /// Direct check of the underlying window's <see cref="IWindow.IsOpen"/> flag without evaluating
    /// <see cref="PassesDrawConditions"/>.
    /// </summary>
    public bool IsUnderlyingOpen => this.TryGetWindow(out var w) && w.IsOpen;

    public bool IsFocused => this.TryGetWindow(out var w) && w.IsFocused;

    /// <summary>
    /// Whether the window is confirmed to be rendered in the active ImGui context with positive dimensions
    /// and visual content. Defaults to true until ImGui context monitoring evaluates it.
    /// </summary>
    public bool HasConfirmedUi { get; set; } = true;

    /// <summary>
    /// Consecutive frames where this window was open but not observed in the active ImGui context.
    /// </summary>
    public int UnseenFrames { get; set; }

    /// <summary>
    /// Whether the window is an interactive, titled user-facing window suitable for management.
    /// Excludes frameless HUD overlays, headless monitors, zero-sized windows, and clickthrough windows.
    /// </summary>
    public bool IsManageable
    {
        get
        {
            if (!this.TryGetWindow(out var w)) return false;
            if (!this.HasConfirmedUi) return false;
            if (w.Size.HasValue && (w.Size.Value.X <= 0 || w.Size.Value.Y <= 0))
                return false;
            if (w.SizeConstraints.HasValue && (w.SizeConstraints.Value.MaximumSize.X <= 0 || w.SizeConstraints.Value.MaximumSize.Y <= 0))
                return false;
            if (w.Flags.HasFlag(Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoTitleBar) ||
                w.Flags.HasFlag(Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoDecoration) ||
                w.Flags.HasFlag(Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoInputs) ||
                (w.Flags & Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoMouseInputs) != 0)
                return false;
            if (w.IsClickthrough)
                return false;
            return true;
        }
    }


    public void BringToFront()
    {
        if (this.TryGetWindow(out var w))
        {
            w.BringToFront();
            w.RequestFocus = true;
        }
    }
}
