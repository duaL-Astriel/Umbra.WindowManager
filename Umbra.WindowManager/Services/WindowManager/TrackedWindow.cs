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

    /// <summary>
    /// Constructs a tracked window that is NOT backed by an <see cref="IWindow"/> (a raw-ImGui window).
    /// The weak reference is left with a null target, so every <see cref="IWindow"/>-coupled path
    /// (<see cref="TryGetWindow"/>, button injection, dock groups) is inert; <see cref="ImGuiTrackedWindow"/>
    /// overrides the capability members to work from observed ImGui state instead (issue #38).
    /// </summary>
    protected TrackedWindow(string windowName)
    {
        this.windowRef = new WeakReference<IWindow>(null!);
        this.WindowName = windowName;
        this.CleanTitle = WindowInfoHelper.GetCleanTitle(windowName);
        this.Id = WindowInfoHelper.GetWindowId(windowName);
        this.Namespace = string.Empty;
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
    /// Whether this tracked entry still corresponds to a live window. For <see cref="IWindow"/>-backed
    /// windows this is "the weak reference is still alive"; raw-ImGui windows override it with an
    /// unseen-frame threshold (they have no reference to keep alive). Callers prune on this signal.
    /// </summary>
    public virtual bool IsAlive => this.TryGetWindow(out _);

    /// <summary>Whether this is a raw-ImGui window (no <see cref="IWindow"/>). Used for best-effort UI copy.</summary>
    public virtual bool IsRawImGui => false;

    /// <summary>
    /// Whether the underlying window passes its <see cref="IWindow.DrawConditions"/>.
    /// Returns <c>false</c> if draw conditions fail or throw an exception.
    /// </summary>
    public virtual bool PassesDrawConditions
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

    public virtual bool IsOpen
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

    public virtual bool IsFocused => this.TryGetWindow(out var w) && w.IsFocused;

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
    public virtual bool IsManageable
    {
        get
        {
            if (!this.TryGetWindow(out var w)) return false;
            if (!this.IsMinimized && !this.HasConfirmedUi) return false;
            if (w.Size.HasValue && (w.Size.Value.X <= 0 || w.Size.Value.Y <= 0))
                return false;
            // NOTE: a zero MaximumSize is Dalamud/ImGui's "no maximum constraint" sentinel, not a
            // zero-sized window -- windows that set only a MinimumSize (e.g. the Dalamud Plugin Installer,
            // 830x570) leave MaximumSize at (0,0) and must still be managed (issue #36). Degenerate windows
            // are already excluded by the actual-Size check above and by HasConfirmedUi (the
            // ImGuiContextMonitor only confirms UI when the window draws with positive dimensions), so no
            // MaximumSize guard is needed here.
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


    public virtual void BringToFront()
    {
        if (this.TryGetWindow(out var w))
        {
            w.BringToFront();
            w.RequestFocus = true;
        }
    }
}
