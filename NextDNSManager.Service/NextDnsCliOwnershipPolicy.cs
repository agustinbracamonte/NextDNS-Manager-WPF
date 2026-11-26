using System.Text.RegularExpressions;

namespace NextDNSManager.Service;

/// <summary>
/// Keeps the packaged CLI source separate from the copy registered as the
/// NextDNS Windows service. The upstream CLI's Windows installer copies its
/// running executable to Program Files\nextdns\nextdns.exe and registers it
/// with the `run` argument.
/// </summary>
internal static class NextDnsCliOwnershipPolicy
{
    private const string InstallDirectory = @"C:\Program Files\nextdns";

    internal static string SourceExecutable => BrokerPolicy.CliExecutable;
    internal static string InstalledExecutable => Path.Combine(InstallDirectory, "nextdns.exe");
    internal static string ExpectedServiceImage => $"\"{InstalledExecutable}\" run";

    internal static bool IsOwnedServiceImage(string image)
    {
        string path = Regex.Escape(InstalledExecutable);
        string quotedOrUnquotedPath = "(?:\"" + path + "\"|" + path + ")";
        return Regex.IsMatch(image, "\\A\\s*" + quotedOrUnquotedPath + "\\s+run\\s*\\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
