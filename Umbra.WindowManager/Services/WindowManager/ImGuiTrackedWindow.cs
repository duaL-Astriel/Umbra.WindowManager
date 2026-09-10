using System.Numerics;

namespace Umbra.WindowManager.Services.WindowManager;

public enum RawFrameActionKind
{
    None,
    HideOffScreen,
    Restore,
    Focus,
}

/// <summary>The single ImGui write the monitor should perform for a raw window this frame.</summary>
public readonly record struct RawFrameAction(RawFrameActionKind Kind, Vector2 Position);

/// <summary>
/// A tracked window with no <see cref="Dalamud.Interface.Windowing.IWindow"/> (a raw
/// <c>ImGui.Begin</c>/<c>ImGui.End</c> window, e.g. Sonar). Because we cannot stop the plugin's own draw
/// loop, minimize is best-effort: the monitor parks the window off-screen every frame while
/// <see cref="TrackedWindow.IsMinimized"/> is set, and repositions + focuses it on restore. State is fed
/// in per-frame by <see cref="ImGuiContextMonitor"/> (issue #38).
/// </summary>
public sealed class ImGuiTrackedWindow : TrackedWindow
{
    /// <summary>Frames a raw window may go unobserved in the ImGui context before it is considered dead.</summary>
    public const int MaxUnseenRawFrames = 30;

    public ImGuiTrackedWindow(string windowName) : base(windowName) { }

    // Observed state, written by the monitor each frame it sees the window.
    public Vector2 ObservedPos { get; set; }
    public Vector2 ObservedSize { get; set; }
    public bool ObservedFocused { get; set; }
    public bool HasTitleBar { get; set; } = true;

    // Intent / bookkeeping, mutated only through the methods below.
    public Vector2? SavedRestorePos { get; private set; }
    public bool PendingFocus { get; private set; }
    public bool WasMinimizedLastFrame { get; private set; }

    public override bool IsRawImGui => true;

    public override bool IsAlive => this.UnseenFrames <= MaxUnseenRawFrames;

    public override bool IsFocused => this.ObservedFocused;

    // Raw windows have no IWindow.DrawConditions.
    public override bool PassesDrawConditions => true;

    // "Open" for a raw window means it is currently drawing and not soft-hidden. The setter is a no-op:
    // we cannot flip a foreign plugin's own visibility flag.
    public override bool IsOpen
    {
        get => !this.IsMinimized && this.HasConfirmedUi;
        set { /* raw windows own their own draw loop; nothing to set */ }
    }

    public override bool IsManageable
    {
        get
        {
            if (!this.IsMinimized && !this.HasConfirmedUi) return false;
            if (this.ObservedSize.X <= 0 || this.ObservedSize.Y <= 0) return false;
            return this.HasTitleBar;
        }
    }

    public override void BringToFront() => this.PendingFocus = true;

    /// <summary>
    /// Decides the single ImGui write for this frame, given where "off-screen" is. Call once per frame
    /// after the observed state has been refreshed. Captures the on-screen position the first frame of a
    /// minimize so restore can put the window back.
    /// </summary>
    public RawFrameAction ComputeFrameAction(Vector2 offScreenPos)
    {
        if (this.IsMinimized)
        {
            this.SavedRestorePos ??= this.ObservedPos;
            this.WasMinimizedLastFrame = true;
            return new RawFrameAction(RawFrameActionKind.HideOffScreen, offScreenPos);
        }

        if (this.WasMinimizedLastFrame)
        {
            this.WasMinimizedLastFrame = false;
            var pos = this.SavedRestorePos ?? this.ObservedPos;
            this.SavedRestorePos = null;
            this.PendingFocus = false;
            return new RawFrameAction(RawFrameActionKind.Restore, pos);
        }

        if (this.PendingFocus)
        {
            this.PendingFocus = false;
            return new RawFrameAction(RawFrameActionKind.Focus, default);
        }

        return new RawFrameAction(RawFrameActionKind.None, default);
    }

    /// <summary>
    /// Clears minimize/hide state when the feature is turned off, returning the position the window should
    /// be moved back to (if it was parked off-screen). Ensures disabling never strands a window off-screen.
    /// </summary>
    public bool ResetOnDisable(out Vector2 pos)
    {
        var wasParked = this.SavedRestorePos is { };
        pos = this.SavedRestorePos ?? this.ObservedPos;
        this.SavedRestorePos = null;
        this.WasMinimizedLastFrame = false;
        this.PendingFocus = false;
        this.IsMinimized = false;
        return wasParked;
    }
}
