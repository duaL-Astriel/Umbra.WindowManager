using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class WindowInfoHelperTests
{
    [Theory]
    [InlineData("My Window", "My Window")]
    [InlineData("Settings##MyPluginSettings", "Settings")]
    [InlineData("Inspector###InspectorWindow_123", "Inspector")]
    [InlineData("   Spaced Title  ##ID", "Spaced Title")]
    [InlineData("##OnlyId", "")]
    public void GetCleanTitle_StripsImGuiIdentifiers(string input, string expected)
    {
        var clean = WindowInfoHelper.GetCleanTitle(input);
        Assert.Equal(expected, clean);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void GetCleanTitle_NullOrWhitespace_ReturnsEmpty(string? input, string expected)
    {
        var clean = WindowInfoHelper.GetCleanTitle(input);
        Assert.Equal(expected, clean);
    }

    [Theory]
    [InlineData("My Window", "My Window")]
    [InlineData("Settings##MyPluginSettings", "MyPluginSettings")]
    [InlineData("Inspector###InspectorWindow_123", "InspectorWindow_123")]
    [InlineData("##OnlyId", "OnlyId")]
    public void GetWindowId_ExtractsIdentifierOrFallback(string input, string expected)
    {
        var id = WindowInfoHelper.GetWindowId(input);
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void GetWindowId_NullOrWhitespace_ReturnsEmpty(string? input, string expected)
    {
        var id = WindowInfoHelper.GetWindowId(input);
        Assert.Equal(expected, id);
    }
}
