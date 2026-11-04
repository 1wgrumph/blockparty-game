// Presentation passthrough for SAM@1: exposes the CaptureSearch model
// (outlines, count, message, misses) to UI drawing. Drawing itself and any
// resident selection/travel stay with UI-NAV/CONTROLS-PLAY; an outline never
// issues a command from here.
using System.Collections.Generic;
using Blockparty.AI;
using Blockparty.Contracts;

namespace Blockparty.Presentation
{
    public sealed class CaptureSearchBridge
    {
        private readonly CaptureSearch search;

        public CaptureSearchBridge(CaptureSearch search)
        {
            this.search = search;
        }

        public SamResult Result { get { return search.Result; } }
        public string CountText { get { return search.CountText; } }
        public string Message { get { return search.Message; } }
        public bool ConnectedSuccess { get { return search.ConnectedSuccess; } }
        public IReadOnlyList<SamMiss> Misses { get { return search.Misses; } }
    }
}
