namespace AnovaDesk;

/// <summary>
/// Isolation hooks for automated UI tests: a named profile redirects the credential and
/// settings file, and offline mode suppresses the network connection. Neither flag changes
/// behaviour when absent, so normal runs are unaffected.
/// </summary>
internal static class AppRuntime
{
    public static string? Profile { get; private set; }

    public static bool Offline { get; private set; }

    public static string CredentialTarget =>
        Profile is null ? "AnovaDesk/PersonalAccessToken" : $"AnovaDesk/PersonalAccessToken/{Profile}";

    public static string SettingsFileName =>
        Profile is null ? "settings.json" : $"settings.{Profile}.json";

    public static void Configure(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--offline", StringComparison.OrdinalIgnoreCase))
            {
                Offline = true;
            }
            else if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                Profile = args[i + 1];
                i++;
            }
        }
    }
}
