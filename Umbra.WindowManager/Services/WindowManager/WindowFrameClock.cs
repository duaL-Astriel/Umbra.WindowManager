using System;

namespace Umbra.WindowManager.Services.WindowManager;

/// <summary>
/// Frame counter shared by a <see cref="WindowManagerService"/> and the <see cref="TrackedWindow"/>s it owns.
/// <see cref="ImGuiContextMonitor"/> advances it once at the start of every draw frame; tracked windows stamp
/// their cached state with <see cref="ActiveFrame"/> so a value is computed at most once per frame (issue #64).
/// Only touched from the framework thread.
/// </summary>
internal sealed class WindowFrameClock
{
    /// <summary>
    /// Longest gap between two <see cref="Advance"/> calls for which the frame number is still trusted. If the
    /// monitor stops drawing (hook not firing, game stalled) the cache must not freeze on the last frame, so
    /// past this age <see cref="ActiveFrame"/> reports 0 and every read goes straight to the window.
    /// </summary>
    internal const long MaxFrameAgeMs = 250;

    private int frame;
    private long lastAdvanceTicks;

    /// <summary>
    /// The current frame number, or 0 when caching is not in effect: no frame has begun yet (e.g. a unit test
    /// that never runs the monitor) or the last one was too long ago to trust.
    /// </summary>
    public int ActiveFrame =>
        this.frame != 0 && Environment.TickCount64 - this.lastAdvanceTicks <= MaxFrameAgeMs ? this.frame : 0;

    public void Advance()
    {
        // Skip 0, which is reserved for "no frame in progress".
        this.frame = this.frame == int.MaxValue ? 1 : this.frame + 1;
        this.lastAdvanceTicks = Environment.TickCount64;
    }
}
