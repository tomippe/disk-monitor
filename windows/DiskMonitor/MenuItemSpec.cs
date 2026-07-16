using System.Windows.Media;

namespace DiskMonitor;

public sealed class MenuItemSpec
{
    public ImageSource? Icon { get; set; }
    public string Title { get; set; } = "";
    public string? Trailing { get; set; }
    public bool Enabled { get; set; } = true;
    public bool IsSeparator { get; init; }
    public bool HasSubmenu { get; set; }
    public Action? OnClick { get; set; }

    /// <summary>Build / refresh a child popup menu (separate window).</summary>
    public Action<VolumeMenuWindow>? PopulateSubmenu { get; set; }

    /// <summary>Optional tag for async title updates (e.g. folder summary).</summary>
    public string? Tag { get; set; }
}
