using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Dalamud.Interface.Windowing;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

/// <summary>
/// Regression cover for the periodic frame stalls in issue #51: a ~90 ms freeze every 2 s from the plugin
/// discovery scan, and a 12-18 ms hitch every 60 frames from the unmanaged-window cache flush. These assert
/// the structural properties that keep the cost down -- work is cached, lookups do not allocate per window --
/// rather than wall-clock timings, which are not stable enough to gate a build on.
/// </summary>
public class ScanPerformanceTests
{
    private class DummyWindow : Window
    {
        public DummyWindow(string name) : base(name) { }
        public override void Draw() { }
    }

    private class ScanRoot
    {
        public ServiceHost Services = new();
        public string Label = "root";
        public int Count = 3;
        public List<string> Tags = ["a", "b"];
    }

    private class ServiceHost
    {
        public Dictionary<string, object> ResolvedServices = new();

        public ServiceHost()
        {
            this.ResolvedServices["ui"] = new UiService();
        }
    }

    private class UiService
    {
        public WindowSystem WindowSystem = new("scan-perf");
        public ViewNode View = new();

        public UiService() => this.WindowSystem.AddWindow(new DummyWindow("ScanPerfWindow"));
    }

    private class ViewNode
    {
        public string Title = "node";
        public float X = 1f;
    }

    private static void Scan(DalamudWindowTracker tracker, object graph)
    {
        var scan = typeof(DalamudWindowTracker).GetMethod(
            "ScanObjectForWindowSystems",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        scan.Invoke(tracker, [graph]);
    }

    [Fact]
    public void ScanObjectForWindowSystems_BuildsEachTypeTraversalPlanOnlyOnce()
    {
        // The 2-second discovery tick re-derived, for every member of every visited type, whether that member
        // was worth reading -- ten OrdinalIgnoreCase substring searches per filter call, up to three calls per
        // member, plus a freshly allocated array from GetProperties/GetFields per type. All of it depends only
        // on the type, so it must be computed once and reused (issue #51).
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        DalamudWindowTracker.ClearTypeScanCache();
        Scan(tracker, new ScanRoot());

        var afterFirstScan = DalamudWindowTracker.TypeScanPlansBuilt;
        Assert.True(afterFirstScan > 0, "the first scan should have built traversal plans");

        // Fresh graphs of the same shape: new objects, same types, so no new plans may be built.
        for (var i = 0; i < 10; i++)
            Scan(tracker, new ScanRoot());

        Assert.Equal(afterFirstScan, DalamudWindowTracker.TypeScanPlansBuilt);
    }

    [Fact]
    public void ScanObjectForWindowSystems_StillDiscoversWindows_WhenReplayedFromACachedPlan()
    {
        // Guards the cache against the obvious failure: a plan that is reused but no longer finds anything.
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        DalamudWindowTracker.ClearTypeScanCache();
        Scan(tracker, new ScanRoot());
        Assert.Contains(service.GetTrackedWindows(), t => t.WindowName == "ScanPerfWindow");

        // Second tracker, warm cache, brand-new graph: discovery must be identical.
        var service2 = new WindowManagerService();
        var tracker2 = new DalamudWindowTracker(service2);
        Scan(tracker2, new ScanRoot());

        Assert.Contains(service2.GetTrackedWindows(), t => t.WindowName == "ScanPerfWindow");
    }

    [Fact]
    public void TypeScanCache_IsWeaklyKeyed()
    {
        // Scanned types belong to other plugins' collectible AssemblyLoadContexts. A strong Dictionary<Type,_>
        // here would pin them and block plugin unload -- re-introducing the reload leak closed by #45/#47.
        var field = typeof(DalamudWindowTracker).GetField(
            "TypeScanCache",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.True(
            field!.FieldType.IsGenericType &&
            field.FieldType.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>),
            $"TypeScanCache must be a ConditionalWeakTable to keep plugin types collectible, but was {field.FieldType}");
    }

    [Fact]
    public void ScanObjectForWindowSystems_DoesNotKeepScannedTypesAlive()
    {
        // The behavioural counterpart to the test above: scan an instance of a collectible (RunAndCollect)
        // type, then drop every reference to it and confirm the type can still be collected.
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        var typeRef = ScanCollectibleTypeAndReturnWeakRef(tracker);

        for (var attempt = 0; attempt < 20 && typeRef.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(typeRef.IsAlive, "the scan cache is pinning a collectible type, which would block plugin unload");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ScanCollectibleTypeAndReturnWeakRef(DalamudWindowTracker tracker)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("Umbra.WindowManager.CollectibleScanProbe"),
            AssemblyBuilderAccess.RunAndCollect);

        var module = assembly.DefineDynamicModule("main");
        var typeBuilder = module.DefineType("ProbeUiManager", TypeAttributes.Public);
        typeBuilder.DefineField("uiService", typeof(object), FieldAttributes.Public);
        typeBuilder.DefineField("windowManagerNode", typeof(string), FieldAttributes.Public);

        var probeType = typeBuilder.CreateType();
        var instance = Activator.CreateInstance(probeType)!;

        Scan(tracker, instance);

        return new WeakReference(probeType);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(40)]
    public void TryFastTrackWindow_AllocationDoesNotGrowWithWindowsPerSystem(int windowsPerSystem)
    {
        // WindowSystem.Windows rebuilds a read-only copy on *every* property access, and the miss path read it
        // twice per loop iteration (`ws.Windows.Count` and `ws.Windows[i]`) -- so one miss allocated two copies
        // per window per window system. On the frame the unmanaged cache was flushed, every uncached name paid
        // that at once: megabytes of garbage in a single frame, which is what the 12-18 ms hitch actually was
        // (issue #51). The property is now read once per system, so cost scales with the number of window
        // systems and not with how many windows they hold -- which is what this asserts, by measuring the same
        // system count at two very different window counts.
        var perCall = MeasureMissAllocation(systemCount: 20, windowsPerSystem);

        Assert.True(
            perCall < 20 * 512,
            $"TryFastTrackWindow allocated {perCall:F0} bytes for a miss across 20 systems of " +
            $"{windowsPerSystem} windows; allocation must scale with systems, not windows");
    }

    private static double MeasureMissAllocation(int systemCount, int windowsPerSystem)
    {
        var service = new WindowManagerService();
        var tracker = new DalamudWindowTracker(service);

        for (var s = 0; s < systemCount; s++)
        {
            var ws = new WindowSystem($"perf-sys-{windowsPerSystem}-{s}");
            for (var i = 0; i < windowsPerSystem; i++)
                ws.AddWindow(new DummyWindow($"perf-sys-{windowsPerSystem}-{s}-win-{i}"));

            tracker.TrackWindowSystem(ws, $"Plugin{s}", null);
        }

        // Warm up any lazily-initialised state so it is not charged to the measured window.
        for (var i = 0; i < 5; i++)
            tracker.TryFastTrackWindow("##definitely-not-a-plugin-window");

        const int iterations = 50;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
            tracker.TryFastTrackWindow("##definitely-not-a-plugin-window");

        return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)iterations;
    }
}
