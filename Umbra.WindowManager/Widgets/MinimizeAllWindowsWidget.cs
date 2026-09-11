using System;
using System.Collections.Generic;
using Dalamud.Interface;
using Umbra.Common;
using Umbra.Widgets;
using Umbra.WindowManager.Services.WindowManager;
using Una.Drawing;

namespace Umbra.WindowManager.Widgets;

[ToolbarWidget(
    "UmbraMinimizeAllWindowsWidget",
    "Minimize All Windows",
    "Minimizes all open plugin windows at once with a single click, and toggles to restore them."
)]
public class MinimizeAllWindowsWidget : ToolbarWidget
{
    private static readonly Stylesheet WidgetStylesheet = new(
    [
        new Stylesheet.StyleDefinition(
            ".minimize-all-btn.decorated",
            new Style
            {
                BackgroundColor = new Color("Widget.Background"),
                BorderColor = new BorderColor(new Color("Widget.Border")),
                BorderWidth = new EdgeSize(1)
            }),
        new Stylesheet.StyleDefinition(
            ".minimize-all-btn.decorated:hover",
            new Style
            {
                BackgroundColor = new Color("Widget.BackgroundHover"),
                BorderColor = new BorderColor(new Color("Widget.BorderHover"))
            }),
        new Stylesheet.StyleDefinition(
            ".minimize-all-btn.decorated.active",
            new Style
            {
                BackgroundColor = new Color("Widget.BackgroundHover"),
                BorderColor = new BorderColor(new Color("Widget.BorderHover"))
            }),
        new Stylesheet.StyleDefinition(
            ".minimize-all-btn-icon",
            new Style
            {
                Color = new Color("Widget.Text"),
                OutlineColor = new Color("Widget.TextOutline"),
                OutlineSize = 2,
                TextShadowColor = new Color(0xFF000000),
                TextShadowSize = 8
            }),
        new Stylesheet.StyleDefinition(
            ".minimize-all-btn:hover",
            new Style
            {
                Color = new Color("Widget.TextHover")
            }),
        new Stylesheet.StyleDefinition(
            ".minimize-all-btn.active",
            new Style
            {
                Color = new Color("Widget.TextHover")
            }),
        new Stylesheet.StyleDefinition(
            ".minimize-all-btn:hover .minimize-all-btn-icon",
            new Style
            {
                Color = new Color("Widget.TextHover")
            }),
        new Stylesheet.StyleDefinition(
            ".minimize-all-btn.active .minimize-all-btn-icon",
            new Style
            {
                Color = new Color("Widget.TextHover")
            })
    ]);

    private readonly WindowManagerService windowManager;
    private readonly Node rootNode;
    private readonly Node buttonNode;
    private readonly Node iconNode;

    private bool decorate = true;
    private bool toggle = true;

    public MinimizeAllWindowsWidget(
        WidgetInfo info,
        string? guid = null,
        Dictionary<string, object>? configValues = null
    ) : this(info, guid, configValues, null)
    {
    }

    public MinimizeAllWindowsWidget(
        WidgetInfo info,
        string? guid,
        Dictionary<string, object>? configValues,
        WindowManagerService? windowManager
    ) : base(info, guid, configValues)
    {
        this.windowManager = windowManager ?? Framework.Service<WindowManagerService>();

        if (configValues != null)
        {
            if (configValues.TryGetValue("Decorate", out var dec) && dec is bool decb)
                this.decorate = decb;
            else if (configValues.TryGetValue("MinimizeAll.Decorate", out var mad) && mad is bool madb)
                this.decorate = madb;

            if (configValues.TryGetValue("MinimizeAll.Toggle", out var tog) && tog is bool togb)
                this.toggle = togb;
            else if (configValues.TryGetValue("Toggle", out var togd) && togd is bool togdb)
                this.toggle = togdb;
        }

        this.iconNode = new Node
        {
            Id = "icon",
            ClassList = { "minimize-all-btn-icon" },
            NodeValue = FontAwesomeIcon.Desktop.ToIconString(),
            Style =
            {
                Font = 2,
                FontSize = 13,
                Anchor = Anchor.MiddleCenter,
                TextAlign = Anchor.MiddleCenter,
                OutlineColor = new Color("Widget.TextOutline"),
                OutlineSize = 2,
                TextShadowColor = new Color(0xFF000000),
                TextShadowSize = 8
            }
        };

        this.buttonNode = new Node
        {
            ClassList = { "minimize-all-btn" },
            Tooltip = "Minimize All Windows",
            Style =
            {
                Flow = Flow.Horizontal,
                Gap = 6,
                Padding = new EdgeSize(4, 6, 4, 6),
                BorderRadius = 5,
                RoundedCorners = RoundedCorners.All,
                Anchor = Anchor.MiddleCenter
            },
            ChildNodes = { this.iconNode }
        };

        this.buttonNode.OnClick += _ => this.PerformAction();

        this.rootNode = new Node
        {
            ClassList = { "widget" },
            Stylesheet = WidgetStylesheet,
            Style =
            {
                Flow = Flow.Horizontal,
                AutoSize = (AutoSize.Fit, AutoSize.Fit),
                Gap = 4
            },
            ChildNodes = { this.buttonNode }
        };

        this.UpdateButtonState();
    }

    public override Node Node => this.rootNode;
    public override WidgetPopup? Popup => null;

    public Node ButtonNode => this.buttonNode;
    public Node IconNode => this.iconNode;

    [ConfigVariable("Decorate", "General", "Minimize All Windows")]
    public bool Decorate
    {
        get
        {
            if (this.HasConfigVariable("Decorate"))
                return this.GetConfigValue<bool>("Decorate");
            if (this.HasConfigVariable("MinimizeAll.Decorate"))
                return this.GetConfigValue<bool>("MinimizeAll.Decorate");
            return this.decorate;
        }
        set
        {
            this.decorate = value;
            if (this.HasConfigVariable("Decorate"))
                this.SetConfigValue("Decorate", value);
            if (this.HasConfigVariable("MinimizeAll.Decorate"))
                this.SetConfigValue("MinimizeAll.Decorate", value);
        }
    }

    [ConfigVariable("MinimizeAll.Toggle", "General", "Minimize All Windows")]
    public bool Toggle
    {
        get
        {
            if (this.HasConfigVariable("MinimizeAll.Toggle"))
                return this.GetConfigValue<bool>("MinimizeAll.Toggle");
            if (this.HasConfigVariable("Toggle"))
                return this.GetConfigValue<bool>("Toggle");
            return this.toggle;
        }
        set
        {
            this.toggle = value;
            if (this.HasConfigVariable("MinimizeAll.Toggle"))
                this.SetConfigValue("MinimizeAll.Toggle", value);
            if (this.HasConfigVariable("Toggle"))
                this.SetConfigValue("Toggle", value);
        }
    }

    protected override void Initialize()
    {
    }

    protected override void OnUpdate()
    {
        this.UpdateButtonState();
    }

    public void PerformAction()
    {
        if (this.Toggle && this.windowManager.CanRestoreBulkMinimized)
        {
            this.windowManager.RestoreAll();
        }
        else
        {
            this.windowManager.MinimizeAll();
        }

        this.UpdateButtonState();
    }

    public void UpdateButtonState()
    {
        this.buttonNode.ToggleClass("decorated", this.Decorate);

        var canRestore = this.Toggle && this.windowManager.CanRestoreBulkMinimized;
        var anyOpen = this.windowManager.AreAnyWindowsOpen;

        if (canRestore)
        {
            this.buttonNode.Tooltip = "Restore Windows";
            this.buttonNode.Style.Opacity = 1.0f;
            this.buttonNode.ClassList.Add("active");
        }
        else
        {
            this.buttonNode.Tooltip = "Minimize All Windows";
            this.buttonNode.ClassList.Remove("active");
            this.buttonNode.Style.Opacity = anyOpen ? 1.0f : 0.6f;
        }
    }

    protected override IEnumerable<IWidgetConfigVariable> GetConfigVariables()
    {
        return
        [
            new BooleanWidgetConfigVariable(
                "Decorate",
                "Decorate",
                "Decorate button with Umbra background and border styling.",
                true
            )
            {
                Category = "General",
                Group = "Minimize All Windows"
            },
            new BooleanWidgetConfigVariable(
                "MinimizeAll.Toggle",
                "Toggle Restore on Click",
                "Allow toggling between minimizing all windows and restoring previously minimized windows.",
                true
            )
            {
                Category = "General",
                Group = "Minimize All Windows"
            }
        ];
    }
}
