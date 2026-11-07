using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexAccountSwitcher.Core;

namespace CodexAccountSwitcher;

public sealed record UpdateJob(string Target, string Payload, string Sha256, string OldSha256,
    string Version, int ParentId, long ParentStartTicks, string PackageType = "portable", string? AppSha256 = null);

public static class UpdateInstaller
{
    public const string HelperLanguageEnvironmentVariable = "STAR_CODEX_SWITCHER_UPDATE_LANGUAGE";
    public static string CacheRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Codex Account Switcher Updates");
    public static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

    public static void VerifyPayload(string path, string hash, string version)
    {
        if (!string.Equals(Hash(path), hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("下载校验失败，已取消更新；原程序未修改。");
        using (var input = File.OpenRead(path)) {
            if (input.ReadByte() != 'M' || input.ReadByte() != 'Z') throw new IOException("下载内容不是 Windows 程序。");
        }
        if (!System.Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var actual) || actual != System.Version.Parse(version))
            throw new IOException("下载程序的版本与服务器版本信息不一致。");
    }

    public static async Task<string> PrepareAsync(UpdateRelease release, IProgress<string> progress, CancellationToken cancel)
    {
        var target = Environment.ProcessPath ?? throw new IOException("无法确定当前程序路径。");
        if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("请使用已发布的 EXE 执行更新。");
        if (release.Version <= System.Version.Parse(FileVersionInfo.GetVersionInfo(target).FileVersion!)) throw new IOException("无需安装相同或较旧版本。");
        SecureFiles.RejectLinks(target);
        var targetDirectory = Path.GetDirectoryName(target)!;
        // Both editions install per-user and must be able to update their own directory.
        var probe = Path.Combine(targetDirectory, ".switcher-write-" + Guid.NewGuid().ToString("N"));
        try { using (File.Create(probe)) { } } catch { throw new IOException("程序目录不可写，请将双击版放到可写目录后重试。"); }
        finally { if (File.Exists(probe)) File.Delete(probe); }
        SecureFiles.CreatePrivateDirectory(CacheRoot);
        var folder = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N")); SecureFiles.CreatePrivateDirectory(folder);
        var payload = Path.Combine(folder, "download.exe");
        try {
            using var handler = WindowsHttp.Handler();
            handler.UseCookies = false;
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await UpdateHttp.GetAsync(http, release.DownloadUrl, timeout.Token);
            response.EnsureSuccessStatusCode();
            const long limit = 512L * 1024 * 1024;
            var length = response.Content.Headers.ContentLength;
            if (length > limit) throw new IOException("更新包超过允许大小。");
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token))
            await using (var output = new FileStream(payload, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true)) {
                var buffer = new byte[81920]; long total = 0; var last = -1;
                while (true) {
                    var count = await input.ReadAsync(buffer, timeout.Token); if (count == 0) break;
                    total += count; if (total > limit) throw new IOException("更新包超过允许大小。");
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
                    var percent = length > 0 ? (int)Math.Min(100, total * 100 / length.Value) : (int)(total / (1024 * 1024));
                    if (percent != last) { last = percent; progress.Report(length > 0 ? $"正在下载更新 {percent}%" : $"正在下载更新 {percent} MB"); }
                }
                if (length.HasValue && total != length.Value) throw new IOException("更新包下载不完整。");
            }
            progress.Report("正在校验更新包……");
            await Task.Run(() => VerifyPayload(payload, release.Sha256, release.Version.ToString()), cancel);
            using var parent = Process.GetCurrentProcess();
            var job = new UpdateJob(target, payload, release.Sha256, Hash(target), release.Version.ToString(), parent.Id, parent.StartTime.ToUniversalTime().Ticks, release.PackageType, release.AppSha256);
            var jobPath = Path.Combine(folder, "job.json");
            File.WriteAllText(jobPath, JsonSerializer.Serialize(job));
            // The independent helper is the current trusted application, not the downloaded payload.
            File.Copy(target, Path.Combine(folder, "update-helper.exe"));
            return jobPath;
        } catch { SecureFiles.DeleteOwnedDirectory(CacheRoot, folder); throw; }
    }

    public static async Task StartHelperAsync(string jobPath)
    {
        var folder = Path.GetDirectoryName(jobPath)!;
        var language = (System.Windows.Application.Current as App)?.LanguagePreference;
        var start = CreateHelperStartInfo(jobPath, language);
        using var helper = Process.Start(start) ?? throw new IOException("无法启动更新程序。");
        for (var attempt = 0; attempt < 100; attempt++) {
            if (File.Exists(Path.Combine(folder, "helper-ready"))) return;
            if (helper.HasExited) throw new IOException("更新程序未能启动，当前版本继续运行。");
            await Task.Delay(100);
        }
        try { if (!helper.HasExited) helper.Kill(); } catch { }
        throw new IOException("更新程序启动超时，当前版本继续运行。");
    }

    public static string ReadHelperLanguage() => NormalizeHelperLanguage(Environment.GetEnvironmentVariable(HelperLanguageEnvironmentVariable));

    private static string NormalizeHelperLanguage(string? language) => language is "simplifiedChinese" or "english" or "system" ? language : "simplifiedChinese";

    public static ProcessStartInfo CreateHelperStartInfo(string jobPath, string? language)
    {
        var folder = Path.GetDirectoryName(jobPath)!;
        var start = new ProcessStartInfo(Path.Combine(folder, "update-helper.exe")) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(jobPath);
        // Resolve the selected UI language before the helper reads any update files.
        start.Environment[HelperLanguageEnvironmentVariable] = NormalizeHelperLanguage(language);
        return start;
    }

    public static async Task ApplyTransactionAsync(UpdateJob job, Func<string, Task> launchAndVerify)
    {
        SecureFiles.RejectLinks(job.Target); SecureFiles.RejectLinks(job.Payload);
        VerifyPayload(job.Payload, job.Sha256, job.Version);
        if (!string.Equals(Hash(job.Target), job.OldSha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("原程序已被其他操作修改，本次更新取消。");
        var stage = job.Target + ".update-" + Guid.NewGuid().ToString("N") + ".tmp";
        var backup = job.Target + ".previous-" + Guid.NewGuid().ToString("N") + ".bak";
        var replaced = false;
        try {
            File.Copy(job.Payload, stage);
            File.Replace(stage, job.Target, backup); replaced = true;
            await launchAndVerify(job.Target);
            // A backup is kept beside the EXE for recovery. No account paths are touched.
        } catch {
            if (replaced) {
                if (!string.Equals(Hash(job.Target), job.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("更新后程序被其他操作修改，未自动覆盖；旧版备份：" + backup);
                File.Replace(backup, job.Target, null);
            }
            throw;
        } finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    public static async Task ApplyInstalledAsync(UpdateJob job, Func<string, Task> launchAndVerify, string? uninstallKey = null)
    {
        if (job.PackageType != "installer" || job.AppSha256 == null ||
            (Path.GetFileName(job.Target) != Distribution.ExecutableName && Path.GetFileName(job.Target) != Distribution.LegacyExecutableName))
            throw new IOException("安装更新任务无效。");
        SecureFiles.RejectLinks(job.Target); SecureFiles.RejectLinks(job.Payload);
        VerifyPayload(job.Payload, job.Sha256, job.Version);
        if (Hash(job.Target) != job.OldSha256) throw new IOException("当前程序已改变，取消更新。");
        var directory = Path.GetDirectoryName(job.Target)!;
        var newTarget = Path.Combine(directory, Distribution.ExecutableName);
        var legacyTarget = Path.Combine(directory, Distribution.LegacyExecutableName);
        var uninstaller = Path.Combine(directory, "Uninstall.exe");
        SecureFiles.RejectLinks(uninstaller);
        var backup = Path.Combine(Path.GetDirectoryName(job.Payload)!, "installed-backup");
        SecureFiles.CreatePrivateDirectory(backup);
        File.Copy(job.Target, Path.Combine(backup, Path.GetFileName(job.Target)), true);
        var otherTarget = job.Target == newTarget ? legacyTarget : newTarget;
        SecureFiles.RejectLinks(otherTarget);
        var otherHash = File.Exists(otherTarget) ? Hash(otherTarget) : null;
        if (otherHash != null) { SecureFiles.RejectLinks(otherTarget); File.Copy(otherTarget, Path.Combine(backup, Path.GetFileName(otherTarget)), true); }
        if (File.Exists(uninstaller)) File.Copy(uninstaller, Path.Combine(backup, "Uninstall.exe"), true);
        using var registry = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(uninstallKey ?? Distribution.UninstallKey, writable: true);
        var registrationSnapshot = new InstalledRegistrationSnapshot(registry);
        try {
            // NSIS requires /D= to be last and unquoted, even for paths containing spaces.
            if (directory.Contains('"') || directory.Contains('\r') || directory.Contains('\n')) throw new IOException("安装路径不受支持。");
            var start = new ProcessStartInfo(job.Payload) {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "/S /UPDATE /MODERNUPDATE /D=" + directory
            };
            using var installer = Process.Start(start) ?? throw new IOException("安装程序无法启动。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            try { await installer.WaitForExitAsync(timeout.Token); }
            catch { if (!installer.HasExited) { installer.Kill(entireProcessTree: true); await installer.WaitForExitAsync(); } throw; }
            finally { registrationSnapshot.CaptureInstalled(registry); }
            if (installer.ExitCode != 0) throw new IOException($"安装更新失败（{installer.ExitCode}）。");
            VerifyPayload(newTarget, job.AppSha256, job.Version);
            await launchAndVerify(newTarget);
        } catch {
            foreach (var path in new[] { job.Target, otherTarget }) {
                if (!File.Exists(path)) continue;
                var currentHash = Hash(path);
                var previousHash = path == job.Target ? job.OldSha256 : otherHash;
                if (currentHash != previousHash && currentHash != job.AppSha256)
                    throw new IOException("安装期间程序被其他操作修改，未覆盖该文件；旧版备份：" + backup);
            }
            File.Copy(Path.Combine(backup, Path.GetFileName(job.Target)), job.Target, true);
            if (otherHash != null) File.Copy(Path.Combine(backup, Path.GetFileName(otherTarget)), otherTarget, true);
            else if (File.Exists(otherTarget)) File.Delete(otherTarget);
            if (File.Exists(Path.Combine(backup, "Uninstall.exe"))) File.Copy(Path.Combine(backup, "Uninstall.exe"), uninstaller, true);
            registrationSnapshot.Restore(registry);
            var migration = Path.Combine(directory, InstalledMigration.MarkerName);
            if (File.Exists(migration)) File.Delete(migration);
            throw;
        }
    }

    public static async Task LaunchAndVerifyAsync(string target, bool selfTest = false, string[]? forwardedArgs = null)
    {
        var eventName = @"Local\CodexSwitcherUpdateReady-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target)! };
        if (selfTest) start.ArgumentList.Add("--self-test");
        if (forwardedArgs != null) {
            for (var index = 0; index < forwardedArgs.Length; index++) {
                if (forwardedArgs[index] == "--update-ready") { index++; continue; }
                start.ArgumentList.Add(forwardedArgs[index]);
            }
        }
        start.ArgumentList.Add("--update-ready"); start.ArgumentList.Add(eventName);
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新后的程序。");
        try {
            for (var i = 0; i < 450; i++) {
                if (ready.WaitOne(0)) return;
                if (process.HasExited) throw new IOException("新版程序启动失败，正在恢复旧版。");
                await Task.Delay(100);
            }
            throw new IOException("新版程序启动超时，正在恢复旧版。");
        } catch {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } } catch { }
            throw;
        }
    }

    public static void SignalReady(string[] args)
    {
        var index = Array.IndexOf(args, "--update-ready");
        if (index < 0 || index + 1 >= args.Length || !args[index + 1].StartsWith(@"Local\CodexSwitcherUpdateReady-", StringComparison.Ordinal)) return;
        try { using var ready = EventWaitHandle.OpenExisting(args[index + 1]); ready.Set(); } catch (WaitHandleCannotBeOpenedException) { }
    }

    public static async Task<bool> RedirectLegacyLaunchAsync(string[] args)
    {
        var current = Environment.ProcessPath;
        if (current == null || !string.Equals(Path.GetFileName(current), Distribution.LegacyExecutableName, StringComparison.OrdinalIgnoreCase)
            || Array.IndexOf(args, "--prepare-install") >= 0) return false;
        var migration = InstalledMigration.Read(Path.GetDirectoryName(current)!);
        if (migration == null) return false;
        SecureFiles.RejectLinks(migration.Current);
        var hash = Hash(current);
        if (Hash(migration.Current) != hash) throw new IOException("安装迁移校验失败。");
        try {
            await LaunchAndVerifyAsync(migration.Current, forwardedArgs: args);
            SignalReady(args);
            return true;
        } catch { migration.CancelLegacyHandoff(hash); throw; }
    }

    public static async Task SignalReadyAsync(string[] args)
    {
        SignalReady(args);
        var current = Environment.ProcessPath;
        if (current == null || !string.Equals(Path.GetFileName(current), Distribution.ExecutableName, StringComparison.OrdinalIgnoreCase)) return;
        // Cleanup is retryable; a valid startup must not be turned into an update failure by a locked shortcut.
        try { var migration = InstalledMigration.Read(Path.GetDirectoryName(current)!); if (migration != null) await migration.CompleteAsync(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    public static async Task RunHelperAsync(string jobPath)
    {
        var fullPath = Path.GetFullPath(jobPath);
        if (!fullPath.StartsWith(Path.GetFullPath(CacheRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("更新任务路径不正确。");
        SecureFiles.RejectLinks(fullPath);
        var folder = Path.GetDirectoryName(fullPath)!;
        var job = JsonSerializer.Deserialize<UpdateJob>(File.ReadAllText(fullPath)) ?? throw new IOException("更新任务无效。");
        if (Path.GetFullPath(job.Payload) != Path.Combine(folder, "download.exe") || !job.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("更新文件路径无效。");
        VerifyPayload(job.Payload, job.Sha256, job.Version);
        using var parent = Process.GetProcessById(job.ParentId);
        if (parent.StartTime.ToUniversalTime().Ticks != job.ParentStartTicks) throw new IOException("原程序身份不匹配。");
        File.WriteAllText(Path.Combine(folder, "helper-ready"), "ready");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await parent.WaitForExitAsync(timeout.Token);
        try {
            if (job.PackageType != Distribution.PackageType) throw new IOException("更新包类型错误。");
            if (job.PackageType == "installer") await ApplyInstalledAsync(job, path => LaunchAndVerifyAsync(path));
            else await ApplyTransactionAsync(job, path => LaunchAndVerifyAsync(path));
            File.WriteAllText(Path.Combine(folder, "result.txt"), "PASS: installed " + job.Version + " and restarted.");
        } catch {
            // Only relaunch the original if it was successfully retained/restored.
            if (File.Exists(job.Target) && Hash(job.Target) == job.OldSha256)
                Process.Start(new ProcessStartInfo(job.Target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(job.Target)! })?.Dispose();
            throw;
        }
    }
}
