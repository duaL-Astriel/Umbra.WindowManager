using System.Numerics;
using Dalamud.Bindings.ImGui;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public class ImGuiWindowClassifierTests
{
    private static readonly Vector2 GoodSize = new(400f, 300f);

    [Fact]
    public void ShouldTrack_TopLevelTitledWindow_IsTracked()
    {
        // A Sonar-like raw window: has a title, real size, content, no exclusion flags.
        Assert.True(ImGuiWindowClassifier.ShouldTrack(
            ImGuiWindowFlags.None, GoodSize, "Sonar##SonarMain", "Sonar", hasContent: true));
    }

    [Theory]
    [InlineData(ImGuiWindowFlags.ChildWindow)]
    [InlineData(ImGuiWindowFlags.Popup)]
    [InlineData(ImGuiWindowFlags.Tooltip)]
    [InlineData(ImGuiWindowFlags.Modal)]
    [InlineData(ImGuiWindowFlags.NoTitleBar)]
    [InlineData(ImGuiWindowFlags.NoDecoration)]
    [InlineData(ImGuiWindowFlags.NoInputs)]
    [InlineData(ImGuiWindowFlags.NoMouseInputs)]
    public void ShouldTrack_ExcludedFlags_AreRejected(ImGuiWindowFlags flag)
    {
        Assert.False(ImGuiWindowClassifier.ShouldTrack(
            flag, GoodSize, "Something##x", "Something", hasContent: true));
    }

    [Theory]
    [InlineData("Debug##Default")]
    [InlineData("Debug##foo")]
    [InlineData("##orchestrion_miniplayer")] // ID-only overlay: empty clean title too
    public void ShouldTrack_InternalOrIdOnlyNames_AreRejected(string name)
    {
        var cleanTitle = name.StartsWith("##") ? "" : "Debug";
        Assert.False(ImGuiWindowClassifier.ShouldTrack(
            ImGuiWindowFlags.None, GoodSize, name, cleanTitle, hasContent: true));
    }

    [Fact]
    public void ShouldTrack_EmptyTitle_IsRejected()
    {
        Assert.False(ImGuiWindowClassifier.ShouldTrack(
            ImGuiWindowFlags.None, GoodSize, "###idOnly", "", hasContent: true));
    }

    [Fact]
    public void ShouldTrack_ZeroSize_IsRejected()
    {
        Assert.False(ImGuiWindowClassifier.ShouldTrack(
            ImGuiWindowFlags.None, new Vector2(0f, 300f), "W##x", "W", hasContent: true));
    }

    [Fact]
    public void ShouldTrack_NoContent_IsRejected()
    {
        Assert.False(ImGuiWindowClassifier.ShouldTrack(
            ImGuiWindowFlags.None, GoodSize, "W##x", "W", hasContent: false));
    }
}
