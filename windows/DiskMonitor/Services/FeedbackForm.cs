using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace DiskMonitor.Services;

/// <summary>
/// Windows port of build-common/TomippeFeedbackForm.swift — same Airtable forms and prefill keys.
/// </summary>
public static class FeedbackForm
{
    private const string AppName = "Disk Monitor";

    private static readonly Dictionary<string, string> FormUrls = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ja"] = "https://airtable.com/appIpAuwoVRCxzjcr/shrJql8sZUaekKD6Y",
        ["en"] = "https://airtable.com/appIpAuwoVRCxzjcr/shrWhPOixBEspTNwS",
        ["zh-Hans"] = "https://airtable.com/appIpAuwoVRCxzjcr/shrYSDAi2CVNPkCCs",
        ["zh"] = "https://airtable.com/appIpAuwoVRCxzjcr/shrYSDAi2CVNPkCCs",
    };

    public static void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = BuildUrl(),
                UseShellExecute = true
            });
        }
        catch
        {
            // ignore
        }
    }

    public static string BuildUrl()
    {
        var baseUrl = FormUrlForCurrentUi();
        var version = MenuActions.AppVersion();
        var os = WindowsDescription();
        // Match Mac TomippeFeedbackForm: prefill_App / prefill_Version / prefill_OS
        var qs = new StringBuilder();
        qs.Append("prefill_App=").Append(Uri.EscapeDataString(AppName));
        qs.Append("&prefill_Version=").Append(Uri.EscapeDataString(version));
        qs.Append("&prefill_OS=").Append(Uri.EscapeDataString(os));
        return baseUrl + "?" + qs;
    }

    private static string FormUrlForCurrentUi()
    {
        var culture = CultureInfo.CurrentUICulture;
        var name = culture.Name;
        if (FormUrls.TryGetValue(name, out var exact))
            return exact;
        if (FormUrls.TryGetValue(culture.TwoLetterISOLanguageName, out var two))
            return two;
        if (name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return FormUrls["zh-Hans"];
        return FormUrls["en"];
    }

    /// <summary>Locale-independent OS string, e.g. Windows 10.0.26200 (x64).</summary>
    private static string WindowsDescription()
    {
        var os = Environment.OSVersion;
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => RuntimeInformation.OSArchitecture.ToString()
        };
        return $"Windows {os.Version.Major}.{os.Version.Minor}.{os.Version.Build} ({arch})";
    }
}
