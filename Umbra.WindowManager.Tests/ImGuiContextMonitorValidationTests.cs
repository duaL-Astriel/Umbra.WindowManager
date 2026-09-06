using System.Numerics;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class ImGuiContextMonitorValidationTests
{
    [Theory]
    [InlineData(100f, 100f, true)]
    [InlineData(1f, 1f, true)]
    [InlineData(0f, 100f, false)]
    [InlineData(100f, 0f, false)]
    [InlineData(0f, 0f, false)]
    [InlineData(-10f, 50f, false)]
    [InlineData(50f, -10f, false)]
    public void ValidateWindowDimensions_EvaluatesDimensionsCorrectly(float x, float y, bool expected)
    {
        var size = new Vector2(x, y);
        Assert.Equal(expected, ImGuiContextMonitor.ValidateWindowDimensions(size));
    }

    [Theory]
    [InlineData(10f, 10f, 0, true)]
    [InlineData(10f, 0f, 0, true)]
    [InlineData(0f, 10f, 0, true)]
    [InlineData(0f, 0f, 3, true)]
    [InlineData(0f, 0f, 2, false)]
    [InlineData(0f, 0f, 1, false)]
    [InlineData(0f, 0f, 0, false)]
    [InlineData(-1f, -1f, 0, false)]
    public void ValidateWindowContent_EvaluatesContentAndDrawCmdsCorrectly(float cx, float cy, int drawCmds, bool expected)
    {
        var contentSize = new Vector2(cx, cy);
        Assert.Equal(expected, ImGuiContextMonitor.ValidateWindowContent(contentSize, drawCmds));
    }
}
