using System.Text.Json;

namespace CodexAccountSwitcher.Core;

internal static class CredentialRefreshPolicy
{
    internal const string Deferred = "当前账号的登录凭据由 Codex 续期，请打开 Codex 或稍后刷新。";
    internal static bool SameIdentity(LoginCredential first, LoginCredential second) => first.AccountID == second.AccountID && first.OrganizationID == second.OrganizationID;
    internal static string? RefreshToken(LoginCredential credential) {
        using var json = JsonDocument.Parse(credential.Bytes);
        return json.RootElement.GetProperty("tokens").TryGetProperty("refresh_token", out var token) ? token.GetString() : null;
    }
    internal static bool Owns(string activePath, LoginCredential credential) {
        if (!File.Exists(activePath)) return false;
        var live = LoginCredential.Read(activePath);
        var refresh = RefreshToken(credential);
        return SameIdentity(live, credential) && !string.IsNullOrWhiteSpace(refresh) && RefreshToken(live) == refresh;
    }
}
internal sealed class CredentialRenewalDeferredException() : IOException(CredentialRefreshPolicy.Deferred);
