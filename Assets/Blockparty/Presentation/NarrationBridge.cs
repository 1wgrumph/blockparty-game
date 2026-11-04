using System;
using System.Collections.Generic;
using System.Text;
using Blockparty.Contracts;
using Blockparty.Simulation;
using Blockparty.UI;

// CONTROLS-PLAY@1: binds each recorded event to the real HUD@1 Narration
// utterance it produced. Text is intent captions for the narration log; a
// missing Linux TTS voice is the named gap G-7, never claimed as spoken audio.
public sealed class NarrationBridge
{
    public sealed class Utterance
    {
        public string EventId;
        public string Flow;
        public string Kind;
        public string Text;
    }

    readonly Narration narration;
    readonly List<Utterance> utterances = new List<Utterance>();

    public NarrationBridge(Narration narration)
    {
        if (narration == null) throw new ArgumentNullException("narration");
        this.narration = narration;
    }

    public string Focus(string eventId, string flow, string label)
    {
        narration.Focus(label);
        return Capture(eventId, flow, "focus");
    }

    public string Selection(string eventId, string flow, ResidentRecord resident)
    {
        narration.Selection(resident);
        return Capture(eventId, flow, "selection");
    }

    public string ActionResult(string eventId, string flow, ResidentRecord resident,
        ActivityId activity, CommandResult result)
    {
        narration.ActionResult(resident, activity, result);
        return Capture(eventId, flow, "action-result");
    }

    string Capture(string eventId, string flow, string kind)
    {
        string text = string.Empty;
        foreach (string line in narration.Utterances) text = line;
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("narration disabled or silent for " + eventId);
        utterances.Add(new Utterance
        {
            EventId = eventId,
            Flow = flow,
            Kind = kind,
            Text = text,
        });
        return text;
    }

    public string BuildJson(string tree, string binary)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"tree_hash\":\"").Append(tree)
          .Append("\",\"binary_sha256\":\"").Append(binary)
          .Append("\",\"utterances\":[");
        for (int i = 0; i < utterances.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("{\"event_id\":").Append(MeasureProbe.Quote(utterances[i].EventId))
              .Append(",\"flow\":").Append(MeasureProbe.Quote(utterances[i].Flow))
              .Append(",\"kind\":").Append(MeasureProbe.Quote(utterances[i].Kind))
              .Append(",\"text\":").Append(MeasureProbe.Quote(utterances[i].Text))
              .Append('}');
        }
        sb.Append("]}");
        return sb.ToString();
    }
}
