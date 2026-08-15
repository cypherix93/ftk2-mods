using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Unity APIs are main-thread only, but RPC handlers run on HttpListener threads. Every game
    /// touch is therefore queued here and drained from a <c>RouterMono.Update</c> postfix — the one
    /// per-frame entry point the game guarantees.
    /// </summary>
    internal static class MainThreadPump
    {
        private sealed class WorkItem
        {
            internal Func<object> Work;
            internal object Result;
            internal string Error;
            internal readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private static readonly Queue<WorkItem> Queue = new Queue<WorkItem>();
        private static readonly object Lock = new object();
        private static ManualLogSource _log;

        /// <summary>Invoked once per frame on the main thread (hotkey polling lives here).</summary>
        internal static Action OnTick;

        internal static bool IsRunning { get; private set; }

        internal static void Initialize(Harmony harmony, ManualLogSource log)
        {
            _log = log;

            Type routerMono = AccessTools.TypeByName("RouterMono");
            var target = routerMono == null ? null : AccessTools.Method(routerMono, "Update");
            if (target == null)
            {
                log.LogError("Target NOT found: RouterMono.Update — pump disabled, RPC calls will time out.");
                DevKitBridge.ReportTarget("RouterMono.Update", null);
                return;
            }

            log.LogInfo("Target found: RouterMono.Update");
            DevKitBridge.ReportTarget("RouterMono.Update", target);
            harmony.Patch(target, null, new HarmonyMethod(AccessTools.Method(typeof(MainThreadPump), "Postfix")));
            IsRunning = true;
        }

        internal static void Postfix()
        {
            if (OnTick != null)
            {
                try { OnTick(); }
                catch (Exception ex) { if (_log != null) _log.LogWarning("OnTick threw: " + ex.Message); }
            }

            int budget = CruciblePlugin.Instance != null ? CruciblePlugin.Instance.CfgMaxWorkPerFrame.Value : 4;
            if (budget < 1) budget = 1;

            for (int i = 0; i < budget; i++)
            {
                WorkItem item = null;
                lock (Lock) { if (Queue.Count > 0) item = Queue.Dequeue(); }
                if (item == null) return;

                try { item.Result = item.Work(); }
                catch (Exception ex) { item.Error = ex.Message; }
                finally { item.Done.Set(); }
            }
        }

        /// <summary>
        /// Blocking call from a worker thread: enqueue, wait for the main thread to run it, return.
        /// A timeout means the game is paused, loading, or the pump never installed — reported as
        /// <c>main_thread_timeout</c> rather than left to hang.
        /// </summary>
        internal static bool Run(Func<object> work, int timeoutMs, out object result, out string error)
        {
            WorkItem item = new WorkItem();
            item.Work = work;
            lock (Lock) { Queue.Enqueue(item); }

            if (!item.Done.Wait(timeoutMs))
            {
                result = null;
                error = "main_thread_timeout";
                return false;
            }

            result = item.Result;
            error = item.Error;
            return error == null;
        }
    }
}
