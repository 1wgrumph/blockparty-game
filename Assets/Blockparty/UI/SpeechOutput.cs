// SPEECH@1: speech-dispatcher output for narration (issue #157, G-7).
// Narration text crosses a process boundary, so it is one ArgumentList
// operand after "--": no shell string and no re-parsing. The default runner
// launches and returns so the game loop never waits for speech; a missing or
// failed backend returns false and is never reported as spoken.
using System;
using System.Diagnostics;
using System.Text;

namespace Blockparty.UI
{
    public interface ISpeechOutput
    {
        bool Speak(string text);
    }

    public sealed class SpeechDispatcherOutput : ISpeechOutput
    {
        // spd-say's native argv path truncates at NUL and refuses oversized
        // arguments; reject both here so a lost utterance is never "spoken".
        private const int MaxTextBytes = 8192;
        private const string SlowRate = "-20";
        private readonly Func<ProcessStartInfo, bool> runner;

        public SpeechDispatcherOutput() : this(Start) { }

        public SpeechDispatcherOutput(Func<ProcessStartInfo, bool> runner)
        {
            this.runner = runner ?? Start;
        }

        public bool Speak(string text)
        {
            string value = text ?? string.Empty;
            if (value.IndexOf('\0') >= 0 || Encoding.UTF8.GetByteCount(value) > MaxTextBytes)
                return false;
            ProcessStartInfo start = new ProcessStartInfo("spd-say");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.ArgumentList.Add("-r");
            start.ArgumentList.Add(SlowRate);
            start.ArgumentList.Add("--");
            start.ArgumentList.Add(value);
            try
            {
                return runner(start);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Start(ProcessStartInfo start)
        {
            Process.Start(start);
            return true;
        }
    }
}
