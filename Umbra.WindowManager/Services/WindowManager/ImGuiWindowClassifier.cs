using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Umbra.WindowManager.Services.WindowManager;

/// <summary>
/// Pure eligibility rules for a raw-ImGui window (a <c>ctx.Windows</c> entry that has no owning
/// <see cref="Dalamud.Interface.Windowing.IWindow"/>). Deliberately strict: <c>ctx.Windows</c> contains
/// child windows, popups, tooltips, and ImGui-internal debug windows, none of which belong in the taskbar
/// (issue #38). Kept side-effect free so it is fully unit-testable without a live ImGui context.
/// </summary>
public static class ImGuiWindowClassifier
{
    // ImGui-internal / debug window name prefixes that must never surface in the taskbar.
    private static readonly string[] InternalNamePrefixes = ["Debug##", "##"];

    // Flags that mark a window as not a manageable, top-level, interactive plugin window.
    private const ImGuiWindowFlags ExcludedFlags =
        ImGuiWindowFlags.ChildWindow |
        ImGuiWindowFlags.Popup |
        ImGuiWindowFlags.Tooltip |
        ImGuiWindowFlags.Modal |
        ImGuiWindowFlags.NoTitleBar |
        ImGuiWindowFlags.NoDecoration |
        ImGuiWindowFlags.NoInputs |
        ImGuiWindowFlags.NoMouseInputs;

    public static bool ShouldTrack(ImGuiWindowFlags flags, Vector2 size, string name, string cleanTitle, bool hasContent)
    {
        if (!ImGuiContextMonitor.ValidateWindowDimensions(size))
            return false;

        if (!hasContent)
            return false;

        // A real, human-readable title is required. ID-only overlays (e.g. "###orchestrion_miniplayer")
        // have an empty clean title and are intentionally excluded (issue #38, dropped #36 part 3).
        if (string.IsNullOrWhiteSpace(cleanTitle))
            return false;

        if ((flags & ExcludedFlags) != 0)
            return false;

        foreach (var prefix in InternalNamePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
