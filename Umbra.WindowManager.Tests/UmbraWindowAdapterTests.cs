using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using Dalamud.Bindings.ImGui;
using Umbra.Windows;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class UmbraWindowAdapterTests
{
    private class MockUmbraWindow : IWindow
    {
        public Vector2 Position { get; set; } = new(100, 150);
        public Vector2 Size { get; set; } = new(400, 300);
        public bool IsClosed { get; set; }
        public bool IsMinimized { get; set; }
        public bool IsFocused { get; set; }
        public bool IsHovered { get; set; }
        public bool CloseCalled { get; private set; }
        public int RenderCallCount { get; private set; }
        public bool DisposeCalled { get; private set; }

        public event Action? RequestClose;

        public void Close()
        {
            this.CloseCalled = true;
            this.IsClosed = true;
            this.RequestClose?.Invoke();
        }

        public void Render(string instanceId)
        {
            this.RenderCallCount++;
        }

        public void Dispose()
        {
            this.DisposeCalled = true;
        }
    }

    private class ConcreteTestWindow : Window
    {
        protected override string UdtResourceName => "test";
        protected override string Title => "Concrete Title";
        protected override Vector2 MinSize => new(100, 100);
        protected override Vector2 MaxSize => new(500, 500);
        protected override Vector2 DefaultSize => new(200, 200);

        public static ConcreteTestWindow CreateUninitialized()
        {
            return (ConcreteTestWindow)RuntimeHelpers.GetUninitializedObject(typeof(ConcreteTestWindow));
        }
    }

    [Fact]
    public void WindowName_WithTitle_FormatsTitleAndInstanceId()
    {
        var mock = new MockUmbraWindow();
        var adapter = new UmbraWindowAdapter("UmbraSettings", mock, "Settings");

        Assert.Equal("Settings###UmbraSettings", adapter.WindowName);
        Assert.Equal("UmbraSettings", adapter.InstanceId);
        Assert.Equal("Settings", adapter.Title);
        Assert.Same(mock, adapter.UnderlyingWindow);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UmbraSettings")]
    public void WindowName_WithoutDistinctTitle_FallsBackToInstanceId(string? title)
    {
        var mock = new MockUmbraWindow();
        var adapter = new UmbraWindowAdapter("UmbraSettings", mock, title);

        Assert.Equal("UmbraSettings", adapter.WindowName);
    }

    [Fact]
    public void WindowName_WhenTitleNotProvided_ExtractsProtectedTitleFromConcreteWindow()
    {
        var concrete = ConcreteTestWindow.CreateUninitialized();
        var adapter = new UmbraWindowAdapter("TestInstance", concrete);

        Assert.Equal("Concrete Title###TestInstance", adapter.WindowName);
        Assert.Equal("Concrete Title", adapter.Title);
    }

    [Fact]
    public void WindowMetadata_HasExpectedDefaults()
    {
        var mock = new MockUmbraWindow();
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test");

        Assert.Equal("Umbra", adapter.Namespace);
        Assert.Equal(ImGuiWindowFlags.None, adapter.Flags);
        Assert.Null(adapter.TitleBarButtons);
        Assert.False(adapter.IsClickthrough);
        Assert.False(adapter.IsPinned);
        Assert.False(adapter.IsTopMost);
    }

    [Fact]
    public void TrackedWindow_EvaluatesManageableForUmbraWindowAdapter()
    {
        var mock = new MockUmbraWindow { Size = new Vector2(300, 200) };
        var adapter = new UmbraWindowAdapter("UmbraSettings", mock, "Settings");
        var tracked = new TrackedWindow(adapter);

        Assert.Equal("UmbraSettings", tracked.Id);
        Assert.Equal("Settings", tracked.CleanTitle);
        Assert.Equal("Settings", tracked.DisplayTitle);
        Assert.Equal("Umbra", tracked.Namespace);
        Assert.True(tracked.IsManageable);
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void IsOpen_Getter_ReturnsTrueOnlyWhenNotClosedAndNotMinimized(bool isClosed, bool isMinimized, bool expectedIsOpen)
    {
        var mock = new MockUmbraWindow
        {
            IsClosed = isClosed,
            IsMinimized = isMinimized
        };
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test");

        Assert.Equal(expectedIsOpen, adapter.IsOpen);
    }

    [Fact]
    public void IsOpen_Setter_True_ClearsMinimizedAndClosedOnMock()
    {
        var mock = new MockUmbraWindow
        {
            IsClosed = true,
            IsMinimized = true
        };
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test");

        adapter.IsOpen = true;

        Assert.False(mock.IsClosed);
        Assert.False(mock.IsMinimized);
        Assert.True(adapter.IsOpen);
    }

    [Fact]
    public void IsOpen_Setter_True_ClearsMinimizedAndClosedOnConcreteWindow()
    {
        var concrete = ConcreteTestWindow.CreateUninitialized();
        var adapter = new UmbraWindowAdapter("TestWindow", concrete, "Test");

        // Manually set minimized and closed on concrete window
        typeof(Window).GetProperty("IsClosed")!.GetSetMethod(true)!.Invoke(concrete, new object[] { true });
        typeof(Window).GetProperty("IsMinimized")!.GetSetMethod(true)!.Invoke(concrete, new object[] { true });

        Assert.True(concrete.IsClosed);
        Assert.True(concrete.IsMinimized);
        Assert.False(adapter.IsOpen);

        adapter.IsOpen = true;

        Assert.False(concrete.IsClosed);
        Assert.False(concrete.IsMinimized);
        Assert.True(adapter.IsOpen);
    }

    [Fact]
    public void IsOpen_Setter_False_WithoutMinimizing_CallsClose()
    {
        var mock = new MockUmbraWindow
        {
            IsClosed = false,
            IsMinimized = false
        };
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test");

        adapter.IsOpen = false;

        Assert.True(mock.CloseCalled);
        Assert.True(mock.IsClosed);
        Assert.False(mock.IsMinimized);
        Assert.False(adapter.IsOpen);
    }

    [Fact]
    public void IsOpen_Setter_False_WhenBeingMinimized_SetsMinimizedWithoutCallingClose()
    {
        var mock = new MockUmbraWindow
        {
            IsClosed = false,
            IsMinimized = false
        };
        var isMinimizing = false;
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test", isBeingMinimized: () => isMinimizing);

        isMinimizing = true;
        adapter.IsOpen = false;

        Assert.False(mock.CloseCalled);
        Assert.False(mock.IsClosed);
        Assert.True(mock.IsMinimized);
        Assert.False(adapter.IsOpen);
    }

    [Fact]
    public void IsOpen_Setter_False_WhenBeingMinimizedReturnsFalse_CallsClose()
    {
        var mock = new MockUmbraWindow
        {
            IsClosed = false,
            IsMinimized = false
        };
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test", isBeingMinimized: () => false);

        adapter.IsOpen = false;

        Assert.True(mock.CloseCalled);
        Assert.True(mock.IsClosed);
        Assert.False(mock.IsMinimized);
    }

    [Fact]
    public void BringToFront_SetsIsFocusedOnMockWindow()
    {
        var mock = new MockUmbraWindow { IsFocused = false };
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test");

        adapter.BringToFront();

        Assert.True(mock.IsFocused);
        Assert.True(adapter.IsFocused);
    }

    [Fact]
    public void BringToFront_SetsIsFocusedOnConcreteWindow()
    {
        var concrete = ConcreteTestWindow.CreateUninitialized();
        var adapter = new UmbraWindowAdapter("TestWindow", concrete, "Test");

        Assert.False(concrete.IsFocused);

        adapter.BringToFront();

        Assert.True(concrete.IsFocused);
        Assert.True(adapter.IsFocused);
    }

    [Fact]
    public void PositionAndSize_ProxiedToUnderlyingWindow()
    {
        var mock = new MockUmbraWindow
        {
            Position = new Vector2(120, 240),
            Size = new Vector2(640, 480)
        };
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test");

        Assert.Equal(new Vector2(120, 240), adapter.Position);
        Assert.Equal(new Vector2(640, 480), adapter.Size);

        adapter.Position = new Vector2(300, 400);
        adapter.Size = new Vector2(800, 600);

        Assert.Equal(new Vector2(300, 400), mock.Position);
        Assert.Equal(new Vector2(800, 600), mock.Size);
    }

    [Fact]
    public void PositionAndSize_SettingNull_DoesNotThrowOrOverwrite()
    {
        var mock = new MockUmbraWindow
        {
            Position = new Vector2(100, 200),
            Size = new Vector2(300, 400)
        };
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test");

        adapter.Position = null;
        adapter.Size = null;

        Assert.Equal(new Vector2(100, 200), mock.Position);
        Assert.Equal(new Vector2(300, 400), mock.Size);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void DrawConditions_ReturnsOppositeOfIsClosed(bool isClosed, bool expectedResult)
    {
        var mock = new MockUmbraWindow { IsClosed = isClosed };
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test");

        Assert.Equal(expectedResult, adapter.DrawConditions());
    }

    [Fact]
    public void Toggle_InvertsIsOpenState()
    {
        var mock = new MockUmbraWindow { IsClosed = false, IsMinimized = false };
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test");

        Assert.True(adapter.IsOpen);

        adapter.Toggle();
        Assert.False(adapter.IsOpen);
        Assert.True(mock.IsClosed);

        adapter.Toggle();
        Assert.True(adapter.IsOpen);
        Assert.False(mock.IsClosed);
    }

    [Fact]
    public void SafeNoOpMethods_ExecuteWithoutExceptions()
    {
        var mock = new MockUmbraWindow();
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test");

        adapter.PreOpenCheck();
        adapter.PreDraw();
        adapter.Draw();
        adapter.PostDraw();
        adapter.OnOpen();
        adapter.OnClose();
        adapter.OnSafeToRemove();
        adapter.Update();
    }

    [Fact]
    public void Proxy_Render_WhenNotHidden_InvokesUnderlyingRender()
    {
        var mock = new MockUmbraWindow();
        var proxy = new UmbraWindowProxy(mock);

        proxy.Render("TestInstance");

        Assert.Equal(1, mock.RenderCallCount);
    }

    [Fact]
    public void Proxy_Render_WhenHidden_SuppressesUnderlyingRender()
    {
        var mock = new MockUmbraWindow();
        var proxy = new UmbraWindowProxy(mock) { IsHidden = true };

        proxy.Render("TestInstance");

        Assert.Equal(0, mock.RenderCallCount);
    }

    [Fact]
    public void Proxy_Properties_WhenHidden_ReflectHiddenState()
    {
        var mock = new MockUmbraWindow
        {
            Position = new Vector2(50, 60),
            Size = new Vector2(300, 200),
            IsClosed = false,
            IsMinimized = false,
            IsFocused = true,
            IsHovered = true
        };
        var proxy = new UmbraWindowProxy(mock) { IsHidden = true };

        Assert.Equal(new Vector2(50, 60), proxy.Position);
        Assert.Equal(Vector2.Zero, proxy.Size);
        Assert.True(proxy.IsClosed);
        Assert.True(proxy.IsMinimized);
        Assert.False(proxy.IsFocused);
        Assert.False(proxy.IsHovered);
        Assert.Same(mock, proxy.UnderlyingWindow);
    }

    [Fact]
    public void Proxy_CloseAndDispose_ForwardToUnderlyingWindow()
    {
        var mock = new MockUmbraWindow();
        var proxy = new UmbraWindowProxy(mock);
        var requestCloseFired = false;
        proxy.RequestClose += () => requestCloseFired = true;

        proxy.Close();
        Assert.True(mock.CloseCalled);
        Assert.True(requestCloseFired);

        proxy.Dispose();
        Assert.True(mock.DisposeCalled);
    }

    [Fact]
    public void Adapter_IsOpen_False_WhenBeingMinimized_WithProxy_HidesProxyAndSuppressesRender()
    {
        var mock = new MockUmbraWindow();
        var proxy = new UmbraWindowProxy(mock);
        var isMinimizing = false;
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test", isBeingMinimized: () => isMinimizing, proxy: proxy);

        isMinimizing = true;
        adapter.IsOpen = false;

        Assert.True(proxy.IsHidden);
        Assert.False(adapter.IsOpen);
        Assert.True(mock.IsMinimized);
        Assert.False(mock.CloseCalled);

        // Rendering through proxy must be suppressed
        proxy.Render("TestWindow");
        Assert.Equal(0, mock.RenderCallCount);
    }

    [Fact]
    public void Adapter_IsOpen_True_WithProxy_RestoresProxyAndPermitsRender()
    {
        var mock = new MockUmbraWindow();
        var proxy = new UmbraWindowProxy(mock) { IsHidden = true };
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test", proxy: proxy);

        adapter.IsOpen = true;

        Assert.False(proxy.IsHidden);
        Assert.True(adapter.IsOpen);
        Assert.False(mock.IsMinimized);
        Assert.False(mock.IsClosed);

        // Rendering through proxy must be allowed now
        proxy.Render("TestWindow");
        Assert.Equal(1, mock.RenderCallCount);
    }

    [Fact]
    public void Adapter_BringToFront_WithProxy_ClearsProxyIsHidden()
    {
        var mock = new MockUmbraWindow();
        var proxy = new UmbraWindowProxy(mock) { IsHidden = true };
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test", proxy: proxy);

        adapter.BringToFront();

        Assert.False(proxy.IsHidden);
        Assert.True(mock.IsFocused);
    }

    [Fact]
    public void HookTitleBarMinimize_GracefullyHandlesMockWithoutWindowNode()
    {
        var mock = new MockUmbraWindow();
        var proxy = new UmbraWindowProxy(mock);
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test", proxy: proxy);
        var service = new WindowManagerService();
        var tracked = service.RegisterWindow(adapter);

        // Should not throw even though MockUmbraWindow has no WindowNode property
        var exception = Record.Exception(() => adapter.HookTitleBarMinimize(service, tracked));
        Assert.Null(exception);
    }

    [Fact]
    public void HookTitleBarMinimize_SynchronizesNativeMinimizeState()
    {
        var mock = new MockUmbraWindow { IsMinimized = true };
        var proxy = new UmbraWindowProxy(mock);
        var adapter = new UmbraWindowAdapter("TestWindow", mock, "Test", proxy: proxy);
        var service = new WindowManagerService();
        var tracked = service.RegisterWindow(adapter);
        adapter.IsBeingMinimized = () => tracked.IsMinimized;

        Assert.False(tracked.IsMinimized);

        adapter.HookTitleBarMinimize(service, tracked);

        Assert.True(tracked.IsMinimized);
        Assert.False(adapter.IsOpen);
        Assert.True(proxy.IsHidden);
    }
}
