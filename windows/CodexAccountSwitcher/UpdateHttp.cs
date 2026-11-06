using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CodexAccountSwitcher;

// Release assets use redirects. Follow only this repository and GitHub's asset hosts,
// with no credentials or cookies. Authenticated account API requests never use this class.
public static class UpdateHttp
{
    public static bool IsRepositoryAsset(Uri uri)
    {
        if (!IsHttps(uri) || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var path = uri.AbsolutePath;
        var tagged = "/" + Distribution.Repository + "/releases/download/";
        var latest = "/" + Distribution.Repository + "/releases/latest/download/";
        if (path.StartsWith(tagged, StringComparison.OrdinalIgnoreCase)) {
            var parts = path[tagged.Length..].Split('/');
            return parts.Length == 2 && IsPathSegment(parts[0]) && IsPathSegment(parts[1]);
        }
        return path.StartsWith(latest, StringComparison.OrdinalIgnoreCase) && IsPathSegment(path[latest.Length..]);
    }

    private static bool IsPathSegment(string value)
    {
        var decoded = Uri.UnescapeDataString(value);
        if (decoded.Length == 0 || decoded is "." or "..") return false;
        foreach (var c in decoded) if (char.IsControl(c) || c is '/' or '\\') return false;
        return true;
    }

    private static bool IsHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    private static bool IsAllowedHop(Uri uri) => IsRepositoryAsset(uri) || IsHttps(uri) &&
        (uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    public static async Task<HttpResponseMessage> GetAsync(HttpClient http, Uri address, CancellationToken ct = default)
    {
        if (!IsRepositoryAsset(address)) throw new IOException("更新地址必须来自本项目的 GitHub Releases。");
        var current = address;
        for (var redirects = 0; redirects <= 5; redirects++) {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (redirects == 5 || location == null) throw new IOException("GitHub 更新重定向异常，请稍后重试。");
            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (!IsAllowedHop(next)) throw new IOException("更新下载跳转到了不受信任的地址，已取消。");
            current = next;
        }
        throw new IOException("GitHub 更新重定向次数过多。");
    }
}
