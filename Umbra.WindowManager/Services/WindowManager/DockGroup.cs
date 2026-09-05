using System.Collections.Generic;
using System.Linq;

namespace Umbra.WindowManager.Services.WindowManager;

public class DockGroup
{
    private readonly List<TrackedWindow> members = [];

    public DockGroup(string groupKey, string activeWindowName, IEnumerable<TrackedWindow> windows)
    {
        this.GroupKey = groupKey;
        this.ActiveWindowName = activeWindowName;
        this.members.AddRange(windows);
        foreach (var w in this.members)
            w.DockGroupKey = groupKey;
    }

    public string GroupKey { get; }
    public string ActiveWindowName { get; set; }
    public IReadOnlyList<TrackedWindow> Members => this.members;
    public bool IsMinimized { get; set; }

    public void Minimize()
    {
        this.IsMinimized = true;
        foreach (var window in this.members)
        {
            window.IsMinimized = true;
            window.IsOpen = false;
        }
    }

    public void Restore()
    {
        this.IsMinimized = false;
        foreach (var window in this.members)
        {
            window.IsMinimized = false;
            window.IsOpen = true;
        }

        var active = this.members.FirstOrDefault(w => w.WindowName == this.ActiveWindowName) 
                     ?? this.members.FirstOrDefault();
        active?.BringToFront();
    }
}
