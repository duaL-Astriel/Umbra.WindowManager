using System;

namespace Umbra.WindowManager.Services.WindowManager;

public static class WindowInfoHelper
{
    public static string GetCleanTitle(string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName))
            return string.Empty;

        var split = windowName.Split("##");
        return split[0].Trim();
    }

    public static string GetWindowId(string? windowName)
    {
        if (string.IsNullOrWhiteSpace(windowName))
            return string.Empty;

        if (windowName.Contains("###"))
        {
            var idx = windowName.IndexOf("###", StringComparison.Ordinal);
            return windowName[(idx + 3)..].Trim();
        }

        if (windowName.Contains("##"))
        {
            var idx = windowName.IndexOf("##", StringComparison.Ordinal);
            return windowName[(idx + 2)..].Trim();
        }

        return windowName.Trim();
    }
}
