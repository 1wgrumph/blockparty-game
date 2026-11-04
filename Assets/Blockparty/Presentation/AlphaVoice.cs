using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Blockparty.AI;
using Blockparty.Contracts;
using Blockparty.Presentation;
using Blockparty.Simulation;
using Blockparty.UI;
using UnityEngine;

// VOICE-LIVE (M5-OD-38): voice commands in the live Alpha player. Hold the
// talk key (default V) to record; release sends the segment through VOICE@1,
// so every command takes the ordinary CommandArbitrator path. The optional
// "Hey Meta" activator (off by default) keeps the microphone open and sends
// only the speech segments the local VoiceGate finds; a command runs only when
// its transcript starts with "hey meta". Custom phrases are VoiceAliases.
// --alpha-voice-fake=<script> swaps the microphone and Muse Voice for a
// synthetic signal and scripted transcripts ("ptt|text" answers the next
// talk-key segment, "say|text" emits one speech burst for the activator), so
// the player can be driven without a microphone, a key or a network call.
public sealed class AlphaVoice
{
    public delegate IEnumerator Transcriber(VoiceAdapter adapter, ProposalBoundary boundary, GatewayRequest request, byte[] pcm);
    public delegate IEnumerator Interpreter(VoiceAdapter adapter, ProposalBoundary boundary, GatewayRequest request);

    static readonly ServiceEndpoint Endpoint = new ServiceEndpoint("Muse Voice Transcribe",
        "wss://api.meta.ai/v1/asr/realtime", "muse-voice-transcribe-1.0", true);
    // VOICE-AI: the decision model that picks an offered command when the local grammar finds none
    // (muse-spark-1.3, owner decision 2026-11-04: the key has no d1-ultra).
    static readonly ServiceEndpoint InterpretEndpoint = new ServiceEndpoint("d1-ultra",
        "https://api.meta.ai/v1/responses", "muse-spark-1.3", true);
    // Keys AlphaDirector reads directly or that close pages; the talk key may not take one.
    static readonly string[] ReservedKeys = { "J", "K", "L", "P", "F5", "F9", "F10", "Escape", "Mouse0", "Mouse1" };
    const int Rate = VoiceGate.SampleRate;
    const int MaxPttSamples = Rate * 10;

    readonly MonoBehaviour host;
    readonly Transcriber transcribe;
    readonly Interpreter interpret;
    readonly Action<string> log;
    readonly Queue<string> fakeScript;
    readonly VoiceGate gate = new VoiceGate();
    readonly List<float> ptt = new List<float>();
    readonly VoiceClip preroll = new VoiceClip();   // last 0.5 s of the open microphone
    int prerollCount;
    public readonly VoiceSettings Settings;
    string directory;
    Accessibility access;
    WorldState world;
    Clock clock;
    VoiceAdapter adapter;
    ProposalBoundary boundary;

    AudioClip clip;
    int clipRate, readPos;
    VoiceResampler resampler;
    int burst, burstGap;
    bool activatorArmed;
    // META-LIVE: a bare "Hey Meta" opens this follow-up window; the next segment is the command.
    public const float FollowUpSeconds = 6f;
    float followUntil;
    float[] held;   // one activator segment that ended while the previous one was still out

    // True while the follow-up window after a bare "Hey Meta" is open (the HUD shows Listening).
    public bool AwaitingCommand { get { return Time.unscaledTime < followUntil; } }
    double phase;

    // Delivers one accepted presentation command (select, follow, stop
    // following, open map, save game) to the player's own controls. Returns the
    // status to show instead of the adapter's, or null to keep it.
    public Func<CommandEnvelope, string> Present;

    // SAM look (owner 2026-11-04): "find / show me / where is / look at / follow <phrase>" for
    // something that is not a resident. The SAM lane subscribes; it survives world rebinds.
    public event Action<string> LookRequested;

    // VOICE-SOCIAL (M5-OD-53): the selected resident, the live simulation and the
    // plan dialog's party venue, so chat, friendly and party phrases match the menus.
    public Func<ResidentId?> Selected;
    public LiveSimulation Live;
    public Func<long> PartyVenue;

    // Meanings of an ambiguous transcript, shown for the player to pick (empty when none).
    public IReadOnlyList<string> Choices { get { return adapter == null || Busy ? NoChoices : adapter.CandidateMeanings; } }
    static readonly IReadOnlyList<string> NoChoices = new string[0];

    public bool Listening { get; private set; }
    public bool Busy { get; private set; }
    public bool Fake { get { return fakeScript != null; } }
    public string Heard { get; private set; } = "";
    public string Status { get; private set; } = "";
    public float ShownUntil { get; private set; }

    // Voice page state (drawn by AlphaHudVoice).
    public bool PageOpen { get; private set; }
    public int Focus;
    public bool Capturing { get; private set; }
    public string Draft = "";
    public int DraftCommand;
    public string PageMessage { get; private set; } = "";
    public bool PageMessageIsError { get; private set; }
    public const int PageRows = 5;   // talk key, activator, command, add, back

    public AlphaVoice(MonoBehaviour host, string directory, string fakeScriptPath, Action<string> log)
    {
        this.host = host;
        this.directory = directory;
        this.log = log ?? delegate { };
        Settings = VoiceSettings.Load(directory);
        if (!string.IsNullOrEmpty(fakeScriptPath))
        {
            fakeScript = new Queue<string>(File.ReadAllLines(fakeScriptPath));
            transcribe = FakeTranscribe;
            interpret = FakeInterpret;
            this.log("voice fake script lines=" + fakeScript.Count);
        }
        else
        {
            LiveTransport transport = new LiveTransport(host);
            transcribe = (a, b, r, pcm) => transport.TranscribeVoice(a, b, r, pcm);
            interpret = (a, b, r) => transport.InterpretVoice(a, b, r);
        }
        this.log("voice settings loaded key=" + Settings.TalkKey + " activator=" + (Settings.Activator ? "on" : "off")
            + " aliases=" + Settings.Aliases.Entries.Count);
    }

    // Rebinds to the current world (new game, continue and reload replace it).
    public void Bind(WorldState world, Clock clock, CommandArbitrator commands, Autonomy autonomy,
        BlockSites sites, ActivityExecutor executor, Accessibility access)
    {
        this.world = world;
        this.clock = clock;
        this.access = access;
        boundary = new ProposalBoundary(world, clock, commands, autonomy);
        adapter = new VoiceAdapter(world, boundary, commands, sites, executor);
        adapter.LookRequested += phrase => { log("voice look phrase=" + Q(phrase)); if (LookRequested != null) LookRequested(phrase); };
        Busy = false;
        Reconcile();
        foreach (KeyValuePair<string, string> off in Settings.Aliases.Inactive) log("voice alias inactive phrase=" + Q(off.Key) + " reason=" + Q(off.Value));
    }

    // Phrases that would shadow a resident of this world are off (and say why).
    void Reconcile()
    {
        List<string> names = new List<string>();
        if (world != null) foreach (ResidentRecord r in world.ResidentsSnapshot()) names.Add(r.DisplayName);
        Settings.Aliases.Reconcile(names);
    }

    public string TalkKeyConflict()
    {
        string owner = access == null ? "" : access.ResolveInput(Settings.TalkKey);
        return owner.Length > 0 ? ControlsLabel(owner) : Array.IndexOf(ReservedKeys, Settings.TalkKey) >= 0 ? Settings.TalkKey : "";
    }

    // canTalk is false while the title, Settings or a page owns the keys.
    public void Tick(float dt, bool canTalk)
    {
        if (adapter == null) return;
        boundary.AdvanceRealSeconds(Math.Max(0.0, dt));
        KeyCode key;
        bool keyOk = Enum.TryParse(Settings.TalkKey, out key) && TalkKeyConflict().Length == 0;
        if (canTalk && keyOk && !Busy && !Listening && Input.GetKeyDown(key)) BeginTalk();
        IReadOnlyList<string> choices = Choices;
        for (int i = 0; canTalk && !Listening && i < choices.Count && i < 9; i++)
        {
            string digit = "Alpha" + (i + 1);
            if (access != null && access.ResolveInput(digit).Length == 0 && digit != Settings.TalkKey && Input.GetKeyDown(KeyCode.Alpha1 + i)) { Choose(i); break; }
        }
        // M5-OD-48: the activator listens only while the game window has focus; push-to-talk is unchanged.
        bool focused = Application.isFocused;
        bool armed = Settings.Activator && canTalk && focused;
        if (armed != activatorArmed) { activatorArmed = armed; log("voice activator listening=" + (armed ? "true" : "false") + " focused=" + (focused ? "true" : "false")); }
        // The microphone stays open while push-to-talk is usable so the clip can start 0.5 s before the key.
        bool wantMic = Listening || armed || (canTalk && keyOk && focused);
        if (!wantMic) { StopMic(); gate.Reset(); preroll.Clear(); return; }
        if (!StartMic()) return;
        float[] samples = Pull(dt);
        if (Listening)
        {
            int room = MaxPttSamples + prerollCount - ptt.Count;
            for (int i = 0; i < samples.Length && i < room; i++) ptt.Add(samples[i]);
            if (!Input.GetKey(key) || !canTalk) EndTalk();
            return;
        }
        preroll.Feed(samples, samples.Length);
        if (!armed) return;
        gate.Feed(samples, samples.Length);
        while (gate.Ready.Count > 0)
        {
            float[] segment = gate.Ready.Dequeue();
            if (Busy) { held = segment; log("voice segment held busy samples=" + segment.Length); continue; }
            log("voice segment samples=" + segment.Length);
            Send(VoiceGate.Pcm16(segment), true);
        }
    }

    void BeginTalk()
    {
        Listening = true;
        ptt.Clear();
        ptt.AddRange(preroll.Take());
        prerollCount = ptt.Count;
        Heard = "";
        Status = "Listening… release " + Settings.TalkKey + " to send to Muse Voice.";
        ShownUntil = float.MaxValue;
        log("voice ptt down key=" + Settings.TalkKey);
    }

    void EndTalk()
    {
        Listening = false;
        float[] clip = VoiceClip.Trim(ptt, gate.Threshold);
        log("voice ptt up samples=" + ptt.Count + " preroll=" + prerollCount + " sent=" + clip.Length);
        if (ptt.Count - prerollCount < Rate / 5) { Show("Hold " + Settings.TalkKey + " while you speak."); return; }
        Send(VoiceGate.Pcm16(clip), false);
    }

    void Send(byte[] pcm, bool activator)
    {
        Reconcile();   // resident names can change during play
        adapter.Aliases = Settings.Aliases;
        bool followUp = activator && AwaitingCommand;
        if (followUp) { followUntil = 0f; log("voice follow-up command"); }
        adapter.RequireActivator = activator && !followUp;
        adapter.SelectedResident = Selected;
        adapter.Live = Live;
        adapter.PartyVenue = PartyVenue;
        bool micGranted = Fake || (Application.HasUserAuthorization(UserAuthorization.Microphone) && Microphone.devices.Length > 0);
        GatewayRequest request = adapter.Request(Endpoint, true, true, micGranted);
        if (request == null) { Show(adapter.Prompt); log("voice refused prompt=" + Q(adapter.Prompt)); return; }
        Busy = true;
        Status = "Sending to Muse Voice…";
        ShownUntil = float.MaxValue;
        log("voice send kind=" + (activator ? "activator" : "ptt") + " bytes=" + pcm.Length);
        host.StartCoroutine(Run(request, pcm, activator));
    }

    IEnumerator Run(GatewayRequest request, byte[] pcm, bool activator)
    {
        float started = Time.realtimeSinceStartup;
        VoiceAdapter sent = adapter;
        yield return transcribe(sent, boundary, request, pcm);
        if (sent != adapter) yield break;   // the world was replaced while the request was out
        string text = sent.Transcript.Text;
        // VOICE-AI: no local command, so the decision model picks one offered command (short timeout).
        GatewayRequest asked = sent.NeedsInterpretation ? sent.RequestInterpretation(InterpretEndpoint, true) : null;
        if (asked != null)
        {
            Heard = text;
            Status = "Heard: “" + text + "” — working out what you meant…";
            log("voice interpret send options=" + sent.InterpretOptions().Count);
            yield return interpret(sent, boundary, asked);
            if (sent != adapter) yield break;
        }
        Busy = false;
        // Activator speech without "hey meta" is background talk: neither shown nor logged.
        bool ambient = sent.Ambient || (activator && text.Length == 0);   // a failed activator segment stays silent, as before
        Heard = ambient ? "" : text;
        string prompt = sent.Prompt;
        // A missing key settles the boundary without a frame, so the adapter still says Listening.
        if (prompt.StartsWith("Listening", StringComparison.Ordinal))
            prompt = "Voice is unavailable (no Muse key?). Keyboard and mouse still work.";
        if (!ambient && !sent.LastResult.HasValue && sent.Candidates.Count == 0)
        {
            string stripped;
            if (!VoiceAdapter.StripWake(text, out stripped)) stripped = text;
            prompt = Settings.Aliases.InactiveReason(stripped) ?? prompt;
        }
        if (sent.WakeOnly && !activator) prompt = "Hold " + Settings.TalkKey + " and say the command after \u201cHey Meta\u201d.";
        if (!ambient) prompt = Deliver(sent, prompt);
        if (ambient) { Status = ""; ShownUntil = 0f; }
        else Show(prompt);
        if (sent.WakeOnly && activator) { followUntil = Time.unscaledTime + FollowUpSeconds; ShownUntil = followUntil; }
        log("voice result kind=" + (activator ? "activator" : "ptt")
            + " heard=" + (ambient ? "<unprefixed len=" + text.Length + ">" : Q(Heard)) + " meaning=" + Q(sent.Meaning)
            + " accepted=" + (sent.LastResult.HasValue && sent.LastResult.Value.Accepted ? "true" : "false")
            + " paused=" + (clock.Paused ? "true" : "false") + " speed=" + clock.Speed + " prompt=" + Q(prompt)
            + " ms=" + Mathf.RoundToInt((Time.realtimeSinceStartup - started) * 1000f) + (AwaitingCommand ? " followup=open" : ""));
        if (held != null && sent == adapter) { float[] next = held; held = null; log("voice segment samples=" + next.Length); Send(VoiceGate.Pcm16(next), true); }
    }

    // The one hand-off of an accepted command's player-facing effect.
    string Deliver(VoiceAdapter sent, string prompt)
    {
        if (Present == null || !sent.LastResult.HasValue || !sent.LastResult.Value.Accepted || sent.LastCommand == null) return prompt;
        string shown = Present(sent.LastCommand);
        return shown ?? prompt;
    }

    // The player picked one shown meaning of an ambiguous transcript.
    public void Choose(int index)
    {
        if (adapter == null || Busy || index < 0 || index >= adapter.Candidates.Count) return;
        string picked = adapter.CandidateMeanings[index];
        CommandResult result = adapter.Choose(index);
        string prompt = Deliver(adapter, adapter.Prompt);
        Show(prompt);
        log("voice choice index=" + index + " meaning=" + Q(picked) + " accepted=" + (result.Accepted ? "true" : "false")
            + " reason=" + result.Reason + " prompt=" + Q(prompt));
    }

    public void DismissChoices()
    {
        if (adapter == null || adapter.Candidates.Count == 0) return;
        adapter.Dismiss();
        Show(adapter.Prompt);
        log("voice choice dismissed");
    }

    void Show(string status)
    {
        Status = status ?? "";
        ShownUntil = Time.unscaledTime + 6f;
    }

    // ---------- microphone (or the fake signal) ----------

    bool StartMic()
    {
        if (Fake || clip != null) return true;
        if (Microphone.devices.Length == 0)
        {
            if (Listening) { Listening = false; Show("No microphone found. Keyboard and mouse still work."); }
            return false;
        }
        Microphone.GetDeviceCaps(null, out int min, out int max);
        clipRate = min == 0 && max == 0 ? Rate : Mathf.Clamp(Rate, min, max);
        clip = Microphone.Start(null, true, 10, clipRate);
        readPos = 0;
        resampler = clipRate == Rate ? null : new VoiceResampler(clipRate);
        log("voice mic start rate=" + clipRate);
        return clip != null;
    }

    void StopMic()
    {
        if (clip == null) return;
        Microphone.End(null);
        clip = null;
        log("voice mic stop");
    }

    float[] Pull(float dt)
    {
        if (Fake) return FakeSignal(dt);
        int pos = Microphone.GetPosition(null);
        int total = clip.samples, channels = Mathf.Max(1, clip.channels);
        int count = (pos - readPos + total) % total;
        if (count == 0) return new float[0];
        float[] raw = new float[count * channels];
        int first = Math.Min(count, total - readPos);
        float[] a = new float[first * channels];
        clip.GetData(a, readPos);
        Array.Copy(a, raw, a.Length);
        if (first < count)
        {
            float[] b = new float[(count - first) * channels];
            clip.GetData(b, 0);
            Array.Copy(b, 0, raw, a.Length, b.Length);
        }
        readPos = pos;
        float[] mono = new float[count];
        for (int i = 0; i < count; i++) mono[i] = raw[i * channels];
        return resampler == null ? mono : resampler.Process(mono, count);
    }

    float[] FakeSignal(float dt)
    {
        int n = Mathf.Clamp(Mathf.RoundToInt(dt * Rate), 1, Rate / 2);
        float[] o = new float[n];
        if (burstGap > 0) burstGap = Math.Max(0, burstGap - n);
        if (!Listening && burst == 0 && burstGap == 0 && !Busy && !gate.InSpeech && fakeScript.Count > 0
            && fakeScript.Peek().StartsWith("say|", StringComparison.Ordinal))
        {
            burst = Rate * 9 / 10;
            burstGap = Rate * 3;
            log("voice fake burst text=" + Q(fakeScript.Peek().Substring(4)));
        }
        for (int i = 0; i < n; i++)
        {
            bool on = Listening || burst > 0;
            if (burst > 0) burst--;
            phase += 2 * Math.PI * 220 / Rate;
            o[i] = on ? 0.3f * (float)Math.Sin(phase) : 0f;
        }
        return o;
    }

    // Fake script line "ai|<status>|<body>" answers the next interpretation (fixture model reply).
    IEnumerator FakeInterpret(VoiceAdapter a, ProposalBoundary b, GatewayRequest request)
    {
        float until = Time.unscaledTime + 0.4f;
        while (Time.unscaledTime < until) yield return null;
        string line = fakeScript.Count > 0 && fakeScript.Peek().StartsWith("ai|", StringComparison.Ordinal) ? fakeScript.Dequeue() : "ai|0|";
        string[] parts = line.Split(new[] { '|' }, 3);
        a.CompleteInterpretation(request.Id, int.Parse(parts[1], CultureInfo.InvariantCulture), parts.Length > 2 ? parts[2] : "");
    }

    IEnumerator FakeTranscribe(VoiceAdapter a, ProposalBoundary b, GatewayRequest request, byte[] pcm)
    {
        float until = Time.unscaledTime + 0.4f;
        while (Time.unscaledTime < until) yield return null;
        string line = fakeScript.Count > 0 ? fakeScript.Dequeue() : "";
        int bar = line.IndexOf('|');
        string frame = bar < 0
            ? "{\"type\":\"error\",\"message\":\"timeout\"}"
            : "{\"type\":\"transcript\",\"final\":true,\"transcript\":" + Q(line.Substring(bar + 1)) + "}";
        a.Complete(request.Id, frame);
    }

    // ---------- Voice page ----------

    public void ShowPage()
    {
        PageOpen = true; Focus = 0; Capturing = false; Draft = ""; Say("", false);
        Reconcile();
        log("voice page open=true");
    }

    public void HidePage()
    {
        if (!PageOpen) return;
        PageOpen = false; Capturing = false;
        log("voice page open=false");
    }

    public void BeginCapture() { Capturing = true; Say("Press a key for talk, or Escape to cancel.", false); log("voice capture start"); }

    public void CancelCapture() { if (!Capturing) return; Capturing = false; log("voice capture cancel"); }

    // Keyboard on the page: N/B move, Return activates, Escape/X closes. Typing goes to the phrase field.
    public void HandlePageKeys()
    {
        if (Capturing)
        {
            if (Input.GetKeyDown(KeyCode.Escape)) { Capturing = false; Say("Talk key unchanged.", false); return; }
            if (!Input.anyKeyDown) return;
            foreach (KeyCode k in Enum.GetValues(typeof(KeyCode)))
                if (k != KeyCode.None && Input.GetKeyDown(k)) { TrySetTalkKey(k.ToString()); return; }
            return;
        }
        if (Input.GetKeyDown(KeyCode.Escape)) { HidePage(); return; }
        if (GUIUtility.keyboardControl != 0) return;   // the phrase field has focus
        if (Pressed("SelectNext")) Focus = (Focus + 1) % PageRows;
        else if (Pressed("SelectPrevious")) Focus = (Focus + PageRows - 1) % PageRows;
        else if (Pressed("Confirm")) Activate(Focus);
        else if (Pressed("CloseActionMenu") || Pressed("OpenSettings")) HidePage();
    }

    bool Pressed(string action)
    {
        return access != null && Enum.TryParse(access.BindingFor(action), out KeyCode key) && Input.GetKeyDown(key);
    }

    public void Activate(int row)
    {
        Focus = row;
        if (row == 0) BeginCapture();
        else if (row == 1) SetActivator(!Settings.Activator);
        else if (row == 2) DraftCommand = (DraftCommand + 1) % VoiceAliases.Commands.Length;
        else if (row == 3) AddDraft();
        else HidePage();
    }

    public void TrySetTalkKey(string key)
    {
        Capturing = false;
        string owner = access == null ? "" : access.ResolveInput(key);
        if (!VoiceSettings.IsKeyName(key)) Say(key + " cannot be the talk key.", true);
        else if (owner.Length > 0) Say(Display(key) + " is already used by " + ControlsLabel(owner) + ".", true);
        else if (Array.IndexOf(ReservedKeys, key) >= 0) Say(Display(key) + " is a fixed game key.", true);
        else
        {
            Settings.TalkKey = key;
            Say("Talk key is now " + Display(key) + ".", false);
            Save();
        }
        log("voice talk key " + key + " result=" + Q(PageMessage));
    }

    public void SetActivator(bool on)
    {
        Settings.Activator = on;
        Say(on ? "Hey Meta is on: the microphone stays open while you play." : "Hey Meta is off.", false);
        Save();
    }

    public void AddDraft()
    {
        string command = VoiceAliases.Commands[DraftCommand];
        List<string> names = new List<string>();
        if (world != null) foreach (ResidentRecord r in world.ResidentsSnapshot()) names.Add(r.DisplayName);
        string conflict = Settings.Aliases.TryAdd(Draft, command, names);
        log("voice alias add phrase=" + Q(VoiceAliases.Normalize(Draft)) + " command=" + Q(command) + " conflict=" + Q(conflict ?? ""));
        if (conflict != null) { Say(conflict, true); return; }
        Say("\"" + VoiceAliases.Normalize(Draft) + "\" now means " + command + ".", false);
        Draft = "";
        Save();
    }

    public void RemoveAlias(string phrase)
    {
        if (!Settings.Aliases.Remove(phrase)) return;
        Say("Removed \"" + phrase + "\".", false);
        log("voice alias remove phrase=" + Q(phrase));
        Save();
    }

    void Save()
    {
        bool ok = Settings.Save(directory, out string detail);
        if (!ok) Say("Voice settings not saved: " + detail, true);
        log("voice settings saved=" + (ok ? "true" : "false") + " key=" + Settings.TalkKey + " activator=" + (Settings.Activator ? "on" : "off")
            + " aliases=" + Settings.Aliases.Entries.Count);
    }

    void Say(string message, bool error) { PageMessage = message ?? ""; PageMessageIsError = error; }

    public static string Display(string key)
    {
        if (key == "Return") return "Enter";
        if (key.StartsWith("Alpha", StringComparison.Ordinal) && key.Length == 6) return key.Substring(5);
        return key;
    }

    static string ControlsLabel(string action)
    {
        StringBuilder b = new StringBuilder();
        foreach (char c in action) { if (char.IsUpper(c) && b.Length > 0) b.Append(' '); b.Append(b.Length == 0 ? c : char.ToLowerInvariant(c)); }
        return b.ToString();
    }

    static string Q(string value)
    {
        string text = value ?? string.Empty;
        return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
    }
}
