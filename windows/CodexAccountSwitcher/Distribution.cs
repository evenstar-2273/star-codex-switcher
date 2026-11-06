namespace CodexAccountSwitcher;

public static class Distribution
{
    public const string Repository = "evenstar-2273/star-codex-switcher";
    public const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
#if INSTALLED_EDITION
    public const string PackageType = "installer";
    public const string FeedUrl = ReleasesUrl + "/latest/download/update-installed.json";
#else
    public const string PackageType = "portable";
    public const string FeedUrl = ReleasesUrl + "/latest/download/update.json";
#endif
    public const string DisplayName = "Star Codex Switcher";
    public const string ExecutableName = "StarCodexSwitcher.exe";
    public const string LegacyExecutableName = "CodexAccountSwitcher.exe";
    public const string LegacyDisplayName = "Codex Account Switcher";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexAccountSwitcher";
}
