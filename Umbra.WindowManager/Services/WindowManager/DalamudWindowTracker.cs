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

    [OnTick(interval: 2000)]
    public void ScanPlugins()
    {
        if (System.Threading.Interlocked.CompareExchange(ref this.isScanning, 1, 0) != 0)
        {
            System.Threading.Interlocked.Exchange(ref this.scanPending, 1);
            return;
        }

        try
        {
            do
            {
                System.Threading.Interlocked.Exchange(ref this.scanPending, 0);
                this.ScanPluginsCore();
            }
            while (System.Threading.Interlocked.CompareExchange(ref this.scanPending, 0, 1) == 1);
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref this.isScanning, 0);
        }
    }

    private void ScanPluginsCore()
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

            Dictionary<string, string>? availableIconUrls = null;
            try
            {
                var availableProp = pmType.GetProperty("AvailablePlugins", BindingFlags.Public | BindingFlags.Instance);
                if (availableProp?.GetValue(pmInstance) is IEnumerable availablePlugins)
                {
                    availableIconUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var remotePlugin in availablePlugins)
                    {
                        if (remotePlugin == null) continue;
                        var rType = remotePlugin.GetType();
                        var rName = rType.GetProperty("InternalName")?.GetValue(remotePlugin) as string;
                        var rIcon = rType.GetProperty("IconUrl")?.GetValue(remotePlugin) as string;
                        if (!string.IsNullOrEmpty(rName) && !string.IsNullOrWhiteSpace(rIcon))
                            availableIconUrls[rName] = rIcon;
                    }
                }
            }
            catch
            {
                // Best effort
            }

            this.ScanInstalledPlugins(installedPlugins, availableIconUrls);

            // Dalamud's own core windows (Plugin Installer, Settings, Console, Data, Changelog, ...) live
            // in DalamudInterface's private WindowSystem, not in PluginManager.InstalledPlugins, so they are
            // never reached by the loop above. Resolve DalamudInterface via the same Service<T> pattern and
            // scan it under a synthetic "Dalamud" context (issue #36).
            var diType = logAssembly.GetType("Dalamud.Interface.Internal.DalamudInterface");
            if (diType != null)
            {
                var diInstance = ResolveDalamudService(serviceOpenType, diType);
                if (diInstance != null)
                    this.ScanDalamudCoreWindows(diInstance, this.TryLoadDalamudCoreIcon(logAssembly, serviceOpenType));
            }

            // Umbra's own internal windows (Settings, Widget Browser, Variable Editor, Toolbar Profile
            // Manager, Installer) live in Umbra.Windows.WindowManager, not in Dalamud's WindowSystem.
            // Discover and register them under the synthetic "Umbra" plugin context (issue #44).
            this.ScanUmbraWindows();
        }
        catch (Exception ex)
        {
            // Reflection into Dalamud internals can break across Dalamud updates (renamed
            // Service`1 / instanceTcs / PluginManager / ServiceContainer). Surface it, throttled,
            // instead of failing silently so discovery breakage is diagnosable (issue #8.2).
            if (this.scanFailLogCounter++ % 30 == 0)
                Logger.Warning($"[WindowManager] Plugin discovery scan failed via reflection: {ex.GetType().Name}: {ex.Message}");
        }
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
            var lpType = localPlugin.GetType();
            manifest ??= lpType.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Instance)?.GetValue(localPlugin);
            var internalName = manifest?.GetType().GetProperty("InternalName")?.GetValue(manifest) as string;
            var manifestIconUrl = manifest?.GetType().GetProperty("IconUrl")?.GetValue(manifest) as string;
            var dip17Channel = manifest?.GetType().GetProperty("Dip17Channel")?.GetValue(manifest) as string;

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

    internal byte[]? LoadPluginIcon(object localPlugin, string? internalName, string? iconUrl)
    {
        try
        {
            var dllFile = localPlugin.GetType().GetProperty("DllFile", BindingFlags.Public | BindingFlags.Instance)?.GetValue(localPlugin) as FileInfo;
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
                {
                    return File.ReadAllBytes(cachedPath);
                }

                // If not cached, trigger background download if IconUrl is available
                if (!string.IsNullOrWhiteSpace(iconUrl) && Uri.TryCreate(iconUrl, UriKind.Absolute, out _))
                {
                    this.TriggerIconDownload(internalName, iconUrl, cachedPath);
                }
            }
        }
        catch
        {
            // Icon is optional; fall back to a monogram in the widget.
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
                    if (IsUiOrServiceType(ait) || ShouldTraverseMemberName(ait.Name))
                    {
                        this.ScanObjectForWindowSystemsRecursive(actualItem, currentDepth + 1, maxDepth, visited);
                    }
                }
            }
        }

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        for (var currentType = obj.GetType(); currentType != null && currentType != typeof(object); currentType = currentType.BaseType)
        {
            foreach (var prop in currentType.GetProperties(flags))
            {
                try
                {
                    if (prop.CanRead && prop.GetIndexParameters().Length == 0)
                    {
                        if (typeof(WindowSystem).IsAssignableFrom(prop.PropertyType))
                        {
                            if (prop.GetValue(obj) is WindowSystem ws)
                                this.TrackWindowSystem(ws);
                        }
                        else if (typeof(IWindow).IsAssignableFrom(prop.PropertyType))
                        {
                            if (prop.GetValue(obj) is IWindow w)
                                this.TrackSingleWindow(w);
                        }
                        else if (currentDepth < maxDepth && ShouldTraverseProperty(prop))
                        {
                            var val = prop.GetValue(obj);
                            if (val is WindowSystem ws)
                            {
                                this.TrackWindowSystem(ws);
                            }
                            else if (val is IWindow w)
                            {
                                this.TrackSingleWindow(w);
                            }
                            else if (val != null && ShouldTraverseType(val.GetType()))
                            {
                                this.ScanObjectForWindowSystemsRecursive(val, currentDepth + 1, maxDepth, visited);
                            }
                        }
                    }
                }
                catch
                {
                    // Expected: plugin property getters may throw when accessed out of context.
                }
            }

            foreach (var field in currentType.GetFields(flags))
            {
                try
                {
                    if (typeof(WindowSystem).IsAssignableFrom(field.FieldType))
                    {
                        if (field.GetValue(obj) is WindowSystem ws)
                            this.TrackWindowSystem(ws);
                    }
                    else if (typeof(IWindow).IsAssignableFrom(field.FieldType))
                    {
                        if (field.GetValue(obj) is IWindow w)
                            this.TrackSingleWindow(w);
                    }
                    else if (currentDepth < maxDepth && ShouldTraverseField(field))
                    {
                        var val = field.GetValue(obj);
                        if (val is WindowSystem ws)
                        {
                            this.TrackWindowSystem(ws);
                        }
                        else if (val is IWindow w)
                        {
                            this.TrackSingleWindow(w);
                        }
                        else if (val != null && ShouldTraverseType(val.GetType()))
                        {
                            this.ScanObjectForWindowSystemsRecursive(val, currentDepth + 1, maxDepth, visited);
                        }
                    }
                }
                catch
                {
                    // Expected: field access may fail for exotic plugin layouts.
                }
            }
        }
    }

    private void TryScanServiceProvider(object obj, int currentDepth, int maxDepth, HashSet<object> visited)
    {
        try
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var objType = obj.GetType();

            var isServiceProvider = typeof(IServiceProvider).IsAssignableFrom(objType) ||
                                   objType.Name.Contains("ServiceProvider");

            if (isServiceProvider)
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
                var root = objType.GetProperty("Root", flags)?.GetValue(obj)
                        ?? objType.GetProperty("_root", flags)?.GetValue(obj)
                        ?? objType.GetField("<Root>k__BackingField", flags)?.GetValue(obj)
                        ?? objType.GetField("<_root>k__BackingField", flags)?.GetValue(obj)
                        ?? objType.GetField("_root", flags)?.GetValue(obj)
                        ?? obj;
                var rootType = root.GetType();

                // Extract resolved singleton services
                var dict = (rootType.GetProperty("ResolvedServices", flags)?.GetValue(root)
                         ?? rootType.GetProperty("_resolvedServices", flags)?.GetValue(root)
                         ?? rootType.GetField("<ResolvedServices>k__BackingField", flags)?.GetValue(root)
                         ?? rootType.GetField("<_resolvedServices>k__BackingField", flags)?.GetValue(root)
                         ?? rootType.GetField("_resolvedServices", flags)?.GetValue(root)
                         ?? rootType.GetField("ResolvedServices", flags)?.GetValue(root)) as IDictionary;

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
                        else if (currentDepth < maxDepth && (IsUiOrServiceType(val.GetType()) || ShouldTraverseMemberName(val.GetType().Name)))
                        {
                            this.ScanObjectForWindowSystemsRecursive(val, currentDepth + 1, maxDepth, visited);
                        }
                    }
                }

                // Extract disposables list
                var disposables = (rootType.GetProperty("Disposables", flags)?.GetValue(root)
                                ?? rootType.GetProperty("_disposables", flags)?.GetValue(root)
                                ?? rootType.GetField("<Disposables>k__BackingField", flags)?.GetValue(root)
                                ?? rootType.GetField("<_disposables>k__BackingField", flags)?.GetValue(root)
                                ?? rootType.GetField("_disposables", flags)?.GetValue(root)) as IEnumerable;

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
                        else if (currentDepth < maxDepth && (IsUiOrServiceType(d.GetType()) || ShouldTraverseMemberName(d.GetType().Name)))
                        {
                            this.ScanObjectForWindowSystemsRecursive(d, currentDepth + 1, maxDepth, visited);
                        }
                    }
                }
            }

            // Luna.ServiceManager or similar service managers:
            // Check for _ownedObjects (HashSet<IDisposable>)
            var ownedField = objType.GetField("_ownedObjects", flags)
                          ?? objType.GetField("<_ownedObjects>k__BackingField", flags);
            var owned = (ownedField?.GetValue(obj)
                      ?? objType.GetProperty("_ownedObjects", flags)?.GetValue(obj)
                      ?? objType.GetProperty("OwnedObjects", flags)?.GetValue(obj)) as IEnumerable;

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
                    else if (currentDepth < maxDepth && (IsUiOrServiceType(o.GetType()) || ShouldTraverseMemberName(o.GetType().Name)))
                    {
                        this.ScanObjectForWindowSystemsRecursive(o, currentDepth + 1, maxDepth, visited);
                    }
                }
            }

            // Check for Provider property on service managers (e.g. Luna.ServiceManager.Provider)
            var provProp = objType.GetProperty("Provider", flags) ?? objType.GetProperty("Services", flags);
            if (provProp != null && provProp.CanRead && provProp.GetIndexParameters().Length == 0)
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

        foreach (var window in ws.Windows)
        {
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
            foreach (var window in ws.Windows)
            {
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
            for (var i = 0; i < ws.Windows.Count; i++)
            {
                var window = ws.Windows[i];
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
        this.knownPluginInstances.TryRemove(internalName, out _);
        this.windowManagerService.UnregisterWindowsForPlugin(internalName);
    }

    /// <summary>
    /// Scans a collection of installed plugins to discover window systems and windows, automatically
    /// detecting plugin reloads, updates, or uninstalls and keeping window registrations up to date.
    /// </summary>
    internal void ScanInstalledPlugins(IEnumerable installedPlugins, IReadOnlyDictionary<string, string>? availableIconUrls)
    {
        var seenPlugins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stateChanged = false;

        var pluginsToScan = new List<object>();
        if (installedPlugins is IList list)
        {
            for (var i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p != null) pluginsToScan.Add(p);
            }
        }
        else
        {
            foreach (var p in installedPlugins)
            {
                if (p != null) pluginsToScan.Add(p);
            }
        }

        foreach (var localPlugin in pluginsToScan)
        {
            var lpType = localPlugin.GetType();
            var manifest = lpType.GetProperty("Manifest", BindingFlags.Public | BindingFlags.Instance)?.GetValue(localPlugin);
            var isHide = manifest?.GetType().GetProperty("IsHide")?.GetValue(manifest) as bool? ?? false;
            if (isHide) continue;

            var internalName = manifest?.GetType().GetProperty("InternalName")?.GetValue(manifest) as string
                            ?? lpType.GetProperty("InternalName", BindingFlags.Public | BindingFlags.Instance)?.GetValue(localPlugin) as string;

            var pluginInstanceField = lpType.GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
            var pluginObj = pluginInstanceField?.GetValue(localPlugin);

            if (!string.IsNullOrEmpty(internalName))
            {
                if (pluginObj == null)
                {
                    if (this.hookedPluginInterface != null)
                    {
                        var piCheckProp = lpType.GetProperty("DalamudInterface", BindingFlags.Public | BindingFlags.Instance)
                                       ?? lpType.GetProperty("PluginInterface", BindingFlags.Public | BindingFlags.Instance);
                        var piCheckInstance = piCheckProp?.GetValue(localPlugin);
                        if (piCheckInstance != null && ReferenceEquals(this.hookedPluginInterface, piCheckInstance))
                        {
                            this.UnhookPluginInterface();
                        }
                    }

                    // Plugin is unloaded or disabled
                    if (this.knownPluginInstances.TryRemove(internalName, out _))
                    {
                        this.UntrackPlugin(internalName);
                        stateChanged = true;
                    }
                    continue;
                }

                seenPlugins.Add(internalName);

                // Detect reload or instance replacement
                if (this.knownPluginInstances.TryGetValue(internalName, out var oldObjRef))
                {
                    if (!oldObjRef.TryGetTarget(out var oldObj) || !ReferenceEquals(oldObj, pluginObj))
                    {
                        // Instance changed! Plugin reloaded or updated
                        this.UntrackPlugin(internalName);
                        this.knownPluginInstances[internalName] = new WeakReference<object>(pluginObj);
                        stateChanged = true;
                    }
                }
                else
                {
                    this.knownPluginInstances[internalName] = new WeakReference<object>(pluginObj);
                    stateChanged = true;
                }
            }
            else if (pluginObj == null)
            {
                continue;
            }

            var piProp = lpType.GetProperty("DalamudInterface", BindingFlags.Public | BindingFlags.Instance)
                      ?? lpType.GetProperty("PluginInterface", BindingFlags.Public | BindingFlags.Instance);
            var piInstance = piProp?.GetValue(localPlugin);
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

            this.currentPluginContext = this.ResolvePluginContext(localPlugin, manifest, availableIconUrls);
            try
            {
                this.ScanObjectForWindowSystems(pluginObj);
            }
            finally
            {
                this.currentPluginContext = null;
            }
        }

        // Untrack any plugins that were uninstalled or removed from installedPlugins
        foreach (var trackedName in this.knownPluginInstances.Keys)
        {
            if (!seenPlugins.Contains(trackedName))
            {
                this.UntrackPlugin(trackedName);
                this.knownPluginInstances.TryRemove(trackedName, out _);
                stateChanged = true;
            }
        }

        if (stateChanged)
        {
            this.PluginReloaded?.Invoke();
        }
    }

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

    private void OnInstalledPluginsChanged()
    {
        this.ScanPlugins();
    }

    private void OnActivePluginsChanged(Dalamud.Plugin.IActivePluginsChangedEventArgs args)
    {
        this.ScanPlugins();
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
    }


    internal sealed record PluginContext(string? InternalName, byte[]? IconBytes);



}
