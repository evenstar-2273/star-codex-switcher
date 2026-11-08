using System;
using System.Linq;

namespace CodexAccountSwitcher;

public sealed record PlanBadgeStyle(string Key, string Label, string Background, string Foreground, string Border, string? Highlight = null, string? Shade = null);

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
            ("free", false) => ("#EEF1F5", "#596575", "#CDD5DF", "#EEF1F5", "#EEF1F5"),
            ("free", true) => ("#293546", "#C5CFDC", "#526075", "#293546", "#293546"),
            ("plus", false) => ("#B9EBD8", "#14513F", "#72BDA2", "#D9F8EC", "#A1DEC7"),
            ("plus", true) => ("#A8E3CD", "#124936", "#70C5A6", "#D0F4E5", "#8DD5B9"),
            ("pro100", _) => ("#2854A8", "#F5F9FF", "#85B8FF", "#3B65B5", "#17366D"),
            ("pro200", _) => ("#E8C67D", "#4B3310", "#C29543", "#FFF0C2", "#D5AB5A"),
            ("pro500", _) => ("#44336B", "#FFF1CE", "#D7B875", "#604681", "#241E40"),
            (_, false) => ("#EEF0F3", "#596372", "#CCD2DA", "#EEF0F3", "#EEF0F3"),
            (_, true) => ("#293546", "#C5CFDC", "#526075", "#293546", "#293546")
        };
        return new(key, label, colors.Item1, colors.Item2, colors.Item3, colors.Item4, colors.Item5);
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

    public static string ResetExpiryTone(DateTimeOffset? until, DateTimeOffset now) => until switch {
        null => "Muted",
        { } date when date - now <= TimeSpan.FromHours(24) => "Danger",
        { } date when date - now <= TimeSpan.FromDays(3) => "Warning",
        _ => "Success"
    };

    public static string ResetExpiryLabel(DateTimeOffset? until, DateTimeOffset now, bool chinese, bool compact = false)
    {
        if (until is not { } expiry) return chinese ? "重置卡：暂无到期信息" : "Reset credits: no expiry information";
        var prefix = chinese ? (compact ? "重置卡最早到期 " : "重置卡最早到期：") : "Earliest reset credit expiry: ";
        var suffix = expiry <= now ? (chinese ? "（已到期，待刷新）" : " (Expiry reached; refresh to confirm)")
            : (chinese ? "（" : " (") + QuotaCountdown(expiry, now, chinese) + (chinese ? "）" : ")");
        return prefix + expiry.ToLocalTime().ToString("MM-dd HH:mm") + suffix;
    }

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
