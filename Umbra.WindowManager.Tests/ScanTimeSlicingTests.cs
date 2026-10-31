using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Interface.Windowing;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

/// <summary>
/// Cover for issue #62: the 2 s plugin discovery pass is spread across frames in ~1 ms slices instead of one
/// 8-10 ms block on the framework thread. These drive a pass tick by tick with a zero budget (one step per
/// slice), which makes the slicing deterministic without depending on wall-clock timings.
/// </summary>
public class ScanTimeSlicingTests
{
    private class DummyWindow : Window
    {
        public DummyWindow(string name) : base(name) { }
        public override void Draw() { }
    }

    private class FakeManifest
    {
        public string? InternalName { get; set; }
        public string? IconUrl { get; set; }
        public string? Dip17Channel { get; set; }
    }

    private class FakeLocalPlugin
    {
        public FakeManifest? Manifest { get; set; }
        public FileInfo? DllFile { get; set; }
        internal object? instance;
    }

    private class ThrowingLocalPlugin
    {
        public FakeManifest Manifest => throw new InvalidOperationException("boom");
        internal object? instance = new object();
    }

    private class PluginHost
    {
        public WindowSystem Sys { get; }
        public PluginHost(WindowSystem ws) => this.Sys = ws;
    }

    private static string WindowName(string plugin) => plugin + "Window";

    private static FakeLocalPlugin MakePlugin(string name)
    {
        var ws = new WindowSystem(name + "Sys");
        ws.AddWindow(new DummyWindow(WindowName(name)));
        return new FakeLocalPlugin
        {
            Manifest = new FakeManifest { InternalName = name },
            instance = new PluginHost(ws),
        };
    }

    private static (DalamudWindowTracker Tracker, WindowManagerService Service) NewTracker()
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service) { ScanSliceBudget = TimeSpan.Zero };
        return (tracker, service);
    }

    private static byte[]? IconOf(WindowManagerService service, string plugin) =>
        service.GetTrackedWindows().Single(t => t.WindowName == WindowName(plugin)).IconBytes;

    private static HashSet<string> TrackedNames(WindowManagerService service) =>
        service.GetTrackedWindows().Select(t => t.WindowName).ToHashSet();

    [Fact]
    public void Pass_SpreadAcrossTicks_StillDiscoversEveryPluginsWindows()
    {
        var (tracker, service) = NewTracker();
        var plugins = Enumerable.Range(0, 5).Select(i => MakePlugin("Plugin" + i)).ToList();

        tracker.BeginScanPass(plugins, null, null);

        // A zero budget still runs one step per tick, so progress is guaranteed but never batched.
        var ticks = 0;
        while (tracker.IsScanPassActive)
        {
            var before = TrackedNames(service).Count;
            tracker.ContinueScan();
            ticks++;

            Assert.True(TrackedNames(service).Count - before <= 1, "a zero-budget slice must scan at most one plugin");
            Assert.True(ticks <= plugins.Count, "the pass must finish in one tick per plugin");
        }

        Assert.Equal(plugins.Count, ticks);
        for (var i = 0; i < plugins.Count; i++)
            Assert.Contains(WindowName("Plugin" + i), TrackedNames(service));
    }

    [Fact]
    public void Pass_RunsTailStepsAfterPlugins_AndCountsThemAsSteps()
    {
        var (tracker, _) = NewTracker();
        var order = new List<string>();
        var plugins = new[] { MakePlugin("A"), MakePlugin("B") };

        tracker.BeginScanPass(plugins, null, new Action[] { () => order.Add("core"), () => order.Add("umbra") });

        Assert.False(tracker.AdvanceScanPass(TimeSpan.Zero));
        Assert.False(tracker.AdvanceScanPass(TimeSpan.Zero));
        Assert.Empty(order);
        Assert.False(tracker.AdvanceScanPass(TimeSpan.Zero));
        Assert.Equal(new[] { "core" }, order);
        Assert.True(tracker.AdvanceScanPass(TimeSpan.Zero));
        Assert.Equal(new[] { "core", "umbra" }, order);
        Assert.False(tracker.IsScanPassActive);
    }

    [Fact]
    public void Pass_WithGenerousBudget_FinishesInOneTick()
    {
        var (tracker, service) = NewTracker();
        tracker.ScanSliceBudget = TimeSpan.FromSeconds(30);
        var plugins = Enumerable.Range(0, 5).Select(i => MakePlugin("Plugin" + i)).ToList();

        tracker.BeginScanPass(plugins, null, null);
        tracker.ContinueScan();

        Assert.False(tracker.IsScanPassActive);
        Assert.Equal(5, service.GetTrackedWindows().Count);
    }

    [Fact]
    public void UnloadDetection_OnlyFiresOnceThePassHasVisitedEveryPlugin()
    {
        var (tracker, service) = NewTracker();
        var a = MakePlugin("PluginA");
        var b = MakePlugin("PluginB");
        var c = MakePlugin("PluginC");
        tracker.ScanInstalledPlugins(new[] { a, b, c }, null);
        Assert.Equal(3, service.GetTrackedWindows().Count);

        var reloaded = 0;
        tracker.PluginReloaded += () => reloaded++;

        // PluginA is gone from the installed list. It is absent from seenPlugins from the very first step,
        // but that only means something once every remaining plugin has been visited.
        tracker.BeginScanPass(new[] { b, c }, null, null);

        Assert.False(tracker.AdvanceScanPass(TimeSpan.Zero));
        Assert.Contains(WindowName("PluginA"), TrackedNames(service));
        Assert.Equal(0, reloaded);

        Assert.True(tracker.AdvanceScanPass(TimeSpan.Zero));
        Assert.DoesNotContain(WindowName("PluginA"), TrackedNames(service));
        Assert.Contains(WindowName("PluginB"), TrackedNames(service));
        Assert.Contains(WindowName("PluginC"), TrackedNames(service));
        Assert.Equal(1, reloaded);
    }

    [Fact]
    public void UnloadDetection_DoesNotUntrackAPluginNotYetReachedByTheCursor()
    {
        var (tracker, service) = NewTracker();
        var plugins = new[] { MakePlugin("PluginA"), MakePlugin("PluginB"), MakePlugin("PluginC") };
        tracker.ScanInstalledPlugins(plugins, null);

        // Mid-pass, PluginC has not been visited yet and is therefore not in seenPlugins -- it must stay tracked.
        tracker.BeginScanPass(plugins, null, null);
        tracker.AdvanceScanPass(TimeSpan.Zero);

        Assert.Equal(3, service.GetTrackedWindows().Count);
    }

    [Fact]
    public void Pass_UsesASnapshotOfThePluginListTakenWhenItBegins()
    {
        var (tracker, service) = NewTracker();
        var list = new List<FakeLocalPlugin> { MakePlugin("PluginA"), MakePlugin("PluginB") };

        tracker.BeginScanPass(list, null, null);
        tracker.AdvanceScanPass(TimeSpan.Zero);

        // The installed list changes between frames: neither a skipped nor a repeated plugin, nor the newcomer.
        list.RemoveAt(0);
        list.Add(MakePlugin("PluginC"));

        Assert.True(tracker.AdvanceScanPass(TimeSpan.Zero));

        var names = TrackedNames(service);
        Assert.Contains(WindowName("PluginA"), names);
        Assert.Contains(WindowName("PluginB"), names);
        Assert.DoesNotContain(WindowName("PluginC"), names);
    }

    [Fact]
    public void FinishedPass_ReleasesItsSnapshot_SoUnloadedPluginsAreNotPinned()
    {
        var (tracker, _) = NewTracker();
        var weak = ScanAndReturnWeakRefToPluginHost(tracker);

        for (var attempt = 0; attempt < 20 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.IsAlive, "an idle tracker is holding on to a scanned plugin instance");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ScanAndReturnWeakRefToPluginHost(DalamudWindowTracker tracker)
    {
        var plugin = new FakeLocalPlugin { Manifest = new FakeManifest { InternalName = "Pinned" } };
        var host = new object();
        plugin.instance = host;
        tracker.ScanInstalledPlugins(new[] { plugin }, null);

        // Unload it again so the only remaining path to the instance would be a leaked snapshot.
        plugin.instance = null;
        tracker.ScanInstalledPlugins(new[] { plugin }, null);
        return new WeakReference(host);
    }

    [Fact]
    public void ThrowingPlugin_DoesNotStarveLaterPlugins_AndSkipsUnloadDetectionForThatPass()
    {
        var (tracker, service) = NewTracker();
        var a = MakePlugin("PluginA");
        var b = MakePlugin("PluginB");
        tracker.ScanInstalledPlugins(new[] { a, b }, null);

        var late = MakePlugin("PluginLate");
        // PluginA is absent this pass, but a throwing step means we cannot trust seenPlugins.
        tracker.ScanInstalledPlugins(new object[] { new ThrowingLocalPlugin(), b, late }, null);

        var names = TrackedNames(service);
        Assert.Contains(WindowName("PluginLate"), names);
        Assert.Contains(WindowName("PluginA"), names);
    }

    [Fact]
    public void ChangeEvents_OnlyRequestAPass_TheyDoNotScanSynchronously()
    {
        var (tracker, _) = NewTracker();
        var pending = typeof(DalamudWindowTracker).GetField("scanPending", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Equal(0, (int)pending.GetValue(tracker)!);

        var onChanged = typeof(DalamudWindowTracker).GetMethod("OnInstalledPluginsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        onChanged.Invoke(tracker, null);

        Assert.Equal(1, (int)pending.GetValue(tracker)!);

        // The next frame's tick consumes the request. Dalamud is not available here, so no pass starts, but the
        // request must not be re-probed every frame afterwards.
        tracker.ContinueScan();
        Assert.Equal(0, (int)pending.GetValue(tracker)!);
        Assert.False(tracker.IsScanPassActive);
    }

    [Fact]
    public void ChangeEventDuringAPass_DoesNotRestartIt_ButIsKeptForAfterwards()
    {
        var (tracker, _) = NewTracker();
        var plugins = new[] { MakePlugin("A"), MakePlugin("B") };
        var pending = typeof(DalamudWindowTracker).GetField("scanPending", BindingFlags.NonPublic | BindingFlags.Instance)!;

        tracker.BeginScanPass(plugins, null, null);
        tracker.ContinueScan();

        var onChanged = typeof(DalamudWindowTracker).GetMethod("OnActivePluginsChanged", BindingFlags.NonPublic | BindingFlags.Instance)!;
        onChanged.Invoke(tracker, new object?[] { null });

        // The running pass carries on from its cursor...
        Assert.True(tracker.IsScanPassActive);
        tracker.ContinueScan();
        Assert.False(tracker.IsScanPassActive);

        // ...and the request survives it (the zero budget is spent, so the follow-up waits for the next frame).
        Assert.Equal(1, (int)pending.GetValue(tracker)!);

        tracker.ContinueScan();
        Assert.Equal(0, (int)pending.GetValue(tracker)!);
    }

    [Fact]
    public void ContinueScan_IsANoOp_WhenIdleAndNothingRequested()
    {
        var (tracker, _) = NewTracker();

        tracker.ContinueScan();

        Assert.False(tracker.IsScanPassActive);
    }

    // ---- icon disk lookups (issue #62 part 2) ----

    [Fact]
    public void NegativeIconDiskLookup_IsNotRepeatedOnEveryPass()
    {
        var (tracker, _) = NewTracker();
        var name = "NoIconYet_" + Guid.NewGuid().ToString("N");
        var tempDir = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(tempDir);
        var plugin = new FakeLocalPlugin
        {
            Manifest = new FakeManifest { InternalName = name },
            DllFile = new FileInfo(Path.Combine(tempDir, "plugin.dll")),
        };
        var cachedPath = DalamudWindowTracker.GetCachedIconPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);

        try
        {
            Assert.Null(tracker.ResolvePluginContext(plugin).IconBytes);

            // If the disk were consulted again, this file would be found. It must not be: the miss is remembered.
            var bytes = new byte[] { 1, 2, 3 };
            File.WriteAllBytes(cachedPath, bytes);
            Assert.Null(tracker.ResolvePluginContext(plugin).IconBytes);
            Assert.Null(tracker.ResolvePluginContext(plugin).IconBytes);

            // A finished download invalidates the memo, and the icon is then picked up.
            tracker.InvalidateIconDiskLookup(name);
            Assert.Equal(bytes, tracker.ResolvePluginContext(plugin).IconBytes);
        }
        finally
        {
            if (File.Exists(cachedPath)) File.Delete(cachedPath);
            try { Directory.Delete(tempDir, true); } catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public void NegativeIconDiskLookup_IsForgotten_WhenThePluginReloads()
    {
        var (tracker, service) = NewTracker();
        var name = "ReloadedNoIcon_" + Guid.NewGuid().ToString("N");
        var plugin = MakePlugin(name);
        plugin.Manifest!.InternalName = name;
        var cachedPath = DalamudWindowTracker.GetCachedIconPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);

        try
        {
            tracker.ScanInstalledPlugins(new[] { plugin }, null);

            var bytes = new byte[] { 4, 5, 6 };
            File.WriteAllBytes(cachedPath, bytes);

            // Same instance, next pass: still the remembered miss.
            tracker.ScanInstalledPlugins(new[] { plugin }, null);
            Assert.Null(IconOf(service, name));

            // Reload: the old state (including the disk miss) is dropped and the disk is consulted afresh.
            var ws = new WindowSystem(name + "Sys2");
            ws.AddWindow(new DummyWindow(WindowName(name)));
            plugin.instance = new PluginHost(ws);
            tracker.ScanInstalledPlugins(new[] { plugin }, null);

            Assert.Equal(bytes, IconOf(service, name));
        }
        finally
        {
            if (File.Exists(cachedPath)) File.Delete(cachedPath);
        }
    }

    [Fact]
    public void ResolvedIcon_IsReadFromDiskOnlyOnce()
    {
        var (tracker, _) = NewTracker();
        var name = "HasIcon_" + Guid.NewGuid().ToString("N");
        var plugin = new FakeLocalPlugin { Manifest = new FakeManifest { InternalName = name } };
        var cachedPath = DalamudWindowTracker.GetCachedIconPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
        var bytes = new byte[] { 7, 8, 9 };
        File.WriteAllBytes(cachedPath, bytes);

        try
        {
            Assert.Equal(bytes, tracker.ResolvePluginContext(plugin).IconBytes);

            // Positive hits come from the in-memory cache: deleting the file changes nothing.
            File.Delete(cachedPath);
            Assert.Equal(bytes, tracker.ResolvePluginContext(plugin).IconBytes);
        }
        finally
        {
            if (File.Exists(cachedPath)) File.Delete(cachedPath);
        }
    }

    // ---- reflection lookup caching (issue #62 part 3) ----

    [Fact]
    public void PluginAccessPlans_AreBuiltOncePerType_NotOncePerScan()
    {
        var (tracker, _) = NewTracker();
        var plugins = Enumerable.Range(0, 4).Select(i => MakePlugin("PlanPlugin" + i)).ToList();

        tracker.ScanInstalledPlugins(plugins, null);
        var afterFirst = DalamudWindowTracker.PluginAccessPlansBuilt;

        for (var i = 0; i < 10; i++)
            tracker.ScanInstalledPlugins(plugins, null);

        Assert.Equal(afterFirst, DalamudWindowTracker.PluginAccessPlansBuilt);
    }

    [Fact]
    public void PluginAccessPlanCaches_AreWeaklyKeyed()
    {
        // Same constraint as TypeScanCache: a strong Dictionary<Type,_> would pin plugin types and block unload.
        foreach (var fieldName in new[] { "LocalPluginPlans", "ManifestPlans" })
        {
            var field = typeof(DalamudWindowTracker).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(field);
            Assert.True(
                field!.FieldType.IsGenericType &&
                field.FieldType.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>),
                $"{fieldName} must be a ConditionalWeakTable to keep plugin types collectible, but was {field.FieldType}");
        }
    }
}
