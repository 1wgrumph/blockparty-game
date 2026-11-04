// KEY-SETTINGS: player-owned key settings model (OD-3 split from DEMO-CAPTURE).
// Holds the player's own Meta Model API key for the local settings file only:
// enter/replace via SetKey, remove via Clear, presence via HasKey/DisplayStatus.
// The value never leaves through any public string: DisplayStatus and ToString
// report "key set"/"no key" only, and the provider handoff is internal. Save
// writes the user file outside the repo/build with owner-only permissions and
// deletes it when no key is set; Load of a missing or corrupt file yields no
// key so local fallbacks run. Plain C# 9, no UnityEngine dependency.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Blockparty.Services
{
    public sealed class KeySettings
    {
        private const string FilePrefix = "{\"modelApiKey\":\"";
        private const string FileSuffix = "\"}";

        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        private static extern int Open(string path, int flags, int mode);
        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        private static extern int OpenAt(int dir, string path, int flags, int mode);
        [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
        private static extern int MkdirAt(int dir, string path, int mode);
        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fd);
        [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
        private static extern int Fchmod(int fd, int mode);
        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        private static extern int Fsync(int fd);
        [DllImport("libc", EntryPoint = "readlinkat", SetLastError = true)]
        private static extern IntPtr ReadlinkAt(int dir, string path, byte[] buffer, UIntPtr size);
        [DllImport("libc", EntryPoint = "renameat", SetLastError = true)]
        private static extern int RenameAt(int fromDir, string from, int toDir, string to);
        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true)]
        private static extern int UnlinkAt(int dir, string path, int flags);

        // Linux flags: the qualified standalone platform.
        private const int DirectoryFlags = 0x10000 | 0x20000 | 0x80000;
        private const int CreateFlags = 1 | 0x40 | 0x80 | 0x20000 | 0x80000;

        private string key;

        public bool HasKey { get { return !string.IsNullOrEmpty(key); } }

        public string DisplayStatus { get { return HasKey ? "key set" : "no key"; } }

        public override string ToString() { return DisplayStatus; }

        // Enter or replace the key. Blank input is rejected so a cleared or
        // broken entry can never persist as an empty file.
        public void SetKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("Key must be non-blank", "value");
            }
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsControl(value[i]))
                    throw new ArgumentException("Key contains a control character", "value");
            }
            key = value.Trim();
        }

        public void Clear() { key = null; }

        // Provider-only handoff: the value crosses internally, never publicly.
        internal bool TryGetKey(out string value)
        {
            value = key;
            return HasKey;
        }

        // Platform config-dir default from CREDENTIAL-DESIGN, outside repo/build.
        public static string DefaultPath()
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                return Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData), "Blockparty", "settings.json");
            }
            string home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            return Path.Combine(home, ".config", "blockparty", "settings.json");
        }

        // Anchor every Unix operation to a no-follow directory fd; never
        // reopen the temporary pathname after its exclusive creation.
        public void Save(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Settings path must be non-blank", "path");
            path = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);
            if (!HasKey && !Directory.Exists(directory)) return;
            if (Path.DirectorySeparatorChar == '\\')
            {
                SaveWindows(path);
                return;
            }
            int dir = PrivateDirectory(directory);
            string temp = name + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool created = false;
            try
            {
                RefuseSymlink(dir, name);
                if (!HasKey)
                {
                    if (UnlinkAt(dir, name, 0) != 0 && Marshal.GetLastWin32Error() != 2)
                        throw new IOException("Could not remove settings file");
                    return;
                }
                byte[] bytes = Encoding.UTF8.GetBytes(FilePrefix + Escape(key) + FileSuffix);
                int fd = OpenAt(dir, temp, CreateFlags, 384); // 0600, O_EXCL | O_NOFOLLOW.
                if (fd < 0) throw new IOException("Could not create private settings file");
                created = true;
                using (var handle = new SafeFileHandle((IntPtr)fd, true))
                using (var stream = new FileStream(handle, FileAccess.Write))
                {
                    if (Fchmod(fd, 384) != 0) throw new IOException("Could not restrict settings file");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                    if (Fsync(fd) != 0) throw new IOException("Could not sync settings file");
                }
                RefuseSymlink(dir, temp);
                RefuseSymlink(dir, name);
                if (RenameAt(dir, temp, dir, name) != 0)
                    throw new IOException("Could not replace settings file");
                created = false;
                if (Fsync(dir) != 0) throw new IOException("Could not sync settings directory");
            }
            finally
            {
                if (created) UnlinkAt(dir, temp, 0);
                Close(dir);
            }
        }

        private void SaveWindows(string path)
        {
            string directory = Path.GetDirectoryName(path);
            for (string level = directory; !string.IsNullOrEmpty(level); level = Path.GetDirectoryName(level))
                if (Directory.Exists(level) && (File.GetAttributes(level) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Settings directory cannot be a link");
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Settings file cannot be a link");
            if (!HasKey) { File.Delete(path); return; }
            Directory.CreateDirectory(directory);
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(FilePrefix + Escape(key) + FileSuffix);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        // Missing or unreadable files yield no key: first run and corruption
        // both fall back to local play instead of bricking startup.
        public static KeySettings Load(string path)
        {
            KeySettings settings = new KeySettings();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return settings;
            }
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (IOException)
            {
                return settings;
            }
            catch (UnauthorizedAccessException)
            {
                return settings;
            }
            string value = Parse(text);
            if (value != null)
            {
                try { settings.SetKey(value); }
                catch (ArgumentException) { return new KeySettings(); }
            }
            return settings;
        }

        private static int PrivateDirectory(string directory)
        {
            int dir = Open("/", DirectoryFlags, 0);
            if (dir < 0) throw new IOException("Could not open settings directory");
            try
            {
                foreach (string part in directory.Split('/'))
                {
                    if (part.Length == 0) continue;
                    int next = OpenAt(dir, part, DirectoryFlags, 0);
                    if (next < 0 && Marshal.GetLastWin32Error() == 2)
                    {
                        if (MkdirAt(dir, part, 448) != 0 && Marshal.GetLastWin32Error() != 17)
                            throw new IOException("Could not create private settings directory");
                        next = OpenAt(dir, part, DirectoryFlags, 0);
                    }
                    if (next < 0) throw new IOException("Settings directory must exist without links");
                    Close(dir);
                    dir = next;
                }
                if (Fchmod(dir, 448) != 0) // 0700, before any credential bytes exist.
                    throw new IOException("Could not restrict settings directory");
                return dir;
            }
            catch { Close(dir); throw; }
        }

        private static void RefuseSymlink(int directory, string name)
        {
            long size = ReadlinkAt(directory, name, new byte[1], (UIntPtr)1).ToInt64();
            if (size >= 0) throw new IOException("Settings paths cannot be links");
            int error = Marshal.GetLastWin32Error();
            if (error != 2 && error != 22) throw new IOException("Could not inspect settings path");
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            string trimmed = text.Trim();
            if (!trimmed.StartsWith(FilePrefix, StringComparison.Ordinal)
                || !trimmed.EndsWith(FileSuffix, StringComparison.Ordinal)
                || trimmed.Length < FilePrefix.Length + FileSuffix.Length + 1)
            {
                return null;
            }
            string inner = trimmed.Substring(FilePrefix.Length,
                trimmed.Length - FilePrefix.Length - FileSuffix.Length);
            StringBuilder builder = new StringBuilder(inner.Length);
            for (int i = 0; i < inner.Length; i++)
            {
                if (inner[i] == '\\' && i + 1 < inner.Length
                    && (inner[i + 1] == '\\' || inner[i + 1] == '"'))
                {
                    i++;
                    builder.Append(inner[i]);
                }
                else if (inner[i] == '\\' || inner[i] == '"' || char.IsControl(inner[i]))
                {
                    return null;
                }
                else
                {
                    builder.Append(inner[i]);
                }
            }
            string value = builder.ToString();
            return value.Length == 0 ? null : value;
        }
    }
}
