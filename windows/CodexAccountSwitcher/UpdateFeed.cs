using System;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Threading;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CodexAccountSwitcher;

public sealed record UpdateRelease(Version Version, Uri DownloadUrl, string Sha256, string Notes,
    string PackageType = "portable", string? AppSha256 = null);

public static class UpdateFeed
{
    public static Uri ParseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new FormatException("请填写完整 HTTPS 地址，不要包含用户名、密码或 # 片段。");
        return uri;
    }

    public static UpdateRelease Parse(string json, string? expectedPackageType = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("platform").GetString() != "windows-x64")
            throw new FormatException("更新文件的平台必须是 windows-x64。");
        var packageType = root.GetProperty("package_type").GetString();
        if (packageType != (expectedPackageType ?? Distribution.PackageType))
            throw new FormatException("更新包类型与当前版本不匹配，请使用对应的安装版或双击版。");
        var value = root.GetProperty("version").GetString() ?? "";
        if (!Regex.IsMatch(value, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z") ||
            !Version.TryParse(value, out var version))
            throw new FormatException("更新版本号必须是四段数字，例如 0.1.27.3。");
        var download = ParseUrl(root.GetProperty("download_url").GetString() ?? "");
        if (!UpdateHttp.IsRepositoryAsset(download)) throw new FormatException("更新包必须来自本项目的 GitHub Releases。");
        var hash = root.GetProperty("sha256").GetString() ?? "";
        if (!Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z")) throw new FormatException("更新文件缺少有效的 SHA-256。");
        var notes = root.TryGetProperty("notes", out var note) ? note.GetString() ?? "" : "";
        string? appHash = null;
        if (packageType == "installer") {
            appHash = root.GetProperty("app_sha256").GetString();
            if (!Regex.IsMatch(appHash ?? "", @"\A[0-9a-fA-F]{64}\z")) throw new FormatException("安装更新缺少程序校验信息。");
        }
        return new UpdateRelease(version!, download, hash.ToLowerInvariant(), notes, packageType!, appHash?.ToLowerInvariant());
    }

    public static async Task<UpdateRelease> FetchAsync(HttpClient http, Uri url, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await UpdateHttp.GetAsync(http, url, timeout.Token);
        response.EnsureSuccessStatusCode();
        const int limit = 64 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new FormatException("更新信息超过允许大小。");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var bytes = new byte[4096];
        while (true) {
            var count = await stream.ReadAsync(bytes, timeout.Token);
            if (count == 0) break;
            if (buffer.Length + count > limit) throw new FormatException("更新信息超过允许大小。");
            buffer.Write(bytes, 0, count);
        }
        return Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }
}
