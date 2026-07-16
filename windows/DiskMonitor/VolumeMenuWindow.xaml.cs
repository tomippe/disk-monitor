using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DiskMonitor.Helpers;
using Microsoft.Win32;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfImage = System.Windows.Controls.Image;

namespace DiskMonitor;

public partial class VolumeMenuWindow : Window
{
    private const double IconSize = 18;
    private const double FontSizeDip = 13;
    private const double RowHeight = 30;
    private const double SubmenuDwellMs = 160;
    private const double EdgePad = 8;
    private const double ChromeMargin = 10;

    private bool _closing;
    private bool _canCloseOnDeactivate;
    private bool _isRoot;
    private DispatcherTimer? _submenuTimer;
    private Border? _pendingSubmenuRow;
    private MenuItemSpec? _pendingSubmenuSpec;
    private Border? _openSubmenuRow;
    private VolumeMenuWindow? _child;
    private int _loadGeneration;
    private CancellationTokenSource? _loadCts;
    private readonly Dictionary<string, TextBlock> _titleBlocksByTag = new(StringComparer.Ordinal);

    public VolumeMenuWindow()
    {
        InitializeComponent();
        ApplyTheme();
        Closed += (_, _) =>
        {
            CancelSubmenuTimer();
            CancelLoad();
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
        foreach (var item in items)
        {
            if (item.IsSeparator)
                ItemsHost.Children.Add(CreateSeparator());
            else
                ItemsHost.Children.Add(CreateRow(item));
        }
        UpdateLayout();
        ApplyMaxHeight();
    }

    public void UpdateTaggedTitle(string tag, string title)
    {
        if (_titleBlocksByTag.TryGetValue(tag, out var block))
            block.Text = title;
    }

    public void ShowAbove(Window anchor)
    {
        _isRoot = true;
        ShowActivated = true;

        var pixelTopLeft = anchor.PointToScreen(new System.Windows.Point(0, 0));
        var fromDevice = PresentationSource.FromVisual(anchor)?.CompositionTarget?.TransformFromDevice
                         ?? Matrix.Identity;
        var dipTopLeft = fromDevice.Transform(pixelTopLeft);

        _canCloseOnDeactivate = false;
        MenuSession.Register(this);

        Left = dipTopLeft.X;
        Top = 0;
        Show();
        UpdateLayout();
        FitAbove(dipTopLeft.Y);
        Activate();
        Focus();

        Dispatcher.BeginInvoke(() =>
        {
            _canCloseOnDeactivate = true;
            FitAbove(dipTopLeft.Y);
            Activate();
        }, DispatcherPriority.ApplicationIdle);
    }

    public void ShowBeside(VolumeMenuWindow parent, Border row)
    {
        _isRoot = false;
        // Keep parent activation — avoids closing the chain when a child opens.
        ShowActivated = false;

        var pixel = row.PointToScreen(new System.Windows.Point(row.ActualWidth + 2, 0));
        var fromDevice = PresentationSource.FromVisual(parent)?.CompositionTarget?.TransformFromDevice
                         ?? Matrix.Identity;
        var dip = fromDevice.Transform(pixel);

        _canCloseOnDeactivate = false;
        MenuSession.Register(this);

        Left = dip.X;
        Top = Math.Max(SystemParameters.WorkArea.Top + EdgePad, dip.Y - 6);
        Show();
        UpdateLayout();
        FitInWorkArea();
        parent.Activate();

        Dispatcher.BeginInvoke(() =>
        {
            _canCloseOnDeactivate = true;
            FitInWorkArea();
        }, DispatcherPriority.ApplicationIdle);
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

    private void ApplyMaxHeight()
    {
        var work = SystemParameters.WorkArea;
        var max = Math.Max(120, work.Height - EdgePad * 2 - ChromeMargin * 2);
        ItemsScroll.MaxHeight = max;
    }

    private void FitAbove(double anchorTopDip)
    {
        var work = SystemParameters.WorkArea;
        var maxH = Math.Max(120, anchorTopDip - work.Top - EdgePad - ChromeMargin * 2 - 4);
        ItemsScroll.MaxHeight = maxH;
        UpdateLayout();

        Left = Math.Clamp(Left, work.Left + EdgePad, Math.Max(work.Left + EdgePad, work.Right - EdgePad - ActualWidth));
        Top = Math.Max(work.Top + EdgePad, anchorTopDip - ActualHeight - 4);
        if (Top + ActualHeight > work.Bottom - EdgePad)
            Top = Math.Max(work.Top + EdgePad, work.Bottom - EdgePad - ActualHeight);
    }

    private void FitInWorkArea()
    {
        var work = SystemParameters.WorkArea;
        ApplyMaxHeight();
        UpdateLayout();

        if (Left + ActualWidth > work.Right - EdgePad)
            Left = Math.Max(work.Left + EdgePad, work.Right - EdgePad - ActualWidth);
        if (Left < work.Left + EdgePad)
            Left = work.Left + EdgePad;
        if (Top + ActualHeight > work.Bottom - EdgePad)
            Top = Math.Max(work.Top + EdgePad, work.Bottom - EdgePad - ActualHeight);
        if (Top < work.Top + EdgePad)
            Top = work.Top + EdgePad;
    }

    private Border CreateRow(MenuItemSpec spec)
    {
        var dark = IsDarkTheme();
        var fg = dark ? WpfColor.FromRgb(0xF3, 0xF3, 0xF3) : WpfColor.FromRgb(0x1A, 0x1A, 0x1A);
        var fgMuted = dark ? WpfColor.FromRgb(0xC0, 0xC0, 0xC0) : WpfColor.FromRgb(0x5A, 0x5A, 0x5A);
        var hover = dark ? WpfColor.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : WpfColor.FromArgb(0x14, 0x00, 0x00, 0x00);
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

        if (spec.Icon is not null)
        {
            var image = new WpfImage
            {
                Source = spec.Icon,
                Width = IconSize,
                Height = IconSize,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = spec.Enabled ? 1 : 0.45,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            Grid.SetColumn(image, 0);
            grid.Children.Add(image);
        }

        var titleBlock = new TextBlock
        {
            Text = spec.Title,
            FontSize = FontSizeDip,
            Foreground = new SolidColorBrush(spec.Enabled ? fg : fgMuted),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(titleBlock, 1);
        grid.Children.Add(titleBlock);
        if (!string.IsNullOrEmpty(spec.Tag))
            _titleBlocksByTag[spec.Tag] = titleBlock;

        if (!string.IsNullOrEmpty(spec.Trailing))
        {
            var freeBlock = new TextBlock
            {
                Text = spec.Trailing,
                FontSize = FontSizeDip,
                Foreground = new SolidColorBrush(fgMuted),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(6, 0, 2, 0)
            };
            Grid.SetColumn(freeBlock, 2);
            grid.Children.Add(freeBlock);
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
            Cursor = spec.Enabled ? WpfCursors.Hand : WpfCursors.Arrow,
            Tag = spec
        };

        if (spec.Enabled)
        {
            border.MouseEnter += (_, _) =>
            {
                border.Background = new SolidColorBrush(hover);
                if (spec.HasSubmenu && spec.PopulateSubmenu is not null)
                    ScheduleSubmenu(border, spec);
                else
                    CloseChild();
            };
            border.MouseLeave += (_, _) => border.Background = WpfBrushes.Transparent;
            border.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (spec.OnClick is not null)
                {
                    var click = spec.OnClick;
                    MenuSession.CloseAll();
                    click();
                    return;
                }
                if (spec.HasSubmenu && spec.PopulateSubmenu is not null)
                {
                    CancelSubmenuTimer();
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
        _submenuTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SubmenuDwellMs) };
        _submenuTimer.Tick += (_, _) =>
        {
            var pendingRow = _pendingSubmenuRow;
            var pendingSpec = _pendingSubmenuSpec;
            CancelSubmenuTimer();
            if (pendingRow is not null && pendingSpec is not null)
                OpenSubmenuNow(pendingRow, pendingSpec);
        };
        _submenuTimer.Start();
    }

    private void OpenSubmenuNow(Border row, MenuItemSpec spec)
    {
        if (spec.PopulateSubmenu is null) return;
        if (_openSubmenuRow == row && _child is { IsVisible: true })
            return;

        CloseChild();

        var child = new VolumeMenuWindow();
        _child = child;
        _openSubmenuRow = row;
        // Show loading placeholder immediately so the popup appears without waiting.
        child.SetItems([new MenuItemSpec { Title = L.Get("menu.directory_loading"), Enabled = false }]);
        child.ShowBeside(this, row);
        spec.PopulateSubmenu(child);
    }

    private void CloseChild()
    {
        CancelSubmenuTimer();
        MenuSession.CloseChildrenOf(this);
        _child = null;
        _openSubmenuRow = null;
    }

    private void CancelSubmenuTimer()
    {
        _submenuTimer?.Stop();
        _submenuTimer = null;
        _pendingSubmenuRow = null;
        _pendingSubmenuSpec = null;
    }

    private Border CreateSeparator()
    {
        var dark = IsDarkTheme();
        var line = dark ? WpfColor.FromRgb(0x4A, 0x4A, 0x4A) : WpfColor.FromRgb(0xE0, 0xE0, 0xE0);
        return new Border
        {
            Height = 1,
            Margin = new Thickness(10, 4, 10, 4),
            Background = new SolidColorBrush(line)
        };
    }

    private void ApplyTheme()
    {
        var dark = IsDarkTheme();
        var bg = dark ? WpfColor.FromRgb(0x2C, 0x2C, 0x2C) : WpfColor.FromRgb(0xF9, 0xF9, 0xF9);
        var border = dark ? WpfColor.FromRgb(0x45, 0x45, 0x45) : WpfColor.FromRgb(0xE5, 0xE5, 0xE5);
        Chrome.Background = new SolidColorBrush(bg);
        Chrome.BorderBrush = new SolidColorBrush(border);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_canCloseOnDeactivate) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_closing) return;
            if (MenuSession.IsPointerOverAny()) return;
            var anyActive = System.Windows.Application.Current.Windows
                .OfType<VolumeMenuWindow>()
                .Any(w => w.IsActive);
            if (anyActive) return;
            MenuSession.CloseAll();
        }, DispatcherPriority.Input);
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

    private static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("SystemUsesLightTheme");
            if (value is int i) return i == 0;
        }
        catch { /* ignore */ }
        return false;
    }
}
