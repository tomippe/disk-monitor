using System.Windows;
using DiskMonitor.Helpers;
using DiskMonitor.Services;
using WinForms = System.Windows.Forms;
using WinFormsApp = System.Windows.Forms.Application;

namespace DiskMonitor;

public partial class App : System.Windows.Application
{
    private AppBarWindow? _bar;
    private WinForms.NotifyIcon? _tray;
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Needed for Win11-styled Task Dialog / common controls v6.
        WinFormsApp.EnableVisualStyles();
        try { WinFormsApp.SetCompatibleTextRenderingDefault(false); }
        catch { /* already initialized */ }

        base.OnStartup(e);

        if (!TryTakeSingleInstance())
        {
            Shutdown();
            return;
        }

        AltKeyState.EnsureMessageHook();

        _bar = new AppBarWindow();
        _bar.StatusChanged += text =>
        {
            if (_tray is not null)
                _tray.Text = text.Length <= 63 ? text : text[..63];
        };
        _bar.Show();

        _tray = new WinForms.NotifyIcon
        {
            Icon = CreateTrayDriveIcon(),
            Text = L.Get("app.name"),
            Visible = true
        };
        _tray.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
                _bar?.Activate();
        };
        _tray.ContextMenuStrip = BuildTrayMenu();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        _bar?.Close();
        _bar = null;
        _mutex?.Dispose();
        _mutex = null;
        base.OnExit(e);
    }

    private WinForms.ContextMenuStrip BuildTrayMenu()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(L.Get("menu.refresh"), null, (_, _) => _bar?.RefreshStatus());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(string.Format(L.Get("menu.quit_format"), L.Get("app.name")), null, (_, _) => Shutdown());
        return menu;
    }

    private static System.Drawing.Icon CreateTrayDriveIcon()
    {
        var root = VolumeService.SystemVolume()?.RootPath ?? "C:\\";
        return ShellVolumeHelper.GetDriveIconForTray(root)
               ?? TrayIconHelper.CreateTrayIcon();
    }

    private bool TryTakeSingleInstance()
    {
        const string name = "Local\\jp.tomippe.diskmonitor.windows";
        _mutex = new Mutex(true, name, out var created);
        if (created) return true;

        AppDialog.Information(
            L.Get("app.name"),
            string.Format(L.Get("alert.already_running_format"), L.Get("app.name")));
        _mutex.Dispose();
        _mutex = null;
        return false;
    }
}
