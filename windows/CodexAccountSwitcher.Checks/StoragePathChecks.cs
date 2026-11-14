using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using CodexAccountSwitcher.Core;

internal static class StoragePathChecks
{
    private static int passed;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
    private static void Reject(Action action, string message) {
        try { action(); } catch (IOException) { passed++; return; }
        throw new Exception(message);
    }
    private static bool Same(string a, string b) => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(string? host)
    {
        if (host == null || !File.Exists(host)) throw new ArgumentException("Pass the built SwitcherHost.exe path.");
        var root = Path.Combine(Path.GetTempPath(), "switcher-storage-中文 空格-" + Guid.NewGuid());
        var priorCli = Environment.GetEnvironmentVariable("CODEX_CLI_PATH");
        var links = new List<string>();
        Directory.CreateDirectory(root);
        void Link(string path, string target) { CreateJunction(path, target); links.Add(path); }
        try {
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", Environment.ProcessPath);
            var physical = Path.Combine(root, "physical"); Directory.CreateDirectory(physical);
            var alias = Path.Combine(root, "alias"); Link(alias, physical);
            var unicodeAlias = Path.Combine(root, "中文 链接"); Link(unicodeAlias, physical);
            Check(Same(SecureFiles.ResolveStorageRoot(alias), physical), "Existing junction root must resolve to its physical target.");
            Check(Same(SecureFiles.ResolveStorageRoot(Path.Combine(unicodeAlias, "fresh data")), Path.Combine(physical, "fresh data")), "A missing suffix under a redirected ancestor must resolve without creating it.");
            Check(!Directory.Exists(Path.Combine(physical, "fresh data")), "Root resolution must not create an empty account store.");
            Reject(() => SecureFiles.ResolveStorageRoot(Path.GetPathRoot(root)!), "Drive root must not receive private account permissions.");
            Reject(() => SecureFiles.ResolveStorageRoot(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)), "User profile root must not receive private account permissions.");
            var broadAlias = Path.Combine(root, "broad"); Link(broadAlias, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            Reject(() => SecureFiles.ResolveStorageRoot(broadAlias), "Aliases must not bypass broad-root checks.");
            var ordinaryFile = Path.Combine(physical, "file"); File.WriteAllText(ordinaryFile, "fixture");
            Reject(() => SecureFiles.ResolveStorageRoot(ordinaryFile), "File roots must fail.");
            Reject(() => SecureFiles.ResolveStorageRoot(Path.Combine(ordinaryFile, "missing")), "A file ancestor must not become a new directory.");
            var broken = Path.Combine(root, "broken"); Link(broken, Path.Combine(root, "missing-target"));
            Reject(() => SecureFiles.ResolveStorageRoot(Path.Combine(broken, "data")), "A broken redirect must fail rather than make a new empty store.");
            var loopA = Path.Combine(root, "loop-a"); var loopB = Path.Combine(root, "loop-b");
            Link(loopA, loopB); Link(loopB, loopA);
            Reject(() => SecureFiles.ResolveStorageRoot(loopA), "A redirect loop must fail.");
            var restricted = Path.Combine(physical, "restricted"); Directory.CreateDirectory(restricted);
            var user = WindowsIdentity.GetCurrent().User!;
            var originalAcl = new DirectoryInfo(restricted).GetAccessControl().GetSecurityDescriptorBinaryForm();
            var parentAcl = new DirectoryInfo(physical).GetAccessControl().GetSecurityDescriptorBinaryForm();
            var deniedAcl = new DirectorySecurity(); deniedAcl.SetOwner(user); deniedAcl.SetAccessRuleProtection(true, false);
            deniedAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAttributes, AccessControlType.Deny));
            deniedAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            try {
                new DirectoryInfo(restricted).SetAccessControl(deniedAcl);
                var blockedParent = new DirectoryInfo(physical).GetAccessControl();
                blockedParent.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny));
                new DirectoryInfo(physical).SetAccessControl(blockedParent);
                Reject(() => SecureFiles.ResolveStorageRoot(restricted), "An inaccessible root must fail, not silently fall back to another store.");
            } finally { RestoreAcl(physical, parentAcl); RestoreAcl(restricted, originalAcl); }

            var activePhysical = Path.Combine(root, "active physical"); Directory.CreateDirectory(activePhysical);
            var activeAlias = Path.Combine(root, "认证 链接"); Link(activeAlias, activePhysical);
            var dataAlias = Path.Combine(unicodeAlias, "fresh data");
            var dataPhysical = Path.Combine(physical, "fresh data");
            await using (var fresh = new CoreClient(host, dataAlias, activeAlias)) {
                await fresh.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Check(fresh.State.Error == null && fresh.State.Accounts.Length == 0 && fresh.State.ActiveIdentityConfirmed,
                    "Fresh redirected roots with no account or auth file must start normally without an error.");
                await fresh.CommandAsync("language", language: "simplifiedChinese");
            }
            Check(File.Exists(Path.Combine(dataPhysical, "settings.json")), "Fresh settings must use the resolved directory.");
            var first = GuardedChecks.Credential("stored-workspace");
            Check(AccountImport.Commit(dataAlias, activeAlias, [first]).Added == 1, "Direct import must accept legitimate configured directory redirects.");
            var registryBefore = File.ReadAllBytes(Path.Combine(dataPhysical, "accounts.json"));
            await using (var reopened = new CoreClient(host, dataAlias, activeAlias)) {
                await reopened.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Check(reopened.State.Error == null && reopened.State.Accounts.Length == 1 && reopened.State.Settings.Language == "simplifiedChinese",
                    "Host restart must read existing accounts and settings through the same redirected root.");
            }
            Check(File.ReadAllBytes(Path.Combine(dataPhysical, "accounts.json")).SequenceEqual(registryBefore), "Restart must not replace the existing registry.");

            // The first switch must also work before Codex has signed in at all.
            // Both roots use an existing redirected ancestor; the active root itself
            // and auth.json deliberately do not exist until the real switch creates them.
            var firstDataAlias = Path.Combine(alias, "first login data");
            var firstActiveAlias = Path.Combine(alias, "first login active");
            var firstActivePhysical = Path.Combine(physical, "first login active");
            var firstLogin = GuardedChecks.Credential("first-login-workspace");
            var firstDesktop = new Desktop();
            Guid firstLoginID;
            await using (var unsigned = new GuardedAccountClient(host, firstDataAlias, firstActiveAlias, firstDesktop, new Validator())) {
                await unsigned.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Check(unsigned.State.Error == null && unsigned.State.Accounts.Length == 0 && unsigned.State.ActiveAccountID == null
                    && unsigned.State.ActiveIdentityConfirmed && !Directory.Exists(firstActivePhysical),
                    "A new installation must start normally with no selected account and no Codex home directory.");
                var imported = await unsigned.ImportTextAsync(Encoding.UTF8.GetString(firstLogin.Bytes));
                Check(imported.Added == 1 && unsigned.State.ActiveAccountID == null && !Directory.Exists(firstActivePhysical),
                    "Importing a first account must not silently select it or pre-create Codex authentication.");
                firstLoginID = unsigned.State.Accounts.Single().Profile.Id;
                await unsigned.CommandAsync("switch", firstLoginID).WaitAsync(TimeSpan.FromSeconds(30));
                Check(unsigned.State.Error == null && unsigned.State.ActiveAccountID == firstLoginID && unsigned.State.ActiveIdentityConfirmed,
                    "Switching directly from signed out must select and confirm the imported account.");
                Check(firstDesktop.Closed == 1 && firstDesktop.Opened == 1 && File.Exists(Path.Combine(firstActivePhysical, "auth.json"))
                    && LoginCredential.Read(Path.Combine(firstActivePhysical, "auth.json")).AccountID == firstLogin.AccountID,
                    "The real first-switch command must create auth.json under the resolved missing directory and restart Desktop once.");
            }
            await using (var firstRestart = new GuardedAccountClient(host, firstDataAlias, firstActiveAlias, new Desktop(), new Validator())) {
                await firstRestart.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Check(firstRestart.State.Error == null && firstRestart.State.ActiveAccountID == firstLoginID && firstRestart.State.ActiveIdentityConfirmed,
                    "The first switched login must remain selected and confirmed after a fresh client starts through the aliases.");
            }

            var active = GuardedChecks.Credential("active-workspace");
            File.WriteAllBytes(Path.Combine(activePhysical, "auth.json"), active.Bytes);
            Guid selectedAccount;
            var desktop = new Desktop();
            await using (var client = new GuardedAccountClient(host, dataAlias, activeAlias, desktop, new Validator())) {
                await client.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Check(client.State.Error == null && client.State.Accounts.Length == 2 && client.State.ActiveAccountID != null && client.State.ActiveIdentityConfirmed
                    && LoginCredential.Read(Path.Combine(dataPhysical, "accounts", client.State.ActiveAccountID.Value.ToString().ToUpperInvariant(), "auth.json")).AccountID == active.AccountID,
                    "The resolved active home must register its existing login with the host.");
                var alternative = Path.Combine(root, "alternative"); Directory.CreateDirectory(alternative);
                var alternateActive = Path.Combine(root, "alternate active"); Directory.CreateDirectory(alternateActive);
                Directory.Delete(unicodeAlias); CreateJunction(unicodeAlias, alternative);
                Directory.Delete(activeAlias); CreateJunction(activeAlias, alternateActive);
                var imported = await client.ImportTextAsync(Encoding.UTF8.GetString(GuardedChecks.Credential("new-workspace").Bytes));
                Check(imported.Added == 1 && client.State.Error == null && client.State.Accounts.Length == 3,
                    "An existing client must continue importing and restarting the host against its pinned physical data root.");
                Check(!Directory.Exists(Path.Combine(alternative, "fresh data")), "Retargeting an alias must not silently create a second account store.");
                var skipped = await client.ImportTextAsync(Encoding.UTF8.GetString(active.Bytes));
                Check(skipped.SkippedActive == 1 && client.State.ActiveAccountID != null && !File.Exists(Path.Combine(alternateActive, "auth.json")),
                    "Retargeting the active-home alias must not change the pinned current login.");
                selectedAccount = client.State.Accounts.Single(row => LoginCredential.Read(Path.Combine(dataPhysical, "accounts", row.Profile.Id.ToString().ToUpperInvariant(), "auth.json")).AccountID == first.AccountID).Profile.Id;
                await client.CommandAsync("switch", selectedAccount).WaitAsync(TimeSpan.FromSeconds(30));
                Check(client.State.Error == null && client.State.ActiveAccountID == selectedAccount && client.State.ActiveIdentityConfirmed,
                    "Switching through the pinned active root must confirm the selected account.");
                Check(desktop.Closed == 1 && desktop.Opened == 1 && LoginCredential.Read(Path.Combine(activePhysical, "auth.json")).AccountID == first.AccountID
                    && !File.Exists(Path.Combine(alternateActive, "auth.json")), "The complete guarded switch must write the original physical active directory and restart once, ignoring a retargeted alias.");
            }
            Directory.Delete(unicodeAlias); CreateJunction(unicodeAlias, physical);
            Directory.Delete(activeAlias); CreateJunction(activeAlias, activePhysical);
            await using (var restarted = new GuardedAccountClient(host, dataAlias, activeAlias, new Desktop(), new Validator())) {
                await restarted.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Check(restarted.State.Error == null && restarted.State.ActiveAccountID == selectedAccount && restarted.State.ActiveIdentityConfirmed,
                    "A fresh client through the configured aliases must retain and confirm the switched login.");
            }
            var registryAcl = new FileInfo(Path.Combine(dataPhysical, "accounts.json")).GetAccessControl();
            Check(registryAcl.AreAccessRulesProtected && registryAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Count == 1,
                "Imported data must retain its private current-user ACL after resolving a root.");

            var guardedRoot = Path.Combine(physical, "guarded"); Directory.CreateDirectory(guardedRoot);
            var outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
            var sentinel = Path.Combine(outside, "keep.txt"); File.WriteAllText(sentinel, "do not modify");
            var innerLink = Path.Combine(guardedRoot, "accounts"); Link(innerLink, outside);
            Reject(() => AccountImport.Commit(guardedRoot, activePhysical, [first]), "Profile subdirectory links must still be refused.");
            await using (var guarded = new CoreClient(host, guardedRoot, activePhysical)) {
                await guarded.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(30));
                Check(guarded.State.Error?.Contains("4393", StringComparison.Ordinal) == true, "The unchanged native host must still reject links inside its storage root.");
            }
            Reject(() => SecureFiles.AtomicWrite(Path.Combine(innerLink, "auth.json"), first.Bytes), "Credential writes through inner links must still be refused.");
            Reject(() => SecureFiles.DeleteOwnedDirectory(physical, guardedRoot), "Recursive cleanup must not traverse inner links.");
            Check(File.ReadAllText(sentinel) == "do not modify" && !File.Exists(Path.Combine(outside, "auth.json")), "Rejected inner links must leave their targets untouched.");
            Console.WriteLine($"PASS: {passed} storage-root assertions; real Windows junctions, fresh startup, import/restart, active login, pinned roots, private ACLs and inner-link rejection (synthetic directories only).");
            return 0;
        } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally {
            Environment.SetEnvironmentVariable("CODEX_CLI_PATH", priorCli);
            foreach (var link in links.AsEnumerable().Reverse()) {
                if (!Path.GetFullPath(link).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected fixture link path.");
                Directory.Delete(link, recursive: false);
            }
            SecureFiles.DeleteOwnedDirectory(Path.GetTempPath(), root);
        }
    }

    private sealed class Desktop : IDesktopOperations {
        public int Closed, Opened;
        public Task CloseAsync(CancellationToken cancellationToken) { Closed++; return Task.CompletedTask; }
        public Task OpenAsync(CancellationToken cancellationToken) { Opened++; return Task.CompletedTask; }
    }
    private sealed class Validator : ILoginValidator {
        public Task ValidateAsync(LoginCredential credential, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static void RestoreAcl(string path, byte[] descriptor) {
        var restored = new DirectorySecurity();
        restored.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
        new DirectoryInfo(path).SetAccessControl(restored);
    }

    internal static void CreateJunction(string link, string target)
    {
        Directory.CreateDirectory(link);
        using var handle = CreateFileW(link, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target));
        var display = Encoding.Unicode.GetBytes(Path.GetFullPath(target));
        var buffer = new byte[16 + substitute.Length + 2 + display.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)display.Length));
        substitute.CopyTo(buffer, 16); display.CopyTo(buffer, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[] input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
}
