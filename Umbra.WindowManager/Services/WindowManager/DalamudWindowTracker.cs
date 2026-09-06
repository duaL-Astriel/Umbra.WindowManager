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
public class DalamudWindowTracker
{
    private readonly WindowManagerService windowManagerService;

    // Marks the minimize button we injected, keyed by the window *instance*. Entries disappear
    // automatically once a window is garbage collected, so re-instantiated windows are re-injected.
    private static readonly ConditionalWeakTable<IWindow, TitleBarButton> InjectedButtons = new();

    // Caches resolved plugin icon bytes by plugin internal name (null = looked up, none found).
    private readonly ConcurrentDictionary<string, byte[]?> iconCache = new();
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
        // Overlays and non-interactive windows should not have minimize buttons injected
        if (!CanInjectMinimizeButton(window))
            return;

        // Idempotent fast exit: if our button is already injected and present, return immediately
        if (InjectedButtons.TryGetValue(window, out var existing) && window.TitleBarButtons.Contains(existing))
            return;

        // Suppress native ImGui collapse triangle in favor of toolbar minimization
        window.Flags |= Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoCollapse;

        // Clean up any duplicate minimize buttons accumulated across assembly hot-reloads
        TitleBarButton? existingInList = null;
        for (var i = window.TitleBarButtons.Count - 1; i >= 0; i--)
        {
            var b = window.TitleBarButtons[i];
            if (b.Icon == FontAwesomeIcon.WindowMinimize && b.Priority == int.MaxValue - 1)
            {
                if (existingInList == null)
                {
                    existingInList = b;
                }
                else
                {
                    window.TitleBarButtons.RemoveAt(i);
                }
            }
        }

        if (existingInList != null)
        {
            existingInList.Click = _ => service.Minimize(tracked);
            InjectedButtons.AddOrUpdate(window, existingInList);
            return;
        }

        // Hook any plugin-provided minimize buttons so clicking them also delegates to WindowManagerService.Minimize
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

        var button = new TitleBarButton
        {
            Icon = FontAwesomeIcon.WindowMinimize,
            Priority = int.MaxValue - 1,
            Click = _ => service.Minimize(tracked),
            ShowTooltip = () =>
            {
                if (Dalamud.Bindings.ImGui.ImGui.IsItemHovered())
                    Dalamud.Bindings.ImGui.ImGui.SetTooltip("Minimize to Umbra Toolbar");
            }
        };

        window.TitleBarButtons.Add(button);
        InjectedButtons.AddOrUpdate(window, button);
    }

    /// <summary>
    /// Removes the minimize button we injected into <paramref name="window"/>, if present. Docked windows
    /// in a multi-tab dock node have no real title bar, so Dalamud renders the injected button inside the
    /// client content area where it collides with (and is drawn beneath) the plugin's own controls,
    /// making it visually obscured and unclickable (issue #25). For those windows we drop the raw button
    /// and rely on the toolbar / context-menu minimize actions instead. The window becomes eligible for
    /// re-injection via <see cref="InjectMinimizeButton"/> once it undocks.
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
    }

    private int isScanning;

    [OnTick(interval: 2000)]
    public void ScanPlugins()
    {
        if (System.Threading.Interlocked.CompareExchange(ref this.isScanning, 1, 0) != 0)
            return;

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
            var scType = logAssembly.GetType("Dalamud.IoC.Internal.ServiceContainer");
            if (scType != null)
            {
                var scService = serviceOpenType.MakeGenericType(scType);
                var scTcsField = scService.GetField("instanceTcs", BindingFlags.NonPublic | BindingFlags.Static);
                var scTcs = scTcsField?.GetValue(null);
                if (scTcs != null)
                {
                    var scTaskProp = scTcs.GetType().GetProperty("Task");
                    if (scTaskProp?.GetValue(scTcs) is not System.Threading.Tasks.Task scTask || !scTask.IsCompleted)
                        return;
                }
            }

            var serviceGeneric = serviceOpenType.MakeGenericType(pmType);
            var tcsField = serviceGeneric.GetField("instanceTcs", BindingFlags.NonPublic | BindingFlags.Static);
            var tcs = tcsField?.GetValue(null);
            if (tcs == null) return;

            var taskProp = tcs.GetType().GetProperty("Task");
            if (taskProp?.GetValue(tcs) is not System.Threading.Tasks.Task task || !task.IsCompleted) return;

            var getMethod = serviceGeneric.GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var pmInstance = getMethod?.Invoke(null, null);
            if (pmInstance == null) return;

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

            foreach (var localPlugin in installedPlugins)
            {
                if (localPlugin == null) continue;

                var manifest = localPlugin.GetType().GetProperty("Manifest", BindingFlags.Public | BindingFlags.Instance)?.GetValue(localPlugin);
                var isHide = manifest?.GetType().GetProperty("IsHide")?.GetValue(manifest) as bool? ?? false;
                if (isHide) continue;

                var pluginInstanceField = localPlugin.GetType().GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
                var pluginObj = pluginInstanceField?.GetValue(localPlugin);
                if (pluginObj == null) continue;

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
            var iconUrl = manifest?.GetType().GetProperty("IconUrl")?.GetValue(manifest) as string;
            if (string.IsNullOrWhiteSpace(iconUrl) && !string.IsNullOrEmpty(internalName) && availableIconUrls != null)
            {
                availableIconUrls.TryGetValue(internalName, out iconUrl);
            }

            byte[]? icon = null;
            if (!string.IsNullOrEmpty(internalName))
            {
                if (!this.iconCache.TryGetValue(internalName, out icon))
                {
                    icon = this.LoadPluginIcon(localPlugin, internalName, iconUrl);
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
                var bytes = await HttpClient.GetByteArrayAsync(url).ConfigureAwait(false);
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

                    this.iconCache[internalName] = bytes;

                    var trackedWindows = this.windowManagerService.GetTrackedWindows();
                    for (var i = 0; i < trackedWindows.Count; i++)
                    {
                        var tw = trackedWindows[i];
                        if (tw.PluginInternalName == internalName)
                            tw.IconBytes = bytes;
                    }
                }
            }
            catch
            {
                // Network download failure; fallback to monogram
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

    internal sealed record PluginContext(string? InternalName, byte[]? IconBytes);
}
