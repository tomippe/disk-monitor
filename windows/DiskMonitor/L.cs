using System.Globalization;
using System.Resources;

namespace DiskMonitor;

public static class L
{
    private static readonly ResourceManager Rm =
        new("DiskMonitor.Resources.Strings", typeof(L).Assembly);

    public static string Get(string key) =>
        Rm.GetString(key, CultureInfo.CurrentUICulture) ?? key;
}
