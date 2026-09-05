using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Interface.Windowing;
using Umbra.Widgets;
using Umbra.WindowManager.Services.WindowManager;
using Umbra.WindowManager.Widgets;
using Una.Drawing;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class WindowManagerWidgetTests
{
    private class DummyWindow : Window
    {
        public DummyWindow(string name) : base(name)
        {
            this.IsOpen = true;
        }

        public override void Draw() { }
    }

    private static WindowManagerWidget CreateWidget(WindowManagerService service)
    {
        var info = new WidgetInfo("WindowManagerWidget", "Window Manager", "Window manager widget");
        return new WindowManagerWidget(info, null, null, service);
    }

    [Fact]
    public void Constructor_InitializesRootNodeWithExpectedStyles()
    {
        var service = new WindowManagerService();
        var widget = CreateWidget(service);

        Assert.NotNull(widget.Node);
        Assert.Null(widget.Popup);
        Assert.Equal(Flow.Horizontal, widget.Node.Style.Flow);
        Assert.Equal((AutoSize.Fit, AutoSize.Fit), widget.Node.Style.AutoSize);
        Assert.Equal(4f, widget.Node.Style.Gap);
        Assert.Equal("Auto", widget.DisplayMode);
        Assert.Equal(140, widget.MaxTitleWidth);
        Assert.True(widget.GroupDockedTabs);
    }

    [Fact]
    public void UpdateButtons_CreatesButtonForOpenWindow()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("TestWindow##TestId");
        service.RegisterWindow(win);

        var widget = CreateWidget(service);
        widget.UpdateButtons();

        Assert.Single(widget.Node.ChildNodes);
        Assert.True(widget.WindowNodes.TryGetValue("TestWindow##TestId", out var btnNode));
        Assert.NotNull(btnNode);
        Assert.Equal("TestWindow", btnNode.NodeValue);
        Assert.Equal("TestWindow", btnNode.Tooltip);
        Assert.Equal(1.0f, btnNode.Style.Opacity);
        Assert.Contains("open", btnNode.ClassList);
        Assert.DoesNotContain("minimized", btnNode.ClassList);
    }

    [Fact]
    public void UpdateButtons_AppliesMinimizedStyleAndTooltip()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("MyTool##ToolId");
        var tw = service.RegisterWindow(win);
        service.Minimize(tw);

        var widget = CreateWidget(service);
        widget.UpdateButtons();

        Assert.Single(widget.Node.ChildNodes);
        var btnNode = widget.WindowNodes["MyTool##ToolId"];
        Assert.Equal("MyTool [Minimized]", btnNode.Tooltip);
        Assert.Contains("minimized", btnNode.ClassList);
        Assert.Equal(0.6f, btnNode.Style.Opacity);
    }

    [Fact]
    public void UpdateButtons_AppliesActiveStyleWhenFocused()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("FocusedWin") { IsFocused = true };
        service.RegisterWindow(win);

        var widget = CreateWidget(service);
        widget.UpdateButtons();

        var btnNode = widget.WindowNodes["FocusedWin"];
        Assert.Contains("active", btnNode.ClassList);
    }

    [Fact]
    public void UpdateButtons_RemovesNodeWhenWindowNoLongerVisible()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("ClosingWin");
        var tw = service.RegisterWindow(win);

        var widget = CreateWidget(service);
        widget.UpdateButtons();
        Assert.Single(widget.Node.ChildNodes);

        // Close window (not open and not minimized)
        service.Close(tw);
        widget.UpdateButtons();

        Assert.Empty(widget.Node.ChildNodes);
        Assert.Empty(widget.WindowNodes);
    }

    [Fact]
    public void UpdateButtons_HandlesDisplayModes()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("DisplayModeWin");
        service.RegisterWindow(win);

        var widget = CreateWidget(service);

        // Auto mode -> shows text
        widget.DisplayMode = "Auto";
        widget.UpdateButtons();
        Assert.Equal("DisplayModeWin", widget.WindowNodes["DisplayModeWin"].NodeValue);

        // Taskbar mode -> shows text
        widget.DisplayMode = "Taskbar";
        widget.UpdateButtons();
        Assert.Equal("DisplayModeWin", widget.WindowNodes["DisplayModeWin"].NodeValue);

        // IconOnly mode -> empty text
        widget.DisplayMode = "IconOnly";
        widget.UpdateButtons();
        Assert.Equal(string.Empty, widget.WindowNodes["DisplayModeWin"].NodeValue);
        Assert.Equal("DisplayModeWin", widget.WindowNodes["DisplayModeWin"].Tooltip);
    }

    [Fact]
    public void UpdateButtons_LeftClickTogglesWindow()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("ToggleWin");
        var tw = service.RegisterWindow(win);

        var widget = CreateWidget(service);
        widget.UpdateButtons();

        var btnNode = widget.WindowNodes["ToggleWin"];

        // Focused open window -> toggle should minimize
        win.IsFocused = true;

        // Trigger Click event via reflection or public event
        var clickEvent = typeof(Node).GetField("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
        var clickDelegate = clickEvent?.GetValue(btnNode) as Action<Node>;
        clickDelegate?.Invoke(btnNode);

        Assert.True(tw.IsMinimized);
        Assert.False(tw.IsOpen);

        // Minimized window -> toggle should restore
        clickDelegate?.Invoke(btnNode);
        Assert.False(tw.IsMinimized);
        Assert.True(tw.IsOpen);
    }

    [Fact]
    public void UpdateButtons_RightClickClosesWindow()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("CloseWin");
        var tw = service.RegisterWindow(win);

        var widget = CreateWidget(service);
        widget.UpdateButtons();

        var btnNode = widget.WindowNodes["CloseWin"];

        var rClickEvent = typeof(Node).GetField("OnRightClick", BindingFlags.Instance | BindingFlags.NonPublic);
        var rClickDelegate = rClickEvent?.GetValue(btnNode) as Action<Node>;
        rClickDelegate?.Invoke(btnNode);

        Assert.False(tw.IsOpen);
        Assert.False(tw.IsMinimized);
    }

    [Fact]
    public void UpdateButtons_MarksDockGroupWhenDockGroupKeyIsPresent()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("DockedTab");
        var tw = service.RegisterWindow(win);
        tw.DockGroupKey = "dock_123";

        var widget = CreateWidget(service);
        widget.UpdateButtons();

        var btnNode = widget.WindowNodes["DockedTab"];
        Assert.Contains("dock-group", btnNode.ClassList);
    }

    [Fact]
    public void UpdateButtons_TogglesDockGroupOff_WhenDockGroupKeyBecomesNull()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("DockedTab");
        var tw = service.RegisterWindow(win);
        tw.DockGroupKey = "dock_123";

        var widget = CreateWidget(service);
        widget.UpdateButtons();

        var btnNode = widget.WindowNodes["DockedTab"];
        Assert.Contains("dock-group", btnNode.ClassList);

        // Undock the window
        tw.DockGroupKey = null;
        widget.UpdateButtons();

        Assert.DoesNotContain("dock-group", btnNode.ClassList);
    }

    [Fact]
    public void GetConfigVariables_ReturnsExpectedVariables()
    {
        var service = new WindowManagerService();
        var widget = CreateWidget(service);

        var method = typeof(WindowManagerWidget).GetMethod("GetConfigVariables", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var vars = (method.Invoke(widget, null) as IEnumerable<IWidgetConfigVariable>)?.ToList();

        Assert.NotNull(vars);
        Assert.Equal(3, vars.Count);
        Assert.Contains(vars, v => v.Id == "WindowManager.DisplayMode");
        Assert.Contains(vars, v => v.Id == "WindowManager.MaxTitleWidth");
        Assert.Contains(vars, v => v.Id == "WindowManager.GroupDockedTabs");
    }

    [Fact]
    public void PropertySetters_UpdateBackingFieldsAndSyncWhenConfigured()
    {
        var service = new WindowManagerService();
        var widget = CreateWidget(service);

        widget.DisplayMode = "IconOnly";
        Assert.Equal("IconOnly", widget.DisplayMode);

        widget.MaxTitleWidth = 200;
        Assert.Equal(200, widget.MaxTitleWidth);

        widget.GroupDockedTabs = false;
        Assert.False(widget.GroupDockedTabs);
    }
}
