using System.Security.AccessControl;
using System.Security.Principal;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexAccountSwitcher.Core;

public static class SecureFiles
{
    private static readonly SecurityIdentifier User = WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("Windows user identity is unavailable.");

    // Resolve only a configured directory root. Once selected, callers retain this
    // physical path; credentials and profile subdirectories still reject links.
    public static string ResolveStorageRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new IOException("The account storage directory is empty.");
        var current = Path.GetFullPath(path);
        var missing = new Stack<string>();
        while (true) {
            var attributes = GetFileAttributesW(current);
            if (attributes != uint.MaxValue) {
                if ((attributes & (uint)FileAttributes.Directory) == 0)
                    throw new IOException("The account storage root must be a directory.");
                // Opening the existing component follows valid directory redirects;
                // broken or looping links fail here rather than becoming a new store.
                var resolved = FinalDirectoryPath(current);
                foreach (var component in missing) resolved = Path.Combine(resolved, component);
                resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(resolved));
                RejectBroadRoot(resolved);
                RejectLinks(resolved);
                return resolved;
            }
            var error = Marshal.GetLastWin32Error();
            if (error is not (2 or 3)) throw StoragePathError(error);
            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (string.IsNullOrEmpty(parent)) throw StoragePathError(error);
            missing.Push(Path.GetFileName(Path.TrimEndingDirectorySeparator(current)));
            current = parent;
        }
    }

    private static string FinalDirectoryPath(string path)
    {
        using var handle = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw StoragePathError(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var information)) throw StoragePathError(Marshal.GetLastWin32Error());
        if ((information.Attributes & (uint)FileAttributes.Directory) == 0)
            throw new IOException("The account storage root must be a directory.");
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0) throw StoragePathError(Marshal.GetLastWin32Error());
        if (length >= buffer.Capacity) {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) throw StoragePathError(Marshal.GetLastWin32Error());
        }
        var result = buffer.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + result[8..];
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) ? result[4..] : result;
    }

    private static void RejectBroadRoot(string path)
    {
        var protectedPaths = new[] {
            Path.GetPathRoot(path), Path.GetTempPath(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        foreach (var candidate in protectedPaths.Where(p => !string.IsNullOrWhiteSpace(p))) {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate!));
            if (string.Equals(path, full, StringComparison.OrdinalIgnoreCase)) throw BroadRootError();
            if (!Directory.Exists(full)) continue;
            string physical;
            try { physical = Path.TrimEndingDirectorySeparator(FinalDirectoryPath(full)); }
            catch (IOException) { continue; }
            if (string.Equals(path, physical, StringComparison.OrdinalIgnoreCase)) throw BroadRootError();
        }
    }

    private static IOException BroadRootError() => new("Choose a dedicated account storage folder, not a drive or Windows user folder.");
    private static IOException StoragePathError(int error) => new($"The account storage directory could not be resolved (Windows error {error}).", new Win32Exception(error));

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, AccessTime, WriteTime;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    public static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Account storage cannot use symbolic links or junctions.");
    }

    public static void CreatePrivateDirectory(string path)
    {
        RejectLinks(path);
        var security = new DirectorySecurity();
        security.SetOwner(User);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(User, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var directory = new DirectoryInfo(path);
        directory.Create(security);
        directory.SetAccessControl(security);
    }

    public static void AtomicWrite(string destination, byte[] bytes)
    {
        RejectLinks(destination);
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".switcher-{Guid.NewGuid():N}.tmp");
        var security = new FileSecurity();
        security.SetOwner(User);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(User, FileSystemRights.FullControl, AccessControlType.Allow));
        try
        {
            using (var stream = FileSystemAclExtensions.Create(new FileInfo(temporary), FileMode.CreateNew,
                FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, security))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            // Same-volume rename, with the new file's restrictive ACL. Never truncate active auth.
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void DeleteOwnedDirectory(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cleanup path is outside account storage.");
        RejectLinks(fullPath);
        if (!Directory.Exists(fullPath)) return;
        // Check each level before descending; do not traverse reparse points.
        CheckTree(fullPath);
        Directory.Delete(fullPath, recursive: true);
    }

    private static void CheckTree(string path)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            RejectLinks(entry);
            if (Directory.Exists(entry)) CheckTree(entry);
        }
    }
}
