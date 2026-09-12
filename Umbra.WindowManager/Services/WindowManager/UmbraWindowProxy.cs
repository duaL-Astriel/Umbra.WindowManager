using System;
using System.Numerics;

namespace Umbra.WindowManager.Services.WindowManager;

/// <summary>
/// A transparent proxy wrapping an internal Umbra window (<see cref="Umbra.Windows.IWindow"/>)
/// inside Umbra's <c>WindowManager._instances</c> dictionary. When <see cref="IsHidden"/> is <c>true</c>,
/// calls to <see cref="Render(string)"/> are suppressed, effectively hiding the window on screen while
/// keeping its instance alive and manageable by <see cref="WindowManagerService"/>.
/// </summary>
public sealed class UmbraWindowProxy : Umbra.Windows.IWindow
{
    private readonly Umbra.Windows.IWindow inner;

    public UmbraWindowProxy(Umbra.Windows.IWindow inner)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <summary>
    /// The underlying raw Umbra window instance.
    /// </summary>
    public Umbra.Windows.IWindow UnderlyingWindow => this.inner;

    /// <summary>
    /// Whether the window is currently minimized/hidden by WindowManager.
    /// When <c>true</c>, <see cref="Render(string)"/> is a no-op, hiding the window from display.
    /// </summary>
    public bool IsHidden { get; set; }

    public Vector2 Position => this.inner.Position;

    public Vector2 Size => this.IsHidden ? Vector2.Zero : this.inner.Size;

    public bool IsClosed => this.IsHidden || this.inner.IsClosed;

    public bool IsMinimized => this.IsHidden || this.inner.IsMinimized;

    public bool IsFocused => !this.IsHidden && this.inner.IsFocused;

    public bool IsHovered => !this.IsHidden && this.inner.IsHovered;

    public event Action? RequestClose
    {
        add => this.inner.RequestClose += value;
        remove => this.inner.RequestClose -= value;
    }

    public void Close() => this.inner.Close();

    public void Dispose() => this.inner.Dispose();

    public void Render(string instanceId)
    {
        if (this.IsHidden)
        {
            return;
        }

        this.inner.Render(instanceId);
    }
}
