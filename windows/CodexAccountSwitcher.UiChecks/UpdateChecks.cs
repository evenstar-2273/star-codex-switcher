using System;
using System.IO;
using System.Threading.Tasks;
using CodexAccountSwitcher;

internal static class UpdateChecks
{
    public static async Task RunAsync(string executable, string directory)
    {
        var root = Path.GetFullPath(directory); Directory.CreateDirectory(root);
        var target = Path.Combine(root, "账号切换器.exe");
        var payload = Path.Combine(root, "新版.exe");
        File.Copy(executable, target, true); File.Copy(executable, payload, true);
        var sentinel = Path.Combine(root, "account-data-sentinel.txt"); File.WriteAllText(sentinel, "must remain unchanged");
        var hash = UpdateInstaller.Hash(payload);
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(payload).FileVersion!;
        var job = new UpdateJob(target, payload, hash, hash, version, 0, 0);
        // Real EXE swap and relaunch; --self-test uses isolated account directories only.
        await UpdateInstaller.ApplyTransactionAsync(job, path => UpdateInstaller.LaunchAndVerifyAsync(path, selfTest: true));
        Check(UpdateInstaller.Hash(target) == hash && File.ReadAllText(sentinel) == "must remain unchanged", "Successful replacement preserves data.");
        var failed = false;
        try { await UpdateInstaller.ApplyTransactionAsync(job, _ => throw new IOException("Simulated startup failure")); } catch (IOException) { failed = true; }
        Check(failed && UpdateInstaller.Hash(target) == hash, "Startup failure restores the original executable.");
        var launched = false; failed = false;
        try { await UpdateInstaller.ApplyTransactionAsync(job with { Sha256 = new string('0', 64) }, _ => { launched = true; return Task.CompletedTask; }); } catch (IOException) { failed = true; }
        Check(failed && !launched && UpdateInstaller.Hash(target) == hash, "Corrupt payload cannot replace or launch.");
        failed = false;
        try { await UpdateInstaller.ApplyTransactionAsync(job with { OldSha256 = new string('0', 64) }, _ => Task.CompletedTask); } catch (IOException) { failed = true; }
        Check(failed && UpdateInstaller.Hash(target) == hash, "Concurrent original change aborts replacement.");
        Check(File.ReadAllText(sentinel) == "must remain unchanged", "All failure paths preserve account data.");
        Console.WriteLine("PASS: real EXE replace/relaunch, SHA-256 rejection, startup rollback, concurrent change and data preservation.");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
