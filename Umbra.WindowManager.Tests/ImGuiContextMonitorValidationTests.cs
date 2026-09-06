using System.Numerics;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class ImGuiContextMonitorValidationTests
{
    [Theory]
    [InlineData(100f, 100f, true)]
    [InlineData(1f, 1f, true)]
    [InlineData(0f, 100f, false)]
    [InlineData(100f, 0f, false)]
    [InlineData(0f, 0f, false)]
    [InlineData(-10f, 50f, false)]
    [InlineData(50f, -10f, false)]
    public void ValidateWindowDimensions_EvaluatesDimensionsCorrectly(float x, float y, bool expected)
    {
        var size = new Vector2(x, y);
        Assert.Equal(expected, ImGuiContextMonitor.ValidateWindowDimensions(size));
    }

    [Theory]
    [InlineData(10f, 10f, 0, true)]
    [InlineData(10f, 0f, 0, true)]
    [InlineData(0f, 10f, 0, true)]
    [InlineData(0f, 0f, 3, true)]
    [InlineData(0f, 0f, 2, false)]
    [InlineData(0f, 0f, 1, false)]
    [InlineData(0f, 0f, 0, false)]
    [InlineData(-1f, -1f, 0, false)]
    public void ValidateWindowContent_EvaluatesContentAndDrawCmdsCorrectly(float cx, float cy, int drawCmds, bool expected)
    {
        var contentSize = new Vector2(cx, cy);
        Assert.Equal(expected, ImGuiContextMonitor.ValidateWindowContent(contentSize, drawCmds));
    }

    [Fact]
    public void ImGuiContextMonitor_ConstructsWithTracker()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);
        var monitor = new ImGuiContextMonitor(service, tracker);
        Assert.NotNull(monitor);
    }

    [Theory]
    // The Dalamud ImGui binding reports ImGuiWindow.DockNode as null from our OnDraw hook even for
    // docked windows, so membership is derived from the persistent DockId plus DockNodeIsVisible instead
    // (issue #25). A window currently bound to a visible dock node has a nonzero DockId and is node-visible.
    [InlineData(5u, true, true)]
    // A previously-docked, now-floating window retains its old DockId as a backup but is no longer
    // node-visible, so it must not be grouped with the windows still in that node.
    [InlineData(5u, false, false)]
    // A window that was never docked has DockId 0.
    [InlineData(0u, true, false)]
    [InlineData(0u, false, false)]
    public void IsWindowDocked_EvaluatesFromDockIdAndNodeVisibility(uint dockId, bool dockNodeVisible, bool expected)
    {
        Assert.Equal(expected, ImGuiContextMonitor.IsWindowDocked(dockId, dockNodeVisible));
    }

    [Fact]
    public void WithWindowMenuButtonSuppressed_SetsInternalNoWindowMenuButtonFlag()
    {
        // The down-arrow window-menu button on a docked tab group is hidden via the internal
        // NoWindowMenuButton dock-node flag (issue #25).
        var result = ImGuiContextMonitor.WithWindowMenuButtonSuppressed(Dalamud.Bindings.ImGui.ImGuiDockNodeFlags.None);

        Assert.True(((long)result & (long)Dalamud.Bindings.ImGui.ImGuiDockNodeFlagsPrivate.NoWindowMenuButton) != 0);
    }

    [Fact]
    public void WithWindowMenuButtonSuppressed_PreservesExistingFlagsAndIsIdempotent()
    {
        var withExisting = Dalamud.Bindings.ImGui.ImGuiDockNodeFlags.NoResize;
        var once = ImGuiContextMonitor.WithWindowMenuButtonSuppressed(withExisting);
        var twice = ImGuiContextMonitor.WithWindowMenuButtonSuppressed(once);

        // Existing flags survive, and re-applying does not change the result.
        Assert.True((once & Dalamud.Bindings.ImGui.ImGuiDockNodeFlags.NoResize) != 0);
        Assert.Equal(once, twice);
    }
}
