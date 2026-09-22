using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace InspectionEditor.Services
{
    /// <summary>Owned working directories, never arbitrary recursive temporary-folder cleanup.</summary>
    internal static class PdfWorkingSession
    {
        internal const string LockName = ".red-pdf-session";
        internal const string Ownership = "RED PDF session v1\n";
        internal static readonly TimeSpan Retention = TimeSpan.FromDays(30);
        private static string Root => Path.Combine(AppIdentity.LocalAppDataPath, "PdfAttachments");

        internal static FileStream CreateLease(string directory)
        {
            RequirePlainPath(directory);
            var lease = OpenRetirementLease(Path.Combine(directory, LockName), create: true);
            try
            {
                lease.Write(Encoding.UTF8.GetBytes(Ownership));
                lease.Flush(true);
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        // Automatic maintenance is only permitted at the application-owned root. Custom roots
        // used by tools/tests can create sessions, but can never broaden production scavenging.
        internal static void Scavenge(string requestedRoot, DateTimeOffset now) => Scavenge(requestedRoot, Root, now);

        internal static int Scavenge(string root, string expectedRoot, DateTimeOffset now)
        {
            int deleted = 0;
            try
            {
                root = Path.GetFullPath(root);
                if (!string.Equals(root, Path.GetFullPath(expectedRoot), StringComparison.Ordinal) ||
                    Path.GetFileName(root) != "PdfAttachments" || !Directory.Exists(root)) return 0;
                RequirePlainPath(root);
                foreach (string identity in Directory.GetDirectories(root))
                {
                    if (!Hex(Path.GetFileName(identity), 64) || !PlainDirectory(identity)) continue;
                    foreach (string session in Directory.GetDirectories(identity))
                    {
                        try
                        {
                            if (!Hex(Path.GetFileName(session), 32) || !PlainDirectory(session)) continue;
                            string marker = Path.Combine(session, LockName);
                            string pdf = Path.Combine(session, "Orientation.pdf");
                            if (!OwnedFilesOnly(session) || !Old(session, now) || !Old(marker, now) || !Old(pdf, now)) continue;
                            // This is the same lifetime lock held by every live monitor, including
                            // other RED processes. Missing/legacy/unknown ownership is never inferred.
                            using var ownerLease = OpenRetirementLease(marker);
                            if (ownerLease.Length != Encoding.UTF8.GetByteCount(Ownership)) continue;
                            using (var reader = new StreamReader(ownerLease, Encoding.UTF8, false, 1024, leaveOpen: true))
                                if (reader.ReadToEnd() != Ownership) continue;
                            using (var pdfLease = OpenRetirementLease(pdf))
                            {
                                RequirePlainPath(session);
                                if (!OwnedFilesOnly(session) || !Old(session, now) || !Old(marker, now) || !Old(pdf, now)) continue;
                                DeleteLeasedFile(pdfLease, pdf);
                            }
                            DeleteLeasedFile(ownerLease, marker);
                            ownerLease.Dispose();
                            Directory.Delete(session, false);
                            deleted++;
                        }
                        catch (IOException) { } catch (UnauthorizedAccessException) { }
                    }
                }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
            return deleted;
        }

        private static bool Hex(string name, int length) => name.Length == length &&
            name.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');
        private static bool PlainDirectory(string path) =>
            (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == FileAttributes.Directory;
        private static bool Old(string path, DateTimeOffset now) => File.GetLastWriteTimeUtc(path) < (now - Retention).UtcDateTime;
        private static bool OwnedFilesOnly(string session)
        {
            var entries = Directory.GetFileSystemEntries(session);
            return entries.Length == 2 && entries.All(p =>
                (Path.GetFileName(p) == LockName || Path.GetFileName(p) == "Orientation.pdf") &&
                (File.GetAttributes(p) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0);
        }

        internal static void RequirePlainPath(string path)
        {
            for (var current = new DirectoryInfo(Path.GetFullPath(path)); current != null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("PDF working paths cannot traverse symbolic links or reparse points.");
        }

        internal static FileStream OpenRetirementLease(string path, bool create = false)
        {
            var directories = new System.Collections.Generic.List<SafeFileHandle>();
            SafeFileHandle? handle = null;
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Pin each ancestor without delete-sharing, root first. OPEN_REPARSE_POINT
                    // and handle attributes reject junctions even if a path check raced a swap.
                    var ancestors = new System.Collections.Generic.Stack<string>();
                    for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!); dir != null; dir = dir.Parent)
                        ancestors.Push(dir.FullName);
                    foreach (string ancestor in ancestors)
                    {
                        var directory = CreateFile(ancestor, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                        directories.Add(directory);
                        VerifyPlainHandle(directory);
                    }
                    // DELETE access permits disposition without closing our exclusive handle.
                    handle = CreateFile(path, 0xC0010000, 0, IntPtr.Zero, create ? 1u : 3u, 0x00200080, IntPtr.Zero);
                    VerifyPlainHandle(handle);
                }
                else
                {
                    RequirePlainPath(Path.GetDirectoryName(path)!);
                    if (create)
                    {
                        // CreateNew uses O_EXCL, so an existing symlink cannot be followed.
                        // Let .NET pass open's variadic mode correctly on Apple ARM64.
                        return new FileStream(path, new FileStreamOptions
                        {
                            Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                        });
                    }
                    // Final components must not be followed, including a swap after the path check.
                    bool mac = OperatingSystem.IsMacOS();
                    if (!mac && !OperatingSystem.IsLinux()) throw new IOException("Unsupported PDF lease platform.");
                    int flags = 2 | (mac ? 0x100 : 0x20000) | (mac ? 0x1000000 : 0x80000); // RDWR, NOFOLLOW, CLOEXEC
                    int descriptor = Open(path, flags);
                    if (descriptor < 0) throw LeaseError();
                    handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
                    if (Flock(descriptor, 6) != 0) throw LeaseError(); // exclusive, nonblocking; same protocol as .NET
                }
                return new DirectoryPinnedStream(handle, directories);
            }
            catch
            {
                handle?.Dispose();
                foreach (var directory in directories) directory.Dispose();
                throw;
            }
        }

        private static IOException LeaseError() => new IOException("Unable to lease the PDF working file.",
            new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
        private static void VerifyPlainHandle(SafeFileHandle handle)
        {
            if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var info, 8)) throw LeaseError();
            if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                throw new IOException("PDF leases cannot follow symbolic links or reparse points.");
        }
        private sealed class DirectoryPinnedStream : FileStream
        {
            private readonly System.Collections.Generic.List<SafeFileHandle> _directories;
            internal DirectoryPinnedStream(SafeFileHandle handle, System.Collections.Generic.List<SafeFileHandle> directories)
                : base(handle, FileAccess.ReadWrite) => _directories = directories;
            protected override void Dispose(bool disposing)
            {
                try { base.Dispose(disposing); }
                finally { foreach (var directory in _directories) directory.Dispose(); }
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct AttributeTag { public uint Attributes; public uint ReparseTag; }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
            out AttributeTag information, uint size);
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open(string path, int flags);
        [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        private static extern int Flock(int descriptor, int operation);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);

        // File.Delete cannot delete our FileShare.None handle on Windows. Set delete disposition
        // on THAT handle instead: no close/reopen gap between the comparison and retirement.
        internal static void DeleteLeasedFile(FileStream lease, string path)
        {
            if (OperatingSystem.IsWindows())
            {
                var disposition = new FileDisposition { DeleteFile = true };
                if (!SetFileInformationByHandle(lease.SafeFileHandle, 4, ref disposition,
                    (uint)Marshal.SizeOf<FileDisposition>()))
                    throw new IOException("Unable to retire the PDF working file under its lease.",
                        new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }
            else File.Delete(path); // POSIX unlink; the lease remains held through retirement.
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDisposition { [MarshalAs(UnmanagedType.Bool)] public bool DeleteFile; }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
            ref FileDisposition information, uint size);
    }
}
