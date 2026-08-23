using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// The <c>crucible.state.v2</c> reader. Additive: <see cref="StateReader"/> v1 is untouched and
    /// still served, so nothing in flight breaks and no existing digest moves.
    ///
    /// Difference that matters: every reflective read here carries a <see cref="WarningSink"/>.
    /// v1 passes <c>null</c> for the run block (<c>GetMember(gameRun, "Seed", null)</c>), which is
    /// why a wrong name there would be silent — the same class of bug as
    /// <c>NetworkData.PlayerCount</c>. The Task 1 addendum confirmed <c>Seed</c>/<c>Day</c>/
    /// <c>Gold</c>/<c>Chapter</c> do not exist on <c>GameRunData</c> at all; v2 still attempts the
    /// same four names v1 does (no invented replacement) but now reports the miss loudly instead of
    /// swallowing it.
    /// </summary>
    internal static class StateReaderV2
    {
        internal static Dictionary<string, object> Snapshot()
        {
            WarningSink warnings = new WarningSink();
            RunView run = new RunView();
            NetworkView network = new NetworkView();
            CombatView combat = null;
            string route = null;
            bool console = false;
            string instance = CruciblePlugin.Instance != null ? CruciblePlugin.Instance.InstanceName : null;

            try
            {
                // Drain any transitions the hooks queued since the last read, so the trace carries
                // them even in a session where nothing else called into the hooks.
                TurnHooks.FlushEvents();

                object env = GameBridge.GetEnv();
                if (env == null)
                {
                    warnings.Note("RouterHelper.Env unavailable (game may still be loading)");
                    return SnapshotShape.BuildV2(instance, null, run, network, null, false, warnings);
                }

                route = MemberResolver.AsString(ReadRoute(warnings));
                console = GameBridge.IsConsoleShowing();

                object gameRun = MemberResolver.GetMember(env, "GameRun", warnings);
                run.Present = gameRun != null;
                if (gameRun != null)
                {
                    // Names confirmed by the Task 1 addendum. Unlike v1, a wrong one is LOUD.
                    run.Seed = MemberResolver.GetMember(gameRun, "Seed", warnings);
                    run.Day = MemberResolver.GetMember(gameRun, "Day", warnings);
                    run.Gold = MemberResolver.GetMember(gameRun, "Gold", warnings);
                    run.Chapter = MemberResolver.GetMember(gameRun, "Chapter", warnings);
                }

                network.Online = GameBridge.IsOnlineSession();
                object networkData = GameBridge.GetNetworkData();
                if (networkData == null)
                {
                    warnings.Note("NetworkData unavailable");
                }
                else
                {
                    network.IsHost = MemberResolver.AsBool(
                        MemberResolver.GetMember(networkData, "IsHost", warnings));
                    ICollection players =
                        MemberResolver.GetMember(networkData, "PlayerList", warnings) as ICollection;
                    network.PlayerCount = players == null ? (int?)null : players.Count;
                }

                combat = CombatReader.Read(gameRun, TurnHooks.Tracker, warnings);
            }
            catch (Exception ex)
            {
                warnings.Add("snapshot_failed: " + ex.Message);
            }

            return SnapshotShape.BuildV2(instance, route, run, network, combat, console, warnings);
        }

        /// <summary>
        /// The same call v1 makes, with the warning sink v1 does not pass. Parameterless and static;
        /// v1's live output proves it returns real route values (SPEC §0), and the Task 1 addendum
        /// confirms the zero-arg signature.
        /// </summary>
        private static object ReadRoute(WarningSink warnings)
        {
            try
            {
                Type type = AccessTools.TypeByName("RouterHelper");
                if (type == null) { warnings.TypeMissing("RouterHelper"); return null; }

                MethodInfo method = AccessTools.Method(type, "GetCurrentRoute");
                if (method == null || method.GetParameters().Length != 0)
                {
                    warnings.MemberMissing("RouterHelper", "GetCurrentRoute()");
                    return null;
                }
                return method.Invoke(null, null);
            }
            catch (Exception ex)
            {
                warnings.Add("invoke_failed: RouterHelper.GetCurrentRoute: " + ex.Message);
                return null;
            }
        }
    }
}
