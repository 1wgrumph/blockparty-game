// NARRATION-AUDIO: external audio helpers live only for their capture body.
// Close runs on success and on failure, so a thrown capture cannot orphan a
// recorder; a failing close never hides the original capture exception.
using System;

namespace Blockparty.UI
{
    public static class NarrationAudioCapture
    {
        public static void Run(Action capture, Action close)
        {
            try
            {
                capture();
            }
            catch
            {
                try { close(); } catch (Exception) { }
                throw;
            }
            close();
        }
    }
}
