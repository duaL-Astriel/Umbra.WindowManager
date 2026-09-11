using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace Umbra.WindowManager.Services.WindowManager;

/// <summary>
/// Adapts an internal Umbra window (<see cref="Umbra.Windows.IWindow"/>) to Dalamud's
/// <see cref="IWindow"/> interface so that <see cref="WindowManagerService"/>,
/// <see cref="TrackedWindow"/>, and taskbar widgets can monitor and manage it.
/// </summary>
public class UmbraWindowAdapter : IWindow
{
    private readonly Umbra.Windows.IWindow umbraWindow;

    /// <summary>
    /// Optional delegate evaluated when <see cref="IsOpen"/> is set to <c>false</c>.
    /// If it returns <c>true</c>, the window is minimized rather than closed.
    /// </summary>
    public Func<bool>? IsBeingMinimized { get; set; }

    public UmbraWindowAdapter(
        string instanceId,
        Umbra.Windows.IWindow umbraWindow,
        string? title = null,
        Func<bool>? isBeingMinimized = null)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(umbraWindow);

        this.InstanceId = instanceId;
        this.umbraWindow = umbraWindow;
        this.IsBeingMinimized = isBeingMinimized;

        var effectiveTitle = title != null
            ? (!string.IsNullOrWhiteSpace(title) ? title : null)
            : TryGetTitle(umbraWindow);

        this.Title = effectiveTitle;

        if (!string.IsNullOrWhiteSpace(effectiveTitle) &&
            !string.Equals(effectiveTitle, instanceId, StringComparison.Ordinal))
        {
            this.WindowName = $"{effectiveTitle}###{instanceId}";
        }
        else
        {
            this.WindowName = instanceId;
        }
    }

    /// <summary>
    /// The unique instance ID of the underlying Umbra window.
    /// </summary>
    public string InstanceId { get; }

    /// <summary>
    /// The human-readable title of the window, if available.
    /// </summary>
    public string? Title { get; }

    /// <summary>
    /// The underlying Umbra window instance.
    /// </summary>
    public Umbra.Windows.IWindow UnderlyingWindow => this.umbraWindow;

    // --- Dalamud.Interface.Windowing.IWindow implementation ---

    public string WindowName { get; set; }

    public string? Namespace { get; set; } = "Umbra";

    public ImGuiWindowFlags Flags { get; set; } = ImGuiWindowFlags.None;

    /// <summary>
    /// Always returns <c>null</c> to suppress title bar minimize button injection by DalamudWindowTracker,
    /// since Umbra renders its own custom window chrome.
    /// </summary>
    public List<TitleBarButton> TitleBarButtons
    {
        get => null!;
        set { }
    }

    public bool IsOpen
    {
        get => !this.umbraWindow.IsClosed && !this.umbraWindow.IsMinimized;
        set
        {
            if (value)
            {
                SetMemberValue(this.umbraWindow, nameof(Umbra.Windows.IWindow.IsMinimized), false);
                SetMemberValue(this.umbraWindow, nameof(Umbra.Windows.IWindow.IsClosed), false);
            }
            else
            {
                if (this.IsBeingMinimized?.Invoke() == true)
                {
                    SetMemberValue(this.umbraWindow, nameof(Umbra.Windows.IWindow.IsMinimized), true);
                }
                else
                {
                    this.umbraWindow.Close();
                }
            }
        }
    }

    public bool IsFocused
    {
        get => this.umbraWindow.IsFocused;
        set => SetMemberValue(this.umbraWindow, nameof(Umbra.Windows.IWindow.IsFocused), value);
    }

    public bool IsHovered
    {
        get => this.umbraWindow.IsHovered;
        set => SetMemberValue(this.umbraWindow, nameof(Umbra.Windows.IWindow.IsHovered), value);
    }

    public Vector2? Position
    {
        get => this.umbraWindow.Position;
        set
        {
            if (value.HasValue)
            {
                SetMemberValue(this.umbraWindow, nameof(Position), value.Value);
            }
        }
    }

    public Vector2? Size
    {
        get => this.umbraWindow.Size;
        set
        {
            if (value.HasValue)
            {
                SetMemberValue(this.umbraWindow, nameof(Size), value.Value);
            }
        }
    }

    public bool RespectCloseHotkey { get; set; } = true;
    public bool InhibitAtkCollision { get; set; }
    public bool DisableWindowSounds { get; set; }
    public uint OnOpenSfxId { get; set; }
    public uint OnCloseSfxId { get; set; }
    public bool DisableFadeInFadeOut { get; set; }
    public ImGuiCond PositionCondition { get; set; }
    public ImGuiCond SizeCondition { get; set; }
    public WindowSizeConstraints? SizeConstraints { get; set; }
    public bool? Collapsed { get; set; }
    public ImGuiCond CollapsedCondition { get; set; }
    public bool ForceMainWindow { get; set; }
    public float? BgAlpha { get; set; }
    public bool ShowCloseButton { get; set; } = true;
    public bool AllowPinning { get; set; }
    public bool AllowClickthrough { get; set; }
    public bool AllowBackgroundBlur { get; set; }
    public bool IsPinned { get; set; }
    public bool IsClickthrough { get; set; }
    public bool IsTopMost { get; set; }
    public bool RequestFocus { get; set; }

    public void Toggle() => this.IsOpen = !this.IsOpen;

    public void BringToFront()
    {
        SetMemberValue(this.umbraWindow, nameof(Umbra.Windows.IWindow.IsFocused), true);
    }

    public bool DrawConditions() => !this.umbraWindow.IsClosed;

    public void PreOpenCheck() { }
    public void PreDraw() { }
    public void PostDraw() { }
    public void Draw() { }
    public void OnOpen() { }
    public void OnClose() { }
    public void OnSafeToRemove() { }
    public void Update() { }

    private static string? TryGetTitle(Umbra.Windows.IWindow window)
    {
        try
        {
            for (var t = window.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                var prop = t.GetProperty("Title", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (prop != null)
                {
                    var val = prop.GetValue(window) as string;
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        return val;
                    }
                }
            }
        }
        catch
        {
            // Fallback cleanly
        }

        return null;
    }

    private static void SetMemberValue(object target, string propertyName, object value)
    {
        var targetType = target.GetType();

        // 1. Try property setter across type hierarchy
        for (var t = targetType; t != null && t != typeof(object); t = t.BaseType)
        {
            var prop = t.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (prop?.SetMethod != null)
            {
                try
                {
                    prop.SetValue(target, value);
                    return;
                }
                catch
                {
                    // Fall through to field search
                }
            }
        }

        // 2. Try backing field or member field across type hierarchy
        var fieldNames = new[]
        {
            $"<{propertyName}>k__BackingField",
            $"_{char.ToLowerInvariant(propertyName[0])}{propertyName[1..]}",
            $"{char.ToLowerInvariant(propertyName[0])}{propertyName[1..]}",
            $"_{propertyName}"
        };

        for (var t = targetType; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var fieldName in fieldNames)
            {
                var field = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    try
                    {
                        field.SetValue(target, value);
                        return;
                    }
                    catch
                    {
                        // Ignore and try next
                    }
                }
            }
        }
    }
}
