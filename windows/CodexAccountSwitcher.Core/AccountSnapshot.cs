using System.Text.Json;

namespace CodexAccountSwitcher.Core;

// Wire DTOs only. Account policy and storage live in Sources/SwitcherCore.
public sealed record Profile(Guid Id, string DisplayName, string? Email);
public sealed record QuotaWindow(long Seconds, int RemainingPercent, DateTimeOffset? ResetsAt);
public sealed record Usage(int? RemainingPercent, DateTimeOffset? ResetsAt,
    int? FiveHourRemainingPercent, DateTimeOffset? FiveHourResetsAt, int? ResetCreditsRemaining = null,
    DateTimeOffset? ResetCreditsExpireAt = null, string? PlanType = null, QuotaWindow[]? Windows = null, BankedCredit[]? ResetCards = null)
{
    public IEnumerable<QuotaWindow> DisplayWindows()
    {
        var legacy = new List<QuotaWindow>(2);
        if (FiveHourRemainingPercent is int shortQuota) legacy.Add(new(18000, shortQuota, FiveHourResetsAt));
        if (RemainingPercent is int weekly) legacy.Add(new(604800, weekly, ResetsAt));
        var windows = Windows ?? legacy.ToArray();
        // Weekly exhaustion blocks short-window availability, without changing
        // the raw quota, window presence or either window's reset timestamp.
        var weeklyBlocksShort = windows.Any(window => window.Seconds == 604800 && window.RemainingPercent == 0);
        return windows.Select(window => weeklyBlocksShort && window.Seconds == 18000
            ? window with { RemainingPercent = 0 } : window);
    }
}
public sealed record SubscriptionSnapshot(DateTimeOffset ActiveUntil, DateTimeOffset LastChecked, string Source = "login_snapshot", bool IsVerified = false);
public sealed record AccountRow(Profile Profile, string Initials, Usage? Usage, string? UsageError,
    string UsageStatus = "loaded", SubscriptionSnapshot? Subscription = null, string? PlanType = null,
    string SubscriptionStatus = "unverified", string? SubscriptionError = null);
public sealed record Preferences(string Language = "system", bool ShowsMenuBarPercentage = true, bool ShowsFiveHourUsage = false,
    string StatusBarUsageWindow = "fiveHour", bool AutoResetEnabled = false, int QuotaRefreshIntervalMinutes = 5);
public sealed record AccountSnapshot(AccountRow[] Accounts, Guid? ActiveAccountID, Preferences Settings,
    bool IsMutating, bool IsAddingAccount, bool ActiveIdentityConfirmed, string? Error, Dictionary<string, string> Strings,
    long UsageRefreshRevision = 0)
{
    public static AccountSnapshot Empty { get; } = new([], null, new(), false, false, true, null, []);
    public string Text(string key) => Strings.GetValueOrDefault(key, key);
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNameCaseInsensitive = true };
}

public interface IAccountClient
{
    AccountSnapshot State { get; }
    event Action? Changed;
    Task CommandAsync(string command, Guid? accountID = null, bool? value = null, string? language = null);
}
