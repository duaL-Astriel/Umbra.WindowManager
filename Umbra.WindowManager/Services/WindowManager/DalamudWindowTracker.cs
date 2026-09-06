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

    public static void InjectMinimizeButton(IWindow window, TrackedWindow tracked, WindowManagerService service)
    {
        if (window.TitleBarButtons == null)
            return;

        // Overlays and non-interactive windows should not have minimize buttons injected
        if (!tracked.IsManageable)
            return;

        // Suppress native ImGui collapse triangle in favor of toolbar minimization
        window.Flags |= Dalamud.Bindings.ImGui.ImGuiWindowFlags.NoCollapse;

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

        // Idempotent per window *instance* rather than per icon: a plugin may ship its own
        // WindowMinimize button, and we must still inject (and stay bound to) our own. Matching on
        // icon alone would skip injection and leave our minimize action unwired (issue #8.1).
        if (InjectedButtons.TryGetValue(window, out var existing) && window.TitleBarButtons.Contains(existing))
            return;

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

    [OnTick(interval: 2000)]
    public void ScanPlugins()
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
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        for (var currentType = obj.GetType(); currentType != null && currentType != typeof(object); currentType = currentType.BaseType)
        {
            foreach (var prop in currentType.GetProperties(flags))
            {
                try
                {
                    if (prop.CanRead && prop.GetIndexParameters().Length == 0 && typeof(WindowSystem).IsAssignableFrom(prop.PropertyType))
                    {
                        if (prop.GetValue(obj) is WindowSystem ws)
                            this.TrackWindowSystem(ws);
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
                }
                catch
                {
                    // Expected: field access may fail for exotic plugin layouts.
                }
            }
        }
    }

    public void TrackWindowSystem(WindowSystem ws)
    {
        this.TrackWindowSystem(ws, this.currentPluginContext?.InternalName, this.currentPluginContext?.IconBytes);
    }

    public void TrackWindowSystem(WindowSystem ws, string? pluginInternalName, byte[]? iconBytes)
    {
        foreach (var window in ws.Windows)
        {
            if (string.IsNullOrWhiteSpace(window.WindowName)) continue;

            var tw = this.windowManagerService.RegisterWindow(window);

            if (pluginInternalName != null) tw.PluginInternalName = pluginInternalName;
            if (iconBytes != null) tw.IconBytes = iconBytes;

            InjectMinimizeButton(window, tw, this.windowManagerService);
        }
    }

    internal sealed record PluginContext(string? InternalName, byte[]? IconBytes);
}
