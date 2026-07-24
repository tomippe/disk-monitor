using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiskMonitor.Helpers;
using DiskMonitor.Services;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfColor = System.Windows.Media.Color;
using WpfCursors = System.Windows.Input.Cursors;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;

namespace DiskMonitor;

public partial class AboutWindow : Window
{
    private static AboutWindow? _open;

    public AboutWindow()
    {
        InitializeComponent();
        ApplyContent();
        ApplyTheme();
    }

    public static void ShowAbout()
    {
        if (_open is { IsVisible: true })
        {
            _open.Activate();
            return;
        }

        var win = new AboutWindow();
        _open = win;
        win.Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, win))
                _open = null;
        };

        var owner = OwnerWindow();
        if (owner is not null)
        {
            win.Owner = owner;
            win.ShowDialog();
        }
        else
        {
            win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            win.ShowDialog();
        }
    }

    private void ApplyContent()
    {
        Title = L.Get("app.name");
        AppNameText.Text = L.Get("app.name");

        var version = MenuActions.AppVersion();
        var detail = string.Format(L.Get("about.format"), version);
        var copyright = L.Get("about.copyright");
        if (!string.IsNullOrWhiteSpace(copyright))
            detail += "\n\n" + copyright;
        DetailText.Text = detail;

        LogoButton.ToolTip = L.Get("about.open_intro");
        IntroButton.Content = L.Get("about.open_website");
        OkButton.Content = L.Get("button.ok");

        var logo = LoadAppsLogo();
        if (logo is not null)
        {
            LogoImage.Source = logo;
            LogoButton.Visibility = Visibility.Visible;
        }
        else
        {
            LogoButton.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyTheme()
    {
        var dark = AppTheme.IsDark();
        var bg = dark
            ? WpfColor.FromRgb(0x2C, 0x2C, 0x2C)
            : WpfColor.FromRgb(0xEC, 0xEC, 0xEC);
        var fg = dark
            ? WpfColor.FromRgb(0xF0, 0xF0, 0xF0)
            : WpfColor.FromRgb(0x1D, 0x1D, 0x1F);
        var btnBgTop = dark ? WpfColor.FromRgb(0x3A, 0x3A, 0x3A) : WpfColor.FromRgb(0xFF, 0xFF, 0xFF);
        var btnBgBottom = dark ? WpfColor.FromRgb(0x32, 0x32, 0x32) : WpfColor.FromRgb(0xF2, 0xF2, 0xF2);
        var btnBorder = dark
            ? WpfColor.FromArgb(0x40, 0xFF, 0xFF, 0xFF)
            : WpfColor.FromArgb(0x2E, 0x00, 0x00, 0x00);
        var primaryTop = WpfColor.FromRgb(0x4D, 0xA3, 0xFF);
        var primaryBottom = WpfColor.FromRgb(0x0A, 0x84, 0xFF);

        Background = new SolidColorBrush(bg);
        RootBorder.Background = WpfBrushes.Transparent;
        AppNameText.Foreground = new SolidColorBrush(fg);
        DetailText.Foreground = new SolidColorBrush(fg);

        StyleSecondaryButton(IntroButton, btnBgTop, btnBgBottom, btnBorder, fg);
        StylePrimaryButton(OkButton, primaryTop, primaryBottom);
    }

    private static void StyleSecondaryButton(
        WpfButton button, WpfColor top, WpfColor bottom, WpfColor border, WpfColor fg)
    {
        button.Foreground = new SolidColorBrush(fg);
        button.BorderBrush = new SolidColorBrush(border);
        button.BorderThickness = new Thickness(1);
        button.Background = new LinearGradientBrush(top, bottom, 90);
        button.Cursor = WpfCursors.Arrow;
        button.HorizontalContentAlignment = WpfHorizontalAlignment.Center;
    }

    private static void StylePrimaryButton(WpfButton button, WpfColor top, WpfColor bottom)
    {
        button.Foreground = WpfBrushes.White;
        button.BorderBrush = new SolidColorBrush(bottom);
        button.BorderThickness = new Thickness(1);
        button.Background = new LinearGradientBrush(top, bottom, 90);
        button.Cursor = WpfCursors.Arrow;
        button.HorizontalContentAlignment = WpfHorizontalAlignment.Center;
        button.FontWeight = FontWeights.SemiBold;
    }

    private static BitmapImage? LoadAppsLogo()
    {
        foreach (var path in AppsLogoCandidatePaths())
        {
            if (!File.Exists(path)) continue;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch
            {
                // try next
            }
        }

        return null;
    }

    private static IEnumerable<string> AppsLogoCandidatePaths()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "Assets", "AppsLogo.png");
        yield return Path.Combine(baseDir, "AppsLogo.png");

        // Dev / shared drive fallback to build-common
        var dir = new DirectoryInfo(baseDir);
        for (var i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            yield return Path.Combine(dir.FullName, "build-common", "Resources", "AppsLogo.png");
            yield return Path.Combine(dir.FullName, "..", "build-common", "Resources", "AppsLogo.png");
        }
    }

    private static Window? OwnerWindow()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return null;
        if (app.MainWindow is { IsVisible: true } main)
            return main;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w is not AboutWindow);
    }

    private void LogoButton_Click(object sender, RoutedEventArgs e)
        => MenuActions.OpenUrl(MenuActions.IntroUrl);

    private void IntroButton_Click(object sender, RoutedEventArgs e)
        => MenuActions.OpenUrl(MenuActions.IntroUrl);

    private void OkButton_Click(object sender, RoutedEventArgs e)
        => Close();
}
