using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using CodexAccountSwitcher.Core;
using Microsoft.Win32;

namespace CodexAccountSwitcher;

internal sealed record InstalledMigration(string Directory, string RegistryKey, string Shortcut, string LegacyShortcut, string OldVersion, bool TestMode)
{
    internal const string MarkerName = ".star-migration.ini";
    internal string Current => Path.Combine(Directory, Distribution.ExecutableName);
    internal string Legacy => Path.Combine(Directory, Distribution.LegacyExecutableName);
    internal static InstalledMigration? Read(string directory)
    {
        var marker = Path.Combine(directory, MarkerName);
        if (!File.Exists(marker)) return null;
        SecureFiles.RejectLinks(marker);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(marker)) {
            var index = line.IndexOf('=');
            if (index > 0) values[line[..index].Trim()] = line[(index + 1)..].Trim();
        }
        var key = values.GetValueOrDefault("RegistryKey", "");
        var test = key.StartsWith(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\StarCodexInstallerTest-", StringComparison.Ordinal);
        if (key != Distribution.UninstallKey && !test) throw new IOException("安装迁移信息无效。");
        using var registration = Registry.CurrentUser.OpenSubKey(key);
        if (registration?.GetValue("InstallLocation") is not string location ||
            !string.Equals(Path.GetFullPath(location), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            throw new IOException("安装迁移目录不匹配。");
        var shortcut = values.GetValueOrDefault("Shortcut", Distribution.DisplayName);
        var legacyShortcut = values.GetValueOrDefault("LegacyShortcut", Distribution.LegacyDisplayName);
        foreach (var name in new[] { shortcut, legacyShortcut })
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("安装快捷方式名称无效。");
        return new(directory, key, shortcut, legacyShortcut, values.GetValueOrDefault("OldVersion", ""), test);
    }

    internal async Task CompleteAsync()
    {
        // Keep the alias usable until every launch entry points at the new file.
        // A locked shortcut leaves the marker and alias for a later retry.
        using var registration = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
        registration?.SetValue("DisplayName", Distribution.DisplayName);
        registration?.SetValue("DisplayIcon", Current);
        foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
            MigrateShortcut(Environment.GetFolderPath(folder));
        if (!TestMode) {
            using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue("CodexAccountSwitcher") is string command && command == $"\"{Legacy}\" --background")
                run.SetValue("CodexAccountSwitcher", $"\"{Current}\" --background");
        }
        // The legacy helper has already verified and launched its alias before this readiness signal.
        if (File.Exists(Legacy)) {
            SecureFiles.RejectLinks(Legacy); SecureFiles.RejectLinks(Current);
            if (UpdateInstaller.Hash(Legacy) != UpdateInstaller.Hash(Current)) return;
            for (var attempt = 0; attempt < 100; attempt++) {
                try {
                    SecureFiles.RejectLinks(Legacy);
                    if (File.Exists(Legacy) && UpdateInstaller.Hash(Legacy) != UpdateInstaller.Hash(Current)) return;
                    File.Delete(Legacy); break;
                }
                catch (IOException) { await Task.Delay(100); }
                catch (UnauthorizedAccessException) { await Task.Delay(100); }
            }
            if (File.Exists(Legacy)) return;
        }
        File.Delete(Path.Combine(Directory, MarkerName));
    }

    private void MigrateShortcut(string folder)
    {
        var oldLink = Path.Combine(folder, LegacyShortcut + ".lnk");
        var newLink = Path.Combine(folder, Shortcut + ".lnk");
        if (!File.Exists(oldLink)) return; // A user-deleted shortcut stays deleted.
        SecureFiles.RejectLinks(oldLink);
        object? shellObject = null, linkObject = null;
        try {
            shellObject = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic shell = shellObject!;
            linkObject = shell.CreateShortcut(oldLink);
            dynamic link = linkObject;
            string target = link.TargetPath;
            if (!string.Equals(target, Legacy, StringComparison.OrdinalIgnoreCase) && !string.Equals(target, Current, StringComparison.OrdinalIgnoreCase)) return;
            link.TargetPath = Current; link.WorkingDirectory = Directory; link.IconLocation = Current + ",0"; link.Save();
            // Keep an unrelated new-name shortcut; the old entry still becomes a working launch path.
            if (!string.Equals(oldLink, newLink, StringComparison.OrdinalIgnoreCase) && !File.Exists(newLink)) File.Move(oldLink, newLink);
        } finally {
            if (linkObject != null) Marshal.FinalReleaseComObject(linkObject);
            if (shellObject != null) Marshal.FinalReleaseComObject(shellObject);
        }
    }

    internal static void CleanupShortcuts(string executable, string shortcut, string legacyShortcut)
    {
        var directory = Path.GetDirectoryName(executable)!;
        foreach (var name in new[] { shortcut, legacyShortcut }) {
            if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new IOException("快捷方式名称无效。");
            foreach (var folder in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs }) {
                var path = Path.Combine(Environment.GetFolderPath(folder), name + ".lnk");
                if (!File.Exists(path)) continue;
                SecureFiles.RejectLinks(path);
                object? shellObject = null, linkObject = null;
                try {
                    shellObject = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                    dynamic shell = shellObject!;
                    linkObject = shell.CreateShortcut(path);
                    dynamic link = linkObject;
                    string target = link.TargetPath;
                    if (string.Equals(target, Path.Combine(directory, Distribution.ExecutableName), StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(target, Path.Combine(directory, Distribution.LegacyExecutableName), StringComparison.OrdinalIgnoreCase)) File.Delete(path);
                } finally {
                    if (linkObject != null) Marshal.FinalReleaseComObject(linkObject);
                    if (shellObject != null) Marshal.FinalReleaseComObject(shellObject);
                }
            }
        }
    }

    internal void CancelLegacyHandoff(string expectedHash)
    {
        if (File.Exists(Current)) {
            SecureFiles.RejectLinks(Current);
            if (UpdateInstaller.Hash(Current) != expectedHash) return;
            File.Delete(Current);
        }
        using var registry = Registry.CurrentUser.OpenSubKey(RegistryKey, writable: true);
        if (OldVersion.Length != 0) registry?.SetValue("DisplayVersion", OldVersion);
        File.Delete(Path.Combine(Directory, MarkerName));
    }
}
