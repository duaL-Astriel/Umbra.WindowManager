// Umbra.WindowManager/Services/WindowManager/ImGuiContextMonitor.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Umbra.Common;

namespace Umbra.WindowManager.Services.WindowManager;

[Service]
public class ImGuiContextMonitor
{
    private readonly WindowManagerService windowManager;

    public ImGuiContextMonitor(WindowManagerService windowManager)
    {
        this.windowManager = windowManager;
    }

    [OnDraw(executionOrder: 10)]
    public unsafe void OnDraw()
    {
        var ctx = ImGui.GetCurrentContext();
        if (ctx.IsNull) return;

        var trackedList = this.windowManager.GetTrackedWindows();
        var trackedMap = trackedList.ToDictionary(t => t.WindowName, t => t);

        // Group tracking dictionary: dockId -> list of windows in that dock
        var dockGroups = new Dictionary<uint, List<TrackedWindow>>();
        var dockActiveTab = new Dictionary<uint, string>();

        for (var i = 0; i < ctx.Windows.Size; i++)
        {
            var win = ctx.Windows[i];
            if (win.IsNull) continue;

            var name = win.Name != null ? System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)win.Name) : null;
            if (string.IsNullOrEmpty(name) || !trackedMap.TryGetValue(name, out var tracked))
                continue;

            // 1. Native collapse guard: if collapsed natively, cancel it and fully minimize
            if (win.Collapsed)
            {
                win.Collapsed = false;
                this.windowManager.Minimize(tracked);
                continue;
            }

            // 2. Dock node tracking
            if (win.DockIsActive && !win.DockNode.IsNull)
            {
                var dockId = win.DockId;
                if (!dockGroups.TryGetValue(dockId, out var groupMembers))
                {
                    groupMembers = [];
                    dockGroups[dockId] = groupMembers;
                }
                groupMembers.Add(tracked);

                if (win.DockTabIsVisible)
                {
                    dockActiveTab[dockId] = name;
                }
            }
        }

        // Register multi-window dock groups
        foreach (var (dockId, members) in dockGroups)
        {
            if (members.Count > 1)
            {
                var activeName = dockActiveTab.GetValueOrDefault(dockId, members[0].WindowName);
                this.windowManager.RegisterDockGroup($"dock_{dockId}", activeName, members);
            }
        }
    }
}
