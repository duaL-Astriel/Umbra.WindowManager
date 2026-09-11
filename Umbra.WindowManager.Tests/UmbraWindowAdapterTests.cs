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

        public event Action? RequestClose;

        public void Close()
        {
            this.CloseCalled = true;
            this.IsClosed = true;
            this.RequestClose?.Invoke();
        }

        public void Render(string instanceId) { }

        public void Dispose() { }
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
}
