// KEY-SETTINGS production checks. Standalone console program: compiled and
// run with the Unity-bundled Roslyn + netcorerun until TOOLCHAIN lands the
// Unity test path. Proves the OD-3 (2026-10-27) key loader plus settings model
// split from DEMO-CAPTURE: KeyProvider resolves env MODEL_API_KEY first then
// the local settings file, redacts every string form, and reports presence to
// AI-BOUNDARY so missing keys run local fallbacks; KeySettings enters,
// replaces, and clears the key, shows "key set"/"no key" only, and persists to
// the user file with owner-only permissions. Positive checks prove source
// order, round-trip, clear-removes-file, owner-only mode, creation-private
// modes, outside-repo default
// path, redaction, and the no-key fallback with no connected-success claim.
// Negative checks prove blank input is rejected, corrupt files fail closed to
// no key, and the public surface carries no value. No network and no real key:
// synthetic fixtures only, built at runtime so no swept shape appears here.
// Mirrors the AI-BOUNDARY/CREDENTIAL-DESIGN/PROBE-VOICE pattern. Run with the
// single argument "negative-fixture" to assert a corrupted file yields a key:
// the harness must reject it and exit 1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Services;
using Blockparty.Simulation;

public static class KeySettingsChecks
{
    private const string EnvKey = "SYNTHETIC-ENV-FIXTURE-7f3a9c";
    private const string FileKey = "SYNTHETIC-FILE-FIXTURE-51bd2e";
    private const string OtherKey = "SYNTHETIC-OTHER-FIXTURE-9e01ab";

    private static int failures;
    private static bool negativeFixture;
    private static string tempRoot;
    private static string savedEnv;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            Console.WriteLine("PASS " + name);
        }
        else
        {
            failures++;
            Console.WriteLine("FAIL " + name);
        }
    }

    private sealed class Stack
    {
        public WorldState World;
        public Clock Clock;
        public CommandArbitrator Arbitrator;
        public Autonomy Autonomy;
        public ProposalBoundary Boundary;
    }

    private static Stack NewStack(long seed)
    {
        WorldState world = WorldState.CreateSeeded(7L, seed, 0, null);
        Clock clock = new Clock(world);
        CommandArbitrator arbitrator = new CommandArbitrator(world, clock);
        Autonomy autonomy = new Autonomy(world, arbitrator);
        return new Stack
        {
            World = world,
            Clock = clock,
            Arbitrator = arbitrator,
            Autonomy = autonomy,
            Boundary = new ProposalBoundary(world, clock, arbitrator, autonomy),
        };
    }

    private static ResidentRecord AddResident(WorldState world, string name)
    {
        ResidentRecord[] existing = world.ResidentsSnapshot();
        HouseholdId household;
        HomeId home;
        if (existing.Length == 0)
        {
            HomeRecord homeRecord = world.AllocateHome(StateWriter.SimCore, 8);
            HouseholdRecord householdRecord = world.AllocateHousehold(StateWriter.SimCore, "H", homeRecord.Id);
            household = householdRecord.Id;
            home = homeRecord.Id;
        }
        else
        {
            household = existing[0].Household;
            home = existing[0].Home;
        }
        return world.AllocateResident(StateWriter.SimCore, name, LifeStage.Adult, household, home);
    }

    private static ServiceEndpoint D1U()
    {
        return new ServiceEndpoint("meta-d1u", "https://api.meta.ai/v1", "d1-ultra", true);
    }

    private static ServiceEndpoint VoiceSvc()
    {
        return new ServiceEndpoint("meta-voice", "wss://api.meta.ai/v1/asr/realtime", "muse-voice-1", true);
    }

    private static bool LogHas(Stack stack, GatewayOutcome outcome, string fragment)
    {
        IReadOnlyList<ProposalLogEntry> log = stack.Boundary.DecisionLog;
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].Outcome == outcome
                && log[i].Detail.IndexOf(fragment, StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    private static bool LogHasOutcome(Stack stack, GatewayOutcome outcome)
    {
        IReadOnlyList<ProposalLogEntry> log = stack.Boundary.DecisionLog;
        for (int i = 0; i < log.Count; i++)
        {
            if (log[i].Outcome == outcome)
            {
                return true;
            }
        }
        return false;
    }

    private static bool LogCarries(Stack stack, params string[] secrets)
    {
        IReadOnlyList<ProposalLogEntry> log = stack.Boundary.DecisionLog;
        for (int i = 0; i < log.Count; i++)
        {
            string both = log[i].ModelId + "|" + log[i].Detail;
            for (int j = 0; j < secrets.Length; j++)
            {
                if (both.IndexOf(secrets[j], StringComparison.Ordinal) >= 0)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static string TempPath(params string[] parts)
    {
        string path = tempRoot;
        for (int i = 0; i < parts.Length; i++)
        {
            path = Path.Combine(path, parts[i]);
        }
        return path;
    }

    private static void CleanTemp()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static void SetEnv(string value)
    {
        Environment.SetEnvironmentVariable(KeyProvider.EnvVarName, value);
    }

    private static bool SetKeyThrows(string value)
    {
        try
        {
            new KeySettings().SetKey(value);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool SaveThrows(string path)
    {
        try
        {
            KeySettings settings = new KeySettings();
            settings.SetKey(OtherKey);
            settings.Save(path);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static bool ResolveThrows(string path)
    {
        try
        {
            KeyProvider.Resolve(path);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    [DllImport("libc", EntryPoint = "umask")]
    private static extern int Umask(int mask);

    private static bool StatMode(string path, out string mode)
    {
        mode = string.Empty;
        ProcessStartInfo start = new ProcessStartInfo();
        start.FileName = "/usr/bin/stat";
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("%a");
        start.ArgumentList.Add(path);
        start.RedirectStandardOutput = true;
        start.UseShellExecute = false;
        using (Process process = Process.Start(start))
        {
            mode = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
    }

    private static object InstanceOf(object[] keyed, Type type)
    {
        for (int i = 0; i < keyed.Length; i++)
        {
            if (keyed[i] != null && keyed[i].GetType() == type)
            {
                return keyed[i];
            }
        }
        return null;
    }

    private static bool ContainsSecret(string text, string[] secrets)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }
        for (int i = 0; i < secrets.Length; i++)
        {
            if (text.IndexOf(secrets[i], StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }
        return false;
    }

    // Every public string on the key types is read off keyed instances and
    // must carry presence text only, never a fixture value.
    private static bool PublicStringsClean(object[] keyed, Type[] types, string[] secrets, out string witness)
    {
        witness = string.Empty;
        for (int t = 0; t < types.Length; t++)
        {
            FieldInfo[] fields = types[t].GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            for (int i = 0; i < fields.Length; i++)
            {
                if (fields[i].FieldType != typeof(string))
                {
                    continue;
                }
                object target = null;
                if (!fields[i].IsStatic)
                {
                    target = InstanceOf(keyed, types[t]);
                    if (target == null)
                    {
                        continue;
                    }
                }
                if (ContainsSecret((string)fields[i].GetValue(target), secrets))
                {
                    witness = types[t].Name + "." + fields[i].Name;
                    return false;
                }
            }
            PropertyInfo[] properties = types[t].GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
            for (int i = 0; i < properties.Length; i++)
            {
                if (properties[i].PropertyType != typeof(string))
                {
                    continue;
                }
                MethodInfo getter = properties[i].GetGetMethod();
                if (getter == null || getter.GetParameters().Length != 0)
                {
                    continue;
                }
                object target = null;
                if (!getter.IsStatic)
                {
                    target = InstanceOf(keyed, types[t]);
                    if (target == null)
                    {
                        continue;
                    }
                }
                if (ContainsSecret((string)getter.Invoke(target, null), secrets))
                {
                    witness = types[t].Name + "." + properties[i].Name;
                    return false;
                }
            }
            MethodInfo[] methods = types[t].GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].ReturnType != typeof(string)
                    || methods[i].GetParameters().Length != 0
                    || methods[i].IsSpecialName)
                {
                    continue;
                }
                object target = null;
                if (!methods[i].IsStatic)
                {
                    target = InstanceOf(keyed, types[t]);
                    if (target == null)
                    {
                        continue;
                    }
                }
                if (ContainsSecret((string)methods[i].Invoke(target, null), secrets))
                {
                    witness = types[t].Name + "." + methods[i].Name;
                    return false;
                }
            }
            object instance = InstanceOf(keyed, types[t]);
            if (instance != null && ContainsSecret(instance.ToString(), secrets))
            {
                witness = types[t].Name + ".ToString";
                return false;
            }
        }
        return true;
    }

    private static void Run()
    {
        string[] secrets = new string[] { EnvKey, FileKey, OtherKey };

        // Enter, replace, clear: presence only, "key set"/"no key" strings.
        {
            KeySettings settings = new KeySettings();
            Check(!settings.HasKey && settings.DisplayStatus == "no key", "Settings.StartsEmpty");
            settings.SetKey(FileKey);
            Check(settings.HasKey && settings.DisplayStatus == "key set", "Settings.EnterShowsSet");
            settings.SetKey(OtherKey);
            string replaced;
            Check(settings.TryGetKey(out replaced) && replaced == OtherKey, "Settings.ReplaceSwapsValue");
            Check(settings.DisplayStatus == "key set", "Settings.ReplaceStaysSet");
            settings.Clear();
            Check(!settings.HasKey && settings.DisplayStatus == "no key", "Settings.ClearShowsNone");
            settings.SetKey(FileKey);
            Check(settings.ToString() == "key set", "Settings.ToStringRedacted");
            Check(settings.ToString().IndexOf(FileKey, StringComparison.Ordinal) < 0, "Settings.ToStringCarriesNoValue");
        }

        // Blank input is rejected: nothing blank can persist or resolve.
        {
            Check(SetKeyThrows(null), "Blank.NullRejected");
            Check(SetKeyThrows(string.Empty), "Blank.EmptyRejected");
            Check(SetKeyThrows("   "), "Blank.WhitespaceRejected");
            Check(SaveThrows(null), "Blank.SaveNullPathRejected");
            Check(SaveThrows("  "), "Blank.SaveBlankPathRejected");
            Check(ResolveThrows(null), "Blank.ResolveNullPathRejected");
            Check(ResolveThrows(string.Empty), "Blank.ResolveEmptyPathRejected");
        }

        // Round-trip through a nested user path: the file is created with the
        // value inside, and loading restores the same entry.
        {
            string path = TempPath("sub", "settings.json");
            KeySettings settings = new KeySettings();
            settings.SetKey(FileKey);
            settings.Save(path);
            Check(File.Exists(path), "RoundTrip.FileCreated");
            Check(File.ReadAllText(path).IndexOf(FileKey, StringComparison.Ordinal) >= 0, "RoundTrip.ValuePersisted");
            KeySettings loaded = KeySettings.Load(path);
            string value;
            Check(loaded.HasKey && loaded.DisplayStatus == "key set", "RoundTrip.LoadsSet");
            Check(loaded.TryGetKey(out value) && value == FileKey, "RoundTrip.ValueMatches");
        }

        // Clear removes the file: no stale key survives on disk.
        {
            string path = TempPath("sub", "settings.json");
            KeySettings loaded = KeySettings.Load(path);
            loaded.Clear();
            loaded.Save(path);
            Check(!File.Exists(path), "Clear.RemovesFile");
            Check(!KeySettings.Load(path).HasKey, "Clear.LoadsEmpty");
        }

        // Missing and corrupt files fail closed to no key so local fallbacks
        // run. In negative-fixture mode the corrupt check asserts a key is
        // yielded, which must fail.
        {
            Check(!KeySettings.Load(TempPath("missing", "settings.json")).HasKey, "FailClosed.MissingIsNoKey");
            Check(KeySettings.Load(TempPath("missing", "settings.json")).DisplayStatus == "no key", "FailClosed.MissingShowsNone");
            string corruptPath = TempPath("corrupt.json");
            File.WriteAllText(corruptPath, "not json {{{");
            bool corruptHasKey = KeySettings.Load(corruptPath).HasKey;
            Check(negativeFixture ? corruptHasKey : !corruptHasKey, "FailClosed.CorruptFile");
            string emptyPath = TempPath("empty.json");
            File.WriteAllText(emptyPath, string.Empty);
            Check(!KeySettings.Load(emptyPath).HasKey, "FailClosed.EmptyIsNoKey");
        }

        // The user file is owner-only on disk, proven by the system stat tool.
        {
            string path = TempPath("perm", "settings.json");
            KeySettings settings = new KeySettings();
            settings.SetKey(FileKey);
            settings.Save(path);
            if (Path.DirectorySeparatorChar == '/' && File.Exists("/usr/bin/stat"))
            {
                string mode;
                Check(StatMode(path, out mode), "Perms.StatRan");
                Check(mode == "600", "Perms.OwnerOnly");
            }
            else
            {
                Check(true, "Perms.SkippedOffLinux");
            }
        }

        // Creation is private from the start: new parent dirs land 0700 and
        // the file is never observable wider than 0600, proven under umask 0
        // so default modes cannot hide a wide creation. Red on the temp-then-
        // chmod baseline (dirs land 777), green with owner-only creation.
        {
            string path = TempPath("priv", "nested", "settings.json");
            KeySettings settings = new KeySettings();
            settings.SetKey(FileKey);
            bool unix = Path.DirectorySeparatorChar == '/' && File.Exists("/usr/bin/stat");
            int savedMask = 0;
            if (unix)
            {
                savedMask = Umask(0);
            }
            try
            {
                settings.Save(path);
            }
            finally
            {
                if (unix)
                {
                    Umask(savedMask);
                }
            }
            if (unix)
            {
                string dirMode;
                Check(StatMode(TempPath("priv"), out dirMode), "CreateMode.StatDirRan");
                Check(dirMode == "700", "CreateMode.DirPrivate");
                string nestedMode;
                Check(StatMode(TempPath("priv", "nested"), out nestedMode), "CreateMode.StatNestedRan");
                Check(nestedMode == "700", "CreateMode.NestedDirPrivate");
                string fileMode;
                Check(StatMode(path, out fileMode), "CreateMode.StatFileRan");
                Check(fileMode == "600", "CreateMode.FileOwnerOnly");
                Check(!File.Exists(path + ".tmp"), "CreateMode.NoTempLeft");
                settings.SetKey(OtherKey);
                settings.Save(path);
                KeySettings replaced = KeySettings.Load(path);
                string current;
                Check(replaced.TryGetKey(out current) && current == OtherKey, "CreateMode.ReplaceOverwrites");
                Check(!File.Exists(path + ".tmp"), "CreateMode.ReplaceNoTempLeft");
            }
            else
            {
                Check(true, "CreateMode.SkippedOffLinux");
            }
        }

        // The default path sits in the platform config dir, outside the repo
        // and the shipped build: never under Assets, StreamingAssets, or
        // Resources, and never relative.
        {
            string path = KeySettings.DefaultPath();
            Check(path.EndsWith("settings.json", StringComparison.Ordinal), "DefaultPath.FileName");
            Check(path.IndexOf("blockparty", StringComparison.OrdinalIgnoreCase) >= 0, "DefaultPath.AppDir");
            Check(Path.IsPathRooted(path), "DefaultPath.Rooted");
            Check(path.IndexOf("Assets", StringComparison.Ordinal) < 0, "DefaultPath.NotUnderAssets");
            Check(path.IndexOf("StreamingAssets", StringComparison.Ordinal) < 0, "DefaultPath.NotUnderStreamingAssets");
            Check(path.IndexOf("Resources", StringComparison.Ordinal) < 0, "DefaultPath.NotUnderResources");
        }

        // Source order: env first, settings file second, each proven by the
        // exact value resolved.
        SetEnv(null);
        {
            string path = TempPath("order", "settings.json");
            KeySettings settings = new KeySettings();
            settings.SetKey(FileKey);
            settings.Save(path);
            SetEnv(EnvKey);
            KeyResolution envFirst = KeyProvider.Resolve(path);
            Check(envFirst.HasCredential, "Order.EnvPresent");
            Check(envFirst.Source == KeySource.Environment, "Order.EnvSource");
            string winner;
            Check(envFirst.TryGetKey(out winner) && winner == EnvKey, "Order.EnvWins");
            SetEnv(null);
            KeyResolution fileNext = KeyProvider.Resolve(path);
            Check(fileNext.HasCredential, "Order.FilePresent");
            Check(fileNext.Source == KeySource.SettingsFile, "Order.FileSource");
            string filed;
            Check(fileNext.TryGetKey(out filed) && filed == FileKey, "Order.FileFallsBack");
            Check(fileNext.ToString().IndexOf(FileKey, StringComparison.Ordinal) < 0, "Order.ToStringRedacted");
        }

        // No source yields no key: presence false, source None, redacted text.
        {
            KeyResolution none = KeyProvider.Resolve(TempPath("absent", "settings.json"));
            Check(!none.HasCredential, "NoKey.Absent");
            Check(none.Source == KeySource.None, "NoKey.SourceNone");
            Check(none.ToString() == "no key", "NoKey.ToString");
        }

        // Redaction covers every embedded form, including repeated values.
        {
            SetEnv(EnvKey);
            KeyResolution keyed = KeyProvider.Resolve(TempPath("absent", "settings.json"));
            string bearerLine = "Bearer " + EnvKey;
            string redacted = keyed.Redact("first " + bearerLine + " then " + EnvKey);
            Check(redacted.IndexOf(EnvKey, StringComparison.Ordinal) < 0, "Redact.NoValueLeft");
            Check(redacted.IndexOf("[REDACTED]", StringComparison.Ordinal) >= 0, "Redact.MarkerPresent");
            Check(keyed.Redact(null) == string.Empty, "Redact.NullEmpty");
            Check(keyed.Redact(string.Empty) == string.Empty, "Redact.EmptyEmpty");
            SetEnv(null);
            KeyResolution none = KeyProvider.Resolve(TempPath("absent", "settings.json"));
            Check(none.Redact("plain text") == "plain text", "Redact.AbsentPassesThrough");
            SetEnv(EnvKey);
        }

        // The public surface carries presence only: every public string read
        // off keyed instances holds no fixture value, and the presence
        // signals are booleans.
        {
            KeySettings settings = new KeySettings();
            settings.SetKey(FileKey);
            KeyResolution keyed = new KeyResolution(EnvKey, KeySource.Environment);
            object[] keyedInstances = new object[] { settings, keyed };
            Type[] types = new Type[] { typeof(KeySettings), typeof(KeyResolution), typeof(KeyProvider) };
            string witness;
            Check(PublicStringsClean(keyedInstances, types, secrets, out witness), "Api.PublicStringsRedacted");
            Check(witness.Length == 0, "Api.NoWitness");
            Check(typeof(KeySettings).GetProperty("HasKey").PropertyType == typeof(bool), "Api.HasKeyIsBool");
            Check(typeof(KeyResolution).GetField("HasCredential").FieldType == typeof(bool), "Api.HasCredentialIsBool");
            Check(typeof(KeyResolution).GetField("Source").FieldType == typeof(KeySource), "Api.SourceIsEnum");
        }

        // No key reaches AI-BOUNDARY as false: decisions refuse with
        // Unprovisioned plus MissingCredential, the local fallback runs, and
        // nothing pending means no connected success is ever claimed.
        Stack keyless;
        {
            SetEnv(null);
            KeyResolution none = KeyProvider.Resolve(TempPath("absent", "settings.json"));
            Check(!none.HasCredential, "Boundary.NoKeyReported");
            keyless = NewStack(301L);
            ResidentRecord resident = AddResident(keyless.World, "Ada");
            Check(keyless.Boundary.RequestDecision(resident.Id, D1U(), none.HasCredential, true) == null, "Boundary.DecisionRefused");
            Check(LogHas(keyless, GatewayOutcome.Unprovisioned, "Unprovisioned:MissingCredential"), "Boundary.DecisionLogged");
            Check(LogHas(keyless, GatewayOutcome.Unprovisioned, "Fallback:"), "Boundary.FallbackRuns");
            Check(keyless.Autonomy.Decisions.Count == 1, "Boundary.AutonomyDecided");
            Check(keyless.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), none.HasCredential, true) == null, "Boundary.ServiceRefused");
            Check(keyless.Boundary.PendingCount == 0, "Boundary.NothingPending");
            Check(keyless.Boundary.Tick() == 0, "Boundary.TickIdle");
            Check(!LogHasOutcome(keyless, GatewayOutcome.Applied), "Boundary.NoAppliedClaim");
        }

        // The keyed path still issues; rejection reasons stay fixed codes and
        // the log carries no fixture value across both stacks.
        {
            SetEnv(EnvKey);
            KeyResolution keyed = KeyProvider.Resolve(TempPath("absent", "settings.json"));
            Check(keyed.HasCredential, "Boundary.KeyReported");
            Stack stack = NewStack(302L);
            GatewayRequest request = stack.Boundary.RequestService(null,
                GatewayPurpose.VoiceTranscription, VoiceSvc(), keyed.HasCredential, true);
            Check(request != null, "Boundary.KeyedIssued");
            Check(stack.Boundary.CompleteService(request.Id, false, "UpstreamError") == ProposalVerdict.DiscardedInvalid, "Boundary.FixedCodeDiscarded");
            Check(LogHas(stack, GatewayOutcome.Malformed, "Parse:UpstreamError"), "Boundary.FixedCodeLogged");
            Check(!LogCarries(stack, secrets), "Boundary.KeyedLogClean");
            Check(!LogCarries(keyless, secrets), "Boundary.KeylessLogClean");
            SetEnv(null);
        }
    }

    public static int Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "negative-fixture", StringComparison.Ordinal))
            {
                negativeFixture = true;
            }
        }
        if (negativeFixture)
        {
            Console.WriteLine("KEY-SETTINGS negative-fixture mode: corrupt file must still fail closed");
        }
        tempRoot = Path.Combine(Path.GetTempPath(), "key-settings-checks");
        CleanTemp();
        savedEnv = Environment.GetEnvironmentVariable(KeyProvider.EnvVarName);
        SetEnv(null);
        try
        {
            Run();
        }
        finally
        {
            SetEnv(savedEnv);
            CleanTemp();
        }
        if (failures == 0)
        {
            Console.WriteLine("KEY-SETTINGS ALL PASS");
        }
        return failures;
    }
}
