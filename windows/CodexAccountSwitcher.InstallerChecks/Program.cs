using System.Diagnostics;
using System.IO;
using CodexAccountSwitcher;
using Microsoft.Win32;

if (args.Length != 5) { Console.Error.WriteLine("Usage: InstallerChecks <test setup> <installed app> <test root> <test uninstall registry key> <previous executable>"); return 2; }
var setup = Path.GetFullPath(args[0]);
var target = Path.GetFullPath(args[1]);
var root = Path.GetFullPath(args[2]);
var registryKey = args[3];
var previous = Path.GetFullPath(args[4]);
if (!registryKey.StartsWith(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\StarCodexInstallerTest-", StringComparison.Ordinal)) throw new Exception("Test-only registry key required.");
if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Test app must be inside the test root.");
Environment.SetEnvironmentVariable("CODEX_SWITCHER_DATA_HOME", Path.Combine(root, "账号数据"));
Environment.SetEnvironmentVariable("CODEX_HOME", Path.Combine(root, "codex"));
Environment.SetEnvironmentVariable("CODEX_CLI_PATH", Path.Combine(root, "no-cli.exe"));
var sentinel = Path.Combine(root, "账号数据", "preserve.txt");
var marker = File.ReadAllText(sentinel);
var appHash = UpdateInstaller.Hash(target);
var oldHash = UpdateInstaller.Hash(previous);
var setupHash = UpdateInstaller.Hash(setup);
var version = FileVersionInfo.GetVersionInfo(target).FileVersion!;
var oldVersion = FileVersionInfo.GetVersionInfo(previous).FileVersion!;
Check(Version.Parse(oldVersion) < Version.Parse(version), "The previous executable must be an older version.");
var job = new UpdateJob(target, setup, setupHash, oldHash, version, 0, 0, "installer", appHash);
RestoreOld();
await UpdateInstaller.ApplyInstalledAsync(job, path => UpdateInstaller.LaunchAndVerifyAsync(path, selfTest: true), registryKey);
Check(UpdateInstaller.Hash(target) == appHash, "Installed upgrade has the new executable.");
Check(RegisteredVersion() == version, "Installed upgrade records the new version.");
Check(File.ReadAllText(sentinel) == marker, "Install update preserves account marker.");
// A startup readiness event can arrive just before self-test process exit.
await Task.Delay(500);
RestoreOld();
var failed = false;
try { await UpdateInstaller.ApplyInstalledAsync(job, _ => throw new IOException("simulated start failure"), registryKey); }
catch (IOException) { failed = true; }
Check(failed && UpdateInstaller.Hash(target) == oldHash, "Failed start rolls back installed executable.");
Check(RegisteredVersion() == oldVersion, "Rollback restores old registration version.");
failed = false; var launched = false;
try { await UpdateInstaller.ApplyInstalledAsync(job with { Sha256 = new string('0',64) }, _ => { launched = true; return Task.CompletedTask; }, registryKey); }
catch (IOException) { failed = true; }
Check(failed && !launched && UpdateInstaller.Hash(target) == oldHash, "Invalid setup hash cannot install or relaunch.");
failed = false;
try { await UpdateInstaller.ApplyInstalledAsync(job with { OldSha256 = new string('0',64) }, _ => Task.CompletedTask, registryKey); }
catch (IOException) { failed = true; }
Check(failed && UpdateInstaller.Hash(target) == oldHash, "Concurrent initial modification is not overwritten.");
failed = false;
try {
    await UpdateInstaller.ApplyInstalledAsync(job, path => {
        File.WriteAllText(path, "concurrent replacement");
        throw new IOException("simulated concurrent replacement");
    }, registryKey);
} catch (IOException) { failed = true; }
Check(failed && File.ReadAllText(target) == "concurrent replacement", "Concurrent post-install replacement is not overwritten by rollback.");
RestoreOld();
await UpdateInstaller.ApplyInstalledAsync(job, path => UpdateInstaller.LaunchAndVerifyAsync(path, selfTest: true), registryKey);
await Task.Delay(500);
Check(UpdateInstaller.Hash(target) == appHash, "Final upgraded executable restored for uninstall checks.");
Check(File.ReadAllText(sentinel) == marker, "All paths preserve account data.");
Console.WriteLine($"PASS: installed updater {oldVersion} -> {version}; NSIS invocation, application checksum, restart readiness, startup rollback, invalid hash, concurrent initial and post-install modifications, preserved account data.");
return 0;
string? RegisteredVersion() { using var key = Registry.CurrentUser.OpenSubKey(registryKey); return key?.GetValue("DisplayVersion") as string; }
void RestoreOld() {
    File.Copy(previous, target, true);
    using var key = Registry.CurrentUser.OpenSubKey(registryKey, writable: true) ?? throw new Exception("Isolated test registration is missing.");
    key.SetValue("DisplayVersion", oldVersion);
}
static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
