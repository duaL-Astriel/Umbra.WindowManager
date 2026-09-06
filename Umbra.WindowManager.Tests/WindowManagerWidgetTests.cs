using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Umbra.Common;
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
        Assert.True(widget.Decorate);
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
        // Button is [icon][label]; the clean title lives on the label child (issue #5).
        Assert.Equal("T", btnNode.ChildNodes[0].NodeValue);       // monogram icon fallback
        Assert.Equal("TestWindow", btnNode.ChildNodes[1].NodeValue);
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
    public void UpdateButtons_IgnoresOverlayWindowsWithoutTitleBar()
    {
        var service = new WindowManagerService();
        var overlay = new DummyWindow("OverlayHelper") { Flags = ImGuiWindowFlags.NoTitleBar };
        service.RegisterWindow(overlay);

        var widget = CreateWidget(service);
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

        // Auto mode (single window) -> Taskbar-like, label visible.
        widget.DisplayMode = "Auto";
        widget.UpdateButtons();
        var node = widget.WindowNodes["DisplayModeWin"];
        Assert.Equal("DisplayModeWin", node.ChildNodes[1].NodeValue);
        Assert.NotEqual(false, node.ChildNodes[1].Style.IsVisible);

        // Taskbar mode -> label visible.
        widget.DisplayMode = "Taskbar";
        widget.UpdateButtons();
        Assert.NotEqual(false, node.ChildNodes[1].Style.IsVisible);

        // IconOnly mode -> label hidden, tooltip retains the title, icon still present.
        widget.DisplayMode = "IconOnly";
        widget.UpdateButtons();
        Assert.False(node.ChildNodes[1].Style.IsVisible ?? true);
        Assert.Equal("D", node.ChildNodes[0].NodeValue);
        Assert.Equal("DisplayModeWin", node.Tooltip);
    }

    [Fact]
    public void MenuPopupButton_SettingInvalidNodeId_ThrowsArgumentException()
    {
        var button = new MenuPopup.Button("test");
        Assert.Throws<ArgumentException>(() => button.Id = "AutoRetainer 4.6.1.34 | Session expires in 2 days 23 hours###AutoRetainer");
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
    public void UpdateButtons_ClickActsOnReinstantiatedWindow()
    {
        // Regression for issue #2: after a plugin re-instantiates a same-named window, the button's
        // click handler must act on the NEW TrackedWindow, not the dead original.
        var service = new WindowManagerService();
        var win1 = new DummyWindow("Reload##Same") { IsOpen = true, IsFocused = true };
        var tw1 = service.RegisterWindow(win1);

        var widget = CreateWidget(service);
        widget.UpdateButtons();

        var win2 = new DummyWindow("Reload##Same") { IsOpen = true, IsFocused = true };
        var tw2 = service.RegisterWindow(win2);
        Assert.NotSame(tw1, tw2);

        widget.UpdateButtons();

        var btnNode = widget.WindowNodes["Reload##Same"];
        var clickEvent = typeof(Node).GetField("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
        var clickDelegate = clickEvent?.GetValue(btnNode) as Action<Node>;
        clickDelegate?.Invoke(btnNode);

        // New instance is focused+open -> minimized; old instance untouched.
        Assert.True(tw2.IsMinimized);
        Assert.False(win2.IsOpen);
        Assert.False(tw1.IsMinimized);
        Assert.True(win1.IsOpen);
    }

    [Fact]
    public void BuildContextActions_OpenWindow_OffersMinimizeAndClose()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Ctx##Open") { IsOpen = true };
        var tw = service.RegisterWindow(win);
        var widget = CreateWidget(service);

        var actions = widget.BuildContextActions(tw);
        Assert.Equal(new[] { "minimize", "close" }, actions.Select(a => a.Id).ToArray());

        actions.Single(a => a.Id == "close").Execute();
        Assert.False(win.IsOpen);
        Assert.False(tw.IsMinimized);
    }

    [Fact]
    public void BuildContextActions_MinimizedWindow_OffersRestoreAndClose()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("Ctx##Min") { IsOpen = true };
        var tw = service.RegisterWindow(win);
        service.Minimize(tw);
        var widget = CreateWidget(service);

        var actions = widget.BuildContextActions(tw);
        Assert.Equal(new[] { "restore", "close" }, actions.Select(a => a.Id).ToArray());

        actions.Single(a => a.Id == "restore").Execute();
        Assert.False(tw.IsMinimized);
        Assert.True(win.IsOpen);
    }

    [Fact]
    public void BuildContextActions_DockGroup_OffersSelectActiveAndCloseAll()
    {
        var service = new WindowManagerService();
        var win1 = new DummyWindow("Tab A") { IsOpen = true };
        var win2 = new DummyWindow("Tab B") { IsOpen = true };
        var tw1 = service.RegisterWindow(win1);
        var tw2 = service.RegisterWindow(win2);
        service.RegisterDockGroup("dock_ctx", "Tab A", new[] { tw1, tw2 });

        var widget = CreateWidget(service);
        var actions = widget.BuildContextActions(tw1);
        Assert.Equal(new[] { "select_active", "close_all" }, actions.Select(a => a.Id).ToArray());

        actions.Single(a => a.Id == "close_all").Execute();
        Assert.False(win1.IsOpen);
        Assert.False(win2.IsOpen);
    }

    [Theory]
    [InlineData("Peeping Tom", "P")]
    [InlineData("  spaced title", "S")]
    [InlineData("", "?")]
    [InlineData("   ", "?")]
    public void GetMonogram_ReturnsFirstUpperNonWhitespaceChar(string input, string expected)
    {
        Assert.Equal(expected, WindowManagerWidget.GetMonogram(input));
    }

    [Theory]
    [InlineData(0, "Taskbar")]
    [InlineData(3, "Taskbar")]
    [InlineData(6, "Taskbar")]
    [InlineData(7, "IconOnly")]
    [InlineData(13, "Dropdown")]
    public void ResolveAutoMode_UnknownWidth_UsesCountHeuristic(int count, string expected)
    {
        Assert.Equal(expected, WindowManagerWidget.ResolveAutoMode(count, 100f, 0f));
    }

    [Fact]
    public void ResolveAutoMode_KnownWidth_CondensesUnderPressure()
    {
        Assert.Equal("Taskbar", WindowManagerWidget.ResolveAutoMode(3, 100f, 400f));
        Assert.Equal("IconOnly", WindowManagerWidget.ResolveAutoMode(5, 100f, 400f));
        Assert.Equal("Dropdown", WindowManagerWidget.ResolveAutoMode(10, 100f, 300f));
    }

    [Fact]
    public void UpdateButtons_LabelConfiguredForEllipsis()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("A Very Long Window Title That Exceeds");
        service.RegisterWindow(win);
        var widget = CreateWidget(service);
        widget.MaxTitleWidth = 120;
        widget.UpdateButtons();

        var label = widget.WindowNodes["A Very Long Window Title That Exceeds"].ChildNodes[1];
        Assert.Equal(120f, label.Style.MaxWidth!.Value);
        Assert.False(label.Style.WordWrap!.Value);
        Assert.False(label.Style.TextOverflow!.Value);
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
        Assert.Equal(4, vars.Count);
        Assert.Contains(vars, v => v.Id == "WindowManager.DisplayMode");
        Assert.Contains(vars, v => v.Id == "WindowManager.MaxTitleWidth");
        Assert.Contains(vars, v => v.Id == "WindowManager.GroupDockedTabs");
        var decorateVar = vars.OfType<BooleanWidgetConfigVariable>().FirstOrDefault(v => v.Id == "WindowManager.Decorate");
        Assert.NotNull(decorateVar);
        Assert.Equal("General", decorateVar.Category);
        Assert.Equal("Window Manager", decorateVar.Group);
    }

    [Fact]
    public void Decorate_ReadsFromDecorateConfigVariable_WhenPresent()
    {
        var info = new WidgetInfo("WindowManagerWidget", "Window Manager", "Window manager widget");
        var configs = new Dictionary<string, object> { { "WindowManager.Decorate", false } };
        var widget = new WindowManagerWidget(info, null, configs, new WindowManagerService());
        Assert.False(widget.Decorate);
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

        widget.Decorate = false;
        Assert.False(widget.Decorate);
    }

    [Fact]
    public void UpdateButtons_AppliesDecoratedClassWhenDecorateIsTrue()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("TestWindow");
        service.RegisterWindow(win);

        var widget = CreateWidget(service);
        widget.Decorate = true;
        widget.UpdateButtons();

        var btnNode = widget.WindowNodes["TestWindow"];
        Assert.Contains("decorated", btnNode.ClassList);
    }

    [Fact]
    public void UpdateButtons_OmitsDecoratedClassWhenDecorateIsFalse()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("TestWindow");
        service.RegisterWindow(win);

        var widget = CreateWidget(service);
        widget.Decorate = false;
        widget.UpdateButtons();

        var btnNode = widget.WindowNodes["TestWindow"];
        Assert.DoesNotContain("decorated", btnNode.ClassList);
    }

    [Fact]
    public void RenderDropdown_TogglesDecoratedClassOnDropdownNode()
    {
        var service = new WindowManagerService();
        var win = new DummyWindow("TestWindow");
        service.RegisterWindow(win);

        var widget = CreateWidget(service);
        widget.DisplayMode = "Dropdown";

        widget.Decorate = true;
        widget.UpdateButtons();
        Assert.Contains("decorated", widget.Node.ChildNodes[0].ClassList);

        widget.Decorate = false;
        widget.UpdateButtons();
        Assert.DoesNotContain("decorated", widget.Node.ChildNodes[0].ClassList);
    }

    [Fact]
    public void Decorate_TogglesDecoratedClassOnRootNode()
    {
        var service = new WindowManagerService();
        var widget = CreateWidget(service);

        // Default Decorate is true, rootNode has widget class
        Assert.Contains("widget", widget.Node.ClassList);

        widget.UpdateButtons();
        Assert.Contains("decorated", widget.Node.ClassList);

        widget.Decorate = false;
        widget.UpdateButtons();
        Assert.DoesNotContain("decorated", widget.Node.ClassList);
    }

    [Fact]
    public void DropdownMode_RendersProperIconBadgeAndCaret()
    {
        var ddNode = WindowManagerWidget.CreateDropdownNode();

        // Should have 3 child nodes: icon (WindowRestore), badge (count), caret
        Assert.Equal(3, ddNode.ChildNodes.Count);
        var iconNode = ddNode.ChildNodes[0];
        var badgeNode = ddNode.ChildNodes[1];
        var caretNode = ddNode.ChildNodes[2];

        Assert.Equal("icon", iconNode.Id);
        Assert.Equal(Dalamud.Interface.FontAwesomeIcon.WindowRestore.ToIconString(), iconNode.NodeValue);

        Assert.Equal("badge", badgeNode.Id);
        Assert.Equal("0", badgeNode.NodeValue);

        Assert.Equal("caret", caretNode.Id);
        Assert.Equal("▾", caretNode.NodeValue);
    }

    [Fact]
    public void WindowButton_IconNode_HasExplicitSizeAndScaleMode()
    {
        var service = new WindowManagerService();
        var widget = CreateWidget(service);
        var win = new DummyWindow("TestWindow") { IsOpen = true };
        var tw = service.RegisterWindow(win);

        widget.UpdateButtons();

        // Find the created button
        Assert.Single(widget.Node.ChildNodes);
        var btnNode = widget.Node.ChildNodes[0];
        var iconNode = btnNode.ChildNodes.FirstOrDefault(c => c.Id == "icon");

        Assert.NotNull(iconNode);
        Assert.NotNull(iconNode!.Style.Size);
        Assert.Equal(18f, iconNode.Style.Size.Width);
        Assert.Equal(18f, iconNode.Style.Size.Height);
        Assert.Equal(Una.Drawing.ImageScaleMode.Adapt, iconNode.Style.ImageScaleMode);
        Assert.Equal(Una.Drawing.Anchor.MiddleLeft, iconNode.Style.Anchor);

        var labelNode = btnNode.ChildNodes.FirstOrDefault(c => c.Id == "label");
        Assert.NotNull(labelNode);
        // Both icon and label must share Anchor.MiddleLeft so Una.Drawing groups them in the same
        // layout pass and places them sequentially along the horizontal flow instead of overlapping
        Assert.Equal(Una.Drawing.Anchor.MiddleLeft, labelNode!.Style.Anchor);
        Assert.Equal(Una.Drawing.Anchor.MiddleLeft, labelNode.Style.TextAlign);

        // When IconBytes is null, monogram text is shown
        Assert.Equal("T", iconNode.NodeValue);
        Assert.Null(iconNode.Style.ImageBytes);

        // When IconBytes is provided, ImageBytes is set and NodeValue is cleared
        var fakePng = new byte[] { 1, 2, 3 };
        tw.IconBytes = fakePng;
        widget.UpdateButtons();

        Assert.Null(iconNode.NodeValue);
        Assert.Same(fakePng, iconNode.Style.ImageBytes);
    }
}

