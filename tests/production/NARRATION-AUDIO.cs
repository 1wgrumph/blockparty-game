/* NARRATION-AUDIO, issue #157/G-7: test-only red contract, isolated-csharp.
 * The task allocates method=test. Separate suite is required by --only.
 * Oracles: task (one dispatch/utterance, no shell, disabled/unavailable),
 * real CONTROLS-PLAY/narration.json (96 event/text pairs), PCM sample bytes.
 * Only external process I/O is injected; Narration and ProcessStartInfo are real.
 * spd-say 0.12.1 --help confirms one text operand; -- terminates its options.
 * No editor, speech service, display, network or i7 is started by these tests.
 *
 * Required public API in Blockparty.UI (reflection keeps receipt checks runnable):
 *   interface ISpeechOutput { bool Speak(string text); }
 *   sealed class SpeechDispatcherOutput : ISpeechOutput
 *     SpeechDispatcherOutput(Func<ProcessStartInfo,bool> runner);
 *     bool Speak(string text); // false on absent/failed backend, never audio proof
 *   Narration(Accessibility, ISpeechOutput); bool SpeechAvailable { get; }
 * Existing Narration(Accessibility) remains compatible. SpeechAvailable reports
 * the latest dispatch result; Utterances remains the existing text intent log.
 *
 * audio.json schema (REF={path:<repo-relative>,sha256:<hex64>}):
 * {schema:"blockparty-narration-audio/1",tree_hash,binary_sha256,controls:REF,
 *  capture:{host:"i7",platform:"Linux",backend:"speech-dispatcher",
 *           method:"PipeWire-output",source:<output monitor>,player_pid:>0,
 *           command:["pw-record",...],log:REF},rms_threshold:0.001,
 *  utterances:[{event_id,flow,kind,text,narration_enabled:true,wav:REF}],
 *  silence_control:{narration_enabled:false,wav:REF}}.
 * controls REF is the canonical CONTROLS-PLAY receipt, whose build and narration
 * REFs are verified; tree/binary bindings agree throughout. Each narration event
 * needs exactly one matching audio entry, including all three kinds. Each WAV is
 * RIFF PCM signed 16-bit, >=0.3 s; RMS is measured, never trusted from JSON.
 * Threshold .001 full scale (-60 dBFS) separates digital silence/low noise from
 * speech; it is a test threshold, not a claim of intelligibility or calibration.
 * Disabled control uses the same capture source/session and must be below it.
 * GATE-M3 acceptance: receipt tree resolves and its Assets/Packages/ProjectSettings
 * subtrees match an ancestor (including evidence-only/squashed tree equivalence).
 * Metadata/logs attest capture provenance; waveform RMS cannot recognize speech.
 * Fixtures are synthetic validator examples, NEVER i7 acceptance evidence.
 * Amendment (owner 2026-10-30, NA-R1..R4): reject NUL without launching;
 * reject >8192 UTF-8 bytes; argv exactly [-r,-20,--,text]. Capture speech
 * metadata includes that rate before -- (capture may also use --wait).
 * capture.narration_build={tree_hash,binary_sha256,build:REF}; validate the
 * successful Linux build, reference hash, source ancestry and binary binding.
 * Cleanup seam, used by the real capture path, required in Blockparty.UI:
 *   static void NarrationAudioCapture.Run(Action capture, Action close);
 * An injected capture exception must close helpers once and propagate intact.
 * Resource group: none; external audio I/O is injected, no service is started.
 */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Blockparty.Contracts;
using Blockparty.Simulation;
using Blockparty.UI;

public static class NarrationAudioChecks
{
    const string Receipt = "evidence/production/NARRATION-AUDIO/audio.json";
    const string Controls = "evidence/production/CONTROLS-PLAY/controls.json";
    const string Fixtures = "tests/production/fixtures/NARRATION-AUDIO/";
    const string SpeakingBuild = "evidence/production/NARRATION-AUDIO/build.json";
    const int MaxTextBytes = 8192;
    const double Threshold = .001, MinDuration = .3;
    static int failures;

    static void Require(bool ok, string id, string detail)
    { if (!ok) throw new InvalidDataException(id + ": " + detail); }
    static void Run(string id, Action action)
    {
        try { action(); }
        catch (Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            failures++; Console.WriteLine("FAIL " + id + " " + e.Message);
        }
    }
    static string Text(JsonElement o, string key) { return o.GetProperty(key).GetString(); }
    static JsonElement Read(string root, string path)
    {
        Require(File.Exists(Path.Combine(root, path)), "NA-Receipt", "missing " + path);
        using (var d = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, path)))) return d.RootElement.Clone();
    }
    static bool Hex(string value, int length)
    {
        if (value == null || value.Length != length) return false;
        foreach (char c in value) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
        return true;
    }
    static string Hash(string path)
    {
        using (var s = SHA256.Create()) using (var f = File.OpenRead(path))
            return BitConverter.ToString(s.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
    }
    static string Reference(string root, JsonElement r)
    {
        string p = Text(r, "path"), h = Text(r, "sha256");
        Require(!string.IsNullOrWhiteSpace(p) && !Path.IsPathRooted(p)
            && Path.GetFullPath(Path.Combine(root, p)).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal),
            "NA-Reference", "reference must be repo-relative");
        string full = Path.Combine(root, p);
        Require(File.Exists(full) && Hex(h, 64) && Hash(full) == h.ToLowerInvariant(), "NA-Hash", "missing file or tampered hash " + p);
        return p;
    }
    static string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) start.ArgumentList.Add(a);
        using (var p = Process.Start(start))
        {
            string output = p.StandardOutput.ReadToEnd(), error = p.StandardError.ReadToEnd(); p.WaitForExit();
            Require(p.ExitCode == 0, "NA-Tree", error.Trim()); return output.Trim();
        }
    }
    static void Tree(string tree)
    {
        Require(Hex(tree, 40), "NA-Tree", "Git tree hash required");
        tree = Git("rev-parse", "--verify", tree + "^{tree}");
        string sources = Git("rev-parse", tree + ":Assets", tree + ":Packages", tree + ":ProjectSettings");
        foreach (string ancestor in Git("rev-list", "HEAD").Split('\n'))
            if (sources == Git("rev-parse", ancestor + ":Assets", ancestor + ":Packages", ancestor + ":ProjectSettings")) return;
        Require(false, "NA-Tree", "receipt source is not an ancestor/equivalent source tree (GATE-M3)");
    }
    static void Binding(JsonElement o, string tree, string binary)
    { Require(Text(o, "tree_hash") == tree && Text(o, "binary_sha256") == binary, "NA-Binding", "source/player differs"); }

    // The header and PCM bytes supply the oracle, independently of receipt claims.
    static double Wave(string root, JsonElement reference)
    {
        string path = Reference(root, reference);
        using (var stream = File.OpenRead(Path.Combine(root, path))) using (var r = new BinaryReader(stream))
        {
            Require(stream.Length >= 44 && Encoding.ASCII.GetString(r.ReadBytes(4)) == "RIFF", "NA-PCM", "missing RIFF header");
            Require(r.ReadUInt32() == stream.Length - 8 && Encoding.ASCII.GetString(r.ReadBytes(4)) == "WAVE", "NA-PCM", "invalid RIFF size/type");
            ushort format = 0, channels = 0, bits = 0, align = 0; uint rate = 0, byteRate = 0;
            byte[] data = null;
            while (stream.Position + 8 <= stream.Length)
            {
                string chunk = Encoding.ASCII.GetString(r.ReadBytes(4)); uint size = r.ReadUInt32();
                long end = stream.Position + size;
                Require(end + (size % 2) <= stream.Length && size <= int.MaxValue, "NA-PCM", "truncated WAV chunk");
                if (chunk == "fmt ")
                {
                    Require(size >= 16, "NA-PCM", "short fmt chunk");
                    format = r.ReadUInt16(); channels = r.ReadUInt16(); rate = r.ReadUInt32();
                    byteRate = r.ReadUInt32(); align = r.ReadUInt16(); bits = r.ReadUInt16();
                }
                else if (chunk == "data") data = r.ReadBytes((int)size);
                stream.Position = end + (size % 2);
            }
            Require(stream.Position == stream.Length && format == 1 && bits == 16 && channels > 0 && rate > 0
                && align == channels * 2 && byteRate == (long)rate * align && data != null && data.Length > 0 && data.Length % align == 0,
                "NA-PCM", "expected valid signed 16-bit PCM fmt/data");
            Require(data.Length / (double)byteRate >= MinDuration, "NA-Duration", "WAV shorter than 0.3 seconds");
            double squares = 0;
            for (int i = 0; i < data.Length; i += 2)
            {
                double sample = (short)(data[i] | data[i + 1] << 8) / 32768.0; squares += sample * sample;
            }
            return Math.Sqrt(squares / (data.Length / 2));
        }
    }
    static void Validate(string root)
    {
        var audio = Read(root, Receipt);
        Require(Text(audio, "schema") == "blockparty-narration-audio/1", "NA-Receipt", "wrong schema");
        string tree = Text(audio, "tree_hash"), binary = Text(audio, "binary_sha256");
        Tree(tree); Require(Hex(binary, 64), "NA-Binding", "binary SHA256 required");
        Require(Reference(root, audio.GetProperty("controls")) == Controls, "NA-Binding", "expected canonical CONTROLS-PLAY receipt");
        var controls = Read(root, Controls); Binding(controls, tree, binary);
        var build = Read(root, Reference(root, controls.GetProperty("build"))); Binding(build, tree, binary);
        Require(Text(build, "method") == "BuildLinux" && Text(build, "target") == "StandaloneLinux64"
            && build.GetProperty("exit_code").GetInt32() == 0, "NA-Binding", "successful real Linux build required");
        var narration = Read(root, Reference(root, controls.GetProperty("narration"))); Binding(narration, tree, binary);
        var capture = audio.GetProperty("capture");
        Require(Text(capture, "host") == "i7" && Text(capture, "platform") == "Linux"
            && Text(capture, "backend") == "speech-dispatcher" && Text(capture, "method") == "PipeWire-output"
            && !string.IsNullOrWhiteSpace(Text(capture, "source")) && capture.GetProperty("player_pid").GetInt32() > 0,
            "NA-Capture", "real i7 Linux player/PipeWire output provenance required");
        var command = capture.GetProperty("command");
        Require(command.GetArrayLength() > 1 && Path.GetFileName(command[0].GetString()) == "pw-record", "NA-Capture", "pw-record command required");
        Reference(root, capture.GetProperty("log"));
        Require(audio.GetProperty("rms_threshold").GetDouble() == Threshold, "NA-RMS", "threshold must be 0.001 full scale");
        var expected = new Dictionary<string, JsonElement>(); var kinds = new HashSet<string>();
        foreach (var u in narration.GetProperty("utterances").EnumerateArray())
        { expected.Add(Text(u, "event_id"), u); kinds.Add(Text(u, "kind")); }
        Require(kinds.SetEquals(new[] { "selection", "action-result", "focus" }), "NA-Coverage", "missing CONTROLS-PLAY event kind");
        var seen = new HashSet<string>(); var waves = new HashSet<string>();
        foreach (var u in audio.GetProperty("utterances").EnumerateArray())
        {
            string id = Text(u, "event_id"); JsonElement e;
            Require(seen.Add(id) && expected.TryGetValue(id, out e), "NA-Coverage", "duplicate/unknown utterance " + id);
            e = expected[id];
            Require(Text(u, "flow") == Text(e, "flow") && Text(u, "kind") == Text(e, "kind")
                && !string.IsNullOrWhiteSpace(Text(u, "text")) && Text(u, "text") == Text(e, "text"), "NA-Text", "utterance text/event mismatch " + id);
            Require(u.GetProperty("narration_enabled").GetBoolean(), "NA-Enabled", "enabled capture required " + id);
            Require(waves.Add(Text(u.GetProperty("wav"), "path")), "NA-Capture", "each utterance needs its own WAV capture " + id);
            Require(Wave(root, u.GetProperty("wav")) > Threshold, "NA-NonSilence", "silent utterance " + id);
        }
        Require(seen.SetEquals(expected.Keys), "NA-Coverage", "missing utterance");
        var control = audio.GetProperty("silence_control");
        Require(!control.GetProperty("narration_enabled").GetBoolean(), "NA-Disabled", "silence control must disable narration");
        Require(waves.Add(Text(control.GetProperty("wav"), "path")), "NA-Capture", "silence control needs its own capture");
        Require(Wave(root, control.GetProperty("wav")) < Threshold, "NA-Silence", "disabled control is audible");
        ValidateSpeakingBuild(root);
    }

    static Type Product(string name)
    {
        var type = typeof(Narration).Assembly.GetType("Blockparty.UI." + name);
        Require(type != null, "NA-Seam", "missing capability Blockparty.UI." + name); return type;
    }
    static object Output(Func<ProcessStartInfo, bool> runner)
    {
        Type contract = Product("ISpeechOutput"), type = Product("SpeechDispatcherOutput");
        Require(contract.IsInterface && contract.IsAssignableFrom(type), "NA-Seam", "SpeechDispatcherOutput must implement ISpeechOutput");
        Require(contract.GetMethod("Speak", new[] { typeof(string) })?.ReturnType == typeof(bool), "NA-Seam", "bool ISpeechOutput.Speak(string) required");
        return Activator.CreateInstance(type, new object[] { runner });
    }
    static bool Speak(object output, string text)
    { return (bool)output.GetType().GetMethod("Speak", new[] { typeof(string) }).Invoke(output, new object[] { text }); }
    static bool Available(Narration narration)
    {
        var p = typeof(Narration).GetProperty("SpeechAvailable");
        Require(p != null && p.PropertyType == typeof(bool), "NA-Seam", "bool Narration.SpeechAvailable required");
        return (bool)p.GetValue(narration);
    }
    static Narration Wired(Accessibility access, object output)
    { return (Narration)Activator.CreateInstance(typeof(Narration), new object[] { access, output }); }
    static void Launch(ProcessStartInfo start, string text)
    {
        Require(!start.UseShellExecute && Path.GetFileName(start.FileName) == "spd-say" && string.IsNullOrEmpty(start.Arguments),
            "NA-Arguments", "spd-say must run directly without a shell/raw argument string");
        int end = start.ArgumentList.IndexOf("--");
        Require(end >= 0 && end == start.ArgumentList.Count - 2 && start.ArgumentList[end + 1] == text,
            "NA-Arguments", "verbatim text must be one argument after --");
    }
    static void UnitUtterances()
    {
        var sent = new List<string>();
        object output = Output(start => { sent.Add(start.ArgumentList[start.ArgumentList.Count - 1]); Launch(start, sent[sent.Count - 1]); return true; });
        var access = new Accessibility(new SettingsRecord(1, 1f, 1f, false)); access.SetNarrationEnabled(true);
        var narration = Wired(access, output);
        var resident = new ResidentRecord(new ResidentId(1), "Alex", LifeStage.Adult, new HouseholdId(1), new HomeId(1));
        var activity = new ActivityId("ACT-FOOD-01");
        narration.Focus("Camera pan right"); narration.Selection(resident);
        narration.ActionResult(resident, activity, CommandResult.Accept());
        narration.ActionResult(resident, activity, CommandResult.Reject(RejectReason.ObjectUnavailable));
        narration.ActionCompleted(resident, activity); narration.Focus("Camera pan right");
        string[] expected = { "Camera pan right", "Selected Alex", "Alex ACT-FOOD-01 Accepted",
            "Alex ACT-FOOD-01 ObjectUnavailable", "Alex ACT-FOOD-01 Completed", "Camera pan right" };
        Require(sent.Count == expected.Length, "NA-Once", "each utterance, including repeated text, must dispatch once");
        for (int i = 0; i < expected.Length; i++) Require(sent[i] == expected[i], "NA-Once", "wrong utterance/order");
        Require(new List<string>(narration.Utterances).Count == expected.Length && Available(narration), "NA-Once", "text log/availability differs");
        access.SetNarrationEnabled(false);
        narration.Focus("Camera pan right"); narration.Selection(resident);
        narration.ActionResult(resident, activity, CommandResult.Accept()); narration.ActionCompleted(resident, activity);
        Require(sent.Count == expected.Length && new List<string>(narration.Utterances).Count == expected.Length,
            "NA-Disabled", "disabled narration must not emit text or call process runner");
        access.SetNarrationEnabled(true); narration.Focus("Camera pan right");
        Require(sent.Count == expected.Length + 1, "NA-Disabled", "live re-enable did not speak");
    }
    static void UnitArguments()
    {
        var corpus = Read(".", Fixtures + "arguments.json");
        foreach (var value in corpus.GetProperty("texts").EnumerateArray())
        {
            string text = value.GetString(); int calls = 0;
            var output = Output(start => { calls++; Launch(start, text); return true; });
            var access = new Accessibility(new SettingsRecord(1, 1f, 1f, false)); access.SetNarrationEnabled(true);
            var narration = Wired(access, output); narration.Focus(text);
            Require(Available(narration) && calls == 1, "NA-Arguments", "one direct process launch per text required");
        }
    }
    static void UnitUnavailable()
    {
        Exception missing = null;
        Fresh(root =>
        {
            Directory.CreateDirectory(root);
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(Path.Combine(root, "missing-spd-say")) { UseShellExecute = false })) { }
            }
            catch (Exception e) { missing = e; }
        });
        Require(missing != null && missing.GetType().Name == "Win32Exception"
            && (int)missing.GetType().GetProperty("NativeErrorCode").GetValue(missing) == 2,
            "NA-Harness", "real missing executable must produce ENOENT");
        foreach (bool throws in new[] { false, true })
        {
            int calls = 0;
            object output = Output(start => { calls++; if (throws) throw missing; return false; });
            Require(!Speak(output, "Camera pan right"), "NA-Unavailable", "missing/failed backend claimed success");
            var access = new Accessibility(new SettingsRecord(1, 1f, 1f, false)); access.SetNarrationEnabled(true);
            var narration = Wired(access, output); narration.Focus("Camera pan right");
            Require(!Available(narration) && calls == 2, "NA-Unavailable", "unavailable backend must be reported without retry/claimed speech");
        }
    }

    static void UnitNul()
    {
        int calls = 0; string argv = "not launched";
        object output = Output(start => { calls++; argv = JsonSerializer.Serialize(new List<string>(start.ArgumentList)); return true; });
        var access = new Accessibility(new SettingsRecord(1, 1f, 1f, false)); access.SetNarrationEnabled(true);
        var narration = Wired(access, output);
        var resident = new ResidentRecord(new ResidentId(1), "Alex\0suffix", LifeStage.Adult, new HouseholdId(1), new HomeId(1));
        narration.ActionResult(resident, new ActivityId("ACT-FOOD-01"), CommandResult.Accept());
        Require(new List<string>(narration.Utterances)[0] == "Alex\0suffix ACT-FOOD-01 Accepted",
            "NA-R1", "real action-result input must include suffix after NUL");
        Require(!Available(narration) && calls == 0, "NA-R1", "NUL must return false without launching; argv=" + argv);
    }
    static void UnitByteBound(bool oversized)
    {
        // UTF-8, not UTF-16 length: these inputs straddle the owner-approved bound.
        string text = new string('\u00e9', MaxTextBytes / 2) + (oversized ? "x" : "");
        Require(Encoding.UTF8.GetByteCount(text) == MaxTextBytes + (oversized ? 1 : 0), "NA-Harness", "wrong byte-bound input");
        int calls = 0; ProcessStartInfo observed = null;
        object output = Output(start => { calls++; observed = start; return true; });
        bool result = Speak(output, text);
        Require(result == !oversized && calls == (oversized ? 0 : 1), "NA-R2",
            (oversized ? "8193 UTF-8 bytes must return false without launching" : "8192 UTF-8 bytes must launch unchanged"));
        if (!oversized) Launch(observed, text);
    }
    static void UnitRate()
    {
        const string text = "Alex ACT-FOOD-01 Accepted";
        ProcessStartInfo observed = null;
        Require(Speak(Output(start => { observed = start; return true; }), text), "NA-Rate", "normal narration did not launch");
        Launch(observed, text);
        Require(observed.ArgumentList.Count == 4 && observed.ArgumentList[0] == "-r" && observed.ArgumentList[1] == "-20"
            && observed.ArgumentList[2] == "--" && observed.ArgumentList[3] == text,
            "NA-Rate", "expected [-r,-20,--,text], got " + JsonSerializer.Serialize(new List<string>(observed.ArgumentList)));
    }
    static void UnitCaptureFailure()
    {
        Type type = Product("NarrationAudioCapture");
        var run = type.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Action), typeof(Action) }, null);
        Require(run != null && run.ReturnType == typeof(void), "NA-R4", "NarrationAudioCapture.Run(Action capture, Action close) required");
        Fresh(root =>
        {
            Directory.CreateDirectory(root); bool entered = false; int closed = 0; Exception failure = null;
            Action capture = () => { entered = true; using (File.OpenRead(Path.Combine(root, "missing-capture-input"))) { } };
            try { run.Invoke(null, new object[] { capture, (Action)(() => closed++) }); }
            catch (TargetInvocationException e) { failure = e.InnerException; }
            Require(entered && closed == 1, "NA-R4", "failed capture must invoke helper cleanup exactly once");
            Require(failure is FileNotFoundException, "NA-R4", "original capture I/O failure must propagate");
        });
    }
    static void ValidateSpeakingBuild(string root)
    {
        var capture = Read(root, Receipt).GetProperty("capture");
        Require(capture.TryGetProperty("narration_build", out var binding), "NA-NarrationBuild", "missing speaking-player binding");
        var build = Read(root, Reference(root, binding.GetProperty("build")));
        string tree = Text(binding, "tree_hash"), binary = Text(binding, "binary_sha256");
        Tree(tree); Require(Hex(binary, 64), "NA-Binding", "speaking binary SHA256 required");
        Binding(build, tree, binary);
        Require(Text(build, "method") == "BuildLinux" && Text(build, "target") == "StandaloneLinux64"
            && build.GetProperty("exit_code").GetInt32() == 0, "NA-NarrationBuild", "successful speaking Linux build required");
    }
    static void ValidateCaptureRate(string root)
    {
        var capture = Read(root, Receipt).GetProperty("capture");
        Require(capture.TryGetProperty("speech", out var speech), "NA-CaptureRate", "missing capture speech argv");
        var argv = new List<string>(); foreach (var arg in speech.EnumerateArray()) argv.Add(arg.GetString());
        int rate = argv.IndexOf("-r"), end = argv.IndexOf("--");
        Require(argv.Count > 0 && Path.GetFileName(argv[0]) == "spd-say" && rate > 0 && rate + 2 < argv.Count
            && argv[rate + 1] == "-20" && rate + 1 < end && end == argv.Count - 1 && argv.LastIndexOf("-r") == rate,
            "NA-CaptureRate", "capture speech argv must record -r -20 before --; refreshed WAVs required");
    }

    static Dictionary<string, object> Obj(params object[] pairs)
    {
        var d = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) d.Add((string)pairs[i], pairs[i + 1]); return d;
    }
    static void Write(string root, string path, object value)
    {
        string p = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(p)); File.WriteAllText(p, JsonSerializer.Serialize(value));
    }
    static object Ref(string root, string path) { return Obj("path", path, "sha256", Hash(Path.Combine(root, path))); }
    static void Fixture(string root, string fault)
    {
        Directory.CreateDirectory(root);
        var controls = Read(".", Controls);
        foreach (string path in new[] { Controls, Text(controls.GetProperty("build"), "path"), Text(controls.GetProperty("narration"), "path") })
        {
            string dest = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(dest)); File.Copy(path, dest);
        }
        var speaking = Read(".", SpeakingBuild);
        var speakingReceipt = Obj("tree_hash", Text(speaking, "tree_hash"), "binary_sha256", Text(speaking, "binary_sha256"),
            "method", Text(speaking, "method"), "target", Text(speaking, "target"), "exit_code", 0);
        if (fault == "speaking-build-exit") speakingReceipt["exit_code"] = 1;
        if (fault == "speaking-build-tree") speakingReceipt["tree_hash"] = Text(controls, "tree_hash");
        if (fault == "speaking-build-binary") speakingReceipt["binary_sha256"] = new string('0', 64);
        Write(root, SpeakingBuild, speakingReceipt);
        var binding = Obj("tree_hash", Text(speaking, "tree_hash"), "binary_sha256", Text(speaking, "binary_sha256"), "build", Ref(root, SpeakingBuild));
        if (fault == "forged-speaking-binding") binding = Obj("tree_hash", new string('0', 40), "binary_sha256", new string('0', 64),
            "build", Obj("path", "does-not-exist.json", "sha256", new string('0', 64)));
        if (fault == "speaking-build-hash") binding["build"] = Obj("path", SpeakingBuild, "sha256", new string('0', 64));
        if (fault == "speaking-tree-unresolved") binding["tree_hash"] = new string('0', 40);
        foreach (string name in new[] { "signal.wav", "silent.wav", "non-pcm.wav" }) File.Copy(Fixtures + name, Path.Combine(root, name));
        File.WriteAllText(Path.Combine(root, "capture.log"), "SYNTHETIC validator fixture; NOT captured audio or acceptance evidence\n");
        var utterances = new List<object>();
        var narration = Read(".", Text(controls.GetProperty("narration"), "path"));
        foreach (var u in narration.GetProperty("utterances").EnumerateArray())
        {
            string wav = "utterance-" + utterances.Count + ".wav";
            File.Copy(Path.Combine(root, "signal.wav"), Path.Combine(root, wav));
            utterances.Add(Obj("event_id", Text(u, "event_id"), "flow", Text(u, "flow"), "kind", Text(u, "kind"),
                "text", Text(u, "text"), "narration_enabled", true, "wav", Ref(root, wav)));
        }
        var first = (Dictionary<string, object>)utterances[0];
        if (fault == "silent-wav") first["wav"] = Ref(root, "silent.wav");
        if (fault == "missing-utterance") utterances.RemoveAt(0);
        if (fault == "tampered-hash") first["wav"] = Obj("path", "signal.wav", "sha256", new string('0', 64));
        if (fault == "non-pcm") first["wav"] = Ref(root, "non-pcm.wav");
        if (fault == "short-wav")
        {
            byte[] bytes = File.ReadAllBytes(Path.Combine(root, "signal.wav")); Array.Resize(ref bytes, 46);
            Array.Copy(BitConverter.GetBytes((uint)38), 0, bytes, 4, 4); Array.Copy(BitConverter.GetBytes((uint)2), 0, bytes, 40, 4);
            File.WriteAllBytes(Path.Combine(root, "short.wav"), bytes); first["wav"] = Ref(root, "short.wav");
        }
        if (fault == "audible-control") File.Copy(Path.Combine(root, "signal.wav"), Path.Combine(root, "silent.wav"), true);
        var capture = Obj("host", "i7", "platform", "Linux", "backend", "speech-dispatcher", "method", "PipeWire-output",
            "source", "fixture-output-monitor", "player_pid", 1, "command", new[] { "pw-record", "--format", "s16", "signal.wav" },
            "log", Ref(root, "capture.log"), "narration_build", binding, "speech", new[] { "spd-say", "--wait", "-r", "-20", "--" });
        if (fault == "missing-speaking-binding") capture.Remove("narration_build");
        Write(root, Receipt, Obj("schema", "blockparty-narration-audio/1", "tree_hash", Text(controls, "tree_hash"),
            "binary_sha256", Text(controls, "binary_sha256"), "controls", Ref(root, Controls), "rms_threshold", Threshold,
            "capture", capture,
            "utterances", utterances, "silence_control", Obj("narration_enabled", false, "wav", Ref(root, "silent.wav"))));
    }
    static void Fault(string root, string fault)
    {
        if (fault == "shell-argument")
        {
            var f = Read(".", Fixtures + "shell-argument.json");
            Launch(new ProcessStartInfo(Text(f, "file_name"), Text(f, "arguments")) { UseShellExecute = f.GetProperty("use_shell_execute").GetBoolean() }, Text(f, "text"));
        }
        else { Fixture(root, fault); Validate(root); }
    }
    static readonly Dictionary<string, string> Negative = new Dictionary<string, string>
    {
        { "silent-wav", "NA-NonSilence" }, { "missing-utterance", "NA-Coverage" }, { "tampered-hash", "NA-Hash" },
        { "non-pcm", "NA-PCM" }, { "shell-argument", "NA-Arguments" }, { "short-wav", "NA-Duration" }, { "audible-control", "NA-Silence" },
        { "missing-speaking-binding", "NA-NarrationBuild" }, { "forged-speaking-binding", "NA-Hash" },
        { "speaking-build-hash", "NA-Hash" }, { "speaking-build-exit", "NA-NarrationBuild" },
        { "speaking-build-tree", "NA-Binding" }, { "speaking-build-binary", "NA-Binding" }, { "speaking-tree-unresolved", "NA-Tree" }
    };
    static void Fresh(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "narration-audio-" + Guid.NewGuid().ToString("N"));
        try { action(root); } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "negative-fixture")
        {
            if (!Negative.ContainsKey(args[1])) { Console.WriteLine("FAIL NA-Harness unknown fixture"); return 1; }
            Run(args[1], () => Fresh(root => Fault(root, args[1])));
            if (failures == 0) { Console.WriteLine("FAIL NA-Harness invalid fixture accepted"); return 2; }
            return 1;
        }
        Run("NA-Positive fixture only", () => Fresh(root =>
        {
            Fixture(root, null); Validate(root);
            using (var d = JsonDocument.Parse(JsonSerializer.Serialize(Ref(root, "signal.wav"))))
                Require(Math.Abs(Wave(root, d.RootElement) - 3277 / 32768.0) < 1e-12, "NA-Harness", "known PCM RMS differs");
        }));
        foreach (var fault in Negative)
            Run("NA-Detector " + fault.Key, () => Fresh(root =>
            {
                try { Fault(root, fault.Key); }
                catch (InvalidDataException e) { Require(e.Message.StartsWith(fault.Value + ":", StringComparison.Ordinal), "NA-Harness", "wrong rejection " + e.Message); return; }
                Require(false, "NA-Harness", "invalid fixture accepted " + fault.Key);
            }));
        if (!(args.Length == 1 && args[0] == "self-check-only"))
        {
            Run("NA-Once/Disabled", UnitUtterances); Run("NA-Arguments", UnitArguments);
            Run("NA-Unavailable", UnitUnavailable); Run("NA-Audio i7 receipt", () => Validate("."));
            Run("NA-R1 NUL", UnitNul);
            Run("NA-R2 byte-bound valid", () => UnitByteBound(false)); Run("NA-R2 byte-bound oversized", () => UnitByteBound(true));
            Run("NA-Rate", UnitRate); Run("NA-R4 capture cleanup", UnitCaptureFailure);
            Run("NA-Capture rate i7 receipt", () => ValidateCaptureRate("."));
        }
        if (failures == 0) Console.WriteLine("NARRATION-AUDIO ALL PASS");
        return failures == 0 ? 0 : 1;
    }
}
