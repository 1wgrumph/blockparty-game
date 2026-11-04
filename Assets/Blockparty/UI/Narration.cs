// HUD@1: narration labels for HUD elements and action results (A7).
// Silent while the persisted NarrationEnabled setting is off; the setting
// is read live from Accessibility so save/load roundtrips take effect.
// SPEECH@1: every emitted utterance is also offered to an optional speech
// output once; SpeechAvailable reports only the latest dispatch, never a
// claim that audio was heard. Plain C# 9, no UnityEngine dependency.
// Instance state only.
using System.Collections.Generic;
using Blockparty.Contracts;
using Blockparty.Simulation;

namespace Blockparty.UI
{
    public sealed class Narration
    {
        private readonly Accessibility accessibility;
        private readonly ISpeechOutput output;
        private readonly List<string> utterances = new List<string>();
        private bool speechAvailable;

        public Narration(Accessibility accessibility) : this(accessibility, null) { }

        public Narration(Accessibility accessibility, ISpeechOutput output)
        {
            this.accessibility = accessibility;
            this.output = output;
        }

        public IEnumerable<string> Utterances
        {
            get { return utterances; }
        }

        public bool SpeechAvailable
        {
            get { return speechAvailable; }
        }

        private bool Enabled
        {
            get { return accessibility != null && accessibility.Settings.NarrationEnabled; }
        }

        private void Emit(string text)
        {
            if (!Enabled) return;
            string utterance = text ?? string.Empty;
            utterances.Add(utterance);
            speechAvailable = output != null && output.Speak(utterance);
        }

        public void Focus(string label)
        {
            Emit(label);
        }

        public void Selection(ResidentRecord resident)
        {
            if (resident != null) Emit("Selected " + resident.DisplayName);
        }

        public void ActionResult(ResidentRecord resident, ActivityId activity, CommandResult result)
        {
            if (resident == null) return;
            Emit(resident.DisplayName + " " + activity.ToString() + " "
                + (result.Accepted ? "Accepted" : result.Reason.ToString()));
        }

        public void ActionCompleted(ResidentRecord resident, ActivityId activity)
        {
            if (resident == null) return;
            Emit(resident.DisplayName + " " + activity.ToString() + " Completed");
        }
    }
}
