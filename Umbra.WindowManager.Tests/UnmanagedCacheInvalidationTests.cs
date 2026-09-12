using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

/// <summary>
/// Cover for replacing the unconditional 60-frame flush of the unmanaged-window cache with invalidation
/// driven by <see cref="WindowManagerService.WindowRegistrationGeneration"/> (issue #51). The flush made
/// every non-managed ImGui window re-run the window-system lookup on one frame, roughly once a second.
/// </summary>
public class UnmanagedCacheInvalidationTests
{
    private class DummyWindow : Window
    {
        public DummyWindow(string name) : base(name) { }
        public override void Draw() { }
    }

    private static int FrameCounter(ImGuiContextMonitor monitor) =>
        (int)typeof(ImGuiContextMonitor)
            .GetField("frameCounter", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(monitor)!;

    private static void AdvanceFrames(ImGuiContextMonitor monitor, int frames)
    {
        var field = typeof(ImGuiContextMonitor)
            .GetField("frameCounter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        field.SetValue(monitor, FrameCounter(monitor) + frames);
    }

    [Fact]
    public void ShouldClearUnmanagedCache_ClearsOnTheFirstGenerationItSees()
    {
        // Regression: seeding the rate limiter with int.MinValue made `frameCounter - lastClearFrame`
        // overflow to a negative difference, suppressing this first clear -- and every clear after it.
        var monitor = new ImGuiContextMonitor(new WindowManagerService());

        AdvanceFrames(monitor, 1);
        Assert.True(monitor.ShouldClearUnmanagedCache(0));
    }

    [Fact]
    public void ShouldClearUnmanagedCache_IsFalseWhileNothingIsRegistered()
    {
        // The whole point of the change: with a steady window set this never fires, so the periodic
        // hitch disappears entirely instead of merely getting cheaper.
        var monitor = new ImGuiContextMonitor(new WindowManagerService());

        AdvanceFrames(monitor, 1);
        Assert.True(monitor.ShouldClearUnmanagedCache(0));

        for (var frame = 0; frame < 600; frame++)
        {
            AdvanceFrames(monitor, 1);
            Assert.False(monitor.ShouldClearUnmanagedCache(0));
        }
    }

    [Fact]
    public void ShouldClearUnmanagedCache_ClearsWhenTheRegistrationGenerationChanges()
    {
        var monitor = new ImGuiContextMonitor(new WindowManagerService());

        AdvanceFrames(monitor, 1);
        Assert.True(monitor.ShouldClearUnmanagedCache(0));

        AdvanceFrames(monitor, ImGuiContextMonitor.MinFramesBetweenUnmanagedClears);
        Assert.True(monitor.ShouldClearUnmanagedCache(1));
    }

    [Fact]
    public void ShouldClearUnmanagedCache_RateLimitsBackToBackGenerationChanges()
    {
        // Bounds the pathological case (a plugin re-registering a window every frame) to no more often
        // than the old timer, so this can never be worse than the behaviour it replaces.
        var monitor = new ImGuiContextMonitor(new WindowManagerService());

        AdvanceFrames(monitor, 1);
        Assert.True(monitor.ShouldClearUnmanagedCache(0));

        var generation = 1;
        for (var frame = 1; frame < ImGuiContextMonitor.MinFramesBetweenUnmanagedClears; frame++)
        {
            AdvanceFrames(monitor, 1);
            Assert.False(monitor.ShouldClearUnmanagedCache(generation++));
        }

        AdvanceFrames(monitor, 1);
        Assert.True(monitor.ShouldClearUnmanagedCache(generation));
    }

    [Fact]
    public void WindowRegistrationGeneration_AdvancesOnlyForGenuinelyNewRegistrations()
    {
        var service = new WindowManagerService();
        var window = new DummyWindow("GenerationWindow");

        var initial = service.WindowRegistrationGeneration;

        service.RegisterWindow(window);
        var afterFirst = service.WindowRegistrationGeneration;
        Assert.True(afterFirst > initial, "registering a new window must advance the generation");

        // Re-registering the same instance is what the 250 ms tick does for every known window; it must
        // not advance the generation, or the cache would be cleared four times a second for no reason.
        for (var i = 0; i < 10; i++)
            service.RegisterWindow(window);

        Assert.Equal(afterFirst, service.WindowRegistrationGeneration);

        // A different instance under the same name is a plugin reload, and does count as a change.
        service.RegisterWindow(new DummyWindow("GenerationWindow"));
        Assert.True(service.WindowRegistrationGeneration > afterFirst);
    }

    [Fact]
    public void ShouldClearUnmanagedCache_ClearsWhenTheCacheExceedsItsCeiling()
    {
        // The flush used to bound the cache implicitly. Nothing else does now, and ImGui synthesises
        // unbounded popup/tooltip ids, so the ceiling is the one unconditional clear left.
        var monitor = new ImGuiContextMonitor(new WindowManagerService());
        AdvanceFrames(monitor, 1);
        Assert.True(monitor.ShouldClearUnmanagedCache(0));

        var names = (System.Collections.Generic.HashSet<string>)typeof(ImGuiContextMonitor)
            .GetField("unmanagedWindowNames", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(monitor)!;

        for (var i = 0; i <= ImGuiContextMonitor.MaxUnmanagedWindowNames; i++)
            names.Add($"##popup-{i}");

        // Same generation, and well inside the rate-limit window: only the ceiling can trigger this.
        AdvanceFrames(monitor, 1);
        Assert.True(monitor.ShouldClearUnmanagedCache(0));
    }

    [Theory]
    // A normal, sized, titled, decorated window: both gates accept.
    [InlineData(ImGuiWindowFlags.None, 200f, 150f, "Plugin Window", true, true)]
    // Zero size, popup, tooltip, child, no-title-bar: the cheap gate already rejects.
    [InlineData(ImGuiWindowFlags.None, 0f, 150f, "Plugin Window", true, false)]
    [InlineData(ImGuiWindowFlags.Popup, 200f, 150f, "Plugin Window", true, false)]
    [InlineData(ImGuiWindowFlags.Tooltip, 200f, 150f, "Plugin Window", true, false)]
    [InlineData(ImGuiWindowFlags.ChildWindow, 200f, 150f, "Plugin Window", true, false)]
    [InlineData(ImGuiWindowFlags.NoTitleBar, 200f, 150f, "Plugin Window", true, false)]
    // ImGui-internal name prefixes.
    [InlineData(ImGuiWindowFlags.None, 200f, 150f, "##hidden", true, false)]
    [InlineData(ImGuiWindowFlags.None, 200f, 150f, "Debug##Default", true, false)]
    // Content-dependent: only ShouldTrack knows, so the cheap gate must still let it through.
    [InlineData(ImGuiWindowFlags.None, 200f, 150f, "Plugin Window", false, true)]
    // ID-only title ("###..." starts with "##"), so the cheap gate already rejects it -- as does ShouldTrack,
    // whose clean title comes out empty. Both agree, which is what matters.
    [InlineData(ImGuiWindowFlags.None, 200f, 150f, "###orchestrion_miniplayer", true, false)]
    public void CouldTrack_IsANecessaryConditionForShouldTrack(
        ImGuiWindowFlags flags, float width, float height, string name, bool hasContent, bool expectedCouldTrack)
    {
        var size = new Vector2(width, height);
        var cleanTitle = WindowInfoHelper.GetCleanTitle(name);

        var couldTrack = ImGuiWindowClassifier.CouldTrack(flags, size, name);
        Assert.Equal(expectedCouldTrack, couldTrack);

        // The contract that makes the cheap pre-gate safe: it may never reject a window ShouldTrack accepts.
        if (ImGuiWindowClassifier.ShouldTrack(flags, size, name, cleanTitle, hasContent))
            Assert.True(couldTrack, "CouldTrack rejected a window that ShouldTrack accepts");
    }

    [Fact]
    public void CouldTrack_SpanAndStringOverloadsAgree()
    {
        foreach (var name in new[] { "Plugin Window", "##hidden", "Debug##Default", "###id-only", "" })
        {
            Assert.Equal(
                ImGuiWindowClassifier.CouldTrack(ImGuiWindowFlags.None, new Vector2(200f, 150f), name),
                ImGuiWindowClassifier.CouldTrack(ImGuiWindowFlags.None, new Vector2(200f, 150f), name.AsSpan()));
        }
    }
}
