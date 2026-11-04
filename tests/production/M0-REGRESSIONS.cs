// Accepted M0-F5/F6 regressions. Only disposable files and synthetic keys.
using System;
using System.IO;
using System.Runtime.InteropServices;
using Blockparty.Services;

public static class MilestoneKeyChecks
{
    [DllImport("libc", EntryPoint = "symlink", SetLastError = true)]
    static extern int Symlink(string target, string link);

    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "race")
        {
            var key = new KeySettings();
            key.SetKey(new string('x', 20 * 1024 * 1024));
            try { key.Save(args[1]); return 0; }
            catch (IOException) { return 2; }
        }
        string root = Path.Combine(Path.GetTempPath(), "blockparty-m0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int failures = 0;
        try
        {
            if (args.Length == 0 || args[0] == "M0-F5")
            {
                string victim = Path.Combine(root, "victim");
                File.WriteAllText(victim, "unchanged synthetic content");
                string link = Path.Combine(root, "settings.json");
                if (Symlink(victim, link) != 0) throw new Exception("fixture symlink failed");
                var key = new KeySettings(); key.SetKey("synthetic-m0-key");
                bool refused = false;
                try { key.Save(link); } catch (IOException) { refused = true; }
                if (!refused || File.ReadAllText(victim) != "unchanged synthetic content") failures++;
                File.Delete(link);
                string linkedDir = Path.Combine(root, "linked-dir");
                string realDir = Path.Combine(root, "real-dir"); Directory.CreateDirectory(realDir);
                if (Symlink(realDir, linkedDir) != 0) throw new Exception("fixture directory symlink failed");
                refused = false;
                try { key.Save(Path.Combine(linkedDir, "settings.json")); } catch (IOException) { refused = true; }
                if (!refused || File.Exists(Path.Combine(realDir, "settings.json"))) failures++;
                File.Delete(linkedDir);
                Console.WriteLine((failures == 0 ? "PASS " : "FAIL ") + "M0-F5 symlink target/directory refused");
            }
            if (args.Length == 0 || args[0] == "M0-F6")
            {
                int before = failures;
                string path = Path.Combine(root, "invalid.json");
                foreach (string value in new[] { "   ", "\t", "\n" })
                {
                    File.WriteAllText(path, "{\"modelApiKey\":\"" + value + "\"}");
                    if (KeySettings.Load(path).HasKey || KeyProvider.Resolve(path).HasCredential) failures++;
                }
                File.WriteAllText(path, "{\"modelApiKey\":\"  synthetic-key  \"}");
                if (!KeyProvider.Resolve(path).Redact("synthetic-key").Equals("[REDACTED]")) failures++;
                Console.WriteLine((before == failures ? "PASS " : "FAIL ") + "M0-F6 invalid entry falls back; valid entry normalized");
            }
        }
        finally { Directory.Delete(root, true); }
        return failures == 0 ? 0 : 1;
    }
}
