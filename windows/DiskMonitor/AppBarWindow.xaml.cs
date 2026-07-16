using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using DiskMonitor.Helpers;
using DiskMonitor.Services;
using Microsoft.Win32;

namespace DiskMonitor;

public partial class AppBarWindow : Window
{
    private AppBarHelper? _appBar;
    private readonly DispatcherTimer _timer;
    private string _statusForCopy = "";

    public event Action<string>? StatusChanged;

    public AppBarWindow()
    {
        InitializeComponent();
        ApplyTheme();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => RefreshStatus();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
        // Preview* so clicks on icon/text reliably open the menu.
        StatusPanel.PreviewMouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowDriveMenu();
        };
        StatusPanel.PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowDriveMenu();
        };

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _appBar = new AppBarHelper(this);
        _appBar.Register();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshStatus();
        _timer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _timer.Stop();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _appBar?.Dispose();
        _appBar = null;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        Dispatcher.Invoke(ApplyTheme);
    }

    public void RefreshStatus()
    {
        var system = VolumeService.SystemVolume();
        if (system is null || !system.IsReady)
        {
            StatusText.Text = L.Get("status.unavailable");
            _statusForCopy = StatusText.Text;
            StatusChanged?.Invoke(StatusText.Text);
            FitToContent();
            return;
        }

        StatusText.Text = VolumeService.StatusLine(system);
        _statusForCopy = StatusText.Text;
        IconImage.Source = ShellVolumeHelper.GetDriveIcon(system.RootPath, 18);
        ToolTip = string.Format(
            L.Get("status.tooltip"),
            ShellVolumeHelper.GetDisplayName(system.RootPath),
            VolumeService.FormatBytes(system.AvailableBytes, concise: false));
        StatusChanged?.Invoke(StatusText.Text);
        FitToContent();
    }

    private void FitToContent()
    {
        StatusPanel.Measure(new System.Windows.Size(double.PositiveInfinity, BarHeight()));
        var contentWidth = StatusPanel.DesiredSize.Width
                           + StatusPanel.Margin.Left
                           + StatusPanel.Margin.Right;
        if (contentWidth < 48) contentWidth = 48;
        Width = contentWidth;
        _appBar?.SetContentWidth(contentWidth);
    }

    private static double BarHeight() => AppBarHelper.BarHeightDip;

    private void ShowDriveMenu()
    {
        MenuSession.CloseAll();
        var menu = new VolumeMenuWindow();
        menu.SetItems(MenuBuilder.BuildRoot(
            refresh: () => { RefreshStatus(); },
            quit: () => System.Windows.Application.Current.Shutdown(),
            statusText: () => _statusForCopy));
        menu.ShowAbove(this);
    }

    private void ApplyTheme()
    {
        var dark = IsDarkTheme();
        var bg = dark ? System.Windows.Media.Color.FromRgb(0x2C, 0x2C, 0x2C) : System.Windows.Media.Color.FromRgb(0xF2, 0xF2, 0xF2);
        var fg = dark ? System.Windows.Media.Color.FromRgb(0xF0, 0xF0, 0xF0) : System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A);
        var border = dark ? System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A) : System.Windows.Media.Color.FromRgb(0xD0, 0xD0, 0xD0);

        Background = new SolidColorBrush(bg);
        RootBorder.Background = new SolidColorBrush(bg);
        RootBorder.BorderBrush = new SolidColorBrush(border);
        StatusText.Foreground = new SolidColorBrush(fg);
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
        catch
        {
            // fall through
        }
        return false;
    }
}
