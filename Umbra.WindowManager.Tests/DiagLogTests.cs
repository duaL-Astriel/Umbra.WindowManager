using System;
using System.IO;
using Umbra.WindowManager.Services.WindowManager;
using Xunit;

namespace Umbra.WindowManager.Tests;

public sealed class DiagLogTests : IDisposable
{
    private readonly string originalPath = DiagLogWriter.LogPath;
    private readonly bool originalEnabled = DiagLogWriter.Enabled;
    private readonly string tempDir = Path.Combine(Path.GetTempPath(), "wm-diaglog-" + Guid.NewGuid().ToString("N"));

    public DiagLogTests()
    {
        Directory.CreateDirectory(this.tempDir);
        DiagLogWriter.LogPath = Path.Combine(this.tempDir, "diag.log");
    }

    public void Dispose()
    {
        DiagLogWriter.Enabled = this.originalEnabled;
        DiagLogWriter.LogPath = this.originalPath;
        Directory.Delete(this.tempDir, recursive: true);
    }

    [Fact]
    public void DiagLog_WhenDisabled_DoesNotEvaluateArgumentsOrWriteFile()
    {
        DiagLogWriter.Enabled = false;
        var evaluations = 0;
        int Expensive() => ++evaluations;

        GameWindowAdapter.DiagLog($"[Test] value={Expensive()}");

        Assert.True(DiagLogWriter.Flush(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, evaluations);
        Assert.False(File.Exists(DiagLogWriter.LogPath));
    }

    [Fact]
    public void DiagLog_WhenEnabled_WritesLineInBackground()
    {
        DiagLogWriter.Enabled = true;

        GameWindowAdapter.DiagLog($"[Test] addr=0x{(nint)0xAB:X}");

        Assert.True(DiagLogWriter.Flush(TimeSpan.FromSeconds(5)));
        var text = File.ReadAllText(DiagLogWriter.LogPath);
        Assert.Contains("[Test] addr=0xAB", text);
    }

    [Fact]
    public void DiagLog_WhenFileReachedCap_StopsWriting()
    {
        DiagLogWriter.Enabled = true;
        using (var fs = File.Create(DiagLogWriter.LogPath))
        {
            fs.SetLength(DiagLogWriter.MaxFileBytes);
        }

        GameWindowAdapter.DiagLog($"[Test] should be dropped");

        Assert.True(DiagLogWriter.Flush(TimeSpan.FromSeconds(5)));
        Assert.Equal(DiagLogWriter.MaxFileBytes, new FileInfo(DiagLogWriter.LogPath).Length);
    }
}
