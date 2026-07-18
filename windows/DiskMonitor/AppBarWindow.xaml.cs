using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DiskMonitor.Helpers;
using DiskMonitor.Services;
using Microsoft.Win32;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfCursors = System.Windows.Input.Cursors;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfImage = System.Windows.Controls.Image;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace DiskMonitor;

public partial class AppBarWindow : Window
{
    private const double ChipIconSize = 16;
    private const double ChipFontSize = 12;
    private const double ChipMaxTitleWidth = 140;
    private const double ChipHoverOpenMs = 500;

    private AppBarHelper? _appBar;
    private readonly DispatcherTimer _timer;
    private DispatcherTimer? _barHoverTimer;
    private Action? _barHoverAction;
    private string _statusForCopy = "";
    private List<MenuItemSpec> _primaryItems = [];
    private List<MenuItemSpec> _overflowItems = [];
    private readonly List<(MenuItemSpec Spec, Border Chip)> _chipEntries = [];
    private SolidColorBrush _fg = WpfBrushes.Black;
    private SolidColorBrush _fgMuted = WpfBrushes.Gray;
    private SolidColorBrush _hover = WpfBrushes.Transparent;
    private SolidColorBrush _sep = WpfBrushes.Gray;
    private bool _applyingOverflow;
    private bool _iconsScheduled;
    private bool _forceChipRebuild;
    private Border? _activeBarChip;
    private int _barMenuGeneration;
    private int _refreshGeneration;

    public event Action<string>? StatusChanged;

    public AppBarWindow()
    {
        InitializeComponent();
        Title = L.Get("app.name");
        ApplyTheme();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => RefreshStatus();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Closed += OnClosed;

        MoreChip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CancelBarHover();
            AltKeyState.NotePointerGesture();
            ShowMoreMenu();
        };
        MoreChip.MouseEnter += (_, _) =>
        {
            MoreChip.Background = _hover;
            AltKeyState.NotePointerGesture();
            ScheduleBarHover(ShowMoreMenu);
        };
        MoreChip.MouseLeave += (_, _) =>
        {
            if (_activeBarChip != MoreChip)
                MoreChip.Background = WpfBrushes.Transparent;
            CancelBarHover();
        };

        TopBarStore.Changed += OnTopBarChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnTopBarChanged() => Dispatcher.BeginInvoke(RefreshStatus);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _appBar = new AppBarHelper(this);
        _appBar.Register();
        // Raw Input INPUTSINK — track Alt while AppBar is not foreground.
        var src = (HwndSource?)PresentationSource.FromVisual(this);
        if (src is not null)
            AltKeyState.Attach(src);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        _timer.Start();
        // ChipsClip.ActualWidth is reliable after first layout pass.
        Dispatcher.BeginInvoke(ApplyOverflowLayout, DispatcherPriority.Loaded);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CancelBarHover();
        ClearActiveBarChip();
        _timer.Stop();
        TopBarStore.Changed -= OnTopBarChanged;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _appBar?.Dispose();
        _appBar = null;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Color / General — light/dark toggle in Settings.
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
            return;

        Dispatcher.BeginInvoke(() =>
        {
            ApplyTheme();
            _forceChipRebuild = true;
            RefreshStatus();
        }, DispatcherPriority.Background);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_applyingOverflow || !e.WidthChanged) return;
        ApplyOverflowLayout();
    }

    public void RefreshStatus()
    {
        // DriveInfo / SHGetFileInfo / Recycle Bin must not run on the UI thread.
        var gen = ++_refreshGeneration;
        _ = Task.Run(() =>
        {
            string status;
            List<MenuItemSpec> items;
            try
            {
                _ = MenuActions.RecycleBinBytes(); // refresh cache off-UI
                // Re-query Explorer display names — volume labels change after format / rename.
                ShellVolumeHelper.InvalidateMenuDisplayNames();
                var system = VolumeService.SystemVolume();
                if (system is null)
                    status = L.Get("app.name");
                else if (system.AvailableBytes is not null)
                    status = $"{system.Name}  {VolumeService.StatusLine(system)}";
                else
                    status = system.Name;
                items = MenuBuilder.BuildPrimaryItems();
            }
            catch
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (gen != _refreshGeneration || !IsVisible) return;
                ApplyRefreshResult(status, items);
            }, DispatcherPriority.Background);
        });
    }

    private void ApplyRefreshResult(string status, List<MenuItemSpec> items)
    {
        _statusForCopy = status;
        StatusChanged?.Invoke(_statusForCopy);

        if (!_forceChipRebuild && SameChipStructure(_primaryItems, items))
        {
            // In-place update — avoid full overflow remeasure unless widths may change.
            if (UpdateChipTrailings(items))
                ApplyOverflowLayout();
            return;
        }

        _forceChipRebuild = false;
        RebuildChips(items);
        ScheduleBarAsync();
    }

    private static bool SameChipStructure(IReadOnlyList<MenuItemSpec> a, IReadOnlyList<MenuItemSpec> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].IsSeparator != b[i].IsSeparator) return false;
            if (a[i].IsSeparator) continue;
            if (!string.Equals(a[i].Title, b[i].Title, StringComparison.Ordinal)
                || !string.Equals(a[i].IconPath, b[i].IconPath, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(a[i].TrailingTag, b[i].TrailingTag, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    /// <summary>Returns true when a trailing length changed (may need overflow reflow).</summary>
    private bool UpdateChipTrailings(IReadOnlyList<MenuItemSpec> items)
    {
        var needReflow = false;
        for (var i = 0; i < _chipEntries.Count && i < items.Count; i++)
        {
            var spec = items[i];
            _primaryItems[i] = spec;
            if (spec.IsSeparator) continue;
            if (_chipEntries[i].Chip.Tag is not ChipTag tag || tag.Trailing is null) continue;
            if (spec.Trailing is null || tag.Trailing.Text == spec.Trailing) continue;
            if (tag.Trailing.Text.Length != spec.Trailing.Length)
                needReflow = true;
            tag.Trailing.Text = spec.Trailing;
        }
        return needReflow;
    }

    private void RebuildChips(List<MenuItemSpec> items)
    {
        CancelBarHover();
        ClearActiveBarChip();
        _primaryItems = items;
        _iconsScheduled = false;
        ChipsHost.Children.Clear();
        _chipEntries.Clear();

        foreach (var spec in _primaryItems)
        {
            var chip = CreateChip(spec);
            chip.Visibility = Visibility.Hidden; // Hidden still measures; Collapsed DesiredSize=0
            _chipEntries.Add((spec, chip));
            ChipsHost.Children.Add(chip);
        }

        ApplyOverflowLayout();
    }

    private Border CreateChip(MenuItemSpec spec)
    {
        if (spec.IsSeparator)
        {
            return new Border
            {
                Width = 1,
                Height = 14,
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Background = _sep,
                IsHitTestVisible = false,
                Tag = new ChipTag(spec, Icon: null, Title: null, Trailing: null)
            };
        }

        var row = new StackPanel
        {
            Orientation = WpfOrientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Fixed box so shell icons and text share the same visual centerline.
        var iconBox = new Grid
        {
            Width = ChipIconSize,
            Height = ChipIconSize,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var image = new WpfImage
        {
            // Glyphs must be drawn on the UI thread — off-thread DrawingImage
            // often ignores the brush and renders white (looks like dark-only icons).
            Source = ResolveBarGlyph(spec),
            Width = ChipIconSize,
            Height = ChipIconSize,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = spec.Enabled ? 1 : 0.45,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        iconBox.Children.Add(image);
        row.Children.Add(iconBox);

        var title = new TextBlock
        {
            Text = spec.Title,
            FontSize = ChipFontSize,
            FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            Foreground = spec.Enabled ? _fg : _fgMuted,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = ChipMaxTitleWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = ChipIconSize,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight
        };
        TextOptions.SetTextFormattingMode(title, TextFormattingMode.Display);
        row.Children.Add(title);

        TextBlock? trailingBlock = null;
        if (!string.IsNullOrEmpty(spec.Trailing) || !string.IsNullOrEmpty(spec.TrailingTag))
        {
            trailingBlock = new TextBlock
            {
                Text = spec.Trailing ?? "",
                FontSize = ChipFontSize,
                FontStyle = spec.TrailingItalic ? FontStyles.Italic : FontStyles.Normal,
                FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
                Foreground = _fgMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                LineHeight = ChipIconSize,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            TextOptions.SetTextFormattingMode(trailingBlock, TextFormattingMode.Display);
            row.Children.Add(trailingBlock);
        }

        var chip = new Border
        {
            Child = row,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(2, 0, 2, 0),
            CornerRadius = new CornerRadius(4),
            Background = WpfBrushes.Transparent,
            Cursor = spec.Enabled || spec.HasSubmenu ? WpfCursors.Hand : WpfCursors.Arrow,
            VerticalAlignment = VerticalAlignment.Stretch,
            Tag = new ChipTag(spec, image, title, trailingBlock)
        };

        chip.MouseEnter += (_, _) =>
        {
            chip.Background = _hover;
            // Sample Alt here (before hover dwell / Activate eats the key).
            AltKeyState.NotePointerGesture();
            if (spec.HasSubmenu && spec.PopulateSubmenu is not null)
                ScheduleBarHover(() => OpenChipSubmenu(chip, spec));
        };
        chip.MouseLeave += (_, _) =>
        {
            if (_activeBarChip != chip)
                chip.Background = WpfBrushes.Transparent;
            CancelBarHover();
        };
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            CancelBarHover();
            AltKeyState.NotePointerGesture();
            if (spec.Enabled && spec.OnClick is not null)
            {
                MenuSession.CloseAll();
                _barMenuGeneration++;
                ClearActiveBarChip();
                spec.OnClick();
            }
            else if (spec.HasSubmenu && spec.PopulateSubmenu is not null)
            {
                OpenChipSubmenu(chip, spec);
            }
        };

        return chip;
    }

    private void SetActiveBarChip(Border chip)
    {
        if (_activeBarChip is not null && _activeBarChip != chip
            && _activeBarChip.Tag is not ChipTag { Spec.IsSeparator: true })
            _activeBarChip.Background = WpfBrushes.Transparent;

        _activeBarChip = chip;
        chip.Background = _hover;
    }

    private void ClearActiveBarChip()
    {
        if (_activeBarChip is null) return;
        var chip = _activeBarChip;
        _activeBarChip = null;
        if (!chip.IsMouseOver)
            chip.Background = WpfBrushes.Transparent;
    }

    private void AttachBarMenuLifetime(VolumeMenuWindow menu, int generation)
    {
        void OnClosed(object? sender, EventArgs e)
        {
            menu.Closed -= OnClosed;
            Dispatcher.BeginInvoke(() =>
            {
                // Ignore closes from switching to another bar menu.
                if (generation != _barMenuGeneration) return;
                if (!MenuSession.AnyVisible())
                    ClearActiveBarChip();
            }, DispatcherPriority.Input);
        }

        menu.Closed += OnClosed;
    }

    private void ScheduleBarHover(Action open)
    {
        CancelBarHover();
        _barHoverAction = open;
        _barHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ChipHoverOpenMs) };
        _barHoverTimer.Tick += OnBarHoverTick;
        _barHoverTimer.Start();
    }

    private void OnBarHoverTick(object? sender, EventArgs e)
    {
        var action = _barHoverAction;
        CancelBarHover();
        action?.Invoke();
    }

    private void CancelBarHover()
    {
        if (_barHoverTimer is not null)
        {
            _barHoverTimer.Stop();
            _barHoverTimer.Tick -= OnBarHoverTick;
            _barHoverTimer = null;
        }
        _barHoverAction = null;
    }

    private void ApplyOverflowLayout()
    {
        if (_applyingOverflow || _chipEntries.Count == 0 || ActualWidth < 40) return;
        _applyingOverflow = true;
        try
        {
            // Hidden (not Collapsed): participates in measure with real DesiredSize / ActualWidth.
            foreach (var (_, chip) in _chipEntries)
                chip.Visibility = Visibility.Hidden;

            UpdateLayout();

            const double marginDip = 10;
            const double moreWidth = 32;
            var available = ChipsClip.ActualWidth > 1
                ? ChipsClip.ActualWidth
                : Math.Max(40, ActualWidth - marginDip - moreWidth);

            var widths = new double[_chipEntries.Count];
            for (var i = 0; i < _chipEntries.Count; i++)
            {
                var chip = _chipEntries[i].Chip;
                var w = chip.ActualWidth;
                if (w < 1)
                {
                    chip.Measure(new System.Windows.Size(double.PositiveInfinity, AppBarHelper.BarHeightDip));
                    w = chip.DesiredSize.Width;
                }
                widths[i] = Math.Ceiling(w);
            }

            double used = 0;
            _overflowItems = [];
            for (var i = 0; i < _chipEntries.Count; i++)
            {
                var (spec, chip) = _chipEntries[i];
                var w = widths[i];
                // Strict: if any part would sit under "…", it does not fit.
                if (used + w <= available)
                {
                    chip.Visibility = Visibility.Visible;
                    used += w;
                }
                else
                {
                    chip.Visibility = Visibility.Collapsed;
                    if (!spec.IsSeparator)
                        _overflowItems.Add(spec);
                }
            }

            UpdateLayout();

            // Second pass: if a visible chip still crosses the clip edge, peel from the right.
            while (true)
            {
                Border? last = null;
                var lastIndex = -1;
                for (var i = 0; i < _chipEntries.Count; i++)
                {
                    if (_chipEntries[i].Chip.Visibility != Visibility.Visible) continue;
                    last = _chipEntries[i].Chip;
                    lastIndex = i;
                }
                if (last is null || lastIndex < 0) break;

                System.Windows.Point right;
                try
                {
                    right = last.TransformToVisual(ChipsClip)
                        .Transform(new System.Windows.Point(last.ActualWidth, 0));
                }
                catch
                {
                    break;
                }

                if (right.X <= available + 0.5) break;

                last.Visibility = Visibility.Collapsed;
                var spec = _chipEntries[lastIndex].Spec;
                if (!spec.IsSeparator)
                    _overflowItems.Insert(0, spec);
                used = Math.Max(0, used - widths[lastIndex]);
            }

            HideOrphanSeparators();
        }
        finally
        {
            _applyingOverflow = false;
        }
    }

    /// <summary>Drop separators that would sit at the end or with nothing after them.</summary>
    private void HideOrphanSeparators()
    {
        for (var i = 0; i < _chipEntries.Count; i++)
        {
            var (spec, chip) = _chipEntries[i];
            if (!spec.IsSeparator || chip.Visibility != Visibility.Visible) continue;

            var hasBefore = false;
            for (var j = i - 1; j >= 0; j--)
            {
                if (_chipEntries[j].Chip.Visibility != Visibility.Visible) continue;
                if (!_chipEntries[j].Spec.IsSeparator) { hasBefore = true; break; }
            }

            var hasAfter = false;
            for (var j = i + 1; j < _chipEntries.Count; j++)
            {
                if (_chipEntries[j].Chip.Visibility != Visibility.Visible) continue;
                if (!_chipEntries[j].Spec.IsSeparator) { hasAfter = true; break; }
            }

            if (!hasBefore || !hasAfter)
                chip.Visibility = Visibility.Collapsed;
        }
    }

    private void ScheduleBarAsync()
    {
        if (_iconsScheduled) return;
        _iconsScheduled = true;

        foreach (var (spec, chip) in _chipEntries)
        {
            if (spec.IsSeparator || chip.Tag is not ChipTag tag) continue;

            if (!string.IsNullOrEmpty(spec.IconPath) && tag.Icon is not null)
            {
                var path = spec.IconPath;
                var img = tag.Icon;
                _ = Task.Run(async () =>
                {
                    ImageSource? icon;
                    try
                    {
                        icon = await ShellIconLoader.GetAsync(path, 18).ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    if (icon is null) return;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsVisible) return;
                        img.Source = icon;
                    }, DispatcherPriority.Background);
                });
            }

            if (spec.TrailingTag is { Length: > 0 } trailingTag && tag.Trailing is not null)
            {
                var trailing = tag.Trailing;
                if (trailingTag.StartsWith("favsize:", StringComparison.Ordinal))
                {
                    var path = trailingTag["favsize:".Length..];
                    _ = Task.Run(async () =>
                    {
                        var bytes = await DirectoryService.GetFolderSizeAsync(path, TimeSpan.FromSeconds(8))
                            .ConfigureAwait(false);
                        if (bytes is null) return;
                        var text = VolumeService.FormatBytes(bytes.Value, concise: false);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (!IsVisible) return;
                            if (trailing.Text == text) return;
                            trailing.Text = text;
                            // Width grew — must re-fit so we don't clip into "…".
                            ApplyOverflowLayout();
                        }, DispatcherPriority.Background);
                    });
                }
                else if (trailingTag == "trashsize")
                {
                    _ = Task.Run(async () =>
                    {
                        long bytes;
                        try { bytes = MenuActions.RecycleBinBytes(forceRefresh: true); }
                        catch { return; }
                        var text = VolumeService.FormatBytes(bytes, concise: false);
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (!IsVisible) return;
                            if (trailing.Text == text) return;
                            trailing.Text = text;
                            ApplyOverflowLayout();
                        }, DispatcherPriority.Background);
                    });
                }
            }
        }
    }

    private void OpenChipSubmenu(Border chip, MenuItemSpec spec)
    {
        if (!spec.HasSubmenu || spec.PopulateSubmenu is null) return;

        MenuSession.CloseAll();
        _barMenuGeneration++;
        var generation = _barMenuGeneration;
        SetActiveBarChip(chip);
        // Snapshot before listing — ShowAbove Activate must not replace a true Alt with false.
        AltKeyState.Capture();
        var menu = new VolumeMenuWindow();
        AttachBarMenuLifetime(menu, generation);
        if (AltKeyState.IsDown())
            AltKeyState.Capture(true);
        // Populate before Show so directory headers (loading / top-bar) paint on first frame.
        spec.PopulateSubmenu(menu);
        menu.ShowAbove(chip);
    }

    private void ShowMoreMenu()
    {
        // Recompute overflow in case of resize race.
        ApplyOverflowLayout();

        MenuSession.CloseAll();
        _barMenuGeneration++;
        var generation = _barMenuGeneration;
        SetActiveBarChip(MoreChip);
        AltKeyState.Capture();
        var menu = new VolumeMenuWindow();
        AttachBarMenuLifetime(menu, generation);
        menu.BeginLoad();
        var items = MenuBuilder.BuildOverflowAndMore(
            _overflowItems,
            refresh: () => RefreshStatus(),
            quit: () => System.Windows.Application.Current.Shutdown(),
            statusText: () => _statusForCopy);
        menu.SetItems(items);
        menu.ShowAbove(MoreChip);
        MenuBuilder.ScheduleRootAsync(menu, items);
    }

    private void ApplyTheme()
    {
        var (bg, fg, muted, border, hover) = AppTheme.AppBarColors();

        _fg = new SolidColorBrush(fg);
        _fg.Freeze();
        _fgMuted = new SolidColorBrush(muted);
        _fgMuted.Freeze();
        _hover = new SolidColorBrush(hover);
        _hover.Freeze();
        _sep = new SolidColorBrush(border);
        _sep.Freeze();

        Background = new SolidColorBrush(bg);
        RootBorder.Background = new SolidColorBrush(bg);
        RootBorder.BorderBrush = new SolidColorBrush(border);
        MoreIcon.Source = MenuIcons.More(_fg, 14);

        foreach (var (spec, chip) in _chipEntries)
        {
            if (spec.IsSeparator)
            {
                chip.Background = _sep;
                continue;
            }

            if (chip.Tag is not ChipTag tag) continue;
            if (tag.Title is not null)
                tag.Title.Foreground = spec.Enabled ? _fg : _fgMuted;
            if (tag.Trailing is not null)
                tag.Trailing.Foreground = _fgMuted;
            // Theme glyphs only — shell icons (IconPath) stay as-is once loaded.
            if (tag.Icon is not null && string.IsNullOrEmpty(spec.IconPath) && spec.Glyph != AppGlyph.None)
                tag.Icon.Source = ResolveBarGlyph(spec);
        }
    }

    /// <summary>Create AppBar glyphs with the current theme brush on the UI thread.</summary>
    private ImageSource? ResolveBarGlyph(MenuItemSpec spec)
    {
        var glyph = spec.Glyph != AppGlyph.None
            ? spec.Glyph
            : string.Equals(spec.TrailingTag, "trashsize", StringComparison.Ordinal)
                ? AppGlyph.Recycle
                : !string.IsNullOrEmpty(spec.IconPath) ? AppGlyph.Disk : AppGlyph.Folder;
        return MenuIcons.Create(glyph, _fg, ChipIconSize);
    }

    private sealed record ChipTag(MenuItemSpec Spec, WpfImage? Icon, TextBlock? Title, TextBlock? Trailing);
}
