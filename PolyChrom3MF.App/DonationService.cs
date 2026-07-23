using System.Diagnostics;

namespace PolyChrom3MF.App;

public static class DonationService
{
    internal const string DonationUrl = "https://www.paypal.com/donate/?business=X65TNHGN5K7QA&no_recurring=0&item_name=PolyChrom%203MF&currency_code=EUR";

    public static void Open()
    {
        if (!IsOfficialPayPalUrl(DonationUrl)) throw new InvalidOperationException("Le lien de don intégré est invalide.");
        _ = Process.Start(new ProcessStartInfo(DonationUrl) { UseShellExecute = true })
            ?? throw new InvalidOperationException("Le navigateur n’a pas pu être démarré.");
    }

    internal static bool IsOfficialPayPalUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo)) return false;
        var host = uri.IdnHost.TrimEnd('.');
        var paypalMe = host.Equals("paypal.me", StringComparison.OrdinalIgnoreCase) || host.Equals("www.paypal.me", StringComparison.OrdinalIgnoreCase);
        var officialHost = paypalMe || host.Equals("paypal.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".paypal.com", StringComparison.OrdinalIgnoreCase);
        var donationPath = uri.AbsolutePath.Equals("/donate", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath.StartsWith("/donate/", StringComparison.OrdinalIgnoreCase);
        return officialHost && (paypalMe || donationPath);
    }
}
