using System;
using System.Linq;

namespace CodexAccountSwitcher;

public sealed record PlanBadgeStyle(string Key, string Label, string Background, string Foreground, string Border);

// Presentation only: colors never change account eligibility or consume reset credits.
public static class AccountVisuals
{
    // Exact plan aliases only: account names, file names and expiry do not identify a subscription tier.
    public static string PlanKey(string? planType)
    {
        var key = new string((planType ?? "").Where(c => !char.IsWhiteSpace(c) && c is not '-' and not '_').ToArray()).ToLowerInvariant();
        return key switch {
            "free" or "chatgptfree" => "free",
            "plus" or "chatgptplus" => "plus",
            "prolite" or "pro100" or "pro5x" or "codexpro5x" or "chatgptprolite" => "pro100",
            "pro" or "pro200" or "pro20x" or "codexpro20x" or "chatgptpro" => "pro200",
            "promax" or "pro500" or "chatgptpromax" => "pro500",
            "team" or "business" or "enterprise" or "edu" or "go" => key,
            _ => "unknown"
        };
    }

    public static PlanBadgeStyle PlanBadge(string? planType, bool dark = false, bool chinese = true)
    {
        var key = PlanKey(planType);
        var label = key switch {
            "pro100" => "PRO 100", "pro200" => "PRO 200", "pro500" => "PRO 500",
            "unknown" => chinese ? "未知" : "Unknown", _ => key.ToUpperInvariant()
        };
        var colors = (key, dark) switch {
            ("plus", false) => ("#C7F5E5", "#145742", "#7ED7B5"),
            ("plus", true) => ("#A8ECCF", "#145742", "#7ED7B5"),
            ("pro100", false) => ("#DFE9F7", "#30425D", "#AABCD3"),
            ("pro100", true) => ("#CDDEF4", "#30425D", "#AABCD3"),
            ("pro200", false) => ("#F6E2B1", "#654516", "#D5B573"),
            ("pro200", true) => ("#EAD299", "#654516", "#D5B573"),
            ("pro500", false) => ("#735784", "#FFF0CF", "#B69A67"),
            ("pro500", true) => ("#634875", "#FFF0CF", "#B69A67"),
            (_, false) => ("#EEF0F3", "#596372", "#CCD2DA"),
            (_, true) => ("#D5DEE9", "#445165", "#A5B4C7")
        };
        return new(key, label, colors.Item1, colors.Item2, colors.Item3);
    }

    public static string QuotaTone(int? percent) => percent switch {
        null => "Muted", <= 20 => "Danger", <= 50 => "Warning", _ => "Success"
    };

    public static string ExpiryTone(DateTimeOffset? until, DateTimeOffset now) => until switch {
        null => "Muted",
        { } date when date - now <= TimeSpan.FromDays(3) => "Danger",
        { } date when date - now <= TimeSpan.FromDays(7) => "Warning",
        _ => "Success"
    };

    public static string ExpiryRemaining(DateTimeOffset until, DateTimeOffset now, bool chinese)
    {
        var remaining = until - now;
        if (remaining <= TimeSpan.Zero) return chinese ? "已到期" : "Expired";
        if (remaining < TimeSpan.FromDays(1)) {
            var hours = Math.Max(1, (int)Math.Ceiling(remaining.TotalHours));
            return chinese ? $"剩余 {hours} 小时" : $"{hours} hours left";
        }
        var days = (int)Math.Ceiling(remaining.TotalDays);
        if (days > 99) return chinese ? "剩余 99+ 天" : "99+ days left";
        return chinese ? $"剩余 {days} 天" : $"{days} days left";
    }

    public static string QuotaCountdown(DateTimeOffset? until, DateTimeOffset now, bool chinese)
    {
        if (until == null) return chinese ? "剩余时间未知" : "Time remaining unknown";
        var remaining = until.Value - now;
        if (remaining <= TimeSpan.Zero) return chinese ? "已到恢复时间，待刷新确认" : "Renewal time reached; refresh to confirm";
        if (remaining < TimeSpan.FromMinutes(1)) return chinese ? "剩余不足 1 分钟" : "Less than 1 minute left";
        var days = (int)remaining.TotalDays;
        var hours = remaining.Hours;
        var minutes = remaining.Minutes;
        if (days > 0) return chinese ? $"剩余 {days} 天 {hours} 小时" : $"{days}d {hours}h left";
        if (hours > 0) return chinese ? $"剩余 {hours} 小时 {minutes} 分钟" : $"{hours}h {minutes}m left";
        return chinese ? $"剩余 {minutes} 分钟" : $"{minutes}m left";
    }
}
