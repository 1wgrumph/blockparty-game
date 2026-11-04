// SELECT-ACTION@1: cursor presentation states (BP-PRES-02).
// Resolves six non-colour cues from hover, blocked reason, busy and placement
// inputs in the SDoc priority order (busy, blocked, placement, resident,
// interactable, default). Each state carries a distinct shape id and a text
// label, so colour alone never identifies it. Plain C# 9, no UnityEngine
// dependency. Fast Enter Play Mode: no mutable state.
using System;
using Blockparty.Contracts;

namespace Blockparty.UI
{
    public enum CursorKind
    {
        Default,
        HoverInteractable,
        HoverResident,
        Blocked,
        Busy,
        Placement,
    }

    public sealed class CursorCue
    {
        public CursorKind Kind { get; private set; }
        public string ShapeId { get; private set; }
        public string Label { get; private set; }

        public CursorCue(CursorKind kind, string shapeId, string label)
        {
            Kind = kind;
            ShapeId = shapeId;
            Label = label;
        }
    }

    public sealed class CursorState
    {
        public CursorState(Accessibility accessibility)
        {
            if (accessibility == null)
            {
                throw new ArgumentNullException("accessibility");
            }
        }

        public CursorCue Resolve(bool interactable, bool resident,
            RejectReason? blocked, bool busy, bool placement)
        {
            if (busy)
            {
                return new CursorCue(CursorKind.Busy, "cursor-busy", "Busy");
            }
            if (blocked.HasValue)
            {
                return new CursorCue(CursorKind.Blocked, "cursor-blocked",
                    "Blocked: " + blocked.Value);
            }
            if (placement)
            {
                return new CursorCue(CursorKind.Placement, "cursor-placement", "Placement");
            }
            if (resident)
            {
                return new CursorCue(CursorKind.HoverResident, "cursor-resident", "Resident");
            }
            if (interactable)
            {
                return new CursorCue(CursorKind.HoverInteractable,
                    "cursor-interactable", "Interact");
            }
            return new CursorCue(CursorKind.Default, "cursor-default", "Default");
        }
    }
}
