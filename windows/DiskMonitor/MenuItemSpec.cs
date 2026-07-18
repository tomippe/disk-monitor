using System.Windows.Media;

namespace DiskMonitor;

/// <summary>Segoe glyph kind — rendered on the UI thread with the current theme brush.</summary>
public enum AppGlyph
{
    None = 0,
    Refresh,
    Copy,
    Info,
    Feedback,
    Restart,
    Quit,
    More,
    Chevron,
    Recycle,
    Disk,
    Star,
    Folder,
    Document,
    Eject,
}

public sealed class MenuItemSpec
{
    public ImageSource? Icon { get; set; }
    /// <summary>Theme glyph; preferred over <see cref="Icon"/> for Segoe icons.</summary>
    public AppGlyph Glyph { get; set; }
    public string Title { get; set; } = "";
    public string? Trailing { get; set; }
    public bool TrailingItalic { get; set; }
    /// <summary>Register trailing TextBlock for async size updates.</summary>
    public string? TrailingTag { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Gray label (hidden / system items) while still optionally clickable.</summary>
    public bool Muted { get; set; }
    public bool IsSeparator { get; init; }
    public bool HasSubmenu { get; set; }
    public Action? OnClick { get; set; }

    /// <summary>Build / refresh a child popup menu (separate window).</summary>
    public Action<VolumeMenuWindow>? PopulateSubmenu { get; set; }

    /// <summary>Optional tag for async title updates (e.g. folder summary).</summary>
    public string? Tag { get; set; }

    /// <summary>Path for async shell icon fetch (folders and files).</summary>
    public string? IconPath { get; set; }

    /// <summary>
    /// Align this row with the parent menu item (directory header sits above it).
    /// </summary>
    public bool AlignBesideParent { get; set; }

    /// <summary>
    /// Invisible row used only as the AlignBesideParent anchor (empty slot beside parent).
    /// </summary>
    public bool IsAlignSpacer { get; set; }
}
