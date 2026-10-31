using System;
using System.Diagnostics.CodeAnalysis;
using Dalamud.Interface.Windowing;

namespace Umbra.WindowManager.Services.WindowManager;

public class TrackedWindow
{
    private readonly WeakReference<IWindow> windowRef;

    // Per-frame state cache (issue #64). Each *Frame field holds the WindowFrameClock frame its value was
    // computed on; 0 means "not cached". See InvalidateStateCache for what resets them.
    private int aliveFrame;
    private int openFrame;
    private int focusedFrame;
    private int manageableFrame;
    private bool aliveValue;
    private bool openValue;
    private bool focusedValue;
    private bool manageableValue;
    private bool hasConfirmedUi = true;

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

    /// <summary>Frame clock assigned by the owning <see cref="WindowManagerService"/>; <c>null</c> disables caching.</summary>
    internal WindowFrameClock? FrameClock { get; set; }

    /// <summary>
    /// Whether the state getters may be cached for a frame. Raw-ImGui windows opt out: their state is plain
    /// fields the monitor rewrites mid-frame, so there is nothing expensive to save and caching would only
    /// add staleness.
    /// </summary>
    protected virtual bool IsStateCacheable => true;

    /// <summary>The frame to stamp cached state with, or 0 when this read must not be cached.</summary>
    private int CacheFrame => this.IsStateCacheable ? this.FrameClock?.ActiveFrame ?? 0 : 0;

    /// <summary>
    /// Drops every cached value so the next read goes back to the window. Called by everything that writes
    /// state the getters depend on: the <see cref="IsOpen"/> setter, <see cref="IsMinimized"/>, and
    /// <see cref="BringToFront"/> (which changes focus). That makes a minimize or restore visible to the
    /// same frame's later reads.
    /// </summary>
    internal void InvalidateStateCache()
    {
        this.aliveFrame = 0;
        this.openFrame = 0;
        this.focusedFrame = 0;
        this.manageableFrame = 0;
    }

    public string WindowName { get; }
    public string CleanTitle { get; }
    public string Id { get; }
    public string DisplayTitle => !string.IsNullOrWhiteSpace(this.CleanTitle)
        ? this.CleanTitle
        : (!string.IsNullOrWhiteSpace(this.Id) ? this.Id : this.WindowName);

    private string? monogram;

    /// <summary>
    /// First non-whitespace character of <see cref="DisplayTitle"/>, upper-cased ("?" if there is none).
    /// <see cref="DisplayTitle"/> is derived from init-only values, so this is computed once on first use
    /// and the widget's per-tick icon fallback no longer allocates a string.
    /// </summary>
    public string Monogram => this.monogram ??= ComputeMonogram(this.DisplayTitle);

    internal static string ComputeMonogram(string title)
    {
        foreach (var ch in title)
        {
            if (!char.IsWhiteSpace(ch))
                return char.ToUpperInvariant(ch).ToString();
        }

        return "?";
    }

    public string Namespace { get; set; }

    private bool isMinimized;

    public bool IsMinimized
    {
        get => this.isMinimized;
        set
        {
            if (this.isMinimized == value) return;
            this.isMinimized = value;
            this.InvalidateStateCache();
        }
    }

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
    public bool IsAlive
    {
        get
        {
            var frame = this.CacheFrame;
            if (frame == 0) return this.EvaluateIsAlive();
            if (this.aliveFrame != frame)
            {
                this.aliveValue = this.EvaluateIsAlive();
                this.aliveFrame = frame;
            }

            return this.aliveValue;
        }
    }

    protected virtual bool EvaluateIsAlive() => this.TryGetWindow(out _);

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

    /// <summary>
    /// Whether the window is open and passes its draw conditions. The getter is cached for the current frame
    /// (see <see cref="WindowFrameClock"/>) so the owning plugin's <see cref="IWindow.DrawConditions"/> runs at
    /// most once per frame however many callers ask; the setter invalidates the cache so a minimize or
    /// restore is visible to the very next read.
    /// </summary>
    public bool IsOpen
    {
        get
        {
            var frame = this.CacheFrame;
            if (frame == 0) return this.EvaluateIsOpen();
            if (this.openFrame != frame)
            {
                this.openValue = this.EvaluateIsOpen();
                this.openFrame = frame;
            }

            return this.openValue;
        }
        set
        {
            this.WriteIsOpen(value);
            this.InvalidateStateCache();
        }
    }

    protected virtual bool EvaluateIsOpen() => this.TryGetWindow(out var w) && w.IsOpen && this.PassesDrawConditions;

    protected virtual void WriteIsOpen(bool value)
    {
        if (this.TryGetWindow(out var w))
            w.IsOpen = value;
    }

    /// <summary>
    /// Direct check of the underlying window's <see cref="IWindow.IsOpen"/> flag without evaluating
    /// <see cref="PassesDrawConditions"/>.
    /// </summary>
    public bool IsUnderlyingOpen => this.TryGetWindow(out var w) && w.IsOpen;

    public bool IsFocused
    {
        get
        {
            var frame = this.CacheFrame;
            if (frame == 0) return this.EvaluateIsFocused();
            if (this.focusedFrame != frame)
            {
                this.focusedValue = this.EvaluateIsFocused();
                this.focusedFrame = frame;
            }

            return this.focusedValue;
        }
    }

    protected virtual bool EvaluateIsFocused() => this.TryGetWindow(out var w) && w.IsFocused;

    /// <summary>
    /// Whether the window is confirmed to be rendered in the active ImGui context with positive dimensions
    /// and visual content. Defaults to true until ImGui context monitoring evaluates it.
    /// </summary>
    public bool HasConfirmedUi
    {
        get => this.hasConfirmedUi;
        set
        {
            if (this.hasConfirmedUi == value) return;
            this.hasConfirmedUi = value;
            this.manageableFrame = 0;   // IsManageable reads this; nothing else cached does
        }
    }

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
            var frame = this.CacheFrame;
            if (frame == 0) return this.EvaluateIsManageable();
            if (this.manageableFrame != frame)
            {
                this.manageableValue = this.EvaluateIsManageable();
                this.manageableFrame = frame;
            }

            return this.manageableValue;
        }
    }

    protected virtual bool EvaluateIsManageable()
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


    public virtual void BringToFront()
    {
        if (this.TryGetWindow(out var w))
        {
            w.BringToFront();
            w.RequestFocus = true;
        }

        this.InvalidateStateCache();
    }
}
