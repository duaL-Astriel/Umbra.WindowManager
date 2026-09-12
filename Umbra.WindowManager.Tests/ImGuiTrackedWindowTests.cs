using System.Numerics;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class ImGuiTrackedWindowTests
{
    private static ImGuiTrackedWindow MakeDrawn(string name = "Sonar##Main")
    {
        return new ImGuiTrackedWindow(name)
        {
            ObservedPos = new Vector2(100, 100),
            ObservedSize = new Vector2(400, 300),
            HasTitleBar = true,
            HasConfirmedUi = true,
            ObservedFocused = false,
        };
    }

    [Fact]
    public void Identity_ParsesTitleAndFlagsRaw()
    {
        var w = MakeDrawn("Sonar##SonarMain");
        Assert.Equal("Sonar", w.CleanTitle);
        Assert.True(w.IsRawImGui);
        Assert.False(w.TryGetWindow(out _)); // no IWindow
    }

    [Fact]
    public void IsAlive_TracksUnseenFrameThreshold()
    {
        var w = MakeDrawn();
        w.UnseenFrames = ImGuiTrackedWindow.MaxUnseenRawFrames;
        Assert.True(w.IsAlive);
        w.UnseenFrames = ImGuiTrackedWindow.MaxUnseenRawFrames + 1;
        Assert.False(w.IsAlive);
    }

    [Fact]
    public void IsOpen_FalseWhenMinimized_TrueWhenDrawn()
    {
        var w = MakeDrawn();
        Assert.True(w.IsOpen);
        w.IsMinimized = true;
        Assert.False(w.IsOpen);
    }

    [Fact]
    public void IsManageable_RequiresPositiveSizeAndTitleBar()
    {
        var w = MakeDrawn();
        Assert.True(w.IsManageable);

        w.ObservedSize = new Vector2(0, 300);
        Assert.False(w.IsManageable);

        w.ObservedSize = new Vector2(400, 300);
        w.HasTitleBar = false;
        Assert.False(w.IsManageable);
    }

    [Fact]
    public void IsManageable_RequiresUnseenFramesWithinGracePeriod()
    {
        var w = MakeDrawn();
        w.UnseenFrames = 5;
        Assert.True(w.IsManageable);

        w.UnseenFrames = 6;
        Assert.False(w.IsManageable);

        // Also applies to minimized raw windows: once closed (unseen > 5), no longer manageable
        w.UnseenFrames = 5;
        w.IsMinimized = true;
        Assert.True(w.IsManageable);

        w.UnseenFrames = 6;
        Assert.False(w.IsManageable);
    }

    [Fact]
    public void IsOpen_SetterIsNoOp()
    {
        var w = MakeDrawn();
        Assert.True(w.IsOpen);

        w.IsOpen = false;
        Assert.True(w.IsOpen); // no-op: foreign plugin owns draw loop
    }

    [Fact]
    public void IsFocused_ReflectsObservedFocus()
    {
        var w = MakeDrawn();
        Assert.False(w.IsFocused);
        w.ObservedFocused = true;
        Assert.True(w.IsFocused);
    }

    [Fact]
    public void ComputeFrameAction_Minimize_CapturesRestorePosAndHides()
    {
        var w = MakeDrawn();
        w.IsMinimized = true;
        var offScreen = new Vector2(-32000, -32000);

        var a1 = w.ComputeFrameAction(offScreen);
        Assert.Equal(RawFrameActionKind.HideOffScreen, a1.Kind);
        Assert.Equal(offScreen, a1.Position);
        Assert.Equal(new Vector2(100, 100), w.SavedRestorePos);

        // The plugin keeps drawing off-screen; the observed pos now reads the off-screen value, but the
        // saved restore pos must NOT be overwritten on later hide frames.
        w.ObservedPos = offScreen;
        var a2 = w.ComputeFrameAction(offScreen);
        Assert.Equal(RawFrameActionKind.HideOffScreen, a2.Kind);
        Assert.Equal(new Vector2(100, 100), w.SavedRestorePos);
    }

    [Fact]
    public void ComputeFrameAction_Restore_RepositionsToSavedPos()
    {
        var w = MakeDrawn();
        var offScreen = new Vector2(-32000, -32000);
        w.IsMinimized = true;
        w.ComputeFrameAction(offScreen);        // captures (100,100), marks minimized-last-frame

        w.IsMinimized = false;                  // service.Restore flips this
        var restore = w.ComputeFrameAction(offScreen);
        Assert.Equal(RawFrameActionKind.Restore, restore.Kind);
        Assert.Equal(new Vector2(100, 100), restore.Position);
        Assert.Null(w.SavedRestorePos);

        // Next frame there is nothing to do.
        Assert.Equal(RawFrameActionKind.None, w.ComputeFrameAction(offScreen).Kind);
    }

    [Fact]
    public void BringToFront_RequestsFocusOnNextFrame()
    {
        var w = MakeDrawn();
        w.BringToFront();
        var a = w.ComputeFrameAction(new Vector2(-32000, -32000));
        Assert.Equal(RawFrameActionKind.Focus, a.Kind);
        // Consumed once.
        Assert.Equal(RawFrameActionKind.None, w.ComputeFrameAction(new Vector2(-32000, -32000)).Kind);
    }

    [Fact]
    public void ResetOnDisable_RestoresPositionAndClearsMinimize()
    {
        var w = MakeDrawn();
        w.IsMinimized = true;
        w.ComputeFrameAction(new Vector2(-32000, -32000)); // captures (100,100)

        Assert.True(w.ResetOnDisable(out var pos));
        Assert.Equal(new Vector2(100, 100), pos);
        Assert.False(w.IsMinimized);
        Assert.Null(w.SavedRestorePos);

        // Nothing to restore the second time.
        Assert.False(w.ResetOnDisable(out _));
    }
}
