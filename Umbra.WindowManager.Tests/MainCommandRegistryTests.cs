using System;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class MainCommandRegistryTests
{
    [Theory]
    [InlineData("Character", AgentId.Status, 2u, 1u, "Character")]
    [InlineData("character", AgentId.Status, 2u, 1u, "Character")] // case-insensitive
    [InlineData("Inventory", AgentId.Inventory, 10u, 2u, "Inventory")]
    [InlineData("InventoryGrid", AgentId.Inventory, 10u, 2u, "Inventory")]
    [InlineData("InventoryExpansion", AgentId.Inventory, 10u, 2u, "Inventory")]
    [InlineData("InventoryLarge", AgentId.Inventory, 10u, 2u, "Inventory")]
    [InlineData("Journal", AgentId.QuestJournal, 4u, 5u, "Journal")]
    [InlineData("ContentsFinder", AgentId.ContentsFinder, 33u, 46u, "Duty Finder")]
    [InlineData("AreaMap", AgentId.Map, 16u, 7u, "Map")]
    [InlineData("ActionMenu", AgentId.ActionMenu, 3u, 4u, "Actions & Traits")]
    [InlineData("Achievement", AgentId.Achievement, 6u, 6u, "Achievements")]
    [InlineData("GoldSaucer", AgentId.GoldSaucer, 65u, 62u, "Gold Saucer")]
    [InlineData("GoldSaucerInfo", AgentId.GoldSaucer, 65u, 62u, "Gold Saucer")]
    [InlineData("Search", AgentId.Search, 15u, 20u, "Player Search")]
    [InlineData("PlayerSearch", AgentId.Search, 15u, 20u, "Player Search")]
    [InlineData("LookingForGroup", AgentId.LookingForGroup, 57u, 54u, "Party Finder")]
    [InlineData("CharaCard", AgentId.CharaCard, 93u, 90u, "Adventurer Plate")]
    public void TryGetByAddonName_SupportedAddons_ReturnsCorrectEntry(
        string addonName,
        AgentId expectedAgentId,
        uint expectedCommandId,
        uint expectedIconId,
        string expectedTitle)
    {
        var found = MainCommandRegistry.TryGetByAddonName(addonName, out var entry);

        Assert.True(found);
        Assert.NotNull(entry);
        Assert.Equal(expectedAgentId, entry.AgentId);
        Assert.Equal(expectedCommandId, entry.MainCommandId);
        Assert.Equal(expectedIconId, entry.IconId);
        Assert.Equal(expectedTitle, entry.DefaultTitle);
        Assert.True(MainCommandRegistry.IsSupported(addonName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Social")]
    [InlineData("ChatLog")]
    [InlineData("_ActionCross")]
    [InlineData("_TargetInfo")]
    [InlineData("_PartyList")]
    [InlineData("HudLayout")]
    [InlineData("ContextMenu")]
    [InlineData("SelectYesno")]
    [InlineData("Talk")]
    public void TryGetByAddonName_UnsupportedAddons_ReturnsFalse(string? addonName)
    {
        var found = MainCommandRegistry.TryGetByAddonName(addonName, out var entry);

        Assert.False(found);
        Assert.Null(entry);
        Assert.False(MainCommandRegistry.IsSupported(addonName));
    }

    [Fact]
    public void TryGetByAgentId_ExistingAgent_ReturnsEntry()
    {
        var found = MainCommandRegistry.TryGetByAgentId(AgentId.Status, out var entry);

        Assert.True(found);
        Assert.NotNull(entry);
        Assert.Equal("Character", entry.AddonName);
        Assert.Equal(2u, entry.MainCommandId);
    }

    [Fact]
    public void TryGetByCommandId_ExistingCommand_ReturnsEntry()
    {
        var found = MainCommandRegistry.TryGetByCommandId(33u, out var entry);

        Assert.True(found);
        Assert.NotNull(entry);
        Assert.Equal("ContentsFinder", entry.AddonName);
        Assert.Equal(AgentId.ContentsFinder, entry.AgentId);
    }

    [Fact]
    public void Entries_AreNonEmpty_AndHaveValidData()
    {
        Assert.NotEmpty(MainCommandRegistry.Entries);
        foreach (var entry in MainCommandRegistry.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.AddonName));
            Assert.False(string.IsNullOrWhiteSpace(entry.DefaultTitle));
            Assert.True(entry.MainCommandId > 0);
            Assert.True(entry.IconId > 0);
        }
    }

    [Theory]
    [InlineData("Character", "Character")]
    [InlineData("character", "Character")]
    [InlineData("InventoryGrid", "Inventory")]
    [InlineData("PlayerSearch", "Search")]
    public void TryGetByUtf8AddonName_NulPaddedBuffer_MatchesLikeStringLookup(string name, string expectedAddon)
    {
        var buffer = new byte[32];
        System.Text.Encoding.UTF8.GetBytes(name, buffer);

        Assert.True(MainCommandRegistry.TryGetByUtf8AddonName(buffer, out var entry));
        Assert.Equal(expectedAddon, entry.AddonName);
        Assert.True(MainCommandRegistry.TryGetByAddonName(name, out var viaString));
        Assert.Same(viaString, entry);
    }

    [Theory]
    [InlineData("")]
    [InlineData("_NaviMap")]
    [InlineData("Characters")]
    [InlineData("Charact")]
    public void TryGetByUtf8AddonName_UnknownOrEmpty_ReturnsFalse(string name)
    {
        var buffer = new byte[32];
        System.Text.Encoding.UTF8.GetBytes(name, buffer);

        Assert.False(MainCommandRegistry.TryGetByUtf8AddonName(buffer, out var entry));
        Assert.Null(entry);
    }

    [Fact]
    public void TryGetByUtf8AddonName_OverlongBuffer_ReturnsFalse()
    {
        Assert.False(MainCommandRegistry.TryGetByUtf8AddonName(new byte[200].Select(_ => (byte)'a').ToArray(), out _));
    }

    [Fact]
    public void TryGetByUtf8AddonName_DoesNotAllocate()
    {
        var buffer = new byte[32];
        System.Text.Encoding.UTF8.GetBytes("Inventory", buffer);
        var miss = new byte[32];
        System.Text.Encoding.UTF8.GetBytes("_NaviMap", miss);

        for (var i = 0; i < 10; i++)
        {
            MainCommandRegistry.TryGetByUtf8AddonName(buffer, out _);
            MainCommandRegistry.TryGetByUtf8AddonName(miss, out _);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++)
        {
            MainCommandRegistry.TryGetByUtf8AddonName(buffer, out _);
            MainCommandRegistry.TryGetByUtf8AddonName(miss, out _);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
