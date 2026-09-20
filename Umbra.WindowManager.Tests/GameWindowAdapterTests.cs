using System;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class GameWindowAdapterTests
{
    private static MainCommandEntry CreateCharacterEntry() =>
        new("Character", AgentId.Status, 2u, 1u, "Character");

    [Fact]
    public void Constructor_SetsExpectedProperties()
    {
        var entry = CreateCharacterEntry();
        var adapter = new GameWindowAdapter("Character", entry);

        Assert.Equal("Character", adapter.AddonName);
        Assert.Same(entry, adapter.Entry);
        Assert.Equal("Character###Game_Character", adapter.WindowName);
        Assert.Equal("Game", adapter.Namespace);
        Assert.Null(adapter.TitleBarButtons);
        Assert.False(adapter.IsLocallyMinimized);
    }

    [Fact]
    public void IsOpen_WhenClosed_InvokesOnClose()
    {
        var entry = CreateCharacterEntry();
        var closeInvoked = false;
        var minimizeInvoked = false;

        var adapter = new GameWindowAdapter("Character", entry)
        {
            CheckIsOpen = () => true,
            CloseAction = () => closeInvoked = true,
            MinimizeAction = () => minimizeInvoked = true,
            IsBeingMinimized = () => false
        };

        Assert.True(adapter.IsOpen);

        adapter.IsOpen = false;

        Assert.True(closeInvoked);
        Assert.False(minimizeInvoked);
        Assert.False(adapter.IsOpen);
    }

    [Fact]
    public void IsOpen_WhenMinimized_InvokesOnMinimize()
    {
        var entry = CreateCharacterEntry();
        var closeInvoked = false;
        var minimizeInvoked = false;

        var adapter = new GameWindowAdapter("Character", entry)
        {
            CheckIsOpen = () => true,
            CloseAction = () => closeInvoked = true,
            MinimizeAction = () => minimizeInvoked = true,
            IsBeingMinimized = () => true
        };

        Assert.True(adapter.IsOpen);

        adapter.IsOpen = false;

        Assert.False(closeInvoked);
        Assert.True(minimizeInvoked);
        Assert.False(adapter.IsOpen);
        Assert.True(adapter.IsLocallyMinimized);
    }

    [Fact]
    public void IsOpen_WhenRestored_InvokesOnRestore()
    {
        var entry = CreateCharacterEntry();
        var restoreInvoked = false;

        var adapter = new GameWindowAdapter("Character", entry)
        {
            CheckIsOpen = () => false,
            RestoreAction = () => restoreInvoked = true,
            IsBeingMinimized = () => true
        };

        adapter.IsOpen = false;
        Assert.True(adapter.IsLocallyMinimized);

        adapter.IsOpen = true;

        Assert.True(restoreInvoked);
        Assert.False(adapter.IsLocallyMinimized);
    }

    [Fact]
    public void BringToFront_InvokesOnBringToFront()
    {
        var entry = CreateCharacterEntry();
        var bringToFrontInvoked = false;

        var adapter = new GameWindowAdapter("Character", entry)
        {
            BringToFrontAction = () => bringToFrontInvoked = true
        };

        adapter.BringToFront();

        Assert.True(bringToFrontInvoked);
    }

    [Fact]
    public void IsFocused_DelegatesToCheckIsFocused()
    {
        var entry = CreateCharacterEntry();
        var focused = false;

        var adapter = new GameWindowAdapter("Character", entry)
        {
            CheckIsFocused = () => focused
        };

        Assert.False(adapter.IsFocused);
        focused = true;
        Assert.True(adapter.IsFocused);
    }

    [Fact]
    public void Integration_WithWindowManagerService_MinimizeAndRestore()
    {
        var service = new WindowManagerService();
        var entry = CreateCharacterEntry();
        var minimized = false;
        var restored = false;
        var isOpen = true;

        var adapter = new GameWindowAdapter("Character", entry)
        {
            CheckIsOpen = () => isOpen,
            MinimizeAction = () => { minimized = true; isOpen = false; },
            RestoreAction = () => { restored = true; isOpen = true; }
        };

        var tracked = service.RegisterWindow(adapter);
        adapter.IsBeingMinimized = () => tracked.IsMinimized;

        Assert.Equal("Character", tracked.CleanTitle);
        Assert.Equal("Game_Character", tracked.Id);
        Assert.Equal("Game", tracked.Namespace);

        // Minimize via service
        service.Minimize(tracked);

        Assert.True(tracked.IsMinimized);
        Assert.True(minimized);
        Assert.False(adapter.IsOpen);

        // Restore via service
        service.Restore(tracked);

        Assert.False(tracked.IsMinimized);
        Assert.True(restored);
        Assert.True(adapter.IsOpen);
    }

    [Fact]
    public void Constructor_WithCustomTitle_UsesCustomTitleInWindowNameAndCleanTitle()
    {
        var entry = CreateCharacterEntry();
        var adapter = new GameWindowAdapter("Character", entry, title: "My Hero");

        Assert.Equal("My Hero", adapter.Title);
        Assert.Equal("My Hero###Game_Character", adapter.WindowName);

        var service = new WindowManagerService();
        var tracked = service.RegisterWindow(adapter);

        Assert.Equal("My Hero", tracked.CleanTitle);
        Assert.Equal("Game_Character", tracked.Id);
    }

    [Fact]
    public void Position_WhenLocallyMinimized_RetainsAndUpdatesSavedPosition()
    {
        var entry = CreateCharacterEntry();
        var adapter = new GameWindowAdapter("Character", entry)
        {
            CheckIsOpen = () => true,
            IsBeingMinimized = () => true
        };

        // Minimize
        adapter.IsOpen = false;
        Assert.True(adapter.IsLocallyMinimized);

        // Setting position while minimized updates saved position
        adapter.Position = new System.Numerics.Vector2(250, 350);
        Assert.Equal(new System.Numerics.Vector2(250, 350), adapter.Position);

        // Native shown resets minimized state and saved position
        adapter.OnNativeShown();
        Assert.False(adapter.IsLocallyMinimized);
    }

    [Theory]
    [InlineData("Character", "CharacterClass", true)]
    [InlineData("Character", "CharacterRepute", true)]
    [InlineData("Character", "CharacterStatus", true)]
    [InlineData("Character", "CharaCard", true)]
    [InlineData("Character", "GearSetList", true)]
    [InlineData("Character", "InventoryGrid", false)]
    [InlineData("Character", "ChatLog", false)]
    [InlineData("Social", "FriendList", true)]
    [InlineData("Social", "PartyMemberList", true)]
    [InlineData("Social", "BlackList", true)]
    [InlineData("Social", "Search", true)]
    [InlineData("FriendList", "Social", true)]
    [InlineData("PartyMemberList", "Social", true)]
    [InlineData("BlackList", "Social", true)]
    [InlineData("GoldSaucer", "GSInfoGeneral", true)]
    [InlineData("GoldSaucer", "GSInfoCardList", true)]
    [InlineData("GoldSaucerInfo", "GSInfoChocoboParam", true)]
    [InlineData("GoldSaucer", "CharacterClass", false)]
    [InlineData("Inventory", "InventoryGrid", true)]
    [InlineData("Inventory", "InventoryLarge", true)]
    [InlineData("Inventory", "InventoryExpansion", true)]
    [InlineData("Inventory", "InventoryEventGrid", true)]
    [InlineData("Inventory", "InventoryBuddy", true)]
    [InlineData("Journal", "JournalDetail", true)]
    [InlineData("Journal", "JournalAccept", true)]
    [InlineData("Journal", "JournalResult", true)]
    [InlineData("ContentsFinder", "ContentsFinderDetail", true)]
    [InlineData("ContentsFinder", "ContentsFinderConfirm", true)]
    [InlineData("Buddy", "BuddyEquip", true)]
    [InlineData("Buddy", "BuddyAction", true)]
    [InlineData("LookingForGroup", "LookingForGroupDetail", true)]
    [InlineData("GuildLeve", "GuildLeveDifficulty", true)]
    [InlineData("RecipeNote", "RecipeMaterialList", true)]
    [InlineData("GatheringNote", "GatheringMasterpiece", true)]
    [InlineData("BannerList", "BannerEditor", true)]
    [InlineData("BannerList", "BannerPreview", true)]
    [InlineData("ArmouryBoard", "ArmouryBoard", true)]
    [InlineData("Macro", "Macro", true)]
    [InlineData("ActionMenu", "ActionMenu", true)]
    [InlineData("ConfigCharacter", "ConfigCharacter", true)]
    [InlineData("ConfigSystem", "ConfigSystem", true)]
    [InlineData("PVPProfile", "PvPCharacter", true)]
    [InlineData(null, "CharacterClass", false)]
    [InlineData("Character", null, false)]
    [InlineData("", "", false)]
    public void IsKnownTabOrChildAddon_ValidatesRelationships(string? parentName, string? childName, bool expected)
    {
        var result = GameWindowAdapter.IsKnownTabOrChildAddon(parentName!, childName!);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void OnNativeShown_WhenLocallyMinimized_ResetsMinimizationState()
    {
        var entry = CreateCharacterEntry();
        var adapter = new GameWindowAdapter("Character", entry)
        {
            CheckIsOpen = () => true,
            IsBeingMinimized = () => true
        };

        adapter.IsOpen = false;
        Assert.True(adapter.IsLocallyMinimized);

        adapter.Position = new System.Numerics.Vector2(100, 200);
        Assert.Equal(new System.Numerics.Vector2(100, 200), adapter.Position);

        adapter.OnNativeShown();

        Assert.False(adapter.IsLocallyMinimized);
        Assert.Equal(0, adapter.SavedChildUnitCount);
    }

    [Fact]
    public void Minimize_WhenCalledRepeatedly_PreservesState()
    {
        var entry = CreateCharacterEntry();
        var adapter = new GameWindowAdapter("Character", entry)
        {
            CheckIsOpen = () => true,
            IsBeingMinimized = () => true
        };

        adapter.IsOpen = false;
        Assert.True(adapter.IsLocallyMinimized);

        adapter.Position = new System.Numerics.Vector2(150, 250);
        Assert.Equal(new System.Numerics.Vector2(150, 250), adapter.Position);

        // Second minimize call while already minimized
        adapter.IsOpen = false;
        Assert.True(adapter.IsLocallyMinimized);
        Assert.Equal(new System.Numerics.Vector2(150, 250), adapter.Position);

        adapter.OnNativeShown();
        Assert.False(adapter.IsLocallyMinimized);
    }
}
