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
    private readonly List<TrackedWindow> windowsBuffer = [];
    private readonly HashSet<string> currentNames = [];
    private readonly List<string> toRemove = [];

    private string displayMode = "Auto";
    private int maxTitleWidth = 140;
    private bool groupDockedTabs = true;

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
    public override WidgetPopup? Popup => null;

    public IReadOnlyDictionary<string, Node> WindowNodes => this.windowNodes;

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
            ),
            new IntegerWidgetConfigVariable(
                "WindowManager.MaxTitleWidth",
                "Max Title Width",
                "Maximum pixel width of window titles.",
                140,
                60,
                300
            ),
            new BooleanWidgetConfigVariable(
                "WindowManager.GroupDockedTabs",
                "Group Docked Tabs",
                "Group docked tabs together in toolbar.",
                true
            )
        ];
    }

    public void UpdateButtons()
    {
        this.windowManager.GetVisibleAndMinimizedWindows(this.windowsBuffer);

        this.currentNames.Clear();
        for (var i = 0; i < this.windowsBuffer.Count; i++)
        {
            this.currentNames.Add(this.windowsBuffer[i].WindowName);
        }

        // Remove old nodes
        this.toRemove.Clear();
        foreach (var name in this.windowNodes.Keys)
        {
            if (!this.currentNames.Contains(name))
            {
                this.toRemove.Add(name);
            }
        }

        for (var i = 0; i < this.toRemove.Count; i++)
        {
            var name = this.toRemove[i];
            if (this.windowNodes.Remove(name, out var node))
            {
                this.rootNode.RemoveChild(node, true);
            }
        }

        // Add or update nodes
        for (var i = 0; i < this.windowsBuffer.Count; i++)
        {
            var window = this.windowsBuffer[i];
            if (!this.windowNodes.TryGetValue(window.WindowName, out var btnNode))
            {
                btnNode = new Node
                {
                    Style =
                    {
                        Flow = Flow.Horizontal,
                        Padding = new EdgeSize(4, 6, 4, 6),
                        BorderRadius = 4,
                        RoundedCorners = RoundedCorners.All
                    }
                };

                btnNode.OnClick += _ => this.windowManager.Toggle(window);
                btnNode.OnRightClick += _ => this.windowManager.Close(window);

                this.rootNode.AppendChild(btnNode);
                this.windowNodes[window.WindowName] = btnNode;
            }

            // Visual styles
            btnNode.ToggleClass("active", window.IsFocused);
            btnNode.ToggleClass("open", window.IsOpen);
            btnNode.ToggleClass("minimized", window.IsMinimized);
            btnNode.ToggleClass("dock-group", this.GroupDockedTabs && window.DockGroupKey != null);

            btnNode.Style.Opacity = window.IsMinimized ? 0.6f : 1.0f;
            btnNode.Style.MaxWidth = this.MaxTitleWidth > 0 ? this.MaxTitleWidth : null;
            btnNode.Tooltip = $"{window.CleanTitle}{(window.IsMinimized ? " [Minimized]" : "")}";

            var showText = this.DisplayMode is "Auto" or "Taskbar";
            btnNode.NodeValue = showText ? window.CleanTitle : string.Empty;
        }
    }
}
