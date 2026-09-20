using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace Umbra.WindowManager.Services.WindowManager;

public record MainCommandEntry(
    string AddonName,
    AgentId AgentId,
    uint MainCommandId,
    uint IconId,
    string DefaultTitle
);

/// <summary>
/// Authoritative registry of supported user-facing native FFXIV in-game windows mapped to
/// their corresponding <see cref="AgentId"/>, <c>MainCommand</c> ID, default title, and icon ID.
/// </summary>
public static class MainCommandRegistry
{
    private static readonly Dictionary<string, MainCommandEntry> AddonMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<AgentId, MainCommandEntry> AgentMap = new();
    private static readonly Dictionary<uint, MainCommandEntry> CommandMap = new();
    private static readonly List<MainCommandEntry> AllEntries = [];

    static MainCommandRegistry()
    {
        Register("Character", AgentId.Status, 2, 1, "Character");
        Register("Inventory", AgentId.Inventory, 10, 2, "Inventory");
        RegisterAlias("InventoryGrid", "Inventory");
        Register("ActionMenu", AgentId.ActionMenu, 3, 4, "Actions & Traits");
        Register("Journal", AgentId.QuestJournal, 4, 5, "Journal");
        Register("ContentsTimer", AgentId.ContentsTimer, 5, 47, "Timers");
        Register("Achievement", AgentId.Achievement, 6, 6, "Achievements");
        Register("GatheringNote", AgentId.GatheringNote, 7, 23, "Gathering Log");
        Register("MonsterNote", AgentId.MonsterNote, 8, 21, "Hunting Log");
        Register("RecipeNote", AgentId.RecipeNote, 9, 22, "Crafting Log");
        Register("ArmouryBoard", AgentId.ArmouryBoard, 25, 32, "Armoury Chest");
        Register("PartyMemberList", AgentId.PartyMember, 12, 17, "Party Members");
        Register("FriendList", AgentId.Friendlist, 13, 18, "Friend List");
        Register("BlackList", AgentId.Blacklist, 14, 19, "Blacklist");
        Register("Search", AgentId.Search, 15, 20, "Player Search");
        RegisterAlias("PlayerSearch", "Search");
        Register("AreaMap", AgentId.Map, 16, 7, "Map");
        Register("Emote", AgentId.Emote, 17, 9, "Emotes");
        Register("Marker", AgentId.Marker, 18, 10, "Signs");
        Register("Macro", AgentId.Macro, 21, 30, "User Macros");
        Register("FreeCompany", AgentId.FreeCompany, 27, 8, "Free Company");
        Register("Linkshell", AgentId.Linkshell, 28, 11, "Linkshells");
        Register("FishingNote", AgentId.FishingNote, 29, 24, "Fishing Log");
        Register("HowTo", AgentId.HowTo, 30, 33, "Active Help");
        Register("RecommendList", AgentId.RecommendList, 31, 25, "Recommendations");
        Register("SupportMain", AgentId.SupportMain, 32, 13, "Support Desk");
        Register("ContentsFinder", AgentId.ContentsFinder, 33, 46, "Duty Finder");
        Register("ConfigCharacter", AgentId.ConfigCharacter, 34, 48, "Character Configuration");
        Register("ConfigSystem", AgentId.Config, 19, 29, "System Configuration");
        Register("ConfigKeyBind", AgentId.Configkey, 20, 28, "Keybind");
        Register("Buddy", AgentId.Buddy, 42, 49, "Companion");
        Register("Housing", AgentId.Housing, 44, 52, "Housing");
        Register("PVPProfile", AgentId.PvpProfile, 56, 53, "PvP Profile");
        Register("LookingForGroup", AgentId.LookingForGroup, 57, 54, "Party Finder");
        Register("FieldMarker", AgentId.FieldMarker, 58, 55, "Waymarks");
        Register("ContentsNote", AgentId.ContentsNote, 60, 57, "Challenge Log");
        Register("MountNotebook", AgentId.MountNotebook, 61, 58, "Mount Guide");
        Register("MinionNotebook", AgentId.MinionNotebook, 62, 59, "Minion Guide");
        Register("AdventureNotebook", AgentId.AdventureNotebook, 64, 61, "Sightseeing Log");
        Register("GoldSaucer", AgentId.GoldSaucer, 65, 62, "Gold Saucer");
        RegisterAlias("GoldSaucerInfo", "GoldSaucer");
        Register("Currency", AgentId.Currency, 66, 63, "Currency");
        Register("AetherCurrent", AgentId.AetherCurrent, 67, 64, "Aether Currents");
        Register("OrchestrionPlayList", AgentId.OrchestrionPlayList, 69, 67, "Orchestrion List");
        Register("RaidFinder", AgentId.RaidFinder, 72, 69, "Raid Finder");
        Register("ContactList", AgentId.ContactList, 74, 71, "Contacts");
        Register("MountSpeed", AgentId.MountSpeed, 75, 72, "Mount Speed");
        Register("InventoryBuddy", AgentId.InventoryBuddy, 77, 74, "Chocobo Saddlebag");
        Register("AozNotebook", AgentId.AozNotebook, 81, 78, "Blue Magic Spellbook");
        Register("Dawn", AgentId.Dawn, 82, 79, "Trust");
        Register("FateProgress", AgentId.FateProgress, 84, 81, "Shared FATE");
        Register("CircleBook", AgentId.CircleBook, 85, 82, "Fellowships");
        Register("CircleFinder", AgentId.CircleFinder, 86, 83, "Fellowship Finder");
        Register("ArmouryNotebook", AgentId.ArmouryNotebook, 87, 85, "Collection");
        Register("QuestRedo", AgentId.QuestRedo, 88, 84, "New Game+");
        Register("OrnamentNoteBook", AgentId.OrnamentNoteBook, 89, 86, "Fashion Accessories");
        Register("DawnStory", AgentId.DawnStory, 91, 89, "Duty Support");
        Register("BannerList", AgentId.BannerList, 92, 88, "Portraits");
        Register("CharaCard", AgentId.CharaCard, 93, 90, "Adventurer Plate");
        Register("VVDFinder", AgentId.VVDFinder, 94, 91, "V&C Dungeon Finder");
        Register("Glasses", AgentId.Glasses, 95, 92, "Facewear");
        Register("MuteList", AgentId.Mutelist, 96, 93, "Mute List");
        Register("TermFilter", AgentId.TermFilter, 97, 94, "Term Filter");
    }

    private static void Register(string addonName, AgentId agentId, uint mainCommandId, uint iconId, string defaultTitle)
    {
        var entry = new MainCommandEntry(addonName, agentId, mainCommandId, iconId, defaultTitle);
        AddonMap[addonName] = entry;
        AgentMap[agentId] = entry;
        CommandMap[mainCommandId] = entry;
        AllEntries.Add(entry);
    }

    private static void RegisterAlias(string aliasAddonName, string primaryAddonName)
    {
        if (AddonMap.TryGetValue(primaryAddonName, out var entry))
        {
            AddonMap[aliasAddonName] = entry;
        }
    }

    public static bool TryGetByAddonName(string? addonName, [NotNullWhen(true)] out MainCommandEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(addonName))
            return false;

        return AddonMap.TryGetValue(addonName, out entry);
    }

    public static bool TryGetByAgentId(AgentId agentId, [NotNullWhen(true)] out MainCommandEntry? entry)
    {
        return AgentMap.TryGetValue(agentId, out entry);
    }

    public static bool TryGetByCommandId(uint commandId, [NotNullWhen(true)] out MainCommandEntry? entry)
    {
        return CommandMap.TryGetValue(commandId, out entry);
    }

    public static bool IsSupported(string? addonName) =>
        !string.IsNullOrWhiteSpace(addonName) && AddonMap.ContainsKey(addonName);

    public static IReadOnlyCollection<MainCommandEntry> Entries => AllEntries;
}
