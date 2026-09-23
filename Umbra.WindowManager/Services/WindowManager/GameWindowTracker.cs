using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.IoC;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Umbra.Common;

namespace Umbra.WindowManager.Services.WindowManager;

/// <summary>
/// Monitors native FFXIV in-game windows via <see cref="IAddonLifecycle"/> and <see cref="RaptureAtkUnitManager"/>,
/// creating and registering <see cref="GameWindowAdapter"/> instances with <see cref="WindowManagerService"/>.
/// </summary>
[Service]
public class GameWindowTracker : IDisposable
{
    private readonly WindowManagerService windowManagerService;
    private readonly IAddonLifecycle? addonLifecycle;
    private readonly IDataManager? dataManager;
    private readonly ConcurrentDictionary<string, GameWindowAdapter> knownAdapters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<uint, byte[]?> iconCache = new();

    [PluginService]
    private IAddonLifecycle? InjectedAddonLifecycle { get; set; }

    [PluginService]
    private IDataManager? InjectedDataManager { get; set; }

    public GameWindowTracker(WindowManagerService windowManagerService)
        : this(windowManagerService, null, null, isTest: false)
    {
    }

    private GameWindowTracker(
        WindowManagerService windowManagerService,
        IAddonLifecycle? addonLifecycle,
        IDataManager? dataManager,
        bool isTest)
    {
        this.windowManagerService = windowManagerService;

        if (!isTest && (addonLifecycle == null || dataManager == null))
        {
            try
            {
                Framework.DalamudPlugin?.Inject(this);
            }
            catch
            {
                // Best effort
            }
        }

        this.addonLifecycle = addonLifecycle ?? this.InjectedAddonLifecycle ?? TryResolveDalamudService<IAddonLifecycle>();
        this.dataManager = dataManager ?? this.InjectedDataManager ?? TryResolveDalamudService<IDataManager>();

        this.HookLifecycleEvents();
        this.ScanActiveWindows();
    }

    internal static GameWindowTracker CreateForTest(
        WindowManagerService windowManagerService,
        IAddonLifecycle? addonLifecycle = null,
        IDataManager? dataManager = null) =>
        new(windowManagerService, addonLifecycle, dataManager, isTest: true);

    public IReadOnlyDictionary<string, GameWindowAdapter> KnownAdapters => this.knownAdapters;

    private void HookLifecycleEvents()
    {
        if (this.addonLifecycle == null) return;

        try
        {
            this.addonLifecycle.RegisterListener(AddonEvent.PostSetup, this.OnAddonLifecycleEvent);
            this.addonLifecycle.RegisterListener(AddonEvent.PostShow, this.OnAddonLifecycleEvent);
            this.addonLifecycle.RegisterListener(AddonEvent.PreClose, this.OnAddonCloseEvent);
            this.addonLifecycle.RegisterListener(AddonEvent.PostHide, this.OnAddonHideEvent);
            this.addonLifecycle.RegisterListener(AddonEvent.PostFocusChanged, this.OnAddonFocusChangedEvent);
        }
        catch
        {
            // Best effort
        }
    }

    private void UnhookLifecycleEvents()
    {
        if (this.addonLifecycle == null) return;

        try
        {
            this.addonLifecycle.UnregisterListener(AddonEvent.PostSetup, this.OnAddonLifecycleEvent);
            this.addonLifecycle.UnregisterListener(AddonEvent.PostShow, this.OnAddonLifecycleEvent);
            this.addonLifecycle.UnregisterListener(AddonEvent.PreClose, this.OnAddonCloseEvent);
            this.addonLifecycle.UnregisterListener(AddonEvent.PostHide, this.OnAddonHideEvent);
            this.addonLifecycle.UnregisterListener(AddonEvent.PostFocusChanged, this.OnAddonFocusChangedEvent);
        }
        catch
        {
            // Best effort
        }
    }

    private void OnAddonLifecycleEvent(AddonEvent type, AddonArgs args)
    {
        GameWindowAdapter.DiagLog($"[AddonLifecycle] {type}: {args.AddonName} addr=0x{args.Addon.Address:X}");
        this.HandleAddonEvent(args.AddonName, args.Addon.Address, isSetupOrShow: true);
    }

    private void OnAddonCloseEvent(AddonEvent type, AddonArgs args)
    {
        GameWindowAdapter.DiagLog($"[AddonLifecycle] {type}: {args.AddonName} addr=0x{args.Addon.Address:X}");
        this.HandleAddonClose(args.AddonName);
    }

    private void OnAddonHideEvent(AddonEvent type, AddonArgs args)
    {
        GameWindowAdapter.DiagLog($"[AddonLifecycle] {type}: {args.AddonName} addr=0x{args.Addon.Address:X}");
        this.HandleAddonHide(args.AddonName);
    }

    private void OnAddonFocusChangedEvent(AddonEvent type, AddonArgs args)
    {
        if (MainCommandRegistry.TryGetByAddonName(args.AddonName, out var entry))
        {
            if (GameWindowAdapter.IsChildTabAddonName(args.AddonName) &&
                !GameWindowAdapter.IsChildTabAddonName(entry.AddonName))
            {
                return;
            }

            if (this.knownAdapters.TryGetValue(entry.AddonName, out var adapter))
            {
                adapter.AddonAddress = args.Addon.Address;
            }
        }
    }

    public bool HandleAddonEvent(string? addonName, nint address = 0, bool isSetupOrShow = true)
    {
        if (string.IsNullOrWhiteSpace(addonName))
            return false;

        GameWindowAdapter.DiagLog($"[HandleAddonEvent] addonName={addonName} addr=0x{address:X} isSetupOrShow={isSetupOrShow}");

        if (MainCommandRegistry.TryGetByAddonName(addonName, out var entry))
        {
            if (isSetupOrShow)
            {
                var effectiveAddress = address;
                if (GameWindowAdapter.IsChildTabAddonName(addonName) &&
                    !GameWindowAdapter.IsChildTabAddonName(entry.AddonName) &&
                    this.knownAdapters.TryGetValue(entry.AddonName, out var existingAdapter) &&
                    existingAdapter.AddonAddress != 0)
                {
                    effectiveAddress = 0;
                }

                this.TrackOrUpdateAddon(entry, effectiveAddress, isNativeShow: true);
                return true;
            }

            return false;
        }

        // If addonName is not in MainCommandRegistry, check if it is a parent container (e.g. "Social")
        // or a related addon of any already known adapter.
        if (isSetupOrShow)
        {
            var handled = false;
            foreach (var (key, adapter) in this.knownAdapters)
            {
                if (GameWindowAdapter.IsKnownTabOrChildAddon(key, addonName))
                {
                    GameWindowAdapter.DiagLog($"[HandleAddonEvent] matched container/tab {addonName} for known adapter {key}");
                    adapter.OnNativeShown();
                    var tw = this.windowManagerService.GetTrackedWindows().FirstOrDefault(w => w.TryGetWindow(out var win) && ReferenceEquals(win, adapter));
                    if (tw != null)
                    {
                        tw.IsMinimized = false;
                    }
                    handled = true;
                }
            }

            if (handled) return true;
        }

        return false;
    }

    public bool HandleAddonClose(string? addonName)
    {
        if (string.IsNullOrWhiteSpace(addonName))
            return false;

        var isChildSubGrid = GameWindowAdapter.IsChildTabAddonName(addonName);
        var handled = false;
        if (MainCommandRegistry.TryGetByAddonName(addonName, out var entry))
        {
            if (!isChildSubGrid || GameWindowAdapter.IsChildTabAddonName(entry.AddonName))
            {
                this.UntrackAddon(entry.AddonName);
                handled = true;
            }
        }

        // Also untrack any known adapter whose parent container matches addonName
        if (!isChildSubGrid)
        {
            foreach (var (key, adapter) in this.knownAdapters)
            {
                if (GameWindowAdapter.IsKnownTabOrChildAddon(key, addonName))
                {
                    this.UntrackAddon(key);
                    handled = true;
                }
            }
        }

        return handled;
    }

    public bool HandleAddonHide(string? addonName)
    {
        if (string.IsNullOrWhiteSpace(addonName))
            return false;

        var isChildSubGrid = GameWindowAdapter.IsChildTabAddonName(addonName);
        var handled = false;
        if (MainCommandRegistry.TryGetByAddonName(addonName, out var entry))
        {
            if (!isChildSubGrid || GameWindowAdapter.IsChildTabAddonName(entry.AddonName))
            {
                if (this.knownAdapters.TryGetValue(entry.AddonName, out var adapter))
                {
                    // If the window was not minimized by WindowManager, native hide means it was hidden/closed in-game
                    var tw = this.windowManagerService.GetTrackedWindows().FirstOrDefault(w => w.TryGetWindow(out var win) && ReferenceEquals(win, adapter));
                    var isMinimized = adapter.IsLocallyMinimized || (tw != null && tw.IsMinimized);
                    if (!isMinimized)
                    {
                        this.UntrackAddon(entry.AddonName);
                        handled = true;
                    }
                }
            }
        }

        // Also check if addonName is a parent container of any known adapter
        if (!isChildSubGrid)
        {
            foreach (var (key, adapter) in this.knownAdapters)
            {
                if (GameWindowAdapter.IsKnownTabOrChildAddon(key, addonName))
                {
                    var tw = this.windowManagerService.GetTrackedWindows().FirstOrDefault(w => w.TryGetWindow(out var win) && ReferenceEquals(win, adapter));
                    var isMinimized = adapter.IsLocallyMinimized || (tw != null && tw.IsMinimized);
                    if (!isMinimized)
                    {
                        this.UntrackAddon(key);
                        handled = true;
                    }
                }
            }
        }

        return handled;
    }

    public TrackedWindow TrackOrUpdateAddon(MainCommandEntry entry, nint address, bool isNativeShow = false)
    {
        GameWindowAdapter.DiagLog($"[TrackOrUpdateAddon] START: Addon={entry.AddonName} addr=0x{address:X} isNativeShow={isNativeShow}");
        // Clean up any sibling/related tab adapter in the same window family so only the active tab is tracked
        foreach (var existingKey in this.knownAdapters.Keys.ToList())
        {
            if (!string.Equals(existingKey, entry.AddonName, StringComparison.OrdinalIgnoreCase) &&
                GameWindowAdapter.IsKnownTabOrChildAddon(existingKey, entry.AddonName))
            {
                if (this.knownAdapters.TryGetValue(existingKey, out var oldAdapter) && oldAdapter.IsLocallyMinimized)
                {
                    oldAdapter.OnNativeShown();
                }
                this.UntrackAddon(existingKey);
            }
        }

        if (this.knownAdapters.TryGetValue(entry.AddonName, out var existing))
        {
            if (address != 0)
                existing.AddonAddress = address;

            var twExisting = this.windowManagerService.RegisterWindow(existing);
            twExisting.PluginInternalName = "FFXIV";
            twExisting.Namespace = "Game";

            if (isNativeShow)
            {
                GameWindowAdapter.DiagLog($"[TrackOrUpdateAddon] Calling existing.OnNativeShown for {entry.AddonName}");
                existing.OnNativeShown(address);
                twExisting.IsMinimized = false;
            }

            if (this.TryGetIconBytes(entry.IconId, out var iconBytes))
                twExisting.IconBytes = iconBytes;
            existing.IsBeingMinimized = () => twExisting.IsMinimized;
            return twExisting;
        }

        GameWindowAdapter.DiagLog($"[TrackOrUpdateAddon] Creating NEW GameWindowAdapter for {entry.AddonName} addr=0x{address:X}");
        var adapter = new GameWindowAdapter(entry.AddonName, entry, address);
        var tw = this.windowManagerService.RegisterWindow(adapter);
        tw.PluginInternalName = "FFXIV";
        tw.Namespace = "Game";
        if (this.TryGetIconBytes(entry.IconId, out var icon))
            tw.IconBytes = icon;
        adapter.IsBeingMinimized = () => tw.IsMinimized;
        this.knownAdapters[entry.AddonName] = adapter;
        return tw;
    }

    public void UntrackAddon(string addonName)
    {
        GameWindowAdapter.DiagLog($"[UntrackAddon] Addon={addonName}");
        if (this.knownAdapters.TryRemove(addonName, out var adapter))
        {
            if (adapter.IsLocallyMinimized)
            {
                adapter.OnNativeShown();
            }
            this.windowManagerService.UnregisterWindow(adapter);
        }
    }

    [OnTick(interval: 2000)]
    public unsafe void ScanActiveWindows()
    {
        try
        {
            var mgr = RaptureAtkUnitManager.Instance();
            if (mgr == null) return;

            var list = mgr->AllLoadedUnitsList;
            var entries = list.Entries;
            var count = list.Count;

            var activeAddonNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < count; i++)
            {
                var unit = entries[i].Value;
                if (unit == null) continue;

                var name = unit->NameString;
                if (string.IsNullOrEmpty(name)) continue;

                if (!MainCommandRegistry.TryGetByAddonName(name, out var entry))
                    continue;

                if (GameWindowAdapter.IsChildTabAddonName(name) &&
                    !GameWindowAdapter.IsChildTabAddonName(entry.AddonName))
                {
                    continue;
                }

                var isCurrentlyMinimized = this.knownAdapters.TryGetValue(entry.AddonName, out var adapter) && adapter.IsLocallyMinimized;

                // Only track units that are visible, or the exact unit already locally minimized by WindowManager
                if (!unit->IsVisible)
                {
                    if (!isCurrentlyMinimized || (adapter!.AddonAddress != 0 && adapter.AddonAddress != (nint)unit))
                        continue;
                }

                if (activeAddonNames.Contains(entry.AddonName))
                    continue;

                // Deduplicate within the current scan pass: if a related unit in the same window family
                // was already collected in activeAddonNames, do not track both.
                var alreadyHasFamilyMember = false;
                foreach (var activeName in activeAddonNames)
                {
                    if (!string.Equals(activeName, entry.AddonName, StringComparison.OrdinalIgnoreCase) &&
                        GameWindowAdapter.IsKnownTabOrChildAddon(activeName, entry.AddonName))
                    {
                        alreadyHasFamilyMember = true;
                        break;
                    }
                }

                if (alreadyHasFamilyMember)
                    continue;

                activeAddonNames.Add(entry.AddonName);

                var isNativeShow = isCurrentlyMinimized && (unit->IsVisible || unit->X > -5000);
                if (isCurrentlyMinimized && !isNativeShow && adapter != null)
                {
                    // Check if any related unit in the same window family (such as parent container "Social") was shown by the game
                    for (var j = 0; j < count; j++)
                    {
                        var other = entries[j].Value;
                        if (other == null || other == unit) continue;
                        if (other->IsVisible && adapter.IsRelatedUnit(unit, other, entry.AddonName))
                        {
                            GameWindowAdapter.DiagLog($"[ScanActiveWindows] detected related unit {other->NameString} shown! Setting isNativeShow=true for {entry.AddonName}");
                            isNativeShow = true;
                            break;
                        }
                    }
                }

                this.TrackOrUpdateAddon(entry, (nint)unit, isNativeShow: isNativeShow);
                this.knownAdapters.TryGetValue(entry.AddonName, out adapter);

                if (!isCurrentlyMinimized)
                {
                    GameWindowAdapter.RestoreChromeVisibility(unit, name);
                    if (adapter != null)
                    {
                        for (var j = 0; j < count; j++)
                        {
                            var other = entries[j].Value;
                            if (other == null || other == unit) continue;
                            if (adapter.IsRelatedUnit(unit, other, entry.AddonName))
                            {
                                GameWindowAdapter.RestoreChromeVisibility(other, other->NameString);
                            }
                        }
                    }
                }
            }

            // Clean up adapters for windows that are no longer loaded in AllLoadedUnitsList
            foreach (var kvp in this.knownAdapters)
            {
                if (!activeAddonNames.Contains(kvp.Key))
                {
                    GameWindowAdapter.DiagLog($"[ScanActiveWindows] Addon {kvp.Key} no longer in activeAddonNames, removing");
                    if (this.knownAdapters.TryRemove(kvp.Key, out var removed))
                    {
                        if (removed.IsLocallyMinimized)
                        {
                            removed.OnNativeShown();
                        }
                        this.windowManagerService.UnregisterWindow(removed);
                    }
                }
            }
        }
        catch
        {
            // Safe outside live game loop / in unit tests
        }
    }

    public bool TryGetIconBytes(uint iconId, out byte[]? bytes)
    {
        if (this.iconCache.TryGetValue(iconId, out bytes))
            return bytes != null;

        if (this.dataManager != null)
        {
            try
            {
                var subfolder = (iconId / 1000 * 1000).ToString("D6");
                var hr1Path = $"ui/icon/{subfolder}/{iconId:D6}_hr1.tex";
                var stdPath = $"ui/icon/{subfolder}/{iconId:D6}.tex";

                var tex = this.dataManager.GetFile<Lumina.Data.Files.TexFile>(hr1Path)
                       ?? this.dataManager.GetFile<Lumina.Data.Files.TexFile>(stdPath);

                if (tex != null && tex.ImageData != null && tex.ImageData.Length > 0)
                {
                    bytes = PngHelper.EncodeBgraToPng(tex.ImageData, tex.Header.Width, tex.Header.Height);
                    this.iconCache[iconId] = bytes;
                    return true;
                }
            }
            catch
            {
                // Fall through to null cache
            }
        }

        this.iconCache[iconId] = null;
        bytes = null;
        return false;
    }

    public void Dispose()
    {
        this.UnhookLifecycleEvents();

        foreach (var adapter in this.knownAdapters.Values)
        {
            this.windowManagerService.UnregisterWindow(adapter);
        }
        this.knownAdapters.Clear();
    }

    private static T? TryResolveDalamudService<T>() where T : class
    {
        try
        {
            var logAssembly = typeof(IPluginLog).Assembly;
            var serviceOpenType = logAssembly.GetType("Dalamud.Service`1");
            if (serviceOpenType == null)
                return null;

            var scType = logAssembly.GetType("Dalamud.IoC.Internal.ServiceContainer");
            if (scType == null)
                return null;

            // Safe guard: verify ServiceContainer is ready before accessing Service<ServiceContainer>
            var scService = serviceOpenType.MakeGenericType(scType);
            var scTcsField = scService.GetField("instanceTcs", BindingFlags.NonPublic | BindingFlags.Static);
            var scTcs = scTcsField?.GetValue(null);
            if (scTcs == null)
                return null;

            var scTaskProp = scTcs.GetType().GetProperty("Task");
            if (scTaskProp?.GetValue(scTcs) is not Task scTask || !scTask.IsCompleted)
                return null;

            var getMethod = scService.GetMethod("Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var container = getMethod?.Invoke(null, null);
            if (container == null)
                return null;

            var getSingletonMethod = scType.GetMethod("GetSingletonService", BindingFlags.NonPublic | BindingFlags.Instance);
            if (getSingletonMethod != null)
            {
                if (getSingletonMethod.Invoke(container, [typeof(T), true]) is Task task)
                {
                    var resultProp = task.GetType().GetProperty("Result");
                    return resultProp?.GetValue(task) as T;
                }
            }
        }
        catch
        {
            // Best effort
        }

        return null;
    }
}
