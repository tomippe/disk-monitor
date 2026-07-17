using System.Diagnostics;

namespace DiskMonitor.Services;

/// <summary>
/// Opens Microsoft Store review / feedback for the published app.
/// </summary>
public static class FeedbackForm
{
    /// <summary>Partner Center / Store product ID (also in repo .env as MS_STORE_PRODUCT_ID).</summary>
    public const string StoreProductId = "9P47CBVHQ797";

    public static void Open()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"ms-windows-store://review/?ProductId={StoreProductId}",
                UseShellExecute = true
            });
        }
        catch
        {
            // Fallback: public Store PDP if the protocol handler is unavailable.
            try
            {
                MenuActions.OpenUrl(
                    $"https://apps.microsoft.com/detail/{StoreProductId}");
            }
            catch
            {
                // ignore
            }
        }
    }
}
