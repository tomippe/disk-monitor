using System.Windows;
using System.Windows.Interop;
using WinForms = System.Windows.Forms;

namespace DiskMonitor.Helpers;

/// <summary>
/// Windows 11 native Task Dialog (comctl v6) instead of classic MessageBox.
/// </summary>
internal static class AppDialog
{
    public static void Information(string caption, string message)
        => Show(caption, message, WinForms.TaskDialogIcon.Information, okOnly: true);

    public static void Warning(string caption, string message)
        => Show(caption, message, WinForms.TaskDialogIcon.Warning, okOnly: true);

    /// <returns>true when the user chose Yes.</returns>
    public static bool ConfirmYesNo(string caption, string message)
    {
        SplitMessage(message, out var heading, out var text);
        var yes = WinForms.TaskDialogButton.Yes;
        var no = WinForms.TaskDialogButton.No;
        var page = new WinForms.TaskDialogPage
        {
            Caption = caption,
            Heading = heading,
            Text = text,
            Icon = WinForms.TaskDialogIcon.Information,
            AllowCancel = true,
            DefaultButton = yes,
            Buttons = { yes, no }
        };
        return ShowPage(page) == yes;
    }

    private static void Show(string caption, string message, WinForms.TaskDialogIcon icon, bool okOnly)
    {
        SplitMessage(message, out var heading, out var text);
        var page = new WinForms.TaskDialogPage
        {
            Caption = caption,
            Heading = heading,
            Text = text,
            Icon = icon,
            AllowCancel = true,
            Buttons = { WinForms.TaskDialogButton.OK }
        };
        ShowPage(page);
    }

    private static WinForms.TaskDialogButton ShowPage(WinForms.TaskDialogPage page)
    {
        var owner = OwnerHandle();
        if (owner != IntPtr.Zero)
            return WinForms.TaskDialog.ShowDialog(owner, page);
        return WinForms.TaskDialog.ShowDialog(page);
    }

    private static IntPtr OwnerHandle()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher.CheckAccess() == false)
            {
                return app.Dispatcher.Invoke(OwnerHandle);
            }

            Window? w = null;
            if (app?.MainWindow is { IsVisible: true } main)
                w = main;
            else if (app is not null)
                w = app.Windows.OfType<Window>().FirstOrDefault(x => x.IsVisible);

            if (w is null) return IntPtr.Zero;
            return new WindowInteropHelper(w).Handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>First paragraph → Heading; remainder → Text (Win11 Task Dialog layout).</summary>
    private static void SplitMessage(string message, out string heading, out string? text)
    {
        message = (message ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (string.IsNullOrEmpty(message))
        {
            heading = string.Empty;
            text = null;
            return;
        }

        var nl = message.IndexOf('\n');
        if (nl < 0)
        {
            heading = message;
            text = null;
            return;
        }

        heading = message[..nl].TrimEnd();
        text = message[(nl + 1)..].Trim();
        if (string.IsNullOrEmpty(text))
            text = null;
    }
}
