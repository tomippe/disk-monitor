using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DiskMonitor.Helpers;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfCursors = System.Windows.Input.Cursors;
using WpfImage = System.Windows.Controls.Image;

namespace DiskMonitor;

public partial class VolumeMenuWindow : Window
{
    private const double IconSize = 18;
    private const double FontSizeDip = 13;
    private const double RowHeight = 30;
    /// <summary>First-open dwell (hover → open submenu).</summary>
    private const double SubmenuOpenDwellMs = 140;
    /// <summary>Switch/close dwell while a submenu is already open (diagonal move forgiveness).</summary>
    private const double SubmenuSwitchDwellMs = 420;
    private const double EdgePad = 8;
    private const double ChromeMargin = 10;
    /// <summary>Win+X / Start context menu style slide (no fade).</summary>
    private const double OpenSlideDip = 12;
    private static readonly Duration OpenSlideDuration = TimeSpan.FromMilliseconds(167);

    private enum OpenSlideKind { Above, BesideRight, BesideLeft }

    private bool _closing;
    private bool _isRoot;
    private bool _openTransitionPlayed;
    private DispatcherTimer? _submenuTimer;
    private Border? _pendingSubmenuRow;
    private MenuItemSpec? _pendingSubmenuSpec;
    private bool _pendingCloseChild;
    private Border? _openSubmenuRow;
    private VolumeMenuWindow? _child;
    private int _loadGeneration;
    private CancellationTokenSource? _loadCts;
    private readonly Dictionary<string, TextBlock> _titleBlocksByTag = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _trailingBlocksByTag = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WpfImage> _iconImagesByPath = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Parent row top in DIP (screen); first AlignBesideParent child lines up with this.</summary>
    private double? _besideParentRowTop;
    private double? _aboveAnchorTop;
    private VolumeMenuWindow? _besideParent;
    private Border? _alignAnchorRow;
    private SolidColorBrush? _hoverBrush;
    private bool _listedWithHidden;
    private Action? _relistDirectory;

    public VolumeMenuWindow()
    {
        InitializeComponent();
        ApplyTheme();
        AltKeyState.EnsureMessageHook();
        SourceInitialized += (_, _) =>
        {
            var src = (HwndSource?)PresentationSource.FromVisual(this);
            if (src is not null)
                AltKeyState.Attach(src);
        };
        Closed += (_, _) =>
        {
            CancelSubmenuTimer();
            CancelLoad();
            _relistDirectory = null;
            MenuSession.Unregister(this);
        };
    }

    /// <summary>Bump generation and cancel in-flight async work for this popup.</summary>
    public int BeginLoad()
    {
        CancelLoad();
        _loadCts = new CancellationTokenSource();
        return ++_loadGeneration;
    }

    public bool IsLoadCurrent(int generation) =>
        !_closing && generation == _loadGeneration && IsVisible;

    public CancellationToken LoadToken =>
        _loadCts?.Token ?? CancellationToken.None;

    public void SetItems(IReadOnlyList<MenuItemSpec> items)
    {
        ItemsHost.Children.Clear();
        _titleBlocksByTag.Clear();
        _trailingBlocksByTag.Clear();
        _iconImagesByPath.Clear();
        _alignAnchorRow = null;
        foreach (var item in items)
        {
            if (item.IsSeparator)
                ItemsHost.Children.Add(CreateSeparator());
            else if (item.IsAlignSpacer)
                ItemsHost.Children.Add(CreateAlignSpacer(item));
            else
                ItemsHost.Children.Add(CreateRow(item));
        }
        // Content often grows after the first show (loading → listing) — re-clamp every time.
        if (IsVisible)
            RefitInWorkArea();
        else
        {
            UpdateLayout();
            ApplyMaxHeightForWorkArea();
        }
    }

    public void UpdateTaggedTitle(
        string tag,
        string title,
        string? italicSuffix = null,
        string? trailingSuffix = null)
    {
        if (!_titleBlocksByTag.TryGetValue(tag, out var block)) return;
        block.Inlines.Clear();
        block.Inlines.Add(new Run(title));
        if (!string.IsNullOrEmpty(italicSuffix))
            block.Inlines.Add(new Run(italicSuffix) { FontStyle = FontStyles.Italic });
        // e.g. "（隠しファイルを含む）" after capacity — not italic (Mac).
        if (!string.IsNullOrEmpty(trailingSuffix))
            block.Inlines.Add(new Run(trailingSuffix));
    }

    public void UpdateTaggedTrailing(string tag, string text)
    {
        if (_trailingBlocksByTag.TryGetValue(tag, out var block))
            block.Text = text;
    }

    public void UpdateIcon(string path, ImageSource icon)
    {
        if (_iconImagesByPath.TryGetValue(path, out var image))
            image.Source = icon;
    }

    public void ShowAbove(Window anchor) =>
        ShowAboveElement(anchor, new System.Windows.Point(0, 0));

    public void ShowAbove(FrameworkElement anchor) =>
        ShowAboveElement(anchor, new System.Windows.Point(0, 0));

    private void ShowAboveElement(Visual anchor, System.Windows.Point elementPoint)
    {
        _isRoot = true;
        ShowActivated = true;

        var pixelTopLeft = anchor is UIElement ui
            ? ui.PointToScreen(elementPoint)
            : new System.Windows.Point(0, 0);
        var fromDevice = PresentationSource.FromVisual(anchor)?.CompositionTarget?.TransformFromDevice
                         ?? Matrix.Identity;
        var dipTopLeft = fromDevice.Transform(pixelTopLeft);

        // Capture Alt before Activate — focus changes can disturb WPF key state.
        AltKeyState.Capture();

        MenuSession.Register(this);

        Left = dipTopLeft.X;
        _aboveAnchorTop = dipTopLeft.Y;
        _besideParentRowTop = null;
        _besideParent = null;
        _openTransitionPlayed = false;
        Show();
        FitAbove(dipTopLeft.Y);
        PlayOpenSlide(OpenSlideKind.Above);
        Activate();
        Focus();

        Dispatcher.BeginInvoke(() =>
        {
            FitAbove(dipTopLeft.Y);
            Activate();
        }, DispatcherPriority.ApplicationIdle);
    }

    public void ShowBeside(VolumeMenuWindow parent, Border row)
    {
        _isRoot = false;
        // Keep parent activation — avoids closing the chain when a child opens.
        ShowActivated = false;

        var fromDevice = PresentationSource.FromVisual(parent)?.CompositionTarget?.TransformFromDevice
                         ?? Matrix.Identity;
        var rowLeftDip = fromDevice.Transform(row.PointToScreen(new System.Windows.Point(0, 0)));

        AltKeyState.Capture();

        MenuSession.Register(this);

        // Measure first (off-screen) so we know width for left/right flip.
        Left = -10000;
        Top = -10000;
        _openTransitionPlayed = false;
        Show();

        _besideParent = parent;
        _besideParentRowTop = rowLeftDip.Y;
        _aboveAnchorTop = null;

        var slide = FitBeside();
        PlayOpenSlide(slide);

        parent.Activate();

        Dispatcher.BeginInvoke(() => FitBeside(), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Start-button / Win+X style: translate only (no opacity fade).
    /// Skipped when system animations are off.
    /// </summary>
    private void PlayOpenSlide(OpenSlideKind kind)
    {
        if (_openTransitionPlayed) return;
        _openTransitionPlayed = true;

        if (!SystemParameters.ClientAreaAnimation && !SystemParameters.MenuAnimation)
        {
            Chrome.RenderTransform = null;
            return;
        }

        double fromX = 0;
        double fromY = 0;
        switch (kind)
        {
            case OpenSlideKind.Above:
                // Rise up from the AppBar (same feel as taskbar menus).
                fromY = OpenSlideDip;
                break;
            case OpenSlideKind.BesideRight:
                fromX = -OpenSlideDip;
                break;
            case OpenSlideKind.BesideLeft:
                fromX = OpenSlideDip;
                break;
        }

        var transform = new TranslateTransform(fromX, fromY);
        Chrome.RenderTransform = transform;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var ax = new DoubleAnimation(fromX, 0, OpenSlideDuration) { EasingFunction = ease };
        var ay = new DoubleAnimation(fromY, 0, OpenSlideDuration) { EasingFunction = ease };
        transform.BeginAnimation(TranslateTransform.XProperty, ax);
        transform.BeginAnimation(TranslateTransform.YProperty, ay);
    }

    /// <summary>Wire directory listing so Alt toggle while open can relist (Mac Option).</summary>
    public void BindDirectoryRelist(bool includeHidden, Action relist)
    {
        _listedWithHidden = includeHidden;
        _relistDirectory = relist;
    }

    /// <summary>Returns true if this window started a relist.</summary>
    public bool PollAltForRelist()
    {
        if (_relistDirectory is null || _closing) return false;
        var alt = AltKeyState.IsDown();
        if (alt == _listedWithHidden) return false;
        _listedWithHidden = alt;
        AltKeyState.Capture(alt);
        _relistDirectory();
        return true;
    }

    public void ForceClose()
    {
        _closing = true;
        CancelSubmenuTimer();
        CancelLoad();
        try { Close(); } catch { /* ignore */ }
    }

    private void CancelLoad()
    {
        try { _loadCts?.Cancel(); } catch { /* ignore */ }
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private void RefitInWorkArea()
    {
        if (_aboveAnchorTop is double anchor)
            FitAbove(anchor);
        else if (_besideParent is not null)
            FitBeside();
        else
        {
            ApplyMaxHeightForWorkArea();
            UpdateLayout();
            ClampToWorkArea();
        }
    }

    /// <summary>Hard cap so the window (incl. chrome margin) never exceeds the work area height.</summary>
    private void ApplyMaxHeightForWorkArea()
    {
        var work = SystemParameters.WorkArea;
        var maxWindow = Math.Max(120, work.Height - EdgePad * 2);
        ItemsScroll.MaxHeight = Math.Max(80, maxWindow - ChromeOverhead());
    }

    private double ChromeOverhead()
    {
        // Margin + border + padding outside the scroll viewport.
        const double borderAndPadding = 2 + 8; // BorderThickness 1*2 + Padding approx
        return ChromeMargin * 2 + borderAndPadding;
    }

    private void FitAbove(double anchorTopDip)
    {
        _aboveAnchorTop = anchorTopDip;
        var work = SystemParameters.WorkArea;
        var maxWindow = Math.Max(120, anchorTopDip - work.Top - EdgePad - 4);
        ItemsScroll.MaxHeight = Math.Max(80, maxWindow - ChromeOverhead());
        UpdateLayout();

        Left = Math.Clamp(Left, work.Left + EdgePad, Math.Max(work.Left + EdgePad, work.Right - EdgePad - ActualWidth));
        Top = Math.Max(work.Top + EdgePad, anchorTopDip - ActualHeight - 4);
        ClampToWorkArea();
    }

    private OpenSlideKind FitBeside()
    {
        if (_besideParent is null) return OpenSlideKind.BesideRight;

        // Full work-area height (Mac-style). Don't clip to space below the focused row.
        ApplyMaxHeightForWorkArea();
        UpdateLayout();

        var parent = _besideParent;
        var fromDevice = PresentationSource.FromVisual(parent)?.CompositionTarget?.TransformFromDevice
                         ?? Matrix.Identity;
        // Use parent Chrome edges (includes scrollbar), not the focused row — avoids scrollbar-width overlap.
        var parentLeft = fromDevice.Transform(parent.Chrome.PointToScreen(new System.Windows.Point(0, 0))).X;
        var parentRight = fromDevice.Transform(
            parent.Chrome.PointToScreen(new System.Windows.Point(parent.Chrome.ActualWidth, 0))).X;

        var work = SystemParameters.WorkArea;
        const double gap = 2;
        var placeRight = parentRight + gap - ChromeMargin;
        var placeLeft = parentLeft - gap + ChromeMargin - ActualWidth;
        OpenSlideKind slide;
        if (placeRight + ActualWidth <= work.Right - EdgePad)
        {
            Left = placeRight;
            slide = OpenSlideKind.BesideRight;
        }
        else if (placeLeft >= work.Left + EdgePad)
        {
            Left = placeLeft;
            slide = OpenSlideKind.BesideLeft;
        }
        else
        {
            Left = Math.Clamp(placeRight, work.Left + EdgePad, work.Right - EdgePad - ActualWidth);
            slide = OpenSlideKind.BesideRight;
        }

        // Align the first content row (not summary / favorites) with the parent item.
        // Header rows sit above that. Bottom-aligning to the screen skips this rule.
        var alignOffset = MeasureAlignOffsetFromWindowTop();
        var parentRowTop = _besideParentRowTop ?? (work.Top + EdgePad);
        Top = parentRowTop - alignOffset;
        if (Top + ActualHeight > work.Bottom - EdgePad)
            Top = work.Bottom - EdgePad - ActualHeight;
        if (Top < work.Top + EdgePad)
            Top = work.Top + EdgePad;

        ClampToWorkArea();
        return slide;
    }

    /// <summary>Distance from this window's top to the row that should sit beside the parent item.</summary>
    private double MeasureAlignOffsetFromWindowTop()
    {
        UpdateLayout();
        if (_alignAnchorRow is not null)
            return _alignAnchorRow.TranslatePoint(new System.Windows.Point(0, 0), this).Y;

        // Loading / no anchor yet — line up the panel top with the parent row.
        return ChromeMargin;
    }

    private void ClampToWorkArea()
    {
        var work = SystemParameters.WorkArea;
        if (Left + ActualWidth > work.Right - EdgePad)
            Left = Math.Max(work.Left + EdgePad, work.Right - EdgePad - ActualWidth);
        if (Left < work.Left + EdgePad)
            Left = work.Left + EdgePad;
        if (Top + ActualHeight > work.Bottom - EdgePad)
            Top = Math.Max(work.Top + EdgePad, work.Bottom - EdgePad - ActualHeight);
        if (Top < work.Top + EdgePad)
            Top = work.Top + EdgePad;
    }

    private SolidColorBrush HoverBrush
    {
        get
        {
            if (_hoverBrush is null)
            {
                var (_, _, _, _, hover) = AppTheme.MenuColors();
                _hoverBrush = new SolidColorBrush(hover);
                _hoverBrush.Freeze();
            }
            return _hoverBrush;
        }
    }

    private Border CreateRow(MenuItemSpec spec)
    {
        var (_, _, fg, fgMuted, _) = AppTheme.MenuColors();
        var iconBrush = new SolidColorBrush(fg);
        iconBrush.Freeze();

        var grid = new Grid
        {
            Height = RowHeight,
            Margin = new Thickness(2, 0, 2, 0)
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(spec.HasSubmenu ? 18 : 6) });

        var titleMuted = !spec.Enabled || spec.Muted;
        // Always paint Segoe glyphs on the UI thread with the menu theme brush.
        var iconSource = spec.Glyph != AppGlyph.None
            ? MenuIcons.Create(spec.Glyph, iconBrush, IconSize)
            : spec.Icon;
        if (iconSource is not null || !string.IsNullOrEmpty(spec.IconPath))
        {
            var image = new WpfImage
            {
                Source = iconSource,
                Width = IconSize,
                Height = IconSize,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = titleMuted ? 0.45 : 1,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Grid.SetColumn(image, 0);
            grid.Children.Add(image);
            if (!string.IsNullOrEmpty(spec.IconPath))
                _iconImagesByPath[spec.IconPath] = image;
        }

        var titleBlock = new TextBlock
        {
            Text = spec.Title,
            FontSize = FontSizeDip,
            Foreground = new SolidColorBrush(titleMuted ? fgMuted : fg),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(titleBlock, 1);
        grid.Children.Add(titleBlock);
        if (!string.IsNullOrEmpty(spec.Tag))
            _titleBlocksByTag[spec.Tag] = titleBlock;

        if (!string.IsNullOrEmpty(spec.Trailing) || !string.IsNullOrEmpty(spec.TrailingTag))
        {
            var freeBlock = new TextBlock
            {
                Text = spec.Trailing ?? "",
                FontSize = FontSizeDip,
                FontStyle = spec.TrailingItalic ? FontStyles.Italic : FontStyles.Normal,
                Foreground = new SolidColorBrush(fgMuted),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(6, 0, 2, 0)
            };
            Grid.SetColumn(freeBlock, 2);
            grid.Children.Add(freeBlock);
            if (!string.IsNullOrEmpty(spec.TrailingTag))
                _trailingBlocksByTag[spec.TrailingTag] = freeBlock;
        }

        if (spec.HasSubmenu)
        {
            var chevron = new WpfImage
            {
                Source = MenuIcons.Chevron(iconBrush, 10),
                Width = 10,
                Height = 10,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.7
            };
            Grid.SetColumn(chevron, 3);
            grid.Children.Add(chevron);
        }

        var border = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(3),
            Background = WpfBrushes.Transparent,
            Cursor = spec.Enabled || spec.HasSubmenu ? WpfCursors.Hand : WpfCursors.Arrow,
            Tag = spec
        };
        if (spec.AlignBesideParent && _alignAnchorRow is null)
            _alignAnchorRow = border;

        // Gray (Enabled=false) rows can still open a favorites-only submenu, like Mac.
        if (spec.Enabled || (spec.HasSubmenu && spec.PopulateSubmenu is not null))
        {
            border.MouseEnter += (_, _) =>
            {
                border.Background = HoverBrush;
                AltKeyState.NotePointerGesture();

                if (spec.HasSubmenu && spec.PopulateSubmenu is not null)
                    ScheduleSubmenu(border, spec);
                else
                    ScheduleCloseChild();
            };
            border.MouseLeave += (_, _) =>
            {
                // Leaving a briefly-hovered neighbor cancels a pending switch (diagonal to submenu).
                if (_pendingSubmenuRow == border || _pendingCloseChild)
                    CancelSubmenuTimer();

                // Parent with an open submenu keeps focus background so the chain stays clear.
                if (_openSubmenuRow == border)
                    return;
                border.Background = WpfBrushes.Transparent;
            };
            border.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (spec.Enabled && spec.OnClick is not null)
                {
                    var click = spec.OnClick;
                    MenuSession.CloseAll();
                    click();
                    return;
                }
                if (spec.HasSubmenu && spec.PopulateSubmenu is not null)
                {
                    CancelSubmenuTimer();
                    AltKeyState.NotePointerGesture();
                    OpenSubmenuNow(border, spec);
                }
            };
        }

        return border;
    }

    private void ScheduleSubmenu(Border row, MenuItemSpec spec)
    {
        if (_openSubmenuRow == row && _child is { IsVisible: true })
            return;

        CancelSubmenuTimer();
        _pendingSubmenuRow = row;
        _pendingSubmenuSpec = spec;
        _pendingCloseChild = false;
        // Longer delay when switching away from an already-open submenu (diagonal travel).
        var dwell = _child is { IsVisible: true } ? SubmenuSwitchDwellMs : SubmenuOpenDwellMs;
        _submenuTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(dwell) };
        _submenuTimer.Tick += (_, _) =>
        {
            var pendingRow = _pendingSubmenuRow;
            var pendingSpec = _pendingSubmenuSpec;
            CancelSubmenuTimer();
            // Still over the row? (avoids switching after a diagonal skim toward the open child)
            if (pendingRow is not null && pendingSpec is not null && pendingRow.IsMouseOver)
                OpenSubmenuNow(pendingRow, pendingSpec);
        };
        _submenuTimer.Start();
    }

    private void ScheduleCloseChild()
    {
        if (_child is not { IsVisible: true })
        {
            CloseChild();
            return;
        }

        CancelSubmenuTimer();
        _pendingCloseChild = true;
        _submenuTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SubmenuSwitchDwellMs) };
        _submenuTimer.Tick += (_, _) =>
        {
            var shouldClose = _pendingCloseChild;
            CancelSubmenuTimer();
            if (!shouldClose) return;
            // Diagonal move landed on the open child / parent — keep the submenu.
            if (_child is { IsMouseOver: true }) return;
            if (_openSubmenuRow is { IsMouseOver: true }) return;
            CloseChild();
        };
        _submenuTimer.Start();
    }

    private void OpenSubmenuNow(Border row, MenuItemSpec spec)
    {
        if (spec.PopulateSubmenu is null) return;
        if (_openSubmenuRow == row && _child is { IsVisible: true })
            return;

        var previousParent = _openSubmenuRow;
        // Close only the child window; keep highlight handling here.
        CancelSubmenuTimer();
        MenuSession.CloseChildrenOf(this);
        _child = null;
        _openSubmenuRow = null;
        if (previousParent is not null && previousParent != row)
            previousParent.Background = WpfBrushes.Transparent;

        // Before ShowBeside Activate — keep Alt for hidden listing.
        AltKeyState.Capture();

        var child = new VolumeMenuWindow();
        _child = child;
        _openSubmenuRow = row;
        row.Background = HoverBrush;
        // Show loading placeholder immediately so the popup appears without waiting.
        child.SetItems([new MenuItemSpec { Title = L.Get("menu.directory_loading"), Enabled = false }]);
        child.ShowBeside(this, row);
        if (AltKeyState.IsDown())
            AltKeyState.Capture(true);
        spec.PopulateSubmenu(child);
    }

    private void CloseChild()
    {
        CancelSubmenuTimer();
        var parentRow = _openSubmenuRow;
        MenuSession.CloseChildrenOf(this);
        _child = null;
        _openSubmenuRow = null;
        if (parentRow is not null && !parentRow.IsMouseOver)
            parentRow.Background = WpfBrushes.Transparent;
    }

    private void CancelSubmenuTimer()
    {
        _submenuTimer?.Stop();
        _submenuTimer = null;
        _pendingSubmenuRow = null;
        _pendingSubmenuSpec = null;
        _pendingCloseChild = false;
    }

    private Border CreateSeparator()
    {
        var (_, border, _, _, _) = AppTheme.MenuColors();
        return new Border
        {
            Height = 1,
            Margin = new Thickness(10, 4, 10, 4),
            Background = new SolidColorBrush(border)
        };
    }

    /// <summary>
    /// Zero-height anchor so header actions sit above the parent row without a blank strip.
    /// </summary>
    private Border CreateAlignSpacer(MenuItemSpec spec)
    {
        var border = new Border
        {
            Height = 0,
            Margin = new Thickness(0),
            Background = WpfBrushes.Transparent,
            IsHitTestVisible = false,
            Tag = spec
        };
        if (spec.AlignBesideParent && _alignAnchorRow is null)
            _alignAnchorRow = border;
        return border;
    }

    private void ApplyTheme()
    {
        _hoverBrush = null;
        var (bg, border, _, _, _) = AppTheme.MenuColors();
        Chrome.Background = new SolidColorBrush(bg);
        Chrome.BorderBrush = new SolidColorBrush(border);
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (_isRoot) MenuSession.CloseAll();
            else MenuSession.CloseFrom(this);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
