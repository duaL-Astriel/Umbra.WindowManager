using System;
using System.Collections.Generic;
using System.Linq;
using Umbra.Common;
using Umbra.Widgets;
using Umbra.WindowManager.Services.WindowManager;
using Una.Drawing;

namespace Umbra.WindowManager.Widgets;

[ToolbarWidget(
    "UmbraWindowManagerWidget",
    "Window Manager",
    "Displays all open and minimized Dalamud plugin windows in the toolbar."
)]
public class WindowManagerWidget : ToolbarWidget
{
    private readonly WindowManagerService windowManager;
    private readonly Node rootNode;
    private readonly Dictionary<string, Node> windowNodes = [];
    private readonly Dictionary<string, MenuPopup.Button> dropdownButtons = [];
    private readonly Dictionary<string, TrackedWindow> currentWindows = [];
    private readonly List<TrackedWindow> windowsBuffer = [];
    private readonly HashSet<string> currentNames = [];
    private readonly List<string> toRemove = [];

    private Node? dropdownNode;
    private MenuPopup? menuPopup;
    private string layout = "";
    private string effectiveMode = "Taskbar";

    private string displayMode = "Auto";
    private int maxTitleWidth = 140;
    private bool groupDockedTabs = true;
    private bool decorate = true;

    public WindowManagerWidget(
        WidgetInfo info,
        string? guid = null,
        Dictionary<string, object>? configValues = null
    ) : this(info, guid, configValues, null)
    {
    }

    public WindowManagerWidget(
        WidgetInfo info,
        string? guid,
        Dictionary<string, object>? configValues,
        WindowManagerService? windowManager
    ) : base(info, guid, configValues)
    {
        this.windowManager = windowManager ?? Framework.Service<WindowManagerService>();

        if (configValues != null)
        {
            if (configValues.TryGetValue("WindowManager.DisplayMode", out var dm) && dm is string dms)
                this.displayMode = dms;
            if (configValues.TryGetValue("WindowManager.MaxTitleWidth", out var mtw) && mtw is int mtwi)
                this.maxTitleWidth = mtwi;
            if (configValues.TryGetValue("WindowManager.GroupDockedTabs", out var gdt) && gdt is bool gdtb)
                this.groupDockedTabs = gdtb;
            if (configValues.TryGetValue("Decorate", out var dec) && dec is bool decb)
                this.decorate = decb;
            else if (configValues.TryGetValue("WindowManager.Decorate", out var wmDec) && wmDec is bool wmDecb)
                this.decorate = wmDecb;
        }

        this.rootNode = new Node
        {
            Style =
            {
                Flow = Flow.Horizontal,
                AutoSize = (AutoSize.Fit, AutoSize.Fit),
                Gap = 4
            }
        };
    }

    public override Node Node => this.rootNode;

    // Only the Dropdown presentation uses an Umbra popup; every other mode renders inline buttons and
    // must expose a null popup so clicks aren't intercepted by the framework (issue #4).
    public override WidgetPopup? Popup =>
        this.DisplayMode == "Dropdown" || this.effectiveMode == "Dropdown" ? this.EnsureMenuPopup() : null;

    public IReadOnlyDictionary<string, Node> WindowNodes => this.windowNodes;
    public IReadOnlyDictionary<string, MenuPopup.Button> DropdownButtons => this.dropdownButtons;

    [ConfigVariable("WindowManager.DisplayMode", "General", "Window Manager", options: ["Auto", "Taskbar", "IconOnly", "Dropdown"])]
    public string DisplayMode
    {
        get => this.HasConfigVariable("WindowManager.DisplayMode")
            ? this.GetConfigValue<string>("WindowManager.DisplayMode")
            : this.displayMode;
        set
        {
            this.displayMode = value;
            if (this.HasConfigVariable("WindowManager.DisplayMode"))
                this.SetConfigValue("WindowManager.DisplayMode", value);
        }
    }

    [ConfigVariable("WindowManager.MaxTitleWidth", "General", "Window Manager", min: 60, max: 300)]
    public int MaxTitleWidth
    {
        get => this.HasConfigVariable("WindowManager.MaxTitleWidth")
            ? this.GetConfigValue<int>("WindowManager.MaxTitleWidth")
            : this.maxTitleWidth;
        set
        {
            this.maxTitleWidth = value;
            if (this.HasConfigVariable("WindowManager.MaxTitleWidth"))
                this.SetConfigValue("WindowManager.MaxTitleWidth", value);
        }
    }

    [ConfigVariable("WindowManager.GroupDockedTabs", "General", "Window Manager")]
    public bool GroupDockedTabs
    {
        get => this.HasConfigVariable("WindowManager.GroupDockedTabs")
            ? this.GetConfigValue<bool>("WindowManager.GroupDockedTabs")
            : this.groupDockedTabs;
        set
        {
            this.groupDockedTabs = value;
            if (this.HasConfigVariable("WindowManager.GroupDockedTabs"))
                this.SetConfigValue("WindowManager.GroupDockedTabs", value);
        }
    }

    [ConfigVariable("Decorate", "General", "Window Manager")]
    public bool Decorate
    {
        get
        {
            if (this.HasConfigVariable("Decorate"))
                return this.GetConfigValue<bool>("Decorate");
            if (this.HasConfigVariable("WindowManager.Decorate"))
                return this.GetConfigValue<bool>("WindowManager.Decorate");
            return this.decorate;
        }
        set
        {
            this.decorate = value;
            if (this.HasConfigVariable("Decorate"))
                this.SetConfigValue("Decorate", value);
            if (this.HasConfigVariable("WindowManager.Decorate"))
                this.SetConfigValue("WindowManager.Decorate", value);
        }
    }

    protected override void Initialize()
    {
    }

    protected override void OnUpdate()
    {
        this.UpdateButtons();
    }

    protected override IEnumerable<IWidgetConfigVariable> GetConfigVariables()
    {
        return
        [
            new SelectWidgetConfigVariable(
                "WindowManager.DisplayMode",
                "Display Mode",
                "Display mode for window buttons.",
                "Auto",
                new Dictionary<string, string>
                {
                    { "Auto", "Auto" },
                    { "Taskbar", "Taskbar" },
                    { "IconOnly", "Icon Only" },
                    { "Dropdown", "Dropdown" }
                },
                false
            )
            {
                Category = "General",
                Group = "Window Manager"
            },
            new IntegerWidgetConfigVariable(
                "WindowManager.MaxTitleWidth",
                "Max Title Width",
                "Maximum pixel width of window titles.",
                140,
                60,
                300
            )
            {
                Category = "General",
                Group = "Window Manager"
            },
            new BooleanWidgetConfigVariable(
                "WindowManager.GroupDockedTabs",
                "Group Docked Tabs",
                "Group docked tabs together in toolbar.",
                true
            )
            {
                Category = "General",
                Group = "Window Manager"
            },
            new BooleanWidgetConfigVariable(
                "Decorate",
                "Decorate",
                "Decorate window buttons with Umbra background and border styling.",
                true
            )
            {
                Category = "General",
                Group = "Window Manager"
            }
        ];
    }

    public void UpdateButtons()
    {
        this.windowManager.GetVisibleAndMinimizedWindows(this.windowsBuffer);

        // Refresh the name -> current TrackedWindow map so click handlers always act on the live window
        // instance, even after a plugin re-instantiates a same-named window (issue #2).
        this.currentWindows.Clear();
        this.currentNames.Clear();
        for (var i = 0; i < this.windowsBuffer.Count; i++)
        {
            var w = this.windowsBuffer[i];
            this.currentWindows[w.WindowName] = w;
            this.currentNames.Add(w.WindowName);
        }

        this.effectiveMode = this.ResolveEffectiveMode(this.windowsBuffer.Count);

        if (this.effectiveMode == "Dropdown")
        {
            this.SwitchToDropdownLayout();
            this.RenderDropdown();
        }
        else
        {
            this.SwitchToButtonLayout();
            this.RenderButtons(this.effectiveMode);
        }
    }

    // --- Mode resolution ----------------------------------------------------------------------------

    private string ResolveEffectiveMode(int count)
    {
        var mode = this.DisplayMode;
        if (mode is "Taskbar" or "IconOnly" or "Dropdown") return mode;

        // Auto: start as Taskbar and condense under width/count pressure.
        var estimatedButtonWidth = this.MaxTitleWidth + 34f; // icon + padding + gap.
        return ResolveAutoMode(count, estimatedButtonWidth, this.GetAvailableWidth());
    }

    /// <summary>
    /// Pure decision for Auto mode: Taskbar while buttons fit, condensing to IconOnly then Dropdown as
    /// the available toolbar width (or, when width is unknown, the window count) is exceeded.
    /// </summary>
    internal static string ResolveAutoMode(int windowCount, float estimatedButtonWidth, float availableWidth)
    {
        if (windowCount == 0) return "Taskbar";
        if (windowCount > 12) return "Dropdown";

        if (availableWidth > 0f)
        {
            if (windowCount * estimatedButtonWidth <= availableWidth) return "Taskbar";

            const float iconOnlyButtonWidth = 32f;
            if (windowCount * iconOnlyButtonWidth <= availableWidth) return "IconOnly";

            return "Dropdown";
        }

        // Width unknown (e.g. before layout): fall back to a conservative count heuristic.
        return windowCount <= 6 ? "Taskbar" : "IconOnly";
    }

    private float GetAvailableWidth()
    {
        try
        {
            return this.GetBarNode?.InnerWidth ?? 0f;
        }
        catch
        {
            return 0f;
        }
    }

    // --- Inline button layout -----------------------------------------------------------------------

    private void SwitchToButtonLayout()
    {
        if (this.layout == "buttons") return;

        if (this.dropdownNode is { } dn && dn.ParentNode == this.rootNode)
            this.rootNode.RemoveChild(dn, false);

        if (this.menuPopup is { } mp)
        {
            mp.Clear(true);
            this.dropdownButtons.Clear();
        }

        this.layout = "buttons";
    }

    private void RenderButtons(string mode)
    {
        this.toRemove.Clear();
        foreach (var name in this.windowNodes.Keys)
        {
            if (!this.currentNames.Contains(name))
                this.toRemove.Add(name);
        }

        for (var i = 0; i < this.toRemove.Count; i++)
        {
            if (this.windowNodes.Remove(this.toRemove[i], out var node))
                this.rootNode.RemoveChild(node, true);
        }

        var showLabel = mode == "Taskbar";
        for (var i = 0; i < this.windowsBuffer.Count; i++)
        {
            var window = this.windowsBuffer[i];
            if (!this.windowNodes.TryGetValue(window.WindowName, out var btnNode))
            {
                btnNode = this.CreateButtonNode(window.WindowName);
                this.rootNode.AppendChild(btnNode);
                this.windowNodes[window.WindowName] = btnNode;
            }

            this.UpdateButtonContent(btnNode, window, showLabel);
        }
    }

    private Node CreateButtonNode(string windowName)
    {
        var node = new Node
        {
            Style =
            {
                Flow = Flow.Horizontal,
                Gap = 4,
                Padding = new EdgeSize(4, 6, 4, 6),
                BorderRadius = 4,
                RoundedCorners = RoundedCorners.All
            },
            ChildNodes =
            {
                new Node
                {
                    Id = "icon",
                    Style =
                    {
                        Size = new Size(16, 16),
                        FontSize = 11,
                        TextAlign = Anchor.MiddleCenter
                    }
                },
                new Node
                {
                    Id = "label",
                    Style =
                    {
                        WordWrap = false,
                        TextOverflow = false // clip + ellipsize instead of overflowing (issue #8.4)
                    }
                }
            }
        };

        // Bind by stable window name and resolve the live TrackedWindow at click time (issue #2).
        node.OnClick += _ =>
        {
            if (this.currentWindows.TryGetValue(windowName, out var w))
                this.windowManager.Toggle(w);
        };
        node.OnRightClick += _ =>
        {
            if (this.currentWindows.TryGetValue(windowName, out var w))
                this.PresentContextMenu(w);
        };

        return node;
    }

    private void UpdateButtonContent(Node btnNode, TrackedWindow window, bool showLabel)
    {
        btnNode.ToggleClass("active", window.IsFocused);
        btnNode.ToggleClass("open", window.IsOpen);
        btnNode.ToggleClass("minimized", window.IsMinimized);
        btnNode.ToggleClass("dock-group", this.GroupDockedTabs && window.DockGroupKey != null);
        btnNode.ToggleClass("decorated", this.Decorate);

        var title = window.DisplayTitle;
        btnNode.Style.Opacity = window.IsMinimized ? 0.6f : 1.0f;
        btnNode.Tooltip = $"{title}{(window.IsMinimized ? " [Minimized]" : "")}";

        ApplyIcon(btnNode.ChildNodes[0], window);

        var labelNode = btnNode.ChildNodes[1];
        if (!Equals(labelNode.NodeValue, title))
            labelNode.NodeValue = title;
        labelNode.Style.MaxWidth = this.MaxTitleWidth > 0 ? (float)this.MaxTitleWidth : null;
        labelNode.Style.IsVisible = showLabel;
    }

    private static void ApplyIcon(Node iconNode, TrackedWindow window)
    {
        if (window.IconBytes != null)
        {
            if (!ReferenceEquals(iconNode.Style.ImageBytes, window.IconBytes))
                iconNode.Style.ImageBytes = window.IconBytes;
            if (iconNode.NodeValue != null)
                iconNode.NodeValue = null;
        }
        else
        {
            if (iconNode.Style.ImageBytes != null)
                iconNode.Style.ImageBytes = null;

            var monogram = GetMonogram(window.DisplayTitle);
            if (!Equals(iconNode.NodeValue, monogram))
                iconNode.NodeValue = monogram;
        }
    }

    /// <summary>First non-whitespace character of the title, upper-cased; a stable icon fallback.</summary>
    internal static string GetMonogram(string cleanTitle)
    {
        foreach (var ch in cleanTitle)
        {
            if (!char.IsWhiteSpace(ch))
                return char.ToUpperInvariant(ch).ToString();
        }

        return "?";
    }

    // --- Dropdown layout ----------------------------------------------------------------------------

    private void SwitchToDropdownLayout()
    {
        if (this.layout == "dropdown") return;

        foreach (var node in this.windowNodes.Values)
            this.rootNode.RemoveChild(node, true);
        this.windowNodes.Clear();

        this.dropdownNode ??= CreateDropdownNode();
        if (this.dropdownNode.ParentNode != this.rootNode)
            this.rootNode.AppendChild(this.dropdownNode);

        this.layout = "dropdown";
    }

    private static Node CreateDropdownNode()
    {
        return new Node
        {
            Tooltip = "Windows",
            Style =
            {
                Flow = Flow.Horizontal,
                Gap = 4,
                Padding = new EdgeSize(4, 6, 4, 6),
                BorderRadius = 4,
                RoundedCorners = RoundedCorners.All
            },
            ChildNodes =
            {
                new Node { Id = "icon", NodeValue = "▾" }, // caret; the popup opens via Popup.
                new Node { Id = "badge", NodeValue = "0" }
            }
        };
    }

    private void RenderDropdown()
    {
        if (this.EnsureMenuPopup() is { } popup)
        {
            this.toRemove.Clear();
            foreach (var name in this.dropdownButtons.Keys)
            {
                if (!this.currentNames.Contains(name))
                    this.toRemove.Add(name);
            }

            for (var i = 0; i < this.toRemove.Count; i++)
            {
                var name = this.toRemove[i];
                if (this.dropdownButtons.Remove(name, out var btn))
                    popup.Remove(btn, true);
            }

            for (var i = 0; i < this.windowsBuffer.Count; i++)
            {
                var window = this.windowsBuffer[i];
                var windowName = window.WindowName;
                var label = $"{window.DisplayTitle}{(window.IsMinimized ? " [Minimized]" : "")}";

                if (!this.dropdownButtons.TryGetValue(windowName, out var btn))
                {
                    btn = new MenuPopup.Button(label)
                    {
                        OnClick = () =>
                        {
                            if (this.currentWindows.TryGetValue(windowName, out var w))
                                this.windowManager.Toggle(w);
                        }
                    };
                    popup.Add(btn);
                    this.dropdownButtons[windowName] = btn;
                }

                if (!Equals(btn.Label, label))
                    btn.Label = label;

                btn.SortIndex = i;
            }
        }

        if (this.dropdownNode is { } dn)
        {
            dn.ChildNodes[1].NodeValue = this.windowsBuffer.Count.ToString();
            dn.ToggleClass("decorated", this.Decorate);
        }
    }

    private MenuPopup? EnsureMenuPopup()
    {
        if (this.menuPopup != null) return this.menuPopup;
        try
        {
            return this.menuPopup = new MenuPopup();
        }
        catch
        {
            return null;
        }
    }

    // --- Context menu (issue #3) --------------------------------------------------------------------

    /// <summary>
    /// Per-state right-click actions from the design spec: Restore/Minimize + Close for standalone
    /// windows, and Select Active Tab / Close All Tabs for docked tab groups.
    /// </summary>
    internal List<MenuAction> BuildContextActions(TrackedWindow window)
    {
        var actions = new List<MenuAction>();

        if (this.GroupDockedTabs && window.DockGroupKey is { } groupKey)
        {
            actions.Add(new MenuAction("select_active", "Select Active Tab", () => this.windowManager.Restore(window)));
            actions.Add(new MenuAction("close_all", "Close All Tabs", () => this.windowManager.CloseDockGroup(groupKey)));
            return actions;
        }

        if (window.IsMinimized)
            actions.Add(new MenuAction("restore", "Restore", () => this.windowManager.Restore(window)));
        else
            actions.Add(new MenuAction("minimize", "Minimize", () => this.windowManager.Minimize(window)));

        actions.Add(new MenuAction("close", "Close", () => this.windowManager.Close(window)));
        return actions;
    }

    private void PresentContextMenu(TrackedWindow window)
    {
        var entries = this.BuildContextActions(window)
            .Select(a => new ContextMenuEntry(a.Id) { Label = a.Label, OnClick = a.Execute })
            .ToList();

        new ContextMenu(entries).Present();
    }

    internal sealed record MenuAction(string Id, string Label, Action Execute);
}
