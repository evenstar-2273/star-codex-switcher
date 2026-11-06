using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace CodexAccountSwitcher;

// Executable backups and registration are a single transaction. Shortcut/Run
// migration is deliberately deferred until startup readiness has succeeded.
internal sealed class InstalledRegistrationSnapshot
{
    private readonly Dictionary<string, (object Value, RegistryValueKind Kind)> before = new(StringComparer.Ordinal);
    private Dictionary<string, (object Value, RegistryValueKind Kind)>? after;
    internal InstalledRegistrationSnapshot(RegistryKey? key) => before = Read(key);
    internal void CaptureInstalled(RegistryKey? key) => after = Read(key);
    internal void Restore(RegistryKey? key)
    {
        if (key == null || after == null) return;
        var current = Read(key);
        foreach (var name in new HashSet<string>(before.Keys.Concat(after.Keys))) {
            var hadBefore = before.TryGetValue(name, out var previous);
            var hadAfter = after.TryGetValue(name, out var installed);
            var hasCurrent = current.TryGetValue(name, out var actual);
            if (hasCurrent != hadAfter || (hasCurrent && (actual.Kind != installed.Kind || !Equals(actual.Value, installed.Value)))) continue;
            if (hadBefore) key.SetValue(name, previous.Value, previous.Kind);
            else key.DeleteValue(name, throwOnMissingValue: false);
        }
    }
    private static Dictionary<string, (object Value, RegistryValueKind Kind)> Read(RegistryKey? key)
    {
        var result = new Dictionary<string, (object, RegistryValueKind)>(StringComparer.Ordinal);
        if (key != null) foreach (var name in key.GetValueNames()) {
            var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value != null) result[name] = (value, key.GetValueKind(name));
        }
        return result;
    }
}
