using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Umbra.Common;

namespace Umbra.WindowManager.Services.WindowManager;

[Service]
public class DalamudWindowTracker : IDisposable
{
    private readonly WindowManagerService windowManagerService;

    // Marks the minimize button we injected, keyed by the window *instance*. Entries disappear
    // automatically once a window is garbage collected, so re-instantiated windows are re-injected.
    private static readonly ConditionalWeakTable<IWindow, TitleBarButton> InjectedButtons = new();

    // Caches resolved plugin icon bytes by plugin internal name (null = looked up, none found).
    private readonly ConcurrentDictionary<string, byte[]?> iconCache = new();

    // Dalamud's own logo (UIRes/logo.png under its asset directory), used as the taskbar icon for
    // Dalamud core windows. Null until successfully resolved; resolution is retried on later ticks.
    private byte[]? dalamudCoreIcon;

    // Umbra's own logo (Umbra.images.logo.png embedded in Umbra assembly), used as the taskbar icon for
    // Umbra core windows. Null until successfully resolved; resolution is retried on later ticks.
    private byte[]? umbraCoreIcon;

    // Discovered Umbra window adapters keyed by instance ID.
    private readonly ConcurrentDictionary<string, UmbraWindowAdapter> knownUmbraWindows = new();

    private object? hookedUmbraWindowManager;
    private Delegate? onUmbraWindowOpenedHandler;
    private Delegate? onUmbraWindowClosedHandler;

    private readonly ConcurrentDictionary<string, byte> pendingDownloads = new();
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Discovered WindowSystem instances with their associated plugin context, polled on a fast tick
    // to discover dynamically added windows without reflection latency.
    private readonly ConcurrentDictionary<WindowSystem, PluginContext> knownWindowSystems = new();

    // Plugin context for the discovery pass currently in progress; read by TrackWindowSystem.
    private PluginContext? currentPluginContext;

    // Throttles the reflection-failure log so a persistent Dalamud API break logs ~once/minute
    // instead of every 2-second tick.
    private int scanFailLogCounter;

    /// <summary>
    /// Event fired whenever a plugin is reloaded, updated, or uninstalled, signalling observers
    /// (such as <see cref="ImGuiContextMonitor"/>) to clear caches and re-evaluate windows.
    /// </summary>
    public event Action? PluginReloaded;

    // Tracks the active plugin instance object (by weak reference) keyed by internal name.
    // If the instance changes, the plugin was reloaded or updated.
    private readonly ConcurrentDictionary<string, WeakReference<object>> knownPluginInstances = new(StringComparer.OrdinalIgnoreCase);

    private object? hookedPluginManager;
    private Delegate? onInstalledPluginsChangedHandler;
    private object? hookedPluginInterface;
    private Delegate? onActivePluginsChangedHandler;



    public DalamudWindowTracker(WindowManagerService windowManagerService)

    {
        this.windowManagerService = windowManagerService;
        this.ScanPlugins();
    }

    /// <summary>
    /// Evaluates whether a window has a title bar and is capable of receiving interactive title bar buttons.
    /// Unlike toolbar manageability, this does not require pre-confirmed ImGui drawing or positive pre-render size.
    /// </summary>
    public static bool CanInjectMinimizeButton(IWindow window)
    {
        if (window.TitleBarButtons == null)
            return false;

        if (window.IsClickthrough)
            return false;

        if (window.Flags.HasFlag(Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoTitleBar) ||
            window.Flags.HasFlag(Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoDecoration) ||
            window.Flags.HasFlag(Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoInputs) ||
            (window.Flags & Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoMouseInputs) != 0)
            return false;

        return true;
    }

    public static void InjectMinimizeButton(IWindow window, TrackedWindow tracked, WindowManagerService service)
    {
        // Remove any legacy injected minimize buttons that may have accumulated
        RemoveMinimizeButton(window);

        // Dock-group members (docked together as tabs) have no title bar; minimize is handled by the tab bar button
        if (tracked.DockGroupKey != null)
            return;

        // Overlays and non-interactive windows should not have minimize enabled
        if (!CanInjectMinimizeButton(window))
            return;

        // Enable the window's original collapse/minimize button by clearing the NoCollapse flag
        window.Flags &= ~Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoCollapse;

        // Hook any plugin-provided minimize buttons so clicking them also delegates to WindowManagerService.Minimize
        if (window.TitleBarButtons != null)
        {
            for (var i = 0; i < window.TitleBarButtons.Count; i++)
            {
                var b = window.TitleBarButtons[i];
                if (b.Icon == FontAwesomeIcon.WindowMinimize && b.Priority != int.MaxValue - 1)
                {
                    var origClick = b.Click;
                    b.Click = mb =>
                    {
                        origClick?.Invoke(mb);
                        service.Minimize(tracked);
                    };
                }
            }
        }
    }

    /// <summary>
    /// Removes any custom minimize button we may have injected into <paramref name="window"/>, if present.
    /// Also cleans up any legacy injected minimize buttons accumulated across assembly hot-reloads.
    /// </summary>
    public static void RemoveMinimizeButton(IWindow window)
    {
        if (window.TitleBarButtons == null)
            return;

        if (InjectedButtons.TryGetValue(window, out var injected))
        {
            window.TitleBarButtons.Remove(injected);
            InjectedButtons.Remove(window);
        }

        for (var i = window.TitleBarButtons.Count - 1; i >= 0; i--)
        {
            var b = window.TitleBarButtons[i];
            if (b.Icon == FontAwesomeIcon.WindowMinimize && b.Priority == int.MaxValue - 1)
            {
                window.TitleBarButtons.RemoveAt(i);
            }
        }
    }

    private int isScanning;
    private int scanPending;

    // Wall-clock budget a single tick may spend on the plugin discovery pass before it yields back to the
    // game loop and resumes on the next frame (issue #62). Tests shrink or widen it to drive a pass tick by tick.
    internal TimeSpan ScanSliceBudget = TimeSpan.FromMilliseconds(1);

    // The discovery pass in progress. A single instance is reused so its snapshot list and seen-set are not
    // reallocated per pass; both are emptied when a pass ends so it never pins unloaded plugin objects.
    private readonly ScanPass pass = new();
    private volatile bool passActive;

    // Reused across passes; rebuilt from PluginManager.AvailablePlugins at the start of each one.
    private readonly Dictionary<string, string> availableIconUrls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 2 s cadence: requests a fresh discovery pass (unless one is already running) and spends the first
    /// slice on it. The pass itself is spread across later frames by <see cref="ContinueScan"/>.
    /// </summary>
    [OnTick(interval: 2000)]
    public void ScanPlugins()
    {
        if (!this.passActive)
            this.RequestScan();

        this.ContinueScan();
    }

    /// <summary>
    /// Marks a discovery pass as wanted. Safe from any thread (plugin lifecycle events fire off the
    /// framework thread); the pass itself always runs on the framework thread, from <see cref="ContinueScan"/>.
    /// </summary>
    private void RequestScan() => System.Threading.Interlocked.Exchange(ref this.scanPending, 1);

    /// <summary>
    /// Every frame: advances the pass in progress, or starts one if a change event or the 2 s tick requested
    /// it. Does nothing (one volatile read) while idle. At most <see cref="ScanSliceBudget"/> is spent per
    /// call; a pass that does not fit resumes on the next frame (issue #62).
    /// </summary>
    [OnTick(interval: 0)]
    public void ContinueScan()
    {
        if (!this.passActive && System.Threading.Volatile.Read(ref this.scanPending) == 0)
            return;

        if (System.Threading.Interlocked.CompareExchange(ref this.isScanning, 1, 0) != 0)
            return; // another thread is mid-slice; it keeps the pass moving and re-reads scanPending

        try
        {
            var slice = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (!this.passActive)
                {
                    // Consumed even when the pass cannot start (Dalamud not ready), so an unready host is
                    // retried on the 2 s cadence instead of being re-probed every frame.
                    if (System.Threading.Interlocked.Exchange(ref this.scanPending, 0) == 0)
                        return;

                    this.BeginDalamudScanPass();
                    if (!this.passActive)
                        return;
                }

                if (!this.AdvanceScanPass(this.ScanSliceBudget - slice.Elapsed))
                    return;

                // Pass complete: a change event that arrived mid-pass rescans straight away, budget permitting.
                if (slice.Elapsed >= this.ScanSliceBudget)
                    return;
            }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref this.isScanning, 0);
        }
    }

    private void LogScanFailure(Exception ex)
    {
        if (this.scanFailLogCounter++ % 30 == 0)
            Logger.Warning($"[WindowManager] Plugin discovery scan failed via reflection: {ex.GetType().Name}: {ex.Message}");
    }

    /// <summary>
    /// Resolves Dalamud's plugin manager and snapshots its installed plugins into a new discovery pass.
    /// Leaves no pass active when Dalamud is not ready (startup, unit tests).
    /// </summary>
    private void BeginDalamudScanPass()
    {
        try
        {
            var logAssembly = typeof(Dalamud.Plugin.Services.IPluginLog).Assembly;
            var pmType = logAssembly.GetType("Dalamud.Plugin.Internal.PluginManager");
            if (pmType == null) return;

            var serviceOpenType = logAssembly.GetType("Dalamud.Service`1");
            if (serviceOpenType == null) return;

            // Safe guard: accessing Service<T> where T != ServiceContainer triggers Service<T>..cctor
            // which calls Service<ServiceContainer>.Get() (blocking until ServiceContainer is provided).
            // Checking Service<ServiceContainer> first prevents deadlock outside the live game loop / in unit tests.
            if (!IsServiceContainerReady(logAssembly, serviceOpenType))
                return;

            var pmInstance = ResolveDalamudService(serviceOpenType, pmType);
            if (pmInstance == null) return;

            this.TryHookPluginManagerEvents(pmInstance);


            var installedProp = pmType.GetProperty("InstalledPlugins", BindingFlags.Public | BindingFlags.Instance);
            if (installedProp?.GetValue(pmInstance) is not IEnumerable installedPlugins) return;

            this.availableIconUrls.Clear();
            try
            {
                var availableProp = pmType.GetProperty("AvailablePlugins", BindingFlags.Public | BindingFlags.Instance);
                if (availableProp?.GetValue(pmInstance) is IEnumerable availablePlugins)
                {
                    foreach (var remotePlugin in availablePlugins)
                    {
                        if (remotePlugin == null) continue;
                        var remote = GetManifestPlan(remotePlugin.GetType());
                        var rName = remote.InternalName?.GetValue(remotePlugin) as string;
                        var rIcon = remote.IconUrl?.GetValue(remotePlugin) as string;
                        if (!string.IsNullOrEmpty(rName) && !string.IsNullOrWhiteSpace(rIcon))
                            this.availableIconUrls[rName] = rIcon;
                    }
                }
            }
            catch
            {
                // Best effort
            }

            // Dalamud's own core windows (Plugin Installer, Settings, Console, Data, Changelog, ...) live
            // in DalamudInterface's private WindowSystem, not in PluginManager.InstalledPlugins, so they are
            // never reached by the plugin loop. Resolve DalamudInterface via the same Service<T> pattern and
            // scan it under a synthetic "Dalamud" context (issue #36).
            //
            // Umbra's own internal windows (Settings, Widget Browser, Variable Editor, Toolbar Profile
            // Manager, Installer) live in Umbra.Windows.WindowManager, not in Dalamud's WindowSystem.
            // Discover and register them under the synthetic "Umbra" plugin context (issue #44).
            //
            // Both run as the last two steps of the pass, each counted against the slice budget like a plugin.
            this.BeginScanPass(installedPlugins, this.availableIconUrls, new Action[]
            {
                () => this.ScanDalamudCoreStep(logAssembly, serviceOpenType),
                this.ScanUmbraWindows,
            });
        }
        catch (Exception ex)
        {
            // Reflection into Dalamud internals can break across Dalamud updates (renamed
            // Service`1 / instanceTcs / PluginManager / ServiceContainer). Surface it, throttled,
            // instead of failing silently so discovery breakage is diagnosable (issue #8.2).
            this.LogScanFailure(ex);
        }
    }

    private void ScanDalamudCoreStep(Assembly logAssembly, Type serviceOpenType)
    {
        var diType = logAssembly.GetType("Dalamud.Interface.Internal.DalamudInterface");
        if (diType == null) return;

        var diInstance = ResolveDalamudService(serviceOpenType, diType);
        if (diInstance != null)
            this.ScanDalamudCoreWindows(diInstance, this.TryLoadDalamudCoreIcon(logAssembly, serviceOpenType));
    }

    /// <summary>
    /// Confirms Dalamud's <c>Service&lt;ServiceContainer&gt;</c> singleton has been provided. Any
    /// <c>Service&lt;T&gt;</c> access (for T != ServiceContainer) must be gated on this, because that
    /// type's static constructor blocks on <c>Service&lt;ServiceContainer&gt;.Get()</c> and would deadlock
    /// outside the live game loop / in unit tests. Returns <c>true</c> (proceed) when the reflection
    /// surface is absent, matching the prior inline fall-through behavior.
    /// </summary>
    private static bool IsServiceContainerReady(Assembly logAssembly, Type serviceOpenType)
    {
        var scType = logAssembly.GetType("Dalamud.IoC.Internal.ServiceContainer");
        if (scType == null) return true;

        var scService = serviceOpenType.MakeGenericType(scType);
        var scTcsField = scService.GetField("instanceTcs", BindingFlags.NonPublic | BindingFlags.Static);
        var scTcs = scTcsField?.GetValue(null);
        if (scTcs == null) return true;

        var scTaskProp = scTcs.GetType().GetProperty("Task");
        return scTaskProp?.GetValue(scTcs) is Task scTask && scTask.IsCompleted;
    }

    /// <summary>
    /// Resolves a live Dalamud <c>Service&lt;T&gt;</c> singleton via reflection, returning <c>null</c>
    /// when the service has not yet been provided. Callers must have already confirmed
    /// <c>Service&lt;ServiceContainer&gt;</c> is ready (see <see cref="ScanPlugins"/>) to avoid the
    /// <c>Service&lt;T&gt;</c> cctor deadlock outside the live game loop.
    /// </summary>
    private static object? ResolveDalamudService(Type serviceOpenType, Type serviceType)
    {
        var serviceGeneric = serviceOpenType.MakeGenericType(serviceType);
        var tcsField = serviceGeneric.GetField("instanceTcs", BindingFlags.NonPublic | BindingFlags.Static);
        var tcs = tcsField?.GetValue(null);
        if (tcs == null) return null;

        var taskProp = tcs.GetType().GetProperty("Task");
        if (taskProp?.GetValue(tcs) is not Task task || !task.IsCompleted) return null;

        var getMethod = serviceGeneric.GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        return getMethod?.Invoke(null, null);
    }

    /// <summary>
    /// Scans Dalamud's own <c>DalamudInterface</c> for its internal core <see cref="WindowSystem"/> and
    /// registers those windows (Plugin Installer, Settings, Console, ...) under a synthetic "Dalamud"
    /// plugin context. The recursion tracks the <see cref="WindowSystem"/>-typed field directly, before the
    /// Dalamud-namespace traversal skip in <c>ShouldTraverseType</c> applies (issue #36).
    /// </summary>
    internal void ScanDalamudCoreWindows(object dalamudInterface)
        => this.ScanDalamudCoreWindows(dalamudInterface, this.TryLoadDalamudCoreIcon());

    internal void ScanDalamudCoreWindows(object dalamudInterface, byte[]? coreIcon)
    {
        this.currentPluginContext = new PluginContext("Dalamud", coreIcon);
        try
        {
            this.ScanObjectForWindowSystems(dalamudInterface);
        }
        finally
        {
            this.currentPluginContext = null;
        }
    }

    /// <summary>
    /// Attempts to resolve <see cref="Umbra.Windows.WindowManager"/> via Umbra's <see cref="Umbra.Common.ServiceContainer"/>
    /// and scans its active windows under the synthetic "Umbra" plugin context.
    /// Safely catches any exception outside the game or during startup.
    /// </summary>
    internal void ScanUmbraWindows()
    {
        try
        {
            var scType = typeof(Umbra.Common.ServiceAttribute).Assembly.GetType("Umbra.Common.ServiceContainer");
            if (scType == null) return;

            object? wmInstance = null;
            var getMethod = scType.GetMethod("GetInstance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(Type)]);
            if (getMethod != null)
            {
                try
                {
                    wmInstance = getMethod.Invoke(null, [typeof(Umbra.Windows.WindowManager)]);
                }
                catch
                {
                    // Fall through to Instances field fallback
                }
            }

            if (wmInstance == null)
            {
                var instancesField = scType.GetField("Instances", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (instancesField?.GetValue(null) is IDictionary instancesDict &&
                    instancesDict.Contains(typeof(Umbra.Windows.WindowManager)))
                {
                    wmInstance = instancesDict[typeof(Umbra.Windows.WindowManager)];
                }
            }

            if (wmInstance != null)
            {
                this.ScanUmbraCoreWindows(wmInstance, this.TryLoadUmbraCoreIcon());
            }
        }
        catch
        {
            // Best effort: safe outside the game loop / in unit tests
        }
    }

    internal void ScanUmbraCoreWindows(object windowManager)
        => this.ScanUmbraCoreWindows(windowManager, this.TryLoadUmbraCoreIcon());

    internal void ScanUmbraCoreWindows(object windowManager, byte[]? coreIcon)
    {
        this.HookUmbraWindowManagerEvents(windowManager);

        coreIcon ??= this.TryLoadUmbraCoreIcon();

        var dict = GetUmbraInstancesDictionary(windowManager);
        var activeInstances = ExtractUmbraInstances(windowManager).ToList();
        var activeKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (instanceId, window) in activeInstances)
        {
            activeKeys.Add(instanceId);

            UmbraWindowProxy? proxy = null;
            if (dict != null)
            {
                try
                {
                    lock (dict)
                    {
                        if (dict.Contains(instanceId))
                        {
                            if (dict[instanceId] is UmbraWindowProxy existingProxy)
                            {
                                proxy = existingProxy;
                            }
                            else
                            {
                                proxy = new UmbraWindowProxy(window);
                                dict[instanceId] = proxy;
                            }
                        }
                    }
                }
                catch
                {
                    // Best effort
                }
            }

            if (this.knownUmbraWindows.TryGetValue(instanceId, out var existingAdapter))
            {
                if (ReferenceEquals(existingAdapter.UnderlyingWindow, window))
                {
                    var twExisting = this.windowManagerService.RegisterWindow(existingAdapter);
                    twExisting.PluginInternalName = "Umbra";
                    if (coreIcon != null) twExisting.IconBytes = coreIcon;
                    existingAdapter.IsBeingMinimized = () => twExisting.IsMinimized;
                    if (proxy != null) existingAdapter.Proxy = proxy;
                    existingAdapter.WindowManager = windowManager;
                    existingAdapter.HookTitleBarMinimize(this.windowManagerService, twExisting);
                    if (window.IsMinimized && !twExisting.IsMinimized)
                    {
                        this.windowManagerService.Minimize(twExisting);
                    }
                    continue;
                }

                this.windowManagerService.UnregisterWindow(existingAdapter);
                this.knownUmbraWindows.TryRemove(instanceId, out _);
            }

            var adapter = new UmbraWindowAdapter(instanceId, window, proxy: proxy, windowManager: windowManager);
            var tw = this.windowManagerService.RegisterWindow(adapter);
            tw.PluginInternalName = "Umbra";
            tw.IconBytes = coreIcon;
            adapter.IsBeingMinimized = () => tw.IsMinimized;
            adapter.HookTitleBarMinimize(this.windowManagerService, tw);
            if (window.IsMinimized && !tw.IsMinimized)
            {
                this.windowManagerService.Minimize(tw);
            }
            this.knownUmbraWindows[instanceId] = adapter;
        }

        foreach (var kvp in this.knownUmbraWindows)
        {
            if (!activeKeys.Contains(kvp.Key))
            {
                if (this.knownUmbraWindows.TryRemove(kvp.Key, out var removed))
                {
                    this.windowManagerService.UnregisterWindow(removed);
                }
            }
        }
    }

    /// <summary>
    /// Loads the embedded Umbra logo (<c>Umbra.images.logo.png</c>) from the Umbra assembly
    /// for use as the taskbar icon for Umbra core windows. Cached once loaded; returns null
    /// safely if the resource cannot be loaded.
    /// </summary>
    internal byte[]? TryLoadUmbraCoreIcon()
    {
        if (this.umbraCoreIcon != null)
            return this.umbraCoreIcon;

        if (this.iconCache.TryGetValue("Umbra", out var cached) && cached != null)
        {
            this.umbraCoreIcon = cached;
            return cached;
        }

        try
        {
            var asm = Type.GetType("Umbra.Plugin, Umbra")?.Assembly ?? typeof(Umbra.Windows.IWindow).Assembly;
            using var stream = asm.GetManifestResourceStream("Umbra.images.logo.png");
            if (stream == null)
                return null;

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            this.umbraCoreIcon = bytes;
            this.iconCache["Umbra"] = bytes;
            return bytes;
        }
        catch
        {
            // Safe fallback: returns null if resource is missing or throws
            return null;
        }
    }

    internal static IDictionary? GetUmbraInstancesDictionary(object windowManager)
    {
        var wmType = windowManager.GetType();
        var field = wmType.GetField("_instances", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                 ?? wmType.GetField("Instances", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var obj = field?.GetValue(windowManager);
        if (obj == null)
        {
            var prop = wmType.GetProperty("Instances", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? wmType.GetProperty("_instances", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            obj = prop?.GetValue(windowManager);
        }

        return obj as IDictionary;
    }

    private static IEnumerable<KeyValuePair<string, Umbra.Windows.IWindow>> ExtractUmbraInstances(object windowManager)
    {
        var dict = GetUmbraInstancesDictionary(windowManager);
        if (dict != null)
        {
            var entries = new List<KeyValuePair<string, Umbra.Windows.IWindow>>();
            try
            {
                lock (dict)
                {
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (entry.Key is string instanceId)
                        {
                            if (entry.Value is UmbraWindowProxy proxy)
                            {
                                entries.Add(new KeyValuePair<string, Umbra.Windows.IWindow>(instanceId, proxy.UnderlyingWindow));
                            }
                            else if (entry.Value is Umbra.Windows.IWindow window)
                            {
                                entries.Add(new KeyValuePair<string, Umbra.Windows.IWindow>(instanceId, window));
                            }
                        }
                    }
                }
            }
            catch
            {
                // Best effort snapshot in case of concurrent dictionary modification by Umbra
            }

            return entries;
        }

        return Enumerable.Empty<KeyValuePair<string, Umbra.Windows.IWindow>>();
    }

    private void HookUmbraWindowManagerEvents(object windowManager)
    {
        lock (this.knownUmbraWindows)
        {
            if (ReferenceEquals(this.hookedUmbraWindowManager, windowManager))
                return;

            if (this.hookedUmbraWindowManager != null)
                this.UnhookUmbraWindowManagerEvents();

            this.hookedUmbraWindowManager = windowManager;

            try
            {
                var wmType = windowManager.GetType();
                var openedEvent = wmType.GetEvent("OnWindowOpened", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (openedEvent != null && openedEvent.EventHandlerType != null)
                {
                    var handler = this.CreateUmbraEventHandler(openedEvent.EventHandlerType, this.OnUmbraWindowOpened);
                    if (handler != null)
                    {
                        openedEvent.AddEventHandler(windowManager, handler);
                        this.onUmbraWindowOpenedHandler = handler;
                    }
                }

                var closedEvent = wmType.GetEvent("OnWindowClosed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (closedEvent != null && closedEvent.EventHandlerType != null)
                {
                    var handler = this.CreateUmbraEventHandler(closedEvent.EventHandlerType, this.OnUmbraWindowClosed);
                    if (handler != null)
                    {
                        closedEvent.AddEventHandler(windowManager, handler);
                        this.onUmbraWindowClosedHandler = handler;
                    }
                }
            }
            catch
            {
                // Best effort
            }
        }
    }

    private void UnhookUmbraWindowManagerEvents()
    {
        lock (this.knownUmbraWindows)
        {
            if (this.hookedUmbraWindowManager == null)
                return;

            try
            {
                var dict = GetUmbraInstancesDictionary(this.hookedUmbraWindowManager);
                if (dict != null)
                {
                    try
                    {
                        lock (dict)
                        {
                            foreach (var kvp in this.knownUmbraWindows)
                            {
                                if (dict.Contains(kvp.Key) && dict[kvp.Key] is UmbraWindowProxy proxy)
                                {
                                    dict[kvp.Key] = proxy.UnderlyingWindow;
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Best effort
                    }
                }

                var wmType = this.hookedUmbraWindowManager.GetType();
                if (this.onUmbraWindowOpenedHandler != null)
                {
                    var openedEvent = wmType.GetEvent("OnWindowOpened", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    openedEvent?.RemoveEventHandler(this.hookedUmbraWindowManager, this.onUmbraWindowOpenedHandler);
                }

                if (this.onUmbraWindowClosedHandler != null)
                {
                    var closedEvent = wmType.GetEvent("OnWindowClosed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    closedEvent?.RemoveEventHandler(this.hookedUmbraWindowManager, this.onUmbraWindowClosedHandler);
                }
            }
            catch
            {
                // Best effort
            }

            this.hookedUmbraWindowManager = null;
            this.onUmbraWindowOpenedHandler = null;
            this.onUmbraWindowClosedHandler = null;
        }
    }

    private Delegate? CreateUmbraEventHandler(Type eventHandlerType, Action<object?> callback)
    {
        var invoke = eventHandlerType.GetMethod("Invoke");
        if (invoke == null) return null;

        var parameters = invoke.GetParameters();
        if (parameters.Length == 0)
        {
            Action handler = () => callback(null);
            return Delegate.CreateDelegate(eventHandlerType, handler.Target, handler.Method);
        }

        if (parameters.Length == 1)
        {
            var paramType = parameters[0].ParameterType;
            var dispatcher = new UmbraEventDispatcher(callback);
            var method = typeof(UmbraEventDispatcher)
                .GetMethod(nameof(UmbraEventDispatcher.Dispatch), BindingFlags.Public | BindingFlags.Instance)!
                .MakeGenericMethod(paramType);
            return Delegate.CreateDelegate(eventHandlerType, dispatcher, method);
        }

        return null;
    }

    private sealed class UmbraEventDispatcher
    {
        private readonly Action<object?> callback;

        public UmbraEventDispatcher(Action<object?> callback)
        {
            this.callback = callback;
        }

        public void Dispatch<T>(T arg)
        {
            this.callback(arg);
        }
    }

    private void OnUmbraWindowOpened(object? rawWindow)
    {
        try
        {
            if (this.hookedUmbraWindowManager != null)
            {
                this.ScanUmbraCoreWindows(this.hookedUmbraWindowManager, this.TryLoadUmbraCoreIcon());
            }
        }
        catch
        {
            // Best effort
        }
    }

    private void OnUmbraWindowClosed(object? rawWindow)
    {
        try
        {
            if (rawWindow is Umbra.Windows.IWindow window)
            {
                var match = this.knownUmbraWindows.FirstOrDefault(kvp => ReferenceEquals(kvp.Value.UnderlyingWindow, window));
                if (!string.IsNullOrEmpty(match.Key))
                {
                    if (this.knownUmbraWindows.TryRemove(match.Key, out var adapter))
                    {
                        this.windowManagerService.UnregisterWindow(adapter);
                    }
                    return;
                }
            }

            if (this.hookedUmbraWindowManager != null)
            {
                this.ScanUmbraCoreWindows(this.hookedUmbraWindowManager, this.TryLoadUmbraCoreIcon());
            }
        }
        catch
        {
            // Best effort
        }
    }

    /// <summary>
    /// Resolves Dalamud's own logo (<c>UIRes/logo.png</c> under <c>Dalamud.AssetDirectory</c>) for use as
    /// the taskbar icon of Dalamud core windows, which carry no owning plugin and would otherwise fall back
    /// to a text monogram. Best-effort and cached: returns <c>null</c> until the Dalamud service and asset
    /// file are available, retries on later ticks, and caches the bytes once loaded. Gated on
    /// <see cref="IsServiceContainerReady"/> so it is safe to call outside the live game loop.
    /// </summary>
    internal byte[]? TryLoadDalamudCoreIcon()
    {
        var logAssembly = typeof(Dalamud.Plugin.Services.IPluginLog).Assembly;
        var serviceOpenType = logAssembly.GetType("Dalamud.Service`1");
        return serviceOpenType == null ? null : this.TryLoadDalamudCoreIcon(logAssembly, serviceOpenType);
    }

    private byte[]? TryLoadDalamudCoreIcon(Assembly logAssembly, Type serviceOpenType)
    {
        if (this.dalamudCoreIcon != null)
            return this.dalamudCoreIcon;

        try
        {
            if (!IsServiceContainerReady(logAssembly, serviceOpenType))
                return null;

            var dalamudType = logAssembly.GetType("Dalamud.Dalamud");
            if (dalamudType == null) return null;

            var dalamud = ResolveDalamudService(serviceOpenType, dalamudType);
            if (dalamud == null) return null;

            // Dalamud.AssetDirectory (DirectoryInfo, non-public getter) points at the active
            // dalamudAssets/<branch> folder; the logo lives at UIRes/logo.png beneath it.
            var assetDir = dalamudType
                .GetProperty("AssetDirectory", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(dalamud) as DirectoryInfo;
            var dir = assetDir?.FullName;
            if (string.IsNullOrEmpty(dir)) return null;

            var logoPath = Path.Combine(dir, "UIRes", "logo.png");
            if (File.Exists(logoPath))
            {
                this.dalamudCoreIcon = File.ReadAllBytes(logoPath);
                return this.dalamudCoreIcon;
            }
        }
        catch
        {
            // Best-effort: Dalamud core windows fall back to a monogram if the logo cannot be resolved.
        }

        return null;
    }

    /// <summary>
    /// Resolves the owning plugin's internal name and icon bytes from a Dalamud <c>LocalPlugin</c>
    /// object via reflection. Best-effort: returns whatever could be resolved, or an empty context.
    /// </summary>
    internal PluginContext ResolvePluginContext(object localPlugin, object? manifest = null, IReadOnlyDictionary<string, string>? availableIconUrls = null)
    {
        try
        {
            manifest ??= GetLocalPluginPlan(localPlugin.GetType()).Manifest?.GetValue(localPlugin);
            var manifestPlan = manifest != null ? GetManifestPlan(manifest.GetType()) : null;
            var internalName = manifestPlan?.InternalName?.GetValue(manifest) as string;
            var manifestIconUrl = manifestPlan?.IconUrl?.GetValue(manifest) as string;
            var dip17Channel = manifestPlan?.Dip17Channel?.GetValue(manifest) as string;

            string? availableIconUrl = null;
            if (!string.IsNullOrEmpty(internalName) && availableIconUrls != null)
                availableIconUrls.TryGetValue(internalName, out availableIconUrl);

            var iconUrl = ResolveIconUrl(manifestIconUrl, availableIconUrl, dip17Channel, internalName);

            byte[]? icon = null;
            if (!string.IsNullOrEmpty(internalName))
            {
                if (this.iconCache.TryGetValue(internalName, out icon))
                {
                    // Cached value present (bytes = resolved icon; null = the background download reported
                    // a definitive miss). Either way, this is the final answer -- reuse it.
                }
                else
                {
                    icon = this.LoadPluginIcon(localPlugin, internalName, iconUrl);

                    // Only cache a positive hit. A null here means "not resolvable yet" (metadata/network
                    // not ready, or a download is in flight): caching it would permanently mask the icon
                    // (issue #37). Re-evaluate on the next tick until the download callback records the
                    // definitive result.
                    if (icon != null)
                        this.iconCache[internalName] = icon;
                }
            }

            return new PluginContext(internalName, icon);
        }
        catch
        {
            return new PluginContext(null, null);
        }
    }

    // Dalamud's dynamic icon URL for main-repo (DIP17) plugins, which ship no static IconUrl. Mirrors
    // PluginImageCache.MainRepoDip17ImageUrl: args are {channel}, {internalName}, {filename} (issue #37).
    private const string Dip17IconUrlTemplate = "https://raw.githubusercontent.com/goatcorp/PluginDistD17/main/{0}/{1}/images/{2}";

    /// <summary>
    /// Resolves the icon URL for a plugin the way Dalamud itself does: a static manifest/remote IconUrl
    /// when present, otherwise the DIP17 dynamic URL synthesized from the plugin's <c>Dip17Channel</c>
    /// (main-repo plugins carry no static IconUrl). Returns null when no icon URL can be determined.
    /// </summary>
    internal static string? ResolveIconUrl(string? manifestIconUrl, string? availableIconUrl, string? dip17Channel, string? internalName)
    {
        if (!string.IsNullOrWhiteSpace(manifestIconUrl))
            return manifestIconUrl;

        if (!string.IsNullOrWhiteSpace(availableIconUrl))
            return availableIconUrl;

        if (!string.IsNullOrWhiteSpace(dip17Channel) && !string.IsNullOrEmpty(internalName))
            return string.Format(Dip17IconUrlTemplate, dip17Channel, internalName, "icon.png");

        return null;
    }

    /// <summary>
    /// Applies a freshly resolved plugin icon to every place a later-opened window can pick it up:
    /// currently tracked windows, and the cached plugin context on known window systems (so the 250ms
    /// fast-path scan and <see cref="TryFastTrackWindow"/> hand the icon to windows opened after the
    /// download completed, closing the background-download race in issue #37).
    /// </summary>
    internal void ApplyDownloadedIcon(string internalName, byte[] bytes)
    {
        this.iconCache[internalName] = bytes;

        foreach (var ws in this.knownWindowSystems.Keys)
        {
            if (this.knownWindowSystems.TryGetValue(ws, out var context) &&
                context.InternalName == internalName && context.IconBytes == null)
            {
                this.knownWindowSystems[ws] = context with { IconBytes = bytes };
            }
        }

        var trackedWindows = this.windowManagerService.GetTrackedWindows();
        for (var i = 0; i < trackedWindows.Count; i++)
        {
            var tw = trackedWindows[i];
            if (tw.PluginInternalName == internalName)
                tw.IconBytes = bytes;
        }
    }

    // Plugins whose icon was looked up on disk (plugin directory + persistent icon cache) and not found.
    // Without it every discovery pass repeated 3-4 File.Exists calls per unresolved plugin for as long as the
    // icon stayed unresolved (issue #62). Only the disk lookup is remembered, not the final answer: the
    // download trigger below still runs on every pass so a transient network failure is retried (issue #37).
    // Cleared when a download for the plugin finishes and when the plugin is untracked (reload/update).
    private readonly ConcurrentDictionary<string, byte> iconDiskMisses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forgets a remembered negative disk lookup so the next resolve checks the disk again.</summary>
    internal void InvalidateIconDiskLookup(string internalName) => this.iconDiskMisses.TryRemove(internalName, out _);

    internal byte[]? LoadPluginIcon(object localPlugin, string? internalName, string? iconUrl)
    {
        try
        {
            var hasName = !string.IsNullOrEmpty(internalName);
            if (!hasName || !this.iconDiskMisses.ContainsKey(internalName!))
            {
                var onDisk = ReadPluginIconFromDisk(localPlugin, internalName);
                if (onDisk != null)
                    return onDisk;

                if (hasName)
                    this.iconDiskMisses[internalName!] = 0;
            }

            // Not on disk: trigger background download if IconUrl is available
            if (hasName && !this.pendingDownloads.ContainsKey(internalName!) &&
                !string.IsNullOrWhiteSpace(iconUrl) && Uri.TryCreate(iconUrl, UriKind.Absolute, out _))
            {
                this.TriggerIconDownload(internalName!, iconUrl, GetCachedIconPath(internalName!));
            }
        }
        catch
        {
            // Icon is optional; fall back to a monogram in the widget.
        }

        return null;
    }

    private static byte[]? ReadPluginIconFromDisk(object localPlugin, string? internalName)
    {
        var dllFile = GetLocalPluginPlan(localPlugin.GetType()).DllFile?.GetValue(localPlugin) as FileInfo;
        var dir = dllFile?.DirectoryName;
        if (!string.IsNullOrEmpty(dir))
        {
            // Dalamud plugin icon convention on disk
            foreach (var candidate in new[] { Path.Combine(dir, "images", "icon.png"), Path.Combine(dir, "icon.png"), Path.Combine(dir, "Images", "Icon.png") })
            {
                if (File.Exists(candidate))
                    return File.ReadAllBytes(candidate);
            }
        }

        // Check persistent icon cache on disk
        if (!string.IsNullOrEmpty(internalName))
        {
            var cachedPath = GetCachedIconPath(internalName);
            if (File.Exists(cachedPath))
                return File.ReadAllBytes(cachedPath);
        }

        return null;
    }

    internal static string GetCachedIconPath(string internalName)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "XIVLauncher", "pluginConfigs", "Umbra", "WindowManager", "icons", $"{internalName}.png");
    }

    private void TriggerIconDownload(string internalName, string url, string cachedPath)
    {
        if (!this.pendingDownloads.TryAdd(internalName, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var response = await HttpClient.GetAsync(url).ConfigureAwait(false);
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Definitive miss (e.g. synthesized DIP17 URL for a plugin that has no icon). Cache the
                    // null so we stop re-requesting it every tick; the widget falls back to a monogram.
                    this.iconCache[internalName] = null;
                    return;
                }

                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                if (bytes is { Length: > 0 })
                {
                    try
                    {
                        var dir = Path.GetDirectoryName(cachedPath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        await File.WriteAllBytesAsync(cachedPath, bytes).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Disk cache write failure should not prevent runtime icon usage
                    }

                    this.ApplyDownloadedIcon(internalName, bytes);
                }
            }
            catch
            {
                // Transient failure (timeout, DNS, connectivity, non-404 error). Do NOT cache a null:
                // leaving the entry unset lets a later tick retry once the network settles (issue #37).
            }
            finally
            {
                this.pendingDownloads.TryRemove(internalName, out _);

                // The download may have changed what is on disk; let the next resolve look again (issue #62).
                this.InvalidateIconDiskLookup(internalName);
            }
        });
    }

    private void ScanObjectForWindowSystems(object obj)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        this.ScanObjectForWindowSystemsRecursive(obj, 0, 6, visited);
    }

    private void ScanObjectForWindowSystemsRecursive(object obj, int currentDepth, int maxDepth, HashSet<object> visited)
    {
        if (obj == null || !visited.Add(obj))
            return;

        if (obj is WindowSystem directWs)
        {
            this.TrackWindowSystem(directWs);
            return;
        }

        if (obj is IWindow directWindow)
        {
            this.TrackSingleWindow(directWindow);
            return;
        }

        if (currentDepth >= maxDepth)
            return;

        // Unpack DI containers (e.g. Luna.ServiceManager, Microsoft.Extensions.DependencyInjection, etc.)
        this.TryScanServiceProvider(obj, currentDepth, maxDepth, visited);

        if (obj is IEnumerable enumerable and not string and not byte[])
        {
            var count = 0;
            foreach (var item in enumerable)
            {
                if (++count > 50) break;
                if (item == null) continue;

                var actualItem = item;
                if (actualItem is DictionaryEntry de)
                {
                    actualItem = de.Value;
                }
                else
                {
                    var itemType = actualItem.GetType();
                    if (itemType.IsGenericType && itemType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                    {
                        actualItem = itemType.GetProperty("Value")?.GetValue(actualItem);
                    }
                }

                if (actualItem is WindowSystem itemWs)
                {
                    this.TrackWindowSystem(itemWs);
                }
                else if (actualItem is IWindow itemWin)
                {
                    this.TrackSingleWindow(itemWin);
                }
                else if (actualItem != null && currentDepth < maxDepth)
                {
                    var ait = actualItem.GetType();
                    if (GetTypeScanInfo(ait).IsTraversableItem)
                    {
                        this.ScanObjectForWindowSystemsRecursive(actualItem, currentDepth + 1, maxDepth, visited);
                    }
                }
            }
        }

        for (var currentType = obj.GetType(); currentType != null && currentType != typeof(object); currentType = currentType.BaseType)
        {
            var plan = GetTypeScanInfo(currentType);

            var props = plan.GetProperties(currentType);
            for (var i = 0; i < props.Length; i++)
            {
                var (prop, kind) = props[i];

                // Traverse members are read only to recurse into, so at maximum depth there is nothing to do
                // with them. WindowSystem/IWindow members are still read -- they are tracked, not descended.
                if (kind == ScanMemberKind.Traverse && currentDepth >= maxDepth)
                    continue;

                try
                {
                    var val = prop.GetValue(obj);
                    if (val == null)
                        continue;

                    if (val is WindowSystem ws)
                    {
                        this.TrackWindowSystem(ws);
                    }
                    else if (val is IWindow w)
                    {
                        this.TrackSingleWindow(w);
                    }
                    else if (kind == ScanMemberKind.Traverse && GetTypeScanInfo(val.GetType()).ShouldTraverse)
                    {
                        this.ScanObjectForWindowSystemsRecursive(val, currentDepth + 1, maxDepth, visited);
                    }
                }
                catch
                {
                    // Expected: plugin property getters may throw when accessed out of context.
                }
            }

            var fields = plan.GetFields(currentType);
            for (var i = 0; i < fields.Length; i++)
            {
                var (field, kind) = fields[i];

                if (kind == ScanMemberKind.Traverse && currentDepth >= maxDepth)
                    continue;

                try
                {
                    var val = field.GetValue(obj);
                    if (val == null)
                        continue;

                    if (val is WindowSystem ws)
                    {
                        this.TrackWindowSystem(ws);
                    }
                    else if (val is IWindow w)
                    {
                        this.TrackSingleWindow(w);
                    }
                    else if (kind == ScanMemberKind.Traverse && GetTypeScanInfo(val.GetType()).ShouldTraverse)
                    {
                        this.ScanObjectForWindowSystemsRecursive(val, currentDepth + 1, maxDepth, visited);
                    }
                }
                catch
                {
                    // Expected: field access may fail for exotic plugin layouts.
                }
            }
        }
    }

    private enum ScanMemberKind : byte
    {
        /// <summary>Declared as a <see cref="WindowSystem"/> or <see cref="IWindow"/>: read the member and track it.</summary>
        Tracked,

        /// <summary>Passed the traversal filters: read the member and recurse into its value.</summary>
        Traverse,
    }

    /// <summary>
    /// Cached, per-<see cref="Type"/> traversal plan for <see cref="ScanObjectForWindowSystemsRecursive"/>.
    /// Whether a member is worth reading is a pure function of its declaring type, its declared member type
    /// and its name -- yet re-deriving it on every pass dominated the 2-second discovery tick (issue #51):
    /// each member re-ran <see cref="ShouldTraverseField"/> / <see cref="ShouldTraverseProperty"/>, which call
    /// <see cref="ShouldTraverseMemberName"/> up to three times for ten <c>Contains(..., OrdinalIgnoreCase)</c>
    /// searches apiece, and <see cref="Type.GetProperties(BindingFlags)"/> / <see cref="Type.GetFields(BindingFlags)"/>
    /// returned a freshly allocated array every call. Caching collapses that to one build per type, and members
    /// that fail the filters are dropped from the plan entirely so later scans never look at them again.
    /// </summary>
    private sealed class TypeScanInfo
    {
        private const BindingFlags MemberFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        /// <summary><see cref="ShouldTraverseType"/> for this type, evaluated once.</summary>
        public readonly bool ShouldTraverse;

        /// <summary>Whether a collection / DI-container item of this type is worth recursing into.</summary>
        public readonly bool IsTraversableItem;

        private (PropertyInfo Member, ScanMemberKind Kind)[]? properties;
        private (FieldInfo Member, ScanMemberKind Kind)[]? fields;
        private ServiceProbePlan? serviceProbes;

        public TypeScanInfo(Type type)
        {
            this.ShouldTraverse = ShouldTraverseType(type);
            this.IsTraversableItem = IsUiOrServiceType(type) || ShouldTraverseMemberName(type.Name);
        }

        // The type is passed back in rather than stored, so a cached plan never references its own
        // ConditionalWeakTable key and the "plugin types stay collectible" guarantee holds by inspection.
        public (PropertyInfo Member, ScanMemberKind Kind)[] GetProperties(Type type) =>
            this.properties ??= BuildProperties(type);

        public (FieldInfo Member, ScanMemberKind Kind)[] GetFields(Type type) =>
            this.fields ??= BuildFields(type);

        public ServiceProbePlan GetServiceProbes(Type type) =>
            this.serviceProbes ??= new ServiceProbePlan(type);

        private static (PropertyInfo, ScanMemberKind)[] BuildProperties(Type type)
        {
            var declared = type.GetProperties(MemberFlags);
            var result = new List<(PropertyInfo, ScanMemberKind)>(declared.Length);

            foreach (var prop in declared)
            {
                // Unreadable and indexed properties were never touched by the scan, whatever their type.
                if (!prop.CanRead || prop.GetIndexParameters().Length != 0)
                    continue;

                if (typeof(WindowSystem).IsAssignableFrom(prop.PropertyType) ||
                    typeof(IWindow).IsAssignableFrom(prop.PropertyType))
                {
                    result.Add((prop, ScanMemberKind.Tracked));
                }
                else if (ShouldTraverseProperty(prop))
                {
                    result.Add((prop, ScanMemberKind.Traverse));
                }
            }

            return result.ToArray();
        }

        private static (FieldInfo, ScanMemberKind)[] BuildFields(Type type)
        {
            var declared = type.GetFields(MemberFlags);
            var result = new List<(FieldInfo, ScanMemberKind)>(declared.Length);

            foreach (var field in declared)
            {
                if (typeof(WindowSystem).IsAssignableFrom(field.FieldType) ||
                    typeof(IWindow).IsAssignableFrom(field.FieldType))
                {
                    result.Add((field, ScanMemberKind.Tracked));
                }
                else if (ShouldTraverseField(field))
                {
                    result.Add((field, ScanMemberKind.Traverse));
                }
            }

            return result.ToArray();
        }
    }

    /// <summary>
    /// Cached, per-<see cref="Type"/> shape probes used by <see cref="TryScanServiceProvider"/>. That method runs
    /// for <em>every</em> object the scan visits -- the <c>_ownedObjects</c> and <c>Provider</c>/<c>Services</c>
    /// probes sit outside the service-provider guard -- so its half-dozen <c>GetField</c>/<c>GetProperty</c>
    /// name lookups were paid thousands of times per discovery tick (issue #51). Which members exist is fixed per
    /// type, so the lookups are resolved once and only the <c>GetValue</c> calls stay on the hot path.
    /// </summary>
    private sealed class ServiceProbePlan
    {
        private const BindingFlags ProbeFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public readonly bool IsServiceProviderShape;

        // Candidate members are kept as ordered lists -- not a single resolved member -- because the original
        // probes fell through on a null *value*, not merely on a missing member.
        public readonly MemberInfo[] RootCandidates;
        public readonly MemberInfo[] ResolvedServicesCandidates;
        public readonly MemberInfo[] DisposablesCandidates;
        public readonly MemberInfo[] OwnedCandidates;
        public readonly PropertyInfo? ProviderProperty;

        public ServiceProbePlan(Type type)
        {
            this.IsServiceProviderShape = typeof(IServiceProvider).IsAssignableFrom(type) ||
                                          type.Name.Contains("ServiceProvider");

            this.RootCandidates = Collect(
                type.GetProperty("Root", ProbeFlags),
                type.GetProperty("_root", ProbeFlags),
                type.GetField("<Root>k__BackingField", ProbeFlags),
                type.GetField("<_root>k__BackingField", ProbeFlags),
                type.GetField("_root", ProbeFlags));

            this.ResolvedServicesCandidates = Collect(
                type.GetProperty("ResolvedServices", ProbeFlags),
                type.GetProperty("_resolvedServices", ProbeFlags),
                type.GetField("<ResolvedServices>k__BackingField", ProbeFlags),
                type.GetField("<_resolvedServices>k__BackingField", ProbeFlags),
                type.GetField("_resolvedServices", ProbeFlags),
                type.GetField("ResolvedServices", ProbeFlags));

            this.DisposablesCandidates = Collect(
                type.GetProperty("Disposables", ProbeFlags),
                type.GetProperty("_disposables", ProbeFlags),
                type.GetField("<Disposables>k__BackingField", ProbeFlags),
                type.GetField("<_disposables>k__BackingField", ProbeFlags),
                type.GetField("_disposables", ProbeFlags));

            // The original picked the backing field by *presence* before falling through to the properties by
            // value, so only the first field that exists takes part in the value chain.
            var ownedField = type.GetField("_ownedObjects", ProbeFlags)
                          ?? type.GetField("<_ownedObjects>k__BackingField", ProbeFlags);
            this.OwnedCandidates = Collect(
                ownedField,
                type.GetProperty("_ownedObjects", ProbeFlags),
                type.GetProperty("OwnedObjects", ProbeFlags));

            var provProp = type.GetProperty("Provider", ProbeFlags) ?? type.GetProperty("Services", ProbeFlags);
            this.ProviderProperty = provProp is { CanRead: true } && provProp.GetIndexParameters().Length == 0
                ? provProp
                : null;
        }

        private static MemberInfo[] Collect(params MemberInfo?[] candidates)
        {
            var count = 0;
            foreach (var c in candidates)
            {
                if (c != null) count++;
            }

            if (count == 0) return [];

            var result = new MemberInfo[count];
            var i = 0;
            foreach (var c in candidates)
            {
                if (c != null) result[i++] = c;
            }

            return result;
        }
    }

    /// <summary>
    /// Reads <paramref name="candidates"/> in order and returns the first non-null value, mirroring the
    /// <c>?.GetValue(x) ?? ...</c> chains these plans replaced. Exceptions propagate to the caller's single
    /// try/catch, exactly as before.
    /// </summary>
    private static object? FirstNonNullValue(MemberInfo[] candidates, object target)
    {
        for (var i = 0; i < candidates.Length; i++)
        {
            var value = candidates[i] switch
            {
                PropertyInfo prop => prop.GetValue(target),
                FieldInfo field => field.GetValue(target),
                _ => null,
            };

            if (value != null) return value;
        }

        return null;
    }

    // Weak-keyed on purpose: scanned types live in other plugins' collectible AssemblyLoadContexts, so a
    // strong Dictionary<Type, ...> here would pin those contexts and block plugin unload -- turning a
    // performance fix into the reload leak #45/#47 just closed. Never key this on Type.Name either: two ALC
    // generations of a reloaded plugin share names but are distinct Types.
    private static readonly ConditionalWeakTable<Type, TypeScanInfo> TypeScanCache = new();

    // Diagnostics only: how many plans have been built, so tests can assert the cache actually hits.
    internal static long TypeScanPlansBuilt;

    private static TypeScanInfo GetTypeScanInfo(Type type) =>
        TypeScanCache.GetValue(type, static t =>
        {
            System.Threading.Interlocked.Increment(ref TypeScanPlansBuilt);
            return new TypeScanInfo(t);
        });

    /// <summary>Test hook: drops every cached traversal plan so plan building can be observed from scratch.</summary>
    internal static void ClearTypeScanCache()
    {
        TypeScanCache.Clear();
        System.Threading.Interlocked.Exchange(ref TypeScanPlansBuilt, 0);
    }

    private void TryScanServiceProvider(object obj, int currentDepth, int maxDepth, HashSet<object> visited)
    {
        try
        {
            var objType = obj.GetType();
            var probes = GetTypeScanInfo(objType).GetServiceProbes(objType);

            if (probes.IsServiceProviderShape)
            {
                if (obj is IServiceProvider sp)
                {
                    try
                    {
                        if (sp.GetService(typeof(WindowSystem)) is WindowSystem directWs)
                        {
                            this.TrackWindowSystem(directWs);
                        }
                    }
                    catch
                    {
                        // Best effort: GetService may throw for unregistered types
                    }
                }

                // Microsoft DI: inspect root engine scope
                var root = FirstNonNullValue(probes.RootCandidates, obj) ?? obj;
                var rootType = root.GetType();
                var rootProbes = GetTypeScanInfo(rootType).GetServiceProbes(rootType);

                // Extract resolved singleton services
                var dict = FirstNonNullValue(rootProbes.ResolvedServicesCandidates, root) as IDictionary;

                if (dict != null)
                {
                    foreach (var val in dict.Values)
                    {
                        if (val == null) continue;
                        if (val is WindowSystem ws)
                        {
                            this.TrackWindowSystem(ws);
                        }
                        else if (val is IWindow w)
                        {
                            this.TrackSingleWindow(w);
                        }
                        else if (currentDepth < maxDepth && GetTypeScanInfo(val.GetType()).IsTraversableItem)
                        {
                            this.ScanObjectForWindowSystemsRecursive(val, currentDepth + 1, maxDepth, visited);
                        }
                    }
                }

                // Extract disposables list
                var disposables = FirstNonNullValue(rootProbes.DisposablesCandidates, root) as IEnumerable;

                if (disposables != null)
                {
                    var dCount = 0;
                    foreach (var d in disposables)
                    {
                        if (++dCount > 100) break;
                        if (d == null) continue;
                        if (d is WindowSystem ws)
                        {
                            this.TrackWindowSystem(ws);
                        }
                        else if (d is IWindow w)
                        {
                            this.TrackSingleWindow(w);
                        }
                        else if (currentDepth < maxDepth && GetTypeScanInfo(d.GetType()).IsTraversableItem)
                        {
                            this.ScanObjectForWindowSystemsRecursive(d, currentDepth + 1, maxDepth, visited);
                        }
                    }
                }
            }

            // Luna.ServiceManager or similar service managers:
            // Check for _ownedObjects (HashSet<IDisposable>)
            var owned = FirstNonNullValue(probes.OwnedCandidates, obj) as IEnumerable;

            if (owned != null)
            {
                var oCount = 0;
                foreach (var o in owned)
                {
                    if (++oCount > 100) break;
                    if (o == null) continue;
                    if (o is WindowSystem ws)
                    {
                        this.TrackWindowSystem(ws);
                    }
                    else if (o is IWindow w)
                    {
                        this.TrackSingleWindow(w);
                    }
                    else if (currentDepth < maxDepth && GetTypeScanInfo(o.GetType()).IsTraversableItem)
                    {
                        this.ScanObjectForWindowSystemsRecursive(o, currentDepth + 1, maxDepth, visited);
                    }
                }
            }

            // Check for Provider property on service managers (e.g. Luna.ServiceManager.Provider)
            var provProp = probes.ProviderProperty;
            if (provProp != null)
            {
                var provVal = provProp.GetValue(obj);
                if (provVal != null && provVal != obj && currentDepth < maxDepth)
                {
                    this.ScanObjectForWindowSystemsRecursive(provVal, currentDepth + 1, maxDepth, visited);
                }
            }
        }
        catch
        {
            // Best effort DI inspection
        }
    }

    private static bool IsUiOrServiceType(Type type)
    {
        return type.Name.Contains("Window", StringComparison.OrdinalIgnoreCase) ||
               type.Name.Contains("Ui", StringComparison.OrdinalIgnoreCase) ||
               type.Name.Contains("Gui", StringComparison.OrdinalIgnoreCase) ||
               type.Namespace?.Contains("Gui", StringComparison.OrdinalIgnoreCase) == true ||
               type.Namespace?.Contains("Ui", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool ShouldTraverseType(Type type)
    {
        if (type == typeof(object))
            return true;

        if (type.IsPrimitive || type.IsEnum || type.IsValueType || type == typeof(string) || type == typeof(byte[]))
            return false;

        if (typeof(Delegate).IsAssignableFrom(type) || typeof(MemberInfo).IsAssignableFrom(type) || typeof(Assembly).IsAssignableFrom(type))
            return false;

        var ns = type.Namespace;
        if (ns != null)
        {
            if (ns.StartsWith("System.") || ns == "System")
            {
                return typeof(IEnumerable).IsAssignableFrom(type) ||
                       typeof(IServiceProvider).IsAssignableFrom(type);
            }

            if (ns.StartsWith("Microsoft."))
            {
                return typeof(IEnumerable).IsAssignableFrom(type) ||
                       typeof(IServiceProvider).IsAssignableFrom(type) ||
                       type.Name.Contains("ServiceProvider") ||
                       type.Name.Contains("ServiceScope") ||
                       type.Name.Contains("Engine");
            }

            // Skip Dalamud internal services / API types, except WindowSystem and Window
            if (ns.StartsWith("Dalamud") && !typeof(WindowSystem).IsAssignableFrom(type) && !typeof(IWindow).IsAssignableFrom(type))
                return false;

            if (ns.StartsWith("ImGuiNET") || ns.StartsWith("Dalamud.Bindings.ImGui") || ns.StartsWith("FFXIVClientStructs") || ns.StartsWith("Lumina"))
                return false;
        }

        return true;
    }

    private static bool ShouldTraverseMemberName(string name)
    {
        return name.Contains("Ui", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Window", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Manager", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("View", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Service", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Container", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Root", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Owned", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("Node", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldTraverseField(FieldInfo field)
    {
        if (typeof(WindowSystem).IsAssignableFrom(field.FieldType) || typeof(IWindow).IsAssignableFrom(field.FieldType))
            return true;

        if (!ShouldTraverseType(field.FieldType))
            return false;

        return ShouldTraverseMemberName(field.Name) ||
               ShouldTraverseMemberName(field.FieldType.Name) ||
               ShouldTraverseMemberName(field.DeclaringType?.Name ?? "");
    }

    private static bool ShouldTraverseProperty(PropertyInfo prop)
    {
        if (typeof(WindowSystem).IsAssignableFrom(prop.PropertyType) || typeof(IWindow).IsAssignableFrom(prop.PropertyType))
            return true;

        if (!prop.CanRead || prop.GetIndexParameters().Length > 0 || !ShouldTraverseType(prop.PropertyType))
            return false;

        return ShouldTraverseMemberName(prop.Name) ||
               ShouldTraverseMemberName(prop.PropertyType.Name) ||
               ShouldTraverseMemberName(prop.DeclaringType?.Name ?? "") ||
               typeof(IEnumerable).IsAssignableFrom(prop.PropertyType) ||
               typeof(IServiceProvider).IsAssignableFrom(prop.PropertyType);
    }

    public void TrackSingleWindow(IWindow window)
    {
        this.TrackSingleWindow(window, this.currentPluginContext?.InternalName, this.currentPluginContext?.IconBytes);
    }

    public void TrackSingleWindow(IWindow window, string? pluginInternalName, byte[]? iconBytes)
    {
        if (string.IsNullOrWhiteSpace(window.WindowName)) return;

        var tw = this.windowManagerService.RegisterWindow(window);

        if (pluginInternalName != null) tw.PluginInternalName = pluginInternalName;
        if (iconBytes != null) tw.IconBytes = iconBytes;

        InjectMinimizeButton(window, tw, this.windowManagerService);
    }

    public void TrackWindowSystem(WindowSystem ws)
    {
        this.TrackWindowSystem(ws, this.currentPluginContext?.InternalName, this.currentPluginContext?.IconBytes);
    }

    public void TrackWindowSystem(WindowSystem ws, string? pluginInternalName, byte[]? iconBytes)
    {
        this.knownWindowSystems[ws] = new PluginContext(pluginInternalName, iconBytes);

        // WindowSystem.Windows builds a fresh read-only copy on every single access, so it is read once
        // into a local here and in every other loop below (issue #51).
        var windows = ws.Windows;
        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            if (string.IsNullOrWhiteSpace(window.WindowName)) continue;

            var tw = this.windowManagerService.RegisterWindow(window);

            if (pluginInternalName != null) tw.PluginInternalName = pluginInternalName;
            if (iconBytes != null) tw.IconBytes = iconBytes;

            InjectMinimizeButton(window, tw, this.windowManagerService);
        }
    }

    /// <summary>
    /// Fast-path periodic check across known <see cref="WindowSystem"/> instances to discover
    /// dynamically added windows without full plugin reflection latency.
    /// </summary>
    [OnTick(interval: 250)]
    public void ScanKnownWindowSystems()
    {
        foreach (var (ws, context) in this.knownWindowSystems)
        {
            var windows = ws.Windows;
            for (var i = 0; i < windows.Count; i++)
            {
                var window = windows[i];
                if (string.IsNullOrWhiteSpace(window.WindowName)) continue;

                var tw = this.windowManagerService.RegisterWindow(window);

                if (context.InternalName != null) tw.PluginInternalName = context.InternalName;
                if (context.IconBytes != null) tw.IconBytes = context.IconBytes;

                InjectMinimizeButton(window, tw, this.windowManagerService);
            }
        }
    }

    /// <summary>
    /// Attempts an immediate fast-path resolution for a newly observed window against all known
    /// <see cref="WindowSystem"/> instances, avoiding discovery scan latency.
    /// </summary>
    public TrackedWindow? TryFastTrackWindow(string windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName)) return null;

        var windowId = WindowInfoHelper.GetWindowId(windowName);

        foreach (var adapter in this.knownUmbraWindows.Values)
        {
            if (string.Equals(adapter.WindowName, windowName, StringComparison.Ordinal) ||
                string.Equals(adapter.InstanceId, windowName, StringComparison.Ordinal) ||
                (!string.IsNullOrEmpty(windowId) && string.Equals(adapter.InstanceId, windowId, StringComparison.Ordinal)))
            {
                var tw = this.windowManagerService.RegisterWindow(adapter);
                tw.PluginInternalName = "Umbra";
                var coreIcon = this.TryLoadUmbraCoreIcon();
                if (coreIcon != null) tw.IconBytes = coreIcon;
                adapter.IsBeingMinimized = () => tw.IsMinimized;
                adapter.HookTitleBarMinimize(this.windowManagerService, tw);
                if (adapter.UnderlyingWindow.IsMinimized && !tw.IsMinimized)
                {
                    this.windowManagerService.Minimize(tw);
                }
                return tw;
            }
        }

        foreach (var (ws, context) in this.knownWindowSystems)
        {
            var windows = ws.Windows;
            for (var i = 0; i < windows.Count; i++)
            {
                var window = windows[i];
                if (string.Equals(window.WindowName, windowName, StringComparison.Ordinal))
                {
                    var tw = this.windowManagerService.RegisterWindow(window);

                    if (context.InternalName != null) tw.PluginInternalName = context.InternalName;
                    if (context.IconBytes != null) tw.IconBytes = context.IconBytes;

                    InjectMinimizeButton(window, tw, this.windowManagerService);
                    return tw;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Removes all tracking state for the specified plugin: drops its known window systems from the
    /// fast-track cache, unregisters its windows from <see cref="WindowManagerService"/>, and evicts its
    /// cached icon so a newly updated or reloaded plugin can be cleanly discovered and registered.
    /// </summary>
    public void UntrackPlugin(string internalName)
    {
        if (string.IsNullOrWhiteSpace(internalName)) return;

        if (string.Equals(internalName, "Umbra", StringComparison.OrdinalIgnoreCase))
        {
            this.UnhookUmbraWindowManagerEvents();
            this.knownUmbraWindows.Clear();
            this.iconCache.TryRemove("Umbra", out _);
            this.umbraCoreIcon = null;
            this.windowManagerService.UnregisterWindowsForPlugin("Umbra");
            return;
        }

        foreach (var (ws, context) in this.knownWindowSystems)
        {
            if (string.Equals(context.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
            {
                this.knownWindowSystems.TryRemove(ws, out _);
            }
        }

        this.iconCache.TryRemove(internalName, out _);
        this.iconDiskMisses.TryRemove(internalName, out _);
        this.knownPluginInstances.TryRemove(internalName, out _);
        this.windowManagerService.UnregisterWindowsForPlugin(internalName);
    }

    /// <summary>
    /// Scans a collection of installed plugins to discover window systems and windows, automatically
    /// detecting plugin reloads, updates, or uninstalls and keeping window registrations up to date.
    /// Runs the whole pass in one go; the live tick spreads the same pass across frames instead
    /// (<see cref="ContinueScan"/>, issue #62).
    /// </summary>
    internal void ScanInstalledPlugins(IEnumerable installedPlugins, IReadOnlyDictionary<string, string>? availableIconUrls)
    {
        this.BeginScanPass(installedPlugins, availableIconUrls, null);
        this.AdvanceScanPass(TimeSpan.MaxValue);
    }

    // State of one discovery pass. Plugins is a snapshot taken when the pass begins, so the installed-plugin
    // list changing between frames cannot skip or repeat a plugin; such a change also fires a lifecycle event,
    // which queues a follow-up pass.
    private sealed class ScanPass
    {
        public readonly List<object> Plugins = new();
        public readonly HashSet<string> Seen = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyDictionary<string, string>? IconUrls;
        public IReadOnlyList<Action>? TailSteps;
        public int Cursor;
        public bool StateChanged;
        public bool PluginStepFailed;

        public int StepCount => this.Plugins.Count + (this.TailSteps?.Count ?? 0);
    }

    /// <summary>
    /// Snapshots <paramref name="installedPlugins"/> and arms a pass over them. <paramref name="tailSteps"/>
    /// run after the last plugin and count as steps for budgeting (Dalamud core / Umbra window scans).
    /// </summary>
    internal void BeginScanPass(IEnumerable installedPlugins, IReadOnlyDictionary<string, string>? availableIconUrls, IReadOnlyList<Action>? tailSteps)
    {
        var p = this.pass;
        p.Plugins.Clear();
        p.Seen.Clear();
        p.IconUrls = availableIconUrls;
        p.TailSteps = tailSteps;
        p.Cursor = 0;
        p.StateChanged = false;
        p.PluginStepFailed = false;

        if (installedPlugins is IList list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var plugin = list[i];
                if (plugin != null) p.Plugins.Add(plugin);
            }
        }
        else
        {
            foreach (var plugin in installedPlugins)
            {
                if (plugin != null) p.Plugins.Add(plugin);
            }
        }

        this.passActive = true;
    }

    internal bool IsScanPassActive => this.passActive;

    /// <summary>
    /// Runs steps of the current pass until <paramref name="budget"/> is spent, always completing at least
    /// one so a pass makes progress even on a zero budget. Returns <c>true</c> once the pass has finished
    /// (or none is active), <c>false</c> if steps remain for a later frame.
    /// </summary>
    internal bool AdvanceScanPass(TimeSpan budget)
    {
        if (!this.passActive)
            return true;

        var p = this.pass;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (p.Cursor < p.StepCount)
        {
            var step = p.Cursor++;
            try
            {
                if (step < p.Plugins.Count)
                    this.ScanOnePlugin(p.Plugins[step], p);
                else
                    p.TailSteps![step - p.Plugins.Count]();
            }
            catch (Exception ex)
            {
                // One misbehaving plugin must not starve every plugin after it on each pass.
                if (step < p.Plugins.Count)
                    p.PluginStepFailed = true;

                this.LogScanFailure(ex);
            }

            if (elapsed.Elapsed >= budget)
                break;
        }

        if (p.Cursor < p.StepCount)
            return false;

        this.FinishScanPass(p);
        return true;
    }

    private void ScanOnePlugin(object localPlugin, ScanPass p)
    {
        var lpType = localPlugin.GetType();
        var lpPlan = GetLocalPluginPlan(lpType);
        var manifest = lpPlan.Manifest?.GetValue(localPlugin);
        var manifestPlan = manifest != null ? GetManifestPlan(manifest.GetType()) : null;
        var isHide = manifestPlan?.IsHide?.GetValue(manifest) as bool? ?? false;
        if (isHide) return;

        var internalName = manifestPlan?.InternalName?.GetValue(manifest) as string
                        ?? lpPlan.InternalName?.GetValue(localPlugin) as string;

        var pluginObj = lpPlan.Instance?.GetValue(localPlugin);

        if (!string.IsNullOrEmpty(internalName))
        {
            if (pluginObj == null)
            {
                if (this.hookedPluginInterface != null)
                {
                    var piCheckInstance = lpPlan.PluginInterface?.GetValue(localPlugin);
                    if (piCheckInstance != null && ReferenceEquals(this.hookedPluginInterface, piCheckInstance))
                    {
                        this.UnhookPluginInterface();
                    }
                }

                // Plugin is unloaded or disabled
                if (this.knownPluginInstances.TryRemove(internalName, out _))
                {
                    this.UntrackPlugin(internalName);
                    p.StateChanged = true;
                }
                return;
            }

            p.Seen.Add(internalName);

            // Detect reload or instance replacement
            if (this.knownPluginInstances.TryGetValue(internalName, out var oldObjRef))
            {
                if (!oldObjRef.TryGetTarget(out var oldObj) || !ReferenceEquals(oldObj, pluginObj))
                {
                    // Instance changed! Plugin reloaded or updated
                    this.UntrackPlugin(internalName);
                    this.knownPluginInstances[internalName] = new WeakReference<object>(pluginObj);
                    p.StateChanged = true;
                }
            }
            else
            {
                this.knownPluginInstances[internalName] = new WeakReference<object>(pluginObj);
                p.StateChanged = true;
            }
        }
        else if (pluginObj == null)
        {
            return;
        }

        var piInstance = lpPlan.PluginInterface?.GetValue(localPlugin);
        if (piInstance != null)
        {
            if (string.Equals(internalName, "Umbra", StringComparison.OrdinalIgnoreCase))
            {
                this.TryHookPreferredPluginInterfaceEvents(piInstance);
            }
            else
            {
                this.TryHookPluginInterfaceEvents(piInstance);
            }
        }

        this.currentPluginContext = this.ResolvePluginContext(localPlugin, manifest, p.IconUrls);
        try
        {
            this.ScanObjectForWindowSystems(pluginObj);
        }
        finally
        {
            this.currentPluginContext = null;
        }
    }

    private void FinishScanPass(ScanPass p)
    {
        // Untrack any plugins that were uninstalled or removed from installedPlugins. Only meaningful once
        // every plugin has been visited, and skipped if a plugin's step threw: its name may be missing from
        // the seen-set only because the scan never got far enough to record it.
        if (!p.PluginStepFailed)
        {
            foreach (var trackedName in this.knownPluginInstances.Keys)
            {
                if (!p.Seen.Contains(trackedName))
                {
                    this.UntrackPlugin(trackedName);
                    this.knownPluginInstances.TryRemove(trackedName, out _);
                    p.StateChanged = true;
                }
            }
        }

        var stateChanged = p.StateChanged;

        // Drop the snapshot so an idle tracker never pins unloaded plugin objects (collectible ALCs).
        p.Plugins.Clear();
        p.Seen.Clear();
        p.IconUrls = null;
        p.TailSteps = null;
        this.passActive = false;

        if (stateChanged)
        {
            this.PluginReloaded?.Invoke();
        }
    }

    private sealed class LocalPluginPlan
    {
        public readonly PropertyInfo? Manifest;
        public readonly PropertyInfo? InternalName;
        public readonly PropertyInfo? PluginInterface;
        public readonly PropertyInfo? DllFile;
        public readonly FieldInfo? Instance;

        public LocalPluginPlan(Type type)
        {
            const BindingFlags publicInstance = BindingFlags.Public | BindingFlags.Instance;
            this.Manifest = type.GetProperty("Manifest", publicInstance);
            this.InternalName = type.GetProperty("InternalName", publicInstance);
            this.PluginInterface = type.GetProperty("DalamudInterface", publicInstance)
                                ?? type.GetProperty("PluginInterface", publicInstance);
            this.DllFile = type.GetProperty("DllFile", publicInstance);
            this.Instance = type.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
        }
    }

    private sealed class ManifestPlan
    {
        public readonly PropertyInfo? InternalName;
        public readonly PropertyInfo? IconUrl;
        public readonly PropertyInfo? Dip17Channel;
        public readonly PropertyInfo? IsHide;

        public ManifestPlan(Type type)
        {
            this.InternalName = type.GetProperty("InternalName");
            this.IconUrl = type.GetProperty("IconUrl");
            this.Dip17Channel = type.GetProperty("Dip17Channel");
            this.IsHide = type.GetProperty("IsHide");
        }
    }

    // Weak-keyed for the same reason as TypeScanCache: LocalPlugin and manifest types are not collectible,
    // but remote-manifest types and test doubles may be, and a strong Dictionary<Type,_> would pin them.
    // Resolved once per type instead of 5-8 GetProperty/GetField calls per plugin per pass (issue #62).
    private static readonly ConditionalWeakTable<Type, LocalPluginPlan> LocalPluginPlans = new();
    private static readonly ConditionalWeakTable<Type, ManifestPlan> ManifestPlans = new();

    // Diagnostics only: how many plans have been built, so tests can assert the caches actually hit.
    internal static long PluginAccessPlansBuilt;

    private static LocalPluginPlan GetLocalPluginPlan(Type type) =>
        LocalPluginPlans.GetValue(type, static t =>
        {
            System.Threading.Interlocked.Increment(ref PluginAccessPlansBuilt);
            return new LocalPluginPlan(t);
        });

    private static ManifestPlan GetManifestPlan(Type type) =>
        ManifestPlans.GetValue(type, static t =>
        {
            System.Threading.Interlocked.Increment(ref PluginAccessPlansBuilt);
            return new ManifestPlan(t);
        });

    internal void TryHookPluginManagerEvents(object pmInstance)
    {
        if (this.hookedPluginManager != null) return;

        try
        {
            var pmType = pmInstance.GetType();
            var installedEvent = pmType.GetEvent("OnInstalledPluginsChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (installedEvent != null)
            {
                var actionMethod = typeof(DalamudWindowTracker).GetMethod(nameof(this.OnInstalledPluginsChanged), BindingFlags.NonPublic | BindingFlags.Instance);
                if (actionMethod != null && installedEvent.EventHandlerType != null)
                {
                    var handler = Delegate.CreateDelegate(installedEvent.EventHandlerType, this, actionMethod);
                    installedEvent.AddEventHandler(pmInstance, handler);
                    this.onInstalledPluginsChangedHandler = handler;
                    this.hookedPluginManager = pmInstance;
                }
            }
        }
        catch
        {
            // Best effort
        }
    }

    internal void TryHookPluginInterfaceEvents(object piInstance)
    {
        this.TryHookPluginInterfaceEventsInternal(piInstance, false);
    }

    internal void TryHookPreferredPluginInterfaceEvents(object piInstance)
    {
        this.TryHookPluginInterfaceEventsInternal(piInstance, true);
    }

    private void TryHookPluginInterfaceEventsInternal(object piInstance, bool isPreferred)
    {
        if (this.hookedPluginInterface != null && !isPreferred) return;
        if (ReferenceEquals(this.hookedPluginInterface, piInstance)) return;

        try
        {
            if (this.hookedPluginInterface != null && isPreferred)
            {
                this.UnhookPluginInterface();
            }

            if (piInstance is Dalamud.Plugin.IDalamudPluginInterface dpi)
            {
                dpi.ActivePluginsChanged += this.OnActivePluginsChanged;
                this.hookedPluginInterface = dpi;
                return;
            }

            var piType = piInstance.GetType();
            var activeEvent = piType.GetEvent("ActivePluginsChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (activeEvent != null)
            {
                var actionMethod = typeof(DalamudWindowTracker).GetMethod(nameof(this.OnActivePluginsChanged), BindingFlags.NonPublic | BindingFlags.Instance);
                if (actionMethod != null && activeEvent.EventHandlerType != null)
                {
                    var handler = Delegate.CreateDelegate(activeEvent.EventHandlerType, this, actionMethod);
                    activeEvent.AddEventHandler(piInstance, handler);
                    this.onActivePluginsChangedHandler = handler;
                    this.hookedPluginInterface = piInstance;
                }
            }
        }
        catch
        {
            // Best effort
        }
    }

    private void UnhookPluginInterface()
    {
        if (this.hookedPluginInterface == null) return;

        try
        {
            if (this.hookedPluginInterface is Dalamud.Plugin.IDalamudPluginInterface dpi)
            {
                dpi.ActivePluginsChanged -= this.OnActivePluginsChanged;
            }
            else if (this.onActivePluginsChangedHandler != null)
            {
                var piType = this.hookedPluginInterface.GetType();
                var activeEvent = piType.GetEvent("ActivePluginsChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                activeEvent?.RemoveEventHandler(this.hookedPluginInterface, this.onActivePluginsChangedHandler);
            }
        }
        catch
        {
            // Best effort
        }

        this.hookedPluginInterface = null;
        this.onActivePluginsChangedHandler = null;
    }

    // Lifecycle events only request a pass: they can fire off the framework thread, and the scan traverses
    // plugin objects and mutates window state, so it runs from the next ContinueScan tick (issue #62).
    private void OnInstalledPluginsChanged()
    {
        this.RequestScan();
    }

    private void OnActivePluginsChanged(Dalamud.Plugin.IActivePluginsChangedEventArgs args)
    {
        this.RequestScan();
    }

    public void Dispose()
    {
        if (this.hookedPluginManager != null && this.onInstalledPluginsChangedHandler != null)
        {
            try
            {
                var pmType = this.hookedPluginManager.GetType();
                var installedEvent = pmType.GetEvent("OnInstalledPluginsChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                installedEvent?.RemoveEventHandler(this.hookedPluginManager, this.onInstalledPluginsChangedHandler);
            }
            catch
            {
                // Best effort
            }
            this.hookedPluginManager = null;
            this.onInstalledPluginsChangedHandler = null;
        }

        this.UnhookPluginInterface();
        this.UnhookUmbraWindowManagerEvents();
        this.knownUmbraWindows.Clear();
        this.passActive = false;
        this.pass.Plugins.Clear();
    }


    internal sealed record PluginContext(string? InternalName, byte[]? IconBytes);



}
