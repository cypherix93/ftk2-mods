using System;
using System.Globalization;
using System.IO;
using System.Threading;
using UnityEngine;

namespace Crucible.Plugin
{
    /// <summary>
    /// Screenshots. This is the harness's eyes: an agent driving the game needs to see it, not just
    /// read state fields.
    ///
    /// <c>ScreenCapture.CaptureScreenshot</c> writes asynchronously at end of frame, so the blocking
    /// path polls until the file exists AND stops growing — returning early would hand back a
    /// truncated PNG.
    /// </summary>
    internal static class CaptureService
    {
        private static int _counter;

        private static string NextPath(string label)
        {
            string folder = Path.Combine(CruciblePlugin.Instance.ArtifactRoot, "screenshots");
            if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);

            string safeLabel = string.IsNullOrEmpty(label) ? "shot" : SanitizeLabel(label);
            _counter++;
            string name = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
                + "_" + CruciblePlugin.Instance.InstanceName
                + "_" + _counter.ToString(CultureInfo.InvariantCulture)
                + "_" + safeLabel + ".png";
            return Path.Combine(folder, name);
        }

        private static string SanitizeLabel(string label)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string result = label;
            for (int i = 0; i < invalid.Length; i++) result = result.Replace(invalid[i], '_');
            if (result.Length > 40) result = result.Substring(0, 40);
            return result;
        }

        /// <summary>Main-thread only. Returns the path the screenshot will be written to.</summary>
        internal static string RequestScreenshot(string label)
        {
            string path = NextPath(label);

            int superSize = CruciblePlugin.Instance.CfgSuperSize.Value;
            if (superSize < 1) superSize = 1;

            bool hideUi = CruciblePlugin.Instance.CfgHideUiForShots.Value;
            string err;
            if (hideUi) GameBridge.Exec("ToggleUI", new string[0], out err);
            ScreenCapture.CaptureScreenshot(path, superSize);
            if (hideUi) GameBridge.Exec("ToggleUI", new string[0], out err);

            CruciblePlugin.LogVerbose("Screenshot queued: " + path);
            return path;
        }

        /// <summary>
        /// Callable from an RPC worker thread: queues the capture on the main thread, then waits for
        /// the file to appear and settle.
        /// </summary>
        internal static bool CaptureBlocking(string label, int timeoutMs, out string path, out string error)
        {
            path = null;
            object result;
            if (!MainThreadPump.Run(delegate { return RequestScreenshot(label); }, timeoutMs, out result, out error))
                return false;

            path = result as string;
            if (string.IsNullOrEmpty(path)) { error = "capture_no_path"; return false; }

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            long lastSize = -1;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        long size = new FileInfo(path).Length;
                        // Two consecutive equal, non-zero readings means Unity finished writing.
                        if (size > 0 && size == lastSize) return true;
                        lastSize = size;
                    }
                }
                catch (IOException)
                {
                    // Still being written; keep waiting.
                }
                Thread.Sleep(50);
            }

            error = "capture_timeout";
            return false;
        }
    }
}
