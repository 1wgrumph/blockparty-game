using System;
using Blockparty.Contracts;
using Blockparty.Simulation;

// REFUSAL-TEXT (owner playtest 2026-11-04): the plain sentence the HUD shows when
// an action is refused, naming the resident and what they need. It only explains
// the stage and course rules in CommandArbitrator.IsStageEligible and Work.Refuse;
// it decides nothing. Plain C#, no UnityEngine, so the probe compiles it alone.
// Sentences stay within about 55 characters: the action card draws one line.
public static class RefusalText
{
    public static string For(ResidentRecord resident, string activityCode, RejectReason reason)
    {
        string who = resident == null || resident.DisplayName.Length == 0 ? "This resident" : resident.DisplayName;
        string code = activityCode ?? "";
        switch (reason)
        {
            case RejectReason.StageIneligible: return resident == null ? who + " can't do that yet." : Stage(resident, who, code);
            case RejectReason.UnknownActivity: return "That activity isn't available here.";
            case RejectReason.TargetMissing:
            case RejectReason.TargetInvalidated: return "That spot isn't there any more. Pick another.";
            case RejectReason.InsufficientFunds: return who + " can't afford that.";
            case RejectReason.ObjectUnavailable: return "That isn't available right now.";
            case RejectReason.ObjectOccupied: return "Someone else is using that. Try again soon.";
            case RejectReason.NoPath: return who + " can't find a way there.";
            case RejectReason.QueueFull: return who + "'s to-do list is full. Cancel something first.";
            case RejectReason.ResidentDeceased: return who + " has passed away.";
            case RejectReason.StaleEpoch:
            case RejectReason.StaleGeneration: return "Things just changed. Try that again.";
            case RejectReason.UnsafeWeather: return "It isn't safe outside in this weather.";
            default: return who + " can't do that right now.";
        }
    }

    static string Stage(ResidentRecord r, string who, string code)
    {
        bool child = r.Stage < LifeStage.Teen, teen = r.Stage == LifeStage.Teen, adult = r.Stage >= LifeStage.YoungAdult;
        switch (code)
        {
            case "ACT-WORK-01": // Barista: teens at career level 1 only; level 3 needs the certificate.
                if (child) return who + " is too young to work (jobs start at Young Adult)";
                if (teen && r.CareerLevel > 1) return who + " is too young for Barista level " + r.CareerLevel + " (Young Adult)";
                if (r.CareerLevel >= 3 && !Has(r, "CulinaryCertificate")) return "Barista level 3 needs the Culinary Certificate course";
                break;
            case "ACT-WORK-02":
                if (!adult) return who + " is too young to work (jobs start at Young Adult)";
                if (r.CareerLevel >= 2 && !Has(r, "ComputerSkills")) return "Technician level 2 needs the Computer Skills course";
                break;
            case "ACT-WORK-04":
                if (!adult) return who + " is too young to work (jobs start at Young Adult)";
                if (!Has(r, "ComputerSkills")) return "AI Engineer needs the Computer Skills course";
                break;
            case "ACT-WORK-03":
                if (child) return who + " is too young to look for a job (starts at Teen)";
                break;
            case "ACT-EDU-01":
                return adult ? who + " is too old for school (it ends after Teen)"
                    : who + " is too young for school (school starts at Child)";
            case "ACT-HOME-05":
                if (r.CareRequired) return who + " needs care and can't be the caregiver";
                break;
        }
        if (code.StartsWith("ACT-WORK-", StringComparison.Ordinal)) return who + " can't take that job yet";
        if (code.StartsWith("ACT-EDU-", StringComparison.Ordinal)) return who + " is too young for that course";
        return adult ? who + " can't do that at their life stage (" + StageName(r.Stage) + ")"
            : who + " is too young for that (" + StageName(r.Stage) + ")";
    }

    static bool Has(ResidentRecord r, string course)
    {
        foreach (string c in r.CompletedCourses) if (string.Equals(c, course, StringComparison.Ordinal)) return true;
        return false;
    }

    static string StageName(LifeStage stage)
    {
        return stage == LifeStage.YoungAdult ? "Young Adult" : stage.ToString();
    }
}
