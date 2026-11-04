// VOICE-LIVE (M5-OD-38): the player's custom voice phrases, the optional
// "Hey Meta" prefix and the local speech gate in front of VOICE@1. Plain C# 9,
// no UnityEngine dependency, no network, no static mutable state.
// An alias maps a phrase to one existing supported meaning; it never adds a
// command type. Aliases may not shadow a built-in phrase, a resident name, the
// activator or another alias, so a phrase always has at most one meaning.
// Settings live in voice-settings.txt beside the settings record: SettingsRecord,
// SaveV1 and Accessibility stay at their GATE-M2-pinned digests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Blockparty.AI
{
    public sealed class VoiceAliases
    {
        // Every meaning an alias may stand for. The last eight take the rest of
        // the phrase (a resident name, "<name> to <destination>", or a party day).
        public static readonly string[] Commands =
        {
            "pause", "resume", "stop following", "open map", "save game",
            "set speed normal", "set speed fast", "set speed ultra",
            "select", "follow", "cancel", "direct",
            "chat with", "be friendly to", "invite", "plan a party",
        };

        private static readonly string[] BuiltInPrefixes =
            { "select", "follow", "cancel", "direct", "set speed", "chat with", "be friendly to", "invite", "plan a party" };

        private readonly SortedDictionary<string, string> map =
            new SortedDictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyDictionary<string, string> Entries { get { return map; } }

        public static bool TakesName(string command)
        {
            return command == "select" || command == "follow" || command == "cancel" || command == "direct"
                || command == "chat with" || command == "be friendly to" || command == "invite" || command == "plan a party";
        }

        // Returns null when the alias was stored (or already stored with the
        // same meaning); otherwise the player-visible conflict, nothing stored.
        public string TryAdd(string phrase, string command, IEnumerable<string> residentNames)
        {
            string p = Normalize(phrase);
            if (p.Length == 0) return "Type a phrase first.";
            if (Array.IndexOf(Commands, command) < 0) return "\"" + command + "\" is not a supported command.";
            if (p == "hey meta" || p.StartsWith("hey meta ", StringComparison.Ordinal))
                return "\"hey meta\" is the activator, not a phrase.";
            for (int i = 0; i < Commands.Length; i++)
                if (p == Commands[i] && !TakesName(Commands[i])) return "\"" + p + "\" is already a built-in command.";
            foreach (string prefix in BuiltInPrefixes)
                if (p == prefix || p.StartsWith(prefix + " ", StringComparison.Ordinal))
                    return "\"" + p + "\" starts with the built-in command \"" + prefix + "\".";
            string shadow = Shadow(p, residentNames);
            if (shadow != null) return shadow;
            string existing;
            if (map.TryGetValue(p, out existing))
                return existing == command ? null : "\"" + p + "\" already means \"" + existing + "\".";
            foreach (KeyValuePair<string, string> entry in map)
            {
                if ((TakesName(entry.Value) && p.StartsWith(entry.Key + " ", StringComparison.Ordinal))
                    || (TakesName(command) && entry.Key.StartsWith(p + " ", StringComparison.Ordinal)))
                    return "\"" + p + "\" overlaps \"" + entry.Key + "\" (" + entry.Value + ").";
            }
            map[p] = command;
            return null;
        }

        public bool Remove(string phrase)
        {
            string p = Normalize(phrase);
            inactive.Remove(p);
            return map.Remove(p);
        }

        // Stored phrases that shadow a resident of the current world, with the
        // player-visible reason. They stay saved but never apply.
        public IReadOnlyDictionary<string, string> Inactive { get { return inactive; } }

        private readonly SortedDictionary<string, string> inactive =
            new SortedDictionary<string, string>(StringComparer.Ordinal);

        // Rechecks every stored phrase against the current roster (a loaded or
        // replaced world, or renamed residents): a phrase that would shadow a
        // resident is made inactive, every other phrase is active again.
        public void Reconcile(IEnumerable<string> residentNames)
        {
            List<string> names = residentNames == null ? new List<string>() : new List<string>(residentNames);
            inactive.Clear();
            foreach (string phrase in map.Keys)
            {
                string reason = Shadow(phrase, names);
                if (reason != null) inactive[phrase] = reason + " It is off in this game; remove it or rename the phrase.";
            }
        }

        // VOICE@1 matches a spoken name as a prefix of the display name.
        private static string Shadow(string p, IEnumerable<string> residentNames)
        {
            if (residentNames == null) return null;
            foreach (string raw in residentNames)
            {
                string name = Normalize(raw);
                if (name.Length == 0) continue;
                if (name.StartsWith(p, StringComparison.Ordinal) || p.StartsWith(name + " ", StringComparison.Ordinal))
                    return "\"" + p + "\" would shadow the resident " + raw + ".";
            }
            return null;
        }

        // The canonical VOICE@1 text for a heard phrase; text with no active alias is returned unchanged.
        public string Apply(string heard)
        {
            string p = Normalize(heard);
            string command;
            if (!inactive.ContainsKey(p) && map.TryGetValue(p, out command)) return command;
            string best = null;
            foreach (KeyValuePair<string, string> entry in map)
                if (!inactive.ContainsKey(entry.Key) && TakesName(entry.Value) && p.StartsWith(entry.Key + " ", StringComparison.Ordinal)
                    && (best == null || entry.Key.Length > best.Length)) best = entry.Key;
            return best == null ? heard ?? string.Empty : map[best] + p.Substring(best.Length);
        }

        // The reason a heard phrase did nothing because its alias is inactive, or null.
        public string InactiveReason(string heard)
        {
            string p = Normalize(heard);
            foreach (KeyValuePair<string, string> entry in inactive)
                if (p == entry.Key || (TakesName(map[entry.Key]) && p.StartsWith(entry.Key + " ", StringComparison.Ordinal))) return entry.Value;
            return null;
        }

        // True when the text starts with "hey meta" (case and punctuation
        // insensitive); rest is the command after it.
        public static bool StripActivator(string heard, out string rest)
        {
            string p = Normalize(heard);
            rest = string.Empty;
            if (p == "hey meta") return true;
            if (!p.StartsWith("hey meta ", StringComparison.Ordinal)) return false;
            rest = p.Substring(9);
            return true;
        }

        // Lower case, sentence punctuation as spaces, single spaces. Apostrophes
        // stay so "cancel Sam's current action" keeps its VOICE@1 form.
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            StringBuilder b = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c) || c == ',' || c == '.' || c == '!' || c == '?' || c == ';' || c == ':' || c == '"')
                {
                    space = b.Length > 0;
                    continue;
                }
                if (space) { b.Append(' '); space = false; }
                b.Append(char.ToLowerInvariant(c));
            }
            return b.ToString();
        }
    }

    // Local voice-activity gate for the activator: only speech segments leave
    // the machine. 24 kHz mono floats in, finished segments out.
    public sealed class VoiceGate
    {
        public const int SampleRate = 24000;
        private const int Window = SampleRate / 50;          // 20 ms
        private const int PreRollWindows = 10;               // 200 ms kept before onset
        private const int HangWindows = 35;                  // 700 ms of quiet ends a segment
        private const int MinVoicedWindows = 12;             // 240 ms of speech minimum
        private const int MaxSamples = SampleRate * 8;
        public float Threshold = 0.02f;                       // RMS onset level

        private readonly float[] window = new float[Window];
        private int filled;
        private readonly Queue<float[]> preRoll = new Queue<float[]>();
        private List<float> segment;
        private int voiced, quiet;
        public readonly Queue<float[]> Ready = new Queue<float[]>();

        public bool InSpeech { get { return segment != null; } }

        public void Feed(float[] samples, int count)
        {
            for (int i = 0; i < count; i++)
            {
                window[filled++] = samples[i];
                if (filled == Window) { Step(); filled = 0; }
            }
        }

        public void Reset()
        {
            filled = 0; preRoll.Clear(); segment = null; voiced = 0; quiet = 0;
        }

        private void Step()
        {
            double sum = 0;
            for (int i = 0; i < Window; i++) sum += window[i] * window[i];
            bool loud = Math.Sqrt(sum / Window) >= Threshold;
            float[] copy = (float[])window.Clone();
            if (segment == null)
            {
                preRoll.Enqueue(copy);
                if (preRoll.Count > PreRollWindows) preRoll.Dequeue();
                if (!loud) return;
                segment = new List<float>();
                foreach (float[] w in preRoll) segment.AddRange(w);
                preRoll.Clear();
                voiced = 1; quiet = 0;
                return;
            }
            segment.AddRange(copy);
            if (loud) { voiced++; quiet = 0; } else quiet++;
            if (quiet < HangWindows && segment.Count < MaxSamples) return;
            if (voiced >= MinVoicedWindows) Ready.Enqueue(segment.ToArray());
            segment = null; voiced = 0; quiet = 0;
        }

        // 16-bit little-endian PCM, the PCM_24KHZ encoding VOICE@1's handshake names.
        public static byte[] Pcm16(IList<float> samples)
        {
            byte[] pcm = new byte[samples.Count * 2];
            for (int i = 0; i < samples.Count; i++)
            {
                float s = samples[i];
                short v = (short)Math.Round((s > 1f ? 1f : s < -1f ? -1f : s) * 32767f);
                pcm[2 * i] = (byte)(v & 0xff);
                pcm[2 * i + 1] = (byte)((v >> 8) & 0xff);
            }
            return pcm;
        }
    }

    // Owner playtest 2026-11-04 (short clips heard as ""): the microphone stays open
    // while voice is usable and this keeps its last half second, so a push-to-talk
    // clip starts before the key went down; Trim drops the quiet around the speech
    // before the clip is sent. Audio in the ring never leaves the machine.
    public sealed class VoiceClip
    {
        public const int PrerollSamples = VoiceGate.SampleRate / 2;
        private const int Window = VoiceGate.SampleRate / 50;          // 20 ms
        private const int LeadMargin = VoiceGate.SampleRate / 5;       // 200 ms kept before the first loud window
        private const int TailMargin = VoiceGate.SampleRate * 3 / 10;  // 300 ms kept after the last
        private readonly float[] ring = new float[PrerollSamples];
        private int head, count;

        public void Feed(float[] samples, int n)
        {
            for (int i = 0; i < n; i++)
            {
                ring[head] = samples[i];
                head = (head + 1) % ring.Length;
                if (count < ring.Length) count++;
            }
        }

        // The kept samples, oldest first; the ring is emptied.
        public float[] Take()
        {
            float[] o = new float[count];
            for (int i = 0; i < count; i++) o[i] = ring[(head - count + i + ring.Length) % ring.Length];
            count = 0;
            return o;
        }

        public void Clear() { count = 0; }

        // The clip from LeadMargin before its first loud 20 ms window to TailMargin after its
        // last; unchanged when no window reaches the threshold (a quiet microphone keeps everything).
        public static float[] Trim(IList<float> clip, float threshold)
        {
            int first = -1, last = -1;
            for (int start = 0; start + Window <= clip.Count; start += Window)
            {
                double sum = 0;
                for (int i = start; i < start + Window; i++) sum += clip[i] * clip[i];
                if (Math.Sqrt(sum / Window) < threshold) continue;
                if (first < 0) first = start;
                last = start + Window;
            }
            int from = first < 0 ? 0 : Math.Max(0, first - LeadMargin);
            int to = first < 0 ? clip.Count : Math.Min(clip.Count, last + TailMargin);
            float[] o = new float[to - from];
            for (int i = from; i < to; i++) o[i - from] = clip[i];
            return o;
        }
    }

    // Linear resampling of a microphone stream to 24 kHz mono, continuous
    // across pulls: the fractional source position and the last sample of the
    // previous pull carry over, so the output does not depend on how frames
    // split the input. Reset (or a new instance) starts a new capture.
    // shortcut: linear interpolation; fine for speech recognition, not for playback.
    public sealed class VoiceResampler
    {
        private const long Out = VoiceGate.SampleRate;
        private readonly long rate;     // source samples per output sample, in 1/Out source samples
        private long pos;               // next output position (1/Out source samples) relative to the next pull's first sample
        private float last;             // the previous pull's last sample (position -1)
        private bool hasLast;

        public VoiceResampler(int sourceRate)
        {
            if (sourceRate <= 0) throw new ArgumentOutOfRangeException("sourceRate");
            rate = sourceRate;
        }

        public void Reset() { pos = 0; last = 0f; hasLast = false; }

        public float[] Process(float[] mono, int count)
        {
            if (count <= 0) return new float[0];
            List<float> o = new List<float>((int)(count * Out / rate) + 2);
            long s = hasLast ? pos : Math.Max(0L, pos);
            long end = (count - 1) * Out;
            // Interpolating at s needs the samples at floor(s) and floor(s)+1; floor -1 is last.
            while (s < end)
            {
                long j = s >= 0 ? s / Out : -1;
                float t = (float)(s - j * Out) / Out;
                float a = j < 0 ? last : mono[j];
                o.Add(a * (1 - t) + mono[j + 1] * t);
                s += rate;
            }
            pos = s - count * Out;
            last = mono[count - 1];
            hasLast = true;
            return o.ToArray();
        }
    }

    // The voice settings saved with the settings record, one key=value per line.
    public sealed class VoiceSettings
    {
        public const string FileName = "voice-settings.txt";
        public const string DefaultTalkKey = "V";
        public string TalkKey = DefaultTalkKey;
        public bool Activator;
        public readonly VoiceAliases Aliases = new VoiceAliases();

        // A missing or unreadable file keeps the defaults; every stored alias
        // is revalidated, so a hand-edited file cannot add a conflict.
        public static VoiceSettings Load(string directory)
        {
            VoiceSettings s = new VoiceSettings();
            string path = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, FileName);
            if (path == null || !File.Exists(path)) return s;
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch (IOException) { return s; }
            catch (UnauthorizedAccessException) { return s; }
            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq), value = line.Substring(eq + 1);
                if (key == "talkKey" && IsKeyName(value)) s.TalkKey = value;
                else if (key == "activator") s.Activator = value == "on";
                else if (key == "alias")
                {
                    int tab = value.IndexOf('\t');
                    if (tab > 0) s.Aliases.TryAdd(value.Substring(0, tab), value.Substring(tab + 1), null);
                }
            }
            return s;
        }

        public bool Save(string directory, out string detail)
        {
            detail = string.Empty;
            if (string.IsNullOrEmpty(directory)) { detail = "no settings folder"; return false; }
            StringBuilder b = new StringBuilder();
            b.Append("talkKey=").Append(TalkKey).Append('\n');
            b.Append("activator=").Append(Activator ? "on" : "off").Append('\n');
            foreach (KeyValuePair<string, string> e in Aliases.Entries)
                b.Append("alias=").Append(e.Key).Append('\t').Append(e.Value).Append('\n');
            string path = Path.Combine(directory, FileName), temp = path + ".tmp";
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(temp, b.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (IOException e) { detail = e.GetType().Name; return false; }
            catch (UnauthorizedAccessException e) { detail = e.GetType().Name; return false; }
        }

        public static bool IsKeyName(string key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > 24) return false;
            foreach (char c in key) if (!char.IsLetterOrDigit(c)) return false;
            return true;
        }
    }
}
