using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Umbra.Common;

namespace Umbra.WindowManager.Services.WindowManager;

[Service]
public class DalamudWindowTracker
{
    private readonly WindowManagerService windowManagerService;

    public DalamudWindowTracker(WindowManagerService windowManagerService)
    {
        this.windowManagerService = windowManagerService;
        this.ScanPlugins();
    }

    public static void InjectMinimizeButton(IWindow window, TrackedWindow tracked, WindowManagerService service)
    {
        if (window.TitleBarButtons == null)
            return;

        if (window.TitleBarButtons.Any(b => b.Icon == FontAwesomeIcon.WindowMinimize))
            return;

        window.TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.WindowMinimize,
            Priority = int.MaxValue - 1,
            Click = _ => service.Minimize(tracked),
            ShowTooltip = () =>
            {
                if (Dalamud.Bindings.ImGui.ImGui.IsItemHovered())
                    Dalamud.Bindings.ImGui.ImGui.SetTooltip("Minimize to Umbra Toolbar");
            }
        });
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

            foreach (var localPlugin in installedPlugins)
            {
                if (localPlugin == null) continue;
                var pluginInstanceField = localPlugin.GetType().GetField("instance", BindingFlags.NonPublic | BindingFlags.Instance);
                var pluginObj = pluginInstanceField?.GetValue(localPlugin);
                if (pluginObj == null) continue;

                this.ScanObjectForWindowSystems(pluginObj);
            }
        }
        catch
        {
            // Defensive: logging or ignore reflection errors
        }
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
                    // Defensive against property getters throwing
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
                    // Defensive against field access issues
                }
            }
        }
    }

    public void TrackWindowSystem(WindowSystem ws)
    {
        foreach (var window in ws.Windows)
        {
            if (string.IsNullOrWhiteSpace(window.WindowName)) continue;
            var tw = this.windowManagerService.RegisterWindow(window);
            InjectMinimizeButton(window, tw, this.windowManagerService);
        }
    }
}
