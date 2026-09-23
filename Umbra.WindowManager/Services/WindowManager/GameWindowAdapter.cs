using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Umbra.WindowManager.Services.WindowManager;

/// <summary>
/// Bridges native FFXIV in-game windows (<see cref="AtkUnitBase"/> / <see cref="FFXIVClientStructs.FFXIV.Client.UI.Agent.AgentInterface"/>)
/// to Dalamud's <see cref="IWindow"/> interface so that <see cref="WindowManagerService"/> and taskbar widgets
/// can monitor, display, focus, minimize, and restore them.
/// </summary>
public class GameWindowAdapter : IWindow
{
    private const short OffscreenCoord = -10000;
    private bool isLocallyMinimized;
    private bool isLocallyClosed;
    private bool hasSavedPosition;
    private short savedX;
    private short savedY;
    private byte savedAlpha = 255;

    private struct SavedUnitState
    {
        public nint Address;
        public short SavedX;
        public short SavedY;
        public byte SavedAlpha;
        public bool SavedVisible;
    }

    private readonly List<SavedUnitState> savedChildUnits = [];
    internal int SavedChildUnitCount => this.savedChildUnits.Count;

    internal static void DiagLog(string message)
    {
        try
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher", "wm-game-diag.log");
            System.IO.File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch
        {
            // Best effort
        }
    }

    /// <summary>
    /// Determines whether an addon is an embedded child tab inside a parent container
    /// (e.g. FriendList/PartyMemberList inside Social, GSInfo inside GoldSaucer).
    /// Child tabs must NOT have their own WindowNode visible because the parent container provides the window chrome.
    /// </summary>
    internal static unsafe bool IsChildTabUnit(AtkUnitBase* unit, string? addonName = null)
    {
        if (unit == null) return false;

        // 1. If FFXIV designated a HostId, it is hosted inside another addon container
        if (unit->HostId != 0) return true;

        // 2. Check known child tab names
        var name = !string.IsNullOrEmpty(addonName) ? addonName : unit->NameString;
        return IsChildTabAddonName(name);
    }

    public static bool IsChildTabAddonName(string? addonName)
    {
        if (string.IsNullOrWhiteSpace(addonName))
            return false;

        // Social window child tabs (FriendList, PartyMemberList, BlackList, Search, PlayerSearch)
        if (addonName.Equals("FriendList", StringComparison.OrdinalIgnoreCase) ||
            addonName.Equals("PartyMemberList", StringComparison.OrdinalIgnoreCase) ||
            addonName.Equals("BlackList", StringComparison.OrdinalIgnoreCase) ||
            addonName.Equals("Search", StringComparison.OrdinalIgnoreCase) ||
            addonName.Equals("PlayerSearch", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Gold Saucer child tabs
        if (addonName.StartsWith("GSInfo", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Character sub-tabs (CharacterClass, CharacterStatus, CharacterRepute, GearSetList, etc.)
        if (addonName.StartsWith("Character", StringComparison.OrdinalIgnoreCase) &&
            !addonName.Equals("Character", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Inventory sub-grids (parent containers Inventory, InventoryLarge, InventoryExpansion, InventoryBuddy are NOT child tabs)
        if (addonName.StartsWith("InventoryGrid", StringComparison.OrdinalIgnoreCase) ||
            addonName.StartsWith("InventoryEventGrid", StringComparison.OrdinalIgnoreCase) ||
            addonName.StartsWith("InventoryCrystalGrid", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Restores proper WindowNode visibility: child tabs have WindowNode suppressed (false)
    /// so their empty chrome does not draw over the parent container's close button and tabs,
    /// while parent containers and standalone windows have WindowNode visible (true).
    /// </summary>
    internal static unsafe void RestoreChromeVisibility(AtkUnitBase* unit, string? addonName = null)
    {
        if (unit == null || unit->WindowNode == null) return;

        try
        {
            var win = (AtkResNode*)unit->WindowNode;
            var isCurrentlyVisible = (win->NodeFlags & NodeFlags.Visible) != 0;
            var shouldBeVisible = !IsChildTabUnit(unit, addonName);

            if (isCurrentlyVisible != shouldBeVisible)
            {
                unit->WindowNode->ToggleVisibility(shouldBeVisible);
                DiagLog($"[ChromeVisibility] Fixed WindowNode visibility for {unit->NameString} from {isCurrentlyVisible} to {shouldBeVisible}");
            }
        }
        catch (Exception ex)
        {
            DiagLog($"[ChromeVisibility] Error fixing WindowNode: {ex.Message}");
        }
    }

    /// <summary>
    /// Diagnostic: logs the visibility flag and alpha of a unit's chrome nodes (RootNode, WindowNode)
    /// so a minimize-vs-restore comparison shows exactly which node is left hidden. Only reads pointers
    /// the surrounding code already dereferences, so it adds no crash risk.
    /// </summary>
    internal static unsafe void DiagDumpChrome(string phase, string label, AtkUnitBase* unit)
    {
        try
        {
            if (unit == null)
            {
                DiagLog($"[Chrome:{phase}] {label} unit=null");
                return;
            }

            var root = unit->RootNode;
            var rootInfo = root != null
                ? $"root(vis={(root->NodeFlags & NodeFlags.Visible) != 0} a={root->Color.A})"
                : "root=null";

            var win = (AtkResNode*)unit->WindowNode;
            var winInfo = win != null
                ? $"window(vis={(win->NodeFlags & NodeFlags.Visible) != 0} a={win->Color.A})"
                : "window=null";

            DiagLog($"[Chrome:{phase}] {label} unitVis={unit->IsVisible} unitAlpha={unit->Alpha} {rootInfo} {winInfo}");
        }
        catch (Exception ex)
        {
            DiagLog($"[Chrome:{phase}] {label} ERROR {ex.Message}");
        }
    }


    public string AddonName { get; }
    public MainCommandEntry Entry { get; }
    public string? Title { get; }
    public nint AddonAddress { get; set; }
    public bool IsLocallyMinimized => this.isLocallyMinimized;

    /// <summary>
    /// Optional delegate evaluated when <see cref="IsOpen"/> is set to <c>false</c>.
    /// If it returns <c>true</c>, the window is minimized rather than closed.
    /// </summary>
    public Func<bool>? IsBeingMinimized { get; set; }

    // Testable / customizable action delegates
    public Func<bool>? CheckIsOpen { get; set; }
    public Func<bool>? CheckIsFocused { get; set; }
    public Action? BringToFrontAction { get; set; }
    public Action? MinimizeAction { get; set; }
    public Action? RestoreAction { get; set; }
    public Action? CloseAction { get; set; }

    public GameWindowAdapter(
        string addonName,
        MainCommandEntry entry,
        nint addonAddress = 0,
        string? title = null,
        Func<bool>? isBeingMinimized = null)
    {
        ArgumentNullException.ThrowIfNull(addonName);
        ArgumentNullException.ThrowIfNull(entry);

        this.AddonName = addonName;
        this.Entry = entry;
        this.AddonAddress = addonAddress;
        this.Title = title ?? entry.DefaultTitle;
        this.IsBeingMinimized = isBeingMinimized;

        this.WindowName = $"{this.Title}###Game_{addonName}";
    }

    /// <summary>
    /// Resets any local suppression (minimized/closed) when the game natively shows or sets up the addon.
    /// </summary>
    public void OnNativeShown(nint address = 0)
    {
        DiagLog($"[OnNativeShown] start: AddonName={this.AddonName} AddonAddress=0x{this.AddonAddress:X} newAddr=0x{address:X} isLocallyMinimized={this.isLocallyMinimized} savedChildren={this.savedChildUnits.Count}");
        if (address != 0)
            this.AddonAddress = address;

        if (this.isLocallyMinimized)
        {
            unsafe
            {
                if (this.AddonAddress != 0)
                {
                    var unit = (AtkUnitBase*)this.AddonAddress;
                    if (unit != null)
                    {
                        unit->IsVisible = true;
                        unit->SetAlpha(this.savedAlpha > 0 ? this.savedAlpha : (byte)255);
                        if (this.hasSavedPosition)
                        {
                            unit->X = this.savedX;
                            unit->Y = this.savedY;
                            unit->SetPosition(this.savedX, this.savedY);
                            DiagLog($"[OnNativeShown] restored unit: {this.AddonName} to ({this.savedX}, {this.savedY})");
                        }
                        if (unit->RootNode != null)
                        {
                            unit->RootNode->ToggleVisibility(true);
                        }
                        RestoreChromeVisibility(unit, this.AddonName);
                    }
                }

                if (this.savedChildUnits.Count > 0)
                {
                    foreach (var childState in this.savedChildUnits)
                    {
                        try
                        {
                            var child = (AtkUnitBase*)childState.Address;
                            if (child != null)
                            {
                                child->IsVisible = childState.SavedVisible;
                                child->SetAlpha(childState.SavedAlpha > 0 ? childState.SavedAlpha : (byte)255);
                                child->X = childState.SavedX;
                                child->Y = childState.SavedY;
                                child->SetPosition(childState.SavedX, childState.SavedY);
                                DiagLog($"[OnNativeShown] restored child: {child->NameString} addr=0x{childState.Address:X} to ({childState.SavedX}, {childState.SavedY}) vis={childState.SavedVisible}");
                                if (childState.SavedVisible && child->RootNode != null)
                                {
                                    child->RootNode->ToggleVisibility(true);
                                }
                                RestoreChromeVisibility(child, child->NameString);
                            }
                        }
                        catch (Exception ex)
                        {
                            DiagLog($"[OnNativeShown] error restoring child: {ex.Message}");
                        }
                    }
                }
            }
        }

        this.isLocallyClosed = false;
        this.isLocallyMinimized = false;
        this.hasSavedPosition = false;
        this.savedChildUnits.Clear();
        DiagLog($"[OnNativeShown] end: AddonName={this.AddonName}");
    }

    // --- Dalamud.Interface.Windowing.IWindow implementation ---

    public string WindowName { get; set; }

    public string? Namespace { get; set; } = "Game";

    public ImGuiWindowFlags Flags { get; set; } = ImGuiWindowFlags.None;

    /// <summary>
    /// Always returns <c>null</c> to suppress title bar minimize button injection by <see cref="DalamudWindowTracker"/>,
    /// since native FFXIV in-game windows use Atk chrome instead of Dear ImGui title bars.
    /// </summary>
    public List<TitleBarButton> TitleBarButtons
    {
        get => null!;
        set { }
    }

    public bool IsOpen
    {
        get => !this.isLocallyClosed && !this.isLocallyMinimized && (this.CheckIsOpen?.Invoke() ?? this.GetNativeIsOpen());
        set
        {
            if (value)
            {
                this.isLocallyClosed = false;
                this.isLocallyMinimized = false;
                this.RestoreAction?.Invoke();
                if (this.RestoreAction == null)
                {
                    this.RestoreNative();
                }
            }
            else
            {
                if (this.IsBeingMinimized?.Invoke() == true)
                {
                    this.isLocallyMinimized = true;
                    this.MinimizeAction?.Invoke();
                    if (this.MinimizeAction == null)
                    {
                        this.MinimizeNative();
                    }
                }
                else
                {
                    this.isLocallyClosed = true;
                    this.isLocallyMinimized = false;
                    this.CloseAction?.Invoke();
                    if (this.CloseAction == null)
                    {
                        this.CloseNative();
                    }
                }
            }
        }
    }

    public bool IsFocused
    {
        get => this.CheckIsFocused?.Invoke() ?? this.GetNativeIsFocused();
        set { }
    }

    public bool IsHovered { get; set; }

    public Vector2? Position
    {
        get
        {
            if (this.isLocallyMinimized && this.hasSavedPosition)
                return new Vector2(this.savedX, this.savedY);

            if (this.AddonAddress == 0) return null;
            unsafe
            {
                var unit = (AtkUnitBase*)this.AddonAddress;
                return unit != null ? new Vector2(unit->X, unit->Y) : null;
            }
        }
        set
        {
            if (value.HasValue)
            {
                if (this.isLocallyMinimized)
                {
                    this.savedX = (short)value.Value.X;
                    this.savedY = (short)value.Value.Y;
                    this.hasSavedPosition = true;
                }
                else if (this.AddonAddress != 0)
                {
                    unsafe
                    {
                        var unit = (AtkUnitBase*)this.AddonAddress;
                        if (unit != null)
                        {
                            unit->SetPosition((short)value.Value.X, (short)value.Value.Y);
                        }
                    }
                }
            }
        }
    }

    public Vector2? Size
    {
        get
        {
            if (this.AddonAddress == 0) return null;
            unsafe
            {
                var unit = (AtkUnitBase*)this.AddonAddress;
                if (unit == null) return null;
                var width = unit->GetScaledWidth(true);
                var height = unit->GetScaledHeight(true);
                return width > 0 && height > 0 ? new Vector2(width, height) : null;
            }
        }
        set { }
    }

    public bool RespectCloseHotkey { get; set; } = true;
    public bool InhibitAtkCollision { get; set; }
    public bool DisableWindowSounds { get; set; }
    public uint OnOpenSfxId { get; set; }
    public uint OnCloseSfxId { get; set; }
    public bool DisableFadeInFadeOut { get; set; }
    public ImGuiCond PositionCondition { get; set; }
    public ImGuiCond SizeCondition { get; set; }
    public WindowSizeConstraints? SizeConstraints { get; set; }
    public bool? Collapsed { get; set; }
    public ImGuiCond CollapsedCondition { get; set; }
    public bool ForceMainWindow { get; set; }
    public float? BgAlpha { get; set; }
    public bool ShowCloseButton { get; set; } = true;
    public bool AllowPinning { get; set; }
    public bool AllowClickthrough { get; set; }
    public bool AllowBackgroundBlur { get; set; }
    public bool IsPinned { get; set; }
    public bool IsClickthrough { get; set; }
    public bool IsTopMost { get; set; }
    public bool RequestFocus { get; set; }

    public void Toggle() => this.IsOpen = !this.IsOpen;

    public void BringToFront()
    {
        this.BringToFrontAction?.Invoke();
        if (this.BringToFrontAction == null)
        {
            this.BringToFrontNative();
        }
    }

    public bool DrawConditions() => true;

    public void PreOpenCheck() { }
    public void PreDraw() { }
    public void PostDraw() { }
    public void Draw() { }
    public void OnOpen() { }
    public void OnClose() { }
    public void OnSafeToRemove() { }
    public void Update() { }

    private unsafe bool GetNativeIsOpen()
    {
        if (this.AddonAddress == 0) return true;
        try
        {
            var unit = (AtkUnitBase*)this.AddonAddress;
            return unit != null && unit->IsVisible;
        }
        catch
        {
            return false;
        }
    }

    private unsafe bool GetNativeIsFocused()
    {
        if (this.AddonAddress == 0) return false;
        try
        {
            var unit = (AtkUnitBase*)this.AddonAddress;
            if (unit == null) return false;

            var mgr = RaptureAtkUnitManager.Instance();
            if (mgr != null)
            {
                var focused = mgr->FocusedUnitsList.Entries;
                for (var i = 0; i < mgr->FocusedUnitsList.Count; i++)
                {
                    var focusedUnit = focused[i].Value;
                    if (focusedUnit == unit || (focusedUnit != null && this.IsRelatedUnit(unit, focusedUnit, this.AddonName)))
                        return true;
                }
            }

            return unit->FocusNode != null;
        }
        catch
        {
            return false;
        }
    }

    private unsafe void MinimizeNative()
    {
        if (this.AddonAddress == 0) return;
        try
        {
            var unit = (AtkUnitBase*)this.AddonAddress;
            if (unit == null) return;

            DiagLog($"[MinimizeNative] START: Addon={this.AddonName} addr=0x{this.AddonAddress:X} unitName={unit->NameString} pos=({unit->X}, {unit->Y}) alpha={unit->Alpha} visible={unit->IsVisible} id={unit->Id} parentId={unit->ParentId} hostId={unit->HostId}");
            DiagDumpChrome("min-before", $"primary:{unit->NameString}", unit);

            if (!this.hasSavedPosition && unit->X > -5000 && unit->Y > -5000)
            {
                this.savedX = unit->X;
                this.savedY = unit->Y;
                this.savedAlpha = unit->Alpha;
                this.hasSavedPosition = true;
                DiagLog($"[MinimizeNative] saved pos=({this.savedX}, {this.savedY}) alpha={this.savedAlpha}");
            }
            else
            {
                DiagLog($"[MinimizeNative] skipped saving pos (hasSaved={this.hasSavedPosition}, pos=({unit->X}, {unit->Y}))");
            }

            unit->IsVisible = false;
            unit->SetAlpha(0);
            unit->X = OffscreenCoord;
            unit->Y = OffscreenCoord;
            unit->SetPosition(OffscreenCoord, OffscreenCoord);

            var mgr = RaptureAtkUnitManager.Instance();
            if (mgr != null)
            {
                var list = mgr->AllLoadedUnitsList;
                var count = list.Count;
                var entries = list.Entries;
                var relatedUnits = new List<nint>();

                for (var i = 0; i < count; i++)
                {
                    var child = entries[i].Value;
                    if (child == null || child == unit) continue;

                    if (this.IsRelatedUnit(unit, child, this.AddonName))
                    {
                        relatedUnits.Add((nint)child);
                        DiagLog($"[MinimizeNative] found direct related unit: {child->NameString} addr=0x{(nint)child:X} pos=({child->X}, {child->Y}) id={child->Id} parentId={child->ParentId} hostId={child->HostId}");
                    }
                }

                // Transitive expansion for child/grandchild addons
                var expanded = true;
                while (expanded)
                {
                    expanded = false;
                    for (var i = 0; i < count; i++)
                    {
                        var candidate = entries[i].Value;
                        if (candidate == null || candidate == unit || relatedUnits.Contains((nint)candidate))
                            continue;

                        if (candidate->ParentId != 0)
                        {
                            for (var j = 0; j < relatedUnits.Count; j++)
                            {
                                var rel = (AtkUnitBase*)relatedUnits[j];
                                if (rel != null && rel->Id != 0 && candidate->ParentId == rel->Id)
                                {
                                    relatedUnits.Add((nint)candidate);
                                    DiagLog($"[MinimizeNative] found transitive related unit: {candidate->NameString} addr=0x{(nint)candidate:X} pos=({candidate->X}, {candidate->Y})");
                                    expanded = true;
                                    break;
                                }
                            }
                        }
                    }
                }

                foreach (var childAddr in relatedUnits)
                {
                    var child = (AtkUnitBase*)childAddr;
                    if (child == null) continue;

                    if (!this.savedChildUnits.Any(s => s.Address == childAddr))
                    {
                        if (child->X > -5000 && child->Y > -5000)
                        {
                            this.savedChildUnits.Add(new SavedUnitState
                            {
                                Address = childAddr,
                                SavedX = child->X,
                                SavedY = child->Y,
                                SavedAlpha = child->Alpha,
                                SavedVisible = child->IsVisible
                            });
                            DiagLog($"[MinimizeNative] saved child: {child->NameString} addr=0x{childAddr:X} pos=({child->X}, {child->Y}) alpha={child->Alpha} vis={child->IsVisible}");
                        }
                        else
                        {
                            DiagLog($"[MinimizeNative] child {child->NameString} addr=0x{childAddr:X} had offscreen pos ({child->X}, {child->Y}), not saving!");
                        }
                    }

                    DiagDumpChrome("min-before", $"child:{child->NameString}", child);
                    child->IsVisible = false;
                    child->SetAlpha(0);
                    child->X = OffscreenCoord;
                    child->Y = OffscreenCoord;
                    child->SetPosition(OffscreenCoord, OffscreenCoord);
                }
            }
            DiagLog($"[MinimizeNative] END: total saved children = {this.savedChildUnits.Count}");
        }
        catch (Exception ex)
        {
            DiagLog($"[MinimizeNative] EXCEPTION: {ex}");
        }
    }

    private unsafe void RestoreNative()
    {
        if (this.AddonAddress == 0) return;
        try
        {
            var unit = (AtkUnitBase*)this.AddonAddress;
            if (unit == null) return;

            DiagLog($"[RestoreNative] START: Addon={this.AddonName} addr=0x{this.AddonAddress:X} unitName={unit->NameString} savedChildren={this.savedChildUnits.Count} hasSavedPos={this.hasSavedPosition} savedPos=({this.savedX}, {this.savedY})");

            if (this.savedChildUnits.Count > 0)
            {
                var mgr = RaptureAtkUnitManager.Instance();
                if (mgr != null)
                {
                    var list = mgr->AllLoadedUnitsList;
                    var count = list.Count;
                    var entries = list.Entries;

                    var loadedAddrs = new HashSet<nint>();
                    for (var i = 0; i < count; i++)
                    {
                        if (entries[i].Value != null)
                            loadedAddrs.Add((nint)entries[i].Value);
                    }

                    foreach (var childState in this.savedChildUnits)
                    {
                        if (loadedAddrs.Contains(childState.Address))
                        {
                            var child = (AtkUnitBase*)childState.Address;
                            child->IsVisible = childState.SavedVisible;
                            child->SetAlpha(childState.SavedAlpha > 0 ? childState.SavedAlpha : (byte)255);
                            child->X = childState.SavedX;
                            child->Y = childState.SavedY;
                            child->SetPosition(childState.SavedX, childState.SavedY);
                            DiagLog($"[RestoreNative] restored child: {child->NameString} addr=0x{childState.Address:X} to ({childState.SavedX}, {childState.SavedY}) vis={childState.SavedVisible}");
                            if (childState.SavedVisible && child->RootNode != null)
                            {
                                child->RootNode->ToggleVisibility(true);
                            }
                            RestoreChromeVisibility(child, child->NameString);
                            DiagDumpChrome("restore-after", $"child:{child->NameString}", child);
                        }
                        else
                        {
                            DiagLog($"[RestoreNative] child addr=0x{childState.Address:X} was NOT in loadedAddrs!");
                        }
                    }
                }
                this.savedChildUnits.Clear();
            }

            unit->IsVisible = true;
            unit->SetAlpha(this.savedAlpha > 0 ? this.savedAlpha : (byte)255);
            if (this.hasSavedPosition)
            {
                unit->X = this.savedX;
                unit->Y = this.savedY;
                unit->SetPosition(this.savedX, this.savedY);
                DiagLog($"[RestoreNative] restored unit {this.AddonName} to ({this.savedX}, {this.savedY})");
                this.hasSavedPosition = false;
            }

            if (unit->RootNode != null)
            {
                unit->RootNode->ToggleVisibility(true);
            }
            RestoreChromeVisibility(unit, this.AddonName);

            DiagDumpChrome("restore-after", $"primary:{unit->NameString}", unit);
            unit->Focus();
            DiagLog($"[RestoreNative] END: Addon={this.AddonName}");
        }
        catch (Exception ex)
        {
            DiagLog($"[RestoreNative] EXCEPTION: {ex}");
        }
    }

    private unsafe void CloseNative()
    {
        if (this.AddonAddress == 0) return;
        try
        {
            var unit = (AtkUnitBase*)this.AddonAddress;
            if (unit != null)
            {
                DiagLog($"[CloseNative] START: Addon={this.AddonName} addr=0x{this.AddonAddress:X}");
                if (this.hasSavedPosition)
                {
                    unit->X = this.savedX;
                    unit->Y = this.savedY;
                    unit->SetPosition(this.savedX, this.savedY);
                    unit->SetAlpha(this.savedAlpha > 0 ? this.savedAlpha : (byte)255);
                    unit->IsVisible = true;
                    if (unit->RootNode != null) unit->RootNode->ToggleVisibility(true);
                    RestoreChromeVisibility(unit, this.AddonName);
                    this.hasSavedPosition = false;
                }

                if (this.savedChildUnits.Count > 0)
                {
                    foreach (var childState in this.savedChildUnits)
                    {
                        var child = (AtkUnitBase*)childState.Address;
                        try
                        {
                            child->X = childState.SavedX;
                            child->Y = childState.SavedY;
                            child->SetPosition(childState.SavedX, childState.SavedY);
                            child->SetAlpha(childState.SavedAlpha > 0 ? childState.SavedAlpha : (byte)255);
                            child->IsVisible = childState.SavedVisible;
                            if (childState.SavedVisible && child->RootNode != null) child->RootNode->ToggleVisibility(true);
                            RestoreChromeVisibility(child, child->NameString);
                        }
                        catch
                        {
                            // Best effort
                        }
                    }
                    this.savedChildUnits.Clear();
                }

                unit->FireCloseCallback();
                DiagLog($"[CloseNative] END: FireCloseCallback called for {this.AddonName}");
            }
        }
        catch (Exception ex)
        {
            DiagLog($"[CloseNative] EXCEPTION: {ex}");
        }
    }

    private unsafe void BringToFrontNative()
    {
        if (this.AddonAddress == 0) return;
        try
        {
            var unit = (AtkUnitBase*)this.AddonAddress;
            if (unit != null)
            {
                unit->Focus();
            }
        }
        catch
        {
            // Best effort
        }
    }

    internal unsafe bool IsRelatedUnit(AtkUnitBase* parentUnit, AtkUnitBase* candidate, string parentAddonName)
    {
        if (candidate == null || candidate == parentUnit)
            return false;

        // 1. Direct ParentId or HostId match
        if (parentUnit->Id != 0 && (candidate->ParentId == parentUnit->Id || candidate->HostId == parentUnit->Id))
            return true;

        // 2. Candidate is the parent of parentUnit (e.g. Social is parent of FriendList)
        if (parentUnit->ParentId != 0 && candidate->Id == parentUnit->ParentId)
            return true;

        var childName = candidate->NameString;
        if (string.IsNullOrEmpty(childName))
            return false;

        // 3. AtkAddonControl child addons (tab units dynamically registered in ChildAddons)
        if (this.IsChildOfAddonControl(parentUnit, candidate, parentAddonName))
            return true;

        // 4. Family / Tab naming conventions for native FFXIV tabbed and multi-part windows
        return IsKnownTabOrChildAddon(parentAddonName, childName);
    }

    private unsafe bool IsChildOfAddonControl(AtkUnitBase* parentUnit, AtkUnitBase* candidate, string parentAddonName)
    {
        try
        {
            nint addr = (nint)parentUnit;
            int offset = parentAddonName.ToLowerInvariant() switch
            {
                "character" => 1216,
                "social" or "friendlist" or "partymemberlist" or "blacklist" => 584,
                "goldsaucer" or "goldsaucerinfo" => 584,
                "buddy" => 576,
                "contentsfinder" => 616,
                "inventory" or "inventorygrid" or "inventoryexpansion" or "inventorylarge" or "inventoryevent" => 704,
                "raidfinder" => 568,
                "guildleve" => 6328,
                _ => -1
            };

            if (offset < 0) return false;

            var control = (AtkAddonControl*)(addr + offset);
            if (control != null && control->ChildAddons.Count > 0 && control->ChildAddons.Count < 50)
            {
                foreach (var childInfoPtr in control->ChildAddons)
                {
                    var childInfo = childInfoPtr.Value;
                    if (childInfo != null && childInfo->AtkUnitBase == candidate)
                        return true;
                }
            }
        }
        catch
        {
            // Best effort
        }

        return false;
    }

    public static bool IsKnownTabOrChildAddon(string parentAddonName, string childName)
    {
        if (string.IsNullOrWhiteSpace(parentAddonName) || string.IsNullOrWhiteSpace(childName))
            return false;

        var p = parentAddonName;
        var c = childName;

        // Character tabs & sub-windows (CharacterClass, CharacterRepute, CharaCard, GearSetList, CharacterInspect, etc.)
        if (p.Equals("Character", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("Character", StringComparison.OrdinalIgnoreCase) ||
                   c.Equals("CharaCard", StringComparison.OrdinalIgnoreCase) ||
                   c.Equals("GearSetList", StringComparison.OrdinalIgnoreCase);
        }

        // Social window & its tabs (Social, FriendList, PartyMemberList, BlackList, Search)
        if (p.Equals("Social", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("FriendList", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("PartyMemberList", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("BlackList", StringComparison.OrdinalIgnoreCase))
        {
            return c.Equals("Social", StringComparison.OrdinalIgnoreCase) ||
                   c.Equals("FriendList", StringComparison.OrdinalIgnoreCase) ||
                   c.Equals("PartyMemberList", StringComparison.OrdinalIgnoreCase) ||
                   c.Equals("BlackList", StringComparison.OrdinalIgnoreCase) ||
                   c.Equals("Search", StringComparison.OrdinalIgnoreCase) ||
                   c.Equals("PlayerSearch", StringComparison.OrdinalIgnoreCase);
        }

        // Gold Saucer window & its tabs (GSInfoGeneral, GSInfoCardList, GSInfoCardDeck, GSInfoEditDeck, GSInfoMinionBattle, GSInfoChocoboParam, GSInfoEmj)
        if (p.Equals("GoldSaucer", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("GoldSaucerInfo", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("GSInfo", StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith("GoldSaucer", StringComparison.OrdinalIgnoreCase);
        }

        // Inventory & grids
        if (p.Equals("Inventory", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("InventoryExpansion", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("InventoryLarge", StringComparison.OrdinalIgnoreCase) ||
            p.Equals("InventoryGrid", StringComparison.OrdinalIgnoreCase))
        {
            return c.Equals("Inventory", StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith("InventoryGrid", StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith("InventoryLarge", StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith("InventoryExpansion", StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith("InventoryEventGrid", StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith("InventoryCrystalGrid", StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith("InventoryBuddy", StringComparison.OrdinalIgnoreCase);
        }

        // Journal & details
        if (p.Equals("Journal", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("Journal", StringComparison.OrdinalIgnoreCase);
        }

        // Duty Finder & details
        if (p.Equals("ContentsFinder", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("ContentsFinder", StringComparison.OrdinalIgnoreCase);
        }

        // Companion / Buddy & tabs
        if (p.Equals("Buddy", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("Buddy", StringComparison.OrdinalIgnoreCase);
        }

        // Guildleves
        if (p.Equals("GuildLeve", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("GuildLeve", StringComparison.OrdinalIgnoreCase);
        }

        // Party Finder
        if (p.Equals("LookingForGroup", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("LookingForGroup", StringComparison.OrdinalIgnoreCase);
        }

        // Crafting Log
        if (p.Equals("RecipeNote", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("Recipe", StringComparison.OrdinalIgnoreCase);
        }

        // Gathering Log
        if (p.Equals("GatheringNote", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("Gathering", StringComparison.OrdinalIgnoreCase);
        }

        // Portraits / Banners
        if (p.Equals("BannerList", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("Banner", StringComparison.OrdinalIgnoreCase);
        }

        // Armoury Chest
        if (p.Equals("ArmouryBoard", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("Armoury", StringComparison.OrdinalIgnoreCase);
        }

        // PvP Profile
        if (p.Equals("PVPProfile", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("PvP", StringComparison.OrdinalIgnoreCase) ||
                   c.StartsWith("Pvp", StringComparison.OrdinalIgnoreCase);
        }

        // User Macros
        if (p.Equals("Macro", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("Macro", StringComparison.OrdinalIgnoreCase);
        }

        // Actions & Traits
        if (p.Equals("ActionMenu", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("ActionMenu", StringComparison.OrdinalIgnoreCase);
        }

        // Config windows
        if (p.Equals("ConfigCharacter", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("ConfigCharacter", StringComparison.OrdinalIgnoreCase);
        }
        if (p.Equals("ConfigSystem", StringComparison.OrdinalIgnoreCase))
        {
            return c.StartsWith("ConfigSystem", StringComparison.OrdinalIgnoreCase);
        }

        // General prefix fallback: if child starts with the parent addon name
        if (c.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
