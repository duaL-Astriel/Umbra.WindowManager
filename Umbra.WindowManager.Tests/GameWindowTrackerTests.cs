using System;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Umbra.Common;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class GameWindowTrackerTests
{
    [Fact]
    public void HandleAddonSetup_SupportedAddon_RegistersWithWindowManagerService()
    {
        var service = new WindowManagerService();
        using var tracker = new GameWindowTracker(service);

        var registered = tracker.HandleAddonEvent(
            "Character",
            address: 0,
            isSetupOrShow: true);

        Assert.True(registered);
        var tracked = service.GetTrackedWindows();
        Assert.Single(tracked);

        var tw = tracked.First();
        Assert.Equal("Character", tw.CleanTitle);
        Assert.Equal("Game_Character", tw.Id);
        Assert.Equal("Game", tw.Namespace);
        Assert.Equal("FFXIV", tw.PluginInternalName);
        Assert.True(tw.HasConfirmedUi);
    }

    [Theory]
    [InlineData("ChatLog")]
    [InlineData("_TargetInfo")]
    [InlineData("ContextMenu")]
    [InlineData("")]
    [InlineData(null)]
    public void HandleAddonSetup_UnsupportedAddon_Ignored(string? addonName)
    {
        var service = new WindowManagerService();
        using var tracker = new GameWindowTracker(service);

        var registered = tracker.HandleAddonEvent(
            addonName!,
            address: 0,
            isSetupOrShow: true);

        Assert.False(registered);
        Assert.Empty(service.GetTrackedWindows());
    }

    [Fact]
    public void HandleAddonClose_SupportedAddon_UnregistersFromWindowManagerService()
    {
        var service = new WindowManagerService();
        using var tracker = new GameWindowTracker(service);

        tracker.HandleAddonEvent("Journal", address: 0, isSetupOrShow: true);
        Assert.Single(service.GetTrackedWindows());

        tracker.HandleAddonClose("Journal");
        Assert.Empty(service.GetTrackedWindows());
    }

    [Fact]
    public void HandleAddonShow_WhenLocallyMinimized_ResetsMinimizedState()
    {
        var service = new WindowManagerService();
        using var tracker = new GameWindowTracker(service);

        tracker.HandleAddonEvent("Inventory", address: 0, isSetupOrShow: true);
        var tw = service.GetTrackedWindows().First();

        // Minimize window via service
        service.Minimize(tw);
        Assert.True(tw.IsMinimized);

        // Native show event occurs (player reopened inventory via hotkey)
        tracker.HandleAddonEvent("Inventory", address: 0, isSetupOrShow: true);

        // Minimized state should be reset
        Assert.False(tw.IsMinimized);
    }

    [Fact]
    public void HandleAddonHide_WhenLocallyMinimized_DoesNotUntrackWindow()
    {
        var service = new WindowManagerService();
        using var tracker = new GameWindowTracker(service);

        tracker.HandleAddonEvent("Inventory", address: 0, isSetupOrShow: true);
        var tw = service.GetTrackedWindows().First();

        // Minimize window via service
        service.Minimize(tw);
        Assert.True(tw.IsMinimized);

        // Native hide event occurs while minimized
        var result = tracker.HandleAddonHide("Inventory");

        // Should NOT untrack minimized window
        Assert.False(result);
        Assert.Single(service.GetTrackedWindows());
        Assert.True(tw.IsMinimized);
    }

    [Fact]
    public void HandleAddonHide_WhenNotMinimized_UntracksWindow()
    {
        var service = new WindowManagerService();
        using var tracker = new GameWindowTracker(service);

        tracker.HandleAddonEvent("Inventory", address: 0, isSetupOrShow: true);
        Assert.Single(service.GetTrackedWindows());

        // Native hide event occurs when NOT minimized (closed in game)
        var result = tracker.HandleAddonHide("Inventory");

        Assert.True(result);
        Assert.Empty(service.GetTrackedWindows());
    }

    [Fact]
    public void TrackOrUpdateAddon_WhenMinimized_WithoutNativeShow_PreservesMinimizedState()
    {
        var service = new WindowManagerService();
        using var tracker = new GameWindowTracker(service);

        MainCommandRegistry.TryGetByAddonName("Character", out var entry);
        Assert.NotNull(entry);

        var tw = tracker.TrackOrUpdateAddon(entry, address: 0, isNativeShow: true);
        service.Minimize(tw);
        Assert.True(tw.IsMinimized);

        // Periodic scan updates addon without native show
        var updated = tracker.TrackOrUpdateAddon(entry, address: 0, isNativeShow: false);

        Assert.Same(tw, updated);
        Assert.True(updated.IsMinimized);
        var adapter = tracker.KnownAdapters["Character"];
        Assert.True(adapter.IsLocallyMinimized);
    }

    [Fact]
    public void Dispose_UnregistersAllTrackedWindows()
    {
        var service = new WindowManagerService();
        var tracker = new GameWindowTracker(service);

        tracker.HandleAddonEvent("Character", address: 0, isSetupOrShow: true);
        tracker.HandleAddonEvent("Journal", address: 0, isSetupOrShow: true);
        Assert.Equal(2, service.GetTrackedWindows().Count);

        tracker.Dispose();

        Assert.Empty(service.GetTrackedWindows());
    }

    [Fact]
    public void PublicConstructors_DoNotDependOnUnregisteredServices()
    {
        var ctors = typeof(GameWindowTracker).GetConstructors();
        Assert.Single(ctors);

        var parameters = ctors[0].GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(WindowManagerService), parameters[0].ParameterType);
    }

    [Fact]
    public void ServiceActivator_CanInstantiateGameWindowTracker()
    {
        Framework.RegisterAssembly(typeof(GameWindowTracker).Assembly);
        var scType = typeof(Umbra.Common.ServiceAttribute).Assembly.GetType("Umbra.Common.ServiceContainer");
        var registerMethod = scType?.GetMethod("RegisterDefinitionsFromAssemblies", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        try
        {
            registerMethod?.Invoke(null, null);
        }
        catch (System.Reflection.TargetInvocationException)
        {
            // Already registered
        }

        using var tracker = Umbra.Common.ServiceActivator.CreateInstance<GameWindowTracker>();
        Assert.NotNull(tracker);
    }

    [Fact]
    public void CreateForTest_AllowsExplicitInjection()
    {
        var service = new WindowManagerService();
        using var tracker = GameWindowTracker.CreateForTest(service, addonLifecycle: null, dataManager: null);
        Assert.NotNull(tracker);
    }

    [Fact]
    public void TabSwitching_UntracksOldTabAndKeepsSingleWindow()
    {
        var service = new WindowManagerService();
        using var tracker = new GameWindowTracker(service);

        // Player opens Party Members tab
        tracker.HandleAddonEvent("PartyMemberList", address: 0, isSetupOrShow: true);
        Assert.Single(service.GetTrackedWindows());
        Assert.Equal("Party Members", service.GetTrackedWindows().First().CleanTitle);

        // Player switches to Friend List tab
        tracker.HandleAddonEvent("FriendList", address: 0, isSetupOrShow: true);
        var tracked = service.GetTrackedWindows();
        Assert.Single(tracked);
        Assert.Equal("Friend List", tracked.First().CleanTitle);
    }

    [Fact]
    public void HandleAddonClose_ParentContainer_UntracksChildTab()
    {
        var service = new WindowManagerService();
        using var tracker = new GameWindowTracker(service);

        tracker.HandleAddonEvent("FriendList", address: 0, isSetupOrShow: true);
        Assert.Single(service.GetTrackedWindows());

        // Player clicks X on the Social container window
        var closed = tracker.HandleAddonClose("Social");
        Assert.True(closed);
        Assert.Empty(service.GetTrackedWindows());
    }

    [Fact]
    public void HandleAddonHide_ParentContainer_UntracksChildTabWhenNotMinimized()
    {
        var service = new WindowManagerService();
        using var tracker = new GameWindowTracker(service);

        tracker.HandleAddonEvent("FriendList", address: 0, isSetupOrShow: true);
        Assert.Single(service.GetTrackedWindows());

        // Parent container hidden while not minimized
        var hidden = tracker.HandleAddonHide("Social");
        Assert.True(hidden);
        Assert.Empty(service.GetTrackedWindows());
    }

    [Fact]
    public void HandleAddonEvent_ParentContainer_RestoresChildTabWhenMinimized()
    {
        var service = new WindowManagerService();
        using var tracker = new GameWindowTracker(service);

        // Player opened Friend List
        tracker.HandleAddonEvent("FriendList", address: 0, isSetupOrShow: true);
        var tw = service.GetTrackedWindows().First();
        Assert.False(tw.IsMinimized);

        // Player minimizes Friend List
        service.Minimize(tw);
        Assert.True(tw.IsMinimized);

        // In-game native event fires for parent container "Social" (player pressed hotkey 'O' or opened menu)
        var handled = tracker.HandleAddonEvent("Social", address: 0, isSetupOrShow: true);

        Assert.True(handled);
        Assert.False(tw.IsMinimized);
        Assert.True(tracker.KnownAdapters.TryGetValue("FriendList", out var adapter));
        Assert.False(adapter.IsLocallyMinimized);
    }

    [Fact]
    public void TabSwitching_WhenOldTabMinimized_RestoresOldTabStateBeforeUntracking()
    {
        var service = new WindowManagerService();
        using var tracker = new GameWindowTracker(service);

        // Player opened Friend List
        tracker.HandleAddonEvent("FriendList", address: 0, isSetupOrShow: true);
        var twFriend = service.GetTrackedWindows().First();

        // Player minimizes Friend List
        service.Minimize(twFriend);
        Assert.True(twFriend.IsMinimized);
        Assert.True(tracker.KnownAdapters.TryGetValue("FriendList", out var friendAdapter));
        Assert.True(friendAdapter.IsLocallyMinimized);


        // Player switches to Party Members tab (in-game or hotkey 'P')
        tracker.HandleAddonEvent("PartyMemberList", address: 0, isSetupOrShow: true);

        // FriendList should be untracked, and PartyMemberList tracked
        var tracked = service.GetTrackedWindows();
        Assert.Single(tracked);
        Assert.Equal("Party Members", tracked.First().CleanTitle);
        Assert.False(tracker.KnownAdapters.ContainsKey("FriendList"));
        // Old tab state must not be left minimized/suppressed
        Assert.False(friendAdapter.IsLocallyMinimized);
    }
}
