using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;
using Xunit.Abstractions;

namespace Umbra.WindowManager.Tests;

public class DebugWindowReproTests
{
    [Fact]
    public unsafe void OnDraw_FallbackWindow_NeverMarkedWriteAccessed()
    {
        var ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(ctx);
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(1920, 1080);
        io.DeltaTime = 1f / 60f;
        io.Fonts.Build();

        var service = new WindowManagerService();
        service.RawTrackingEnabled = true;
        var monitor = new ImGuiContextMonitor(service);

        ImGui.NewFrame();

        // Deliberately set WriteAccessed and NavWindow on the fallback window before OnDraw
        var fallback = ctx.CurrentWindow;
        Assert.True(fallback.IsFallbackWindow);
        fallback.WriteAccessed = true;
        ctx.NavWindow = fallback;

        monitor.OnDraw();

        // OnDraw must have unconditionally reset WriteAccessed and NavWindow
        Assert.False(fallback.WriteAccessed);
        Assert.True(ctx.NavWindow.IsNull);

        ImGui.EndFrame();

        // At EndFrame, because WriteAccessed was false, the fallback window must be inactive
        Assert.False(fallback.Active);

        ImGui.DestroyContext(ctx);
    }

    [Fact]
    public unsafe void OnDraw_FallbackAndInternalWindows_AreNeverTracked()
    {
        var ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(ctx);
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(1920, 1080);
        io.DeltaTime = 1f / 60f;
        io.Fonts.Build();

        var service = new WindowManagerService();
        service.RawTrackingEnabled = true;
        var monitor = new ImGuiContextMonitor(service);

        ImGui.NewFrame();
        // Regular plugin window
        ImGui.Begin("MyPluginWindow");
        ImGui.Text("Plugin Content");
        ImGui.End();

        // Internal popup/modal window
        ImGui.Begin("##InternalPopup");
        ImGui.Text("Popup Content");
        ImGui.End();

        monitor.OnDraw();

        var tracked = service.GetTrackedWindows();
        Assert.DoesNotContain(tracked, w => w.WindowName.StartsWith("Debug##", StringComparison.Ordinal));
        Assert.DoesNotContain(tracked, w => w.WindowName.StartsWith("##", StringComparison.Ordinal));
        Assert.Contains(tracked, w => w.WindowName == "MyPluginWindow");

        ImGui.EndFrame();
        ImGui.DestroyContext(ctx);
    }

    [Fact]
    public unsafe void TabSwitch_And_RawWindowHide_DebugWindowRemainsInactive()
    {
        var ctx = ImGui.CreateContext();
        ImGui.SetCurrentContext(ctx);
        var io = ImGui.GetIO();
        io.DisplaySize = new Vector2(1920, 1080);
        io.DeltaTime = 1f / 60f;
        io.Fonts.Build();

        var service = new WindowManagerService();
        service.RawTrackingEnabled = true;
        var tracker = GameWindowTracker.CreateForTest(service);
        var monitor = new ImGuiContextMonitor(service);

        // Frame 1: Normal frame with Sonar open
        ImGui.NewFrame();
        var fallback = ctx.CurrentWindow;
        Assert.True(fallback.IsFallbackWindow);
        ImGui.Begin("Sonar");
        ImGui.Text("Sonar Content");
        ImGui.End();
        monitor.OnDraw();
        ImGui.EndFrame();

        Assert.False(fallback.Active);

        // Frame 2: User switches tab in in-game window (causes cache invalidation)
        ImGui.NewFrame();
        ImGui.Begin("Sonar");
        ImGui.Text("Sonar Content");
        ImGui.End();

        tracker.HandleAddonEvent("PartyMemberList", 100, isSetupOrShow: true);
        tracker.HandleAddonEvent("FriendList", 101, isSetupOrShow: true);

        monitor.OnDraw();
        Assert.False(ctx.CurrentWindow.WriteAccessed);
        ImGui.EndFrame();

        Assert.False(fallback.Active);

        // Frame 3: User minimizes raw ImGui window
        ImGui.NewFrame();
        ImGui.Begin("Sonar");
        ImGui.Text("Sonar Content");
        ImGui.End();

        var twSonar = service.GetTrackedWindows().First(w => w.WindowName == "Sonar");
        service.Minimize(twSonar);

        monitor.OnDraw();
        Assert.False(ctx.CurrentWindow.WriteAccessed);
        ImGui.EndFrame();

        Assert.False(fallback.Active);

        // Frame 4: User restores raw ImGui window
        ImGui.NewFrame();
        service.Restore(twSonar);

        monitor.OnDraw();
        Assert.False(ctx.CurrentWindow.WriteAccessed);
        ImGui.EndFrame();

        Assert.False(fallback.Active);

        ImGui.DestroyContext(ctx);
    }
}
