using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Umbra.Common;
using Umbra.Widgets;
using Umbra.WindowManager.Services.WindowManager;
using Umbra.WindowManager.Widgets;
using Una.Drawing;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class MinimizeAllWindowsWidgetTests
{
    private class DummyWindow : Window
    {
        public DummyWindow(string name) : base(name)
        {
            this.IsOpen = true;
        }

        public override void Draw() { }
    }

    private static MinimizeAllWindowsWidget CreateWidget(
        WindowManagerService service,
        Dictionary<string, object>? config = null)
    {
        var info = new WidgetInfo("UmbraMinimizeAllWindowsWidget", "Minimize All Windows", "Minimizes all windows");
        return new MinimizeAllWindowsWidget(info, null, config, service);
    }

    [Fact]
    public void Constructor_InitializesRootNodeAndButtonWithExpectedStyles()
    {
        var service = new WindowManagerService();
        var widget = CreateWidget(service);

        Assert.NotNull(widget.Node);
        Assert.Null(widget.Popup);
        Assert.Equal(Flow.Horizontal, widget.Node.Style.Flow);
        Assert.Equal((AutoSize.Fit, AutoSize.Fit), widget.Node.Style.AutoSize);
        Assert.Equal(4f, widget.Node.Style.Gap);
        Assert.True(widget.Decorate);
        Assert.True(widget.Toggle);
        Assert.True(widget.AutoHide);

        var btn = widget.ButtonNode;
        Assert.NotNull(btn);
        Assert.Contains("minimize-all-btn", btn.ClassList);
        Assert.Contains("decorated", btn.ClassList);

        var icon = widget.IconNode;
        Assert.NotNull(icon);
        Assert.Equal(FontAwesomeIcon.Desktop.ToIconString(), icon.NodeValue);
        Assert.Equal(2u, icon.Style.Font);
        Assert.Equal(13, icon.Style.FontSize);
    }

    [Fact]
    public void UpdateButtonState_WithOpenWindows_SetsMinimizeTooltipAndFullOpacity()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("OpenWindow");
        service.RegisterWindow(win);

        var widget = CreateWidget(service);
        widget.UpdateButtonState();

        Assert.Equal("Minimize All Windows", widget.ButtonNode.Tooltip);
        Assert.Equal(1.0f, widget.ButtonNode.Style.Opacity);
        Assert.NotEqual(false, widget.ButtonNode.Style.IsVisible);
        Assert.DoesNotContain("active", widget.ButtonNode.ClassList);
    }

    [Fact]
    public void UpdateButtonState_WithNoWindowsOpen_WhenAutoHideDisabled_SetsDimmedOpacity()
    {
        var service = new WindowManagerService();
        var widget = CreateWidget(service);
        widget.AutoHide = false;

        widget.UpdateButtonState();

        Assert.Equal("Minimize All Windows", widget.ButtonNode.Tooltip);
        Assert.Equal(0.6f, widget.ButtonNode.Style.Opacity);
        Assert.NotEqual(false, widget.ButtonNode.Style.IsVisible);
        Assert.DoesNotContain("active", widget.ButtonNode.ClassList);
    }

    [Fact]
    public void UpdateButtonState_WithAutoHideOn_HidesButtonWhenNoWindowsOpen()
    {
        var service = new WindowManagerService();
        var widget = CreateWidget(service);

        widget.UpdateButtonState();

        Assert.False(widget.ButtonNode.Style.IsVisible ?? true);
        Assert.False(widget.Node.Style.IsVisible ?? true);
    }

    [Fact]
    public void UpdateButtonState_WithAutoHideOn_ShowsButtonWhenWindowsOpen()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("OpenWindow");
        service.RegisterWindow(win);

        var widget = CreateWidget(service);
        widget.UpdateButtonState();

        Assert.NotEqual(false, widget.ButtonNode.Style.IsVisible);
        Assert.NotEqual(false, widget.Node.Style.IsVisible);
    }

    [Fact]
    public void UpdateButtonState_WithAutoHideOn_ShowsButtonWhenBulkMinimized()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("OpenWindow");
        service.RegisterWindow(win);

        var widget = CreateWidget(service);
        widget.PerformAction(); // minimizes all

        Assert.True(service.CanRestoreBulkMinimized);
        Assert.NotEqual(false, widget.ButtonNode.Style.IsVisible);
        Assert.NotEqual(false, widget.Node.Style.IsVisible);
        Assert.Equal("Restore Windows", widget.ButtonNode.Tooltip);
    }

    [Fact]
    public void PerformAction_WhenWindowsOpen_MinimizesAllWindowsAndSetsActiveState()
    {
        var service = new WindowManagerService();
        var win1 = new DummyWindow("Win1");
        var win2 = new DummyWindow("Win2");
        var tw1 = service.RegisterWindow(win1);
        var tw2 = service.RegisterWindow(win2);

        var widget = CreateWidget(service);
        Assert.True(service.AreAnyWindowsOpen);

        widget.PerformAction();

        Assert.True(tw1.IsMinimized);
        Assert.False(win1.IsOpen);
        Assert.True(tw2.IsMinimized);
        Assert.False(win2.IsOpen);

        Assert.True(service.CanRestoreBulkMinimized);
        Assert.Equal("Restore Windows", widget.ButtonNode.Tooltip);
        Assert.Equal(1.0f, widget.ButtonNode.Style.Opacity);
        Assert.Contains("active", widget.ButtonNode.ClassList);
    }

    [Fact]
    public void PerformAction_WhenBulkMinimized_RestoresWindowsAndResetsState()
    {
        var service = new WindowManagerService();
        var win1 = new DummyWindow("Win1");
        var tw1 = service.RegisterWindow(win1);

        var widget = CreateWidget(service);

        // First click: minimizes
        widget.PerformAction();
        Assert.True(tw1.IsMinimized);
        Assert.Equal("Restore Windows", widget.ButtonNode.Tooltip);

        // Second click: restores
        widget.PerformAction();
        Assert.False(tw1.IsMinimized);
        Assert.True(win1.IsOpen);
        Assert.False(service.CanRestoreBulkMinimized);
        Assert.Equal("Minimize All Windows", widget.ButtonNode.Tooltip);
        Assert.DoesNotContain("active", widget.ButtonNode.ClassList);
    }

    [Fact]
    public void PerformAction_WhenToggleDisabled_AlwaysMinimizes()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Win");
        var tw = service.RegisterWindow(win);

        var widget = CreateWidget(service, new Dictionary<string, object>
        {
            { "MinimizeAll.Toggle", false }
        });

        Assert.False(widget.Toggle);

        widget.PerformAction();
        Assert.True(tw.IsMinimized);

        // Even though windows were minimized, toggle is disabled so next click does not restore
        widget.PerformAction();
        Assert.True(tw.IsMinimized);
        Assert.Equal("Minimize All Windows", widget.ButtonNode.Tooltip);
        Assert.DoesNotContain("active", widget.ButtonNode.ClassList);
    }

    [Fact]
    public void ClickEvent_TriggersPerformAction()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Win");
        var tw = service.RegisterWindow(win);

        var widget = CreateWidget(service);

        // Trigger Click event via reflection matching Una.Drawing.Node
        var clickEvent = typeof(Node).GetField("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
        var clickDelegate = clickEvent?.GetValue(widget.ButtonNode) as Action<Node>;
        Assert.NotNull(clickDelegate);

        clickDelegate.Invoke(widget.ButtonNode);

        Assert.True(tw.IsMinimized);
        Assert.False(win.IsOpen);
        Assert.Equal("Restore Windows", widget.ButtonNode.Tooltip);
    }

    [Fact]
    public void ConfigVariables_DecorateAndToggle_ReadAndWriteCorrectly()
    {
        var service = new WindowManagerService();
        var widget = CreateWidget(service);

        var method = typeof(MinimizeAllWindowsWidget).GetMethod("GetConfigVariables", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var configVars = (method.Invoke(widget, null) as IEnumerable<IWidgetConfigVariable>)?.ToList();
        Assert.NotNull(configVars);
        Assert.Equal(3, configVars.Count);
        Assert.Contains(configVars, v => v.Id == "Decorate");
        Assert.Contains(configVars, v => v.Id == "MinimizeAll.Toggle");
        Assert.Contains(configVars, v => v.Id == "MinimizeAll.AutoHide");

        widget.Decorate = false;
        Assert.False(widget.Decorate);
        widget.UpdateButtonState();
        Assert.DoesNotContain("decorated", widget.ButtonNode.ClassList);

        widget.Decorate = true;
        Assert.True(widget.Decorate);
        widget.UpdateButtonState();
        Assert.Contains("decorated", widget.ButtonNode.ClassList);

        widget.Toggle = false;
        Assert.False(widget.Toggle);
        widget.Toggle = true;
        Assert.True(widget.Toggle);

        widget.AutoHide = false;
        Assert.False(widget.AutoHide);
        widget.AutoHide = true;
        Assert.True(widget.AutoHide);
    }

    [Fact]
    public void PerformAction_InterleavedWindowOpen_ResetsToggleStateToMinimize()
    {
        var service = new WindowManagerService();
        var win1 = new DummyWindow("Win1");
        var tw1 = service.RegisterWindow(win1);

        var widget = CreateWidget(service);

        // Bulk minimize win1
        widget.PerformAction();
        Assert.True(tw1.IsMinimized);
        Assert.Equal("Restore Windows", widget.ButtonNode.Tooltip);
        Assert.Contains("active", widget.ButtonNode.ClassList);

        // User manually opens/restores another window
        var win2 = new DummyWindow("Win2");
        var tw2 = service.RegisterWindow(win2);

        // Next frame / update
        widget.UpdateButtonState();
        Assert.Equal("Minimize All Windows", widget.ButtonNode.Tooltip);
        Assert.DoesNotContain("active", widget.ButtonNode.ClassList);

        // Clicking again minimizes all open windows (now win2 is minimized too)
        widget.PerformAction();
        Assert.True(tw1.IsMinimized);
        Assert.True(tw2.IsMinimized);
        Assert.Equal("Restore Windows", widget.ButtonNode.Tooltip);
        Assert.Contains("active", widget.ButtonNode.ClassList);
    }

    [Fact]
    public void Constructor_WithDecorateFalseInConfig_AppliesUndecoratedStyle()
    {
        var service = new WindowManagerService();
        var widget = CreateWidget(service, new Dictionary<string, object>
        {
            { "Decorate", false }
        });

        Assert.False(widget.Decorate);
        Assert.DoesNotContain("decorated", widget.ButtonNode.ClassList);
    }

    [Fact]
    public void OnUpdate_RefreshesButtonState()
    {
        var service = new WindowManagerService();
        var widget = CreateWidget(service);

        var win = new DummyWindow("NewWin");
        service.RegisterWindow(win);

        var method = typeof(MinimizeAllWindowsWidget).GetMethod("OnUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method.Invoke(widget, null);

        Assert.Equal(1.0f, widget.ButtonNode.Style.Opacity);
        Assert.Equal("Minimize All Windows", widget.ButtonNode.Tooltip);
    }
}

