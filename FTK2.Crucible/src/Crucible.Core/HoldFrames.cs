using System;

namespace FTK2Mods.Crucible
{
    /// <summary>
    /// Converts a requested hold duration in milliseconds into whole game frames.
    ///
    /// Input taps must span real frames. Command handlers run on the game thread inside the
    /// RouterMono.Update postfix, so pressing and releasing inside one handler call means no frame
    /// ever renders with the button held and the game's Input Actions never observe a press.
    /// Sleeping on the main thread cannot create a frame - it prevents one. So a tap queues the
    /// press and schedules the release this many frames later.
    /// </summary>
    public static class HoldFrames
    {
        /// <summary>Assumed frame budget in ms. 60fps; a slower machine only holds the key LONGER, which is harmless.</summary>
        public const int MillisecondsPerFrame = 16;

        /// <summary>At least one full frame must observe the button down, so this is never below 2.</summary>
        public const int Minimum = 2;

        public static int ForMilliseconds(int milliseconds)
        {
            if (milliseconds <= 0) return Minimum;
            int frames = milliseconds / MillisecondsPerFrame;
            return frames < Minimum ? Minimum : frames;
        }
    }
}
