using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using FTK2Mods.DevKit;
using HarmonyLib;
using UnityEngine;

namespace DevKit.Plugin
{
    /// <summary>
    /// Polls the game's own desync detector and, the moment it trips, tells every peer on screen and in
    /// the log — including <b>why</b>, which the vendor detector alone cannot say.
    ///
    /// <para>Reads <c>RouterHelper.Env.NetworkData</c> reflectively, matching the posture ClassForge's
    /// <c>NetworkSessionState</c> already uses for the same object: a rename or relocation in a future game
    /// build must degrade to "watcher inactive", never to a crash or a false alarm. Every read here
    /// fail-closed reports <i>no desync</i> on failure, because a watchdog that cries wolf when it can't see
    /// is worse than one that stays quiet.</para>
    ///
    /// <para>Presentation only — this never touches replicated state and never draws from any RNG stream,
    /// so the watcher itself can never be a desync cause.</para>
    /// </summary>
    internal sealed class DesyncWatchRunner : MonoBehaviour
    {
        private const string ReportFolderName = "NetworkDesyncReport";

        private static bool _resolved;
        private static PropertyInfo _envProperty;
        private static FieldInfo _networkDataField;
        private static FieldInfo _playingOnlineField;
        private static FieldInfo _monitorField;
        private static FieldInfo _detectedField;
        private static FieldInfo _isHostField;
        private static FieldInfo _hashIndexField;
        private static FieldInfo _randomIndexField;
        private static FieldInfo _hashDataField;
        private static FieldInfo _randomDataField;
        private static MethodInfo _showEventTitle;

        private float _nextPollAt;
        private float _nextRepeatBannerAt;

        internal static float PollSeconds = 2f;
        internal static float RepeatBannerSeconds = 60f;
        internal static bool ShowBanner = true;
        internal static bool DebugForceTrip;

        /// <summary>
        /// Resolves the game-side members and reports what was found, so a build where the game has
        /// renamed something says so loudly at startup instead of silently never firing. This is the
        /// only way to know the reader works without waiting for a real desync.
        /// </summary>
        internal static string DescribeResolution()
        {
            EnsureResolved();

            if (_envProperty == null) return "UNRESOLVED: RouterHelper.Env not found — watcher inactive";
            if (_networkDataField == null) return "UNRESOLVED: Env.NetworkData not found — watcher inactive";

            List<string> missing = new List<string>();
            if (_playingOnlineField == null) missing.Add("PlayingOnlineMultiplayer");
            if (_monitorField == null) missing.Add("DoMonitorForDesyncs");
            if (_detectedField == null) missing.Add("HasADesyncBeenDetected");
            if (_isHostField == null) missing.Add("IsHost");
            if (_hashIndexField == null) missing.Add("LatestDesyncHashIndex");
            if (_randomIndexField == null) missing.Add("LatestGameRandomDesyncIndex");
            if (_hashDataField == null) missing.Add("DebugDesyncHashData");
            if (_randomDataField == null) missing.Add("DebugDesyncGameRandomData");

            return missing.Count == 0
                ? "resolved all 8 NetworkData members"
                : "PARTIAL: missing " + string.Join(", ", missing.ToArray());
        }

        /// <summary>Creates the watcher on a persistent object. Safe to call once from plugin Awake.</summary>
        internal static void Install()
        {
            GameObject host = new GameObject("FTK2Mods.DevKit.DesyncWatch");
            DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            host.AddComponent<DesyncWatchRunner>();
        }

        private void Update()
        {
            if (Time.unscaledTime < _nextPollAt) return;
            _nextPollAt = Time.unscaledTime + Math.Max(0.25f, PollSeconds);

            // Diagnostic: exercise the whole report path solo, without a second peer. The synthetic
            // snapshot is clearly labelled in the output so a forced report can never be mistaken for
            // a real one.
            if (DebugForceTrip && !DesyncWatch.HasReported)
            {
                DesyncSnapshot fake = new DesyncSnapshot
                {
                    PlayingOnlineMultiplayer = true,
                    DoMonitorForDesyncs = true,
                    HasADesyncBeenDetected = true,
                    LatestGameRandomDesyncIndex = 42,
                    GameRandomDataIndices = new List<int> { 42 }
                };
                if (DesyncWatch.Observe(fake) == DesyncVerdict.TrippedNow)
                {
                    DevKitPlugin.Log.LogWarning(
                        "[DEVKIT_DESYNC] *** SIMULATED TRIP ([Desync] DebugForceTrip=true) — not a real desync ***");
                    ReportTrip(fake);
                    _nextRepeatBannerAt = Time.unscaledTime + RepeatBannerSeconds;
                }
                return;
            }

            DesyncSnapshot snapshot;
            try
            {
                snapshot = ReadSnapshot();
            }
            catch
            {
                return; // fail-closed: unreadable NetworkData means "no opinion", not "desync".
            }

            if (snapshot == null) return;

            DesyncVerdict verdict = DesyncWatch.Observe(snapshot);

            if (verdict == DesyncVerdict.TrippedNow)
            {
                ReportTrip(snapshot);
                _nextRepeatBannerAt = Time.unscaledTime + RepeatBannerSeconds;
                return;
            }

            // A 4-second banner during a busy fight is easy to miss, and the condition persists for the
            // rest of the session, so re-assert it on a slow cadence until someone acts on it.
            if (verdict == DesyncVerdict.AlreadyReported
                && ShowBanner
                && RepeatBannerSeconds > 0f
                && Time.unscaledTime >= _nextRepeatBannerAt)
            {
                _nextRepeatBannerAt = Time.unscaledTime + RepeatBannerSeconds;
                ShowDesyncBanner(DesyncWatch.LastChannel, repeat: true);
            }
        }

        private void ReportTrip(DesyncSnapshot snapshot)
        {
            DesyncChannel channel = DesyncWatch.LastChannel;

            string[] safeMode = SafeCall(ParityService.GetSafeModeGuids, new string[0]);
            bool blocked = SafeCall(ParityService.IsSessionBlocked, false);
            string[] guids = SafeCall(ParityService.GetRegisteredGuids, new string[0]);
            string dump = SafeCall(ParityService.DumpRegistrations, string.Empty);
            string mismatch = SafeCall(ParityService.GetLastMismatchSummary, string.Empty);
            string peerId = SafeCall(ParityService.GetLocalPeerId, string.Empty);

            string cause = DesyncWatch.BuildCause(channel, safeMode, blocked);
            string timestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

            // Loud, and structured enough to grep. This is the line people will paste into chat.
            DevKitPlugin.Log.LogError("=================== DESYNC DETECTED ===================");
            DevKitPlugin.Log.LogError("[DEVKIT_DESYNC] channel=" + channel
                + " host=" + snapshot.IsHost
                + " peer=" + (string.IsNullOrEmpty(peerId) ? "(unidentified)" : peerId));
            DevKitPlugin.Log.LogError("[DEVKIT_DESYNC] why: " + cause);
            DevKitPlugin.Log.LogError("[DEVKIT_DESYNC] vendor: hashIndex="
                + snapshot.LatestDesyncHashIndex.ToString(CultureInfo.InvariantCulture)
                + " randomIndex=" + snapshot.LatestGameRandomDesyncIndex.ToString(CultureInfo.InvariantCulture));
            if (safeMode.Length > 0)
            {
                DevKitPlugin.Log.LogError("[DEVKIT_DESYNC] mods already in SafeMode: " + string.Join(", ", safeMode));
            }

            string report = DesyncWatch.BuildReport(
                snapshot, channel, peerId, guids, dump, safeMode, blocked, mismatch, timestamp);

            string written = TryWriteReport(report);
            DevKitPlugin.Log.LogError("[DEVKIT_DESYNC] report: "
                + (written ?? "(could not be written — the log block above is the whole record)"));
            DevKitPlugin.Log.LogError("=======================================================");

            if (ShowBanner) ShowDesyncBanner(channel, repeat: false);
        }

        /// <summary>
        /// Puts the state on screen. Every peer runs its own watcher, so every peer banners itself —
        /// which is the point: nobody has to be told by the person who noticed first.
        /// </summary>
        private static void ShowDesyncBanner(DesyncChannel channel, bool repeat)
        {
            try
            {
                if (_showEventTitle == null)
                {
                    _showEventTitle = AccessTools.Method("GameplayDialogViewHelper:ShowEventTitle",
                        new[] { typeof(string), typeof(int) });
                    if (_showEventTitle == null) return;
                }

                string text = repeat
                    ? "Still desynced (" + channel + ") — see BepInEx log"
                    : "DESYNC DETECTED (" + channel + ") — this session is no longer in sync";
                _showEventTitle.Invoke(null, new object[] { text, 6000 });
            }
            catch
            {
                // Presentation is never allowed to take the game down (repo R4 posture). The log block
                // above already carries the full record.
            }
        }

        /// <summary>Writes beside the vendor's own reports so one folder holds the whole picture.</summary>
        private static string TryWriteReport(string body)
        {
            try
            {
                string folder = Path.Combine(Application.persistentDataPath, ReportFolderName);
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder,
                    "DEVKIT_DESYNC_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
                File.WriteAllText(path, body);
                return path;
            }
            catch
            {
                return null;
            }
        }

        private DesyncSnapshot ReadSnapshot()
        {
            EnsureResolved();
            if (_envProperty == null || _networkDataField == null) return null;

            object env = _envProperty.GetValue(null, null);
            if (env == null) return null;

            object nd = _networkDataField.GetValue(env);
            if (nd == null) return null;

            DesyncSnapshot s = new DesyncSnapshot();
            s.PlayingOnlineMultiplayer = ReadBool(_playingOnlineField, nd);
            s.DoMonitorForDesyncs = ReadBool(_monitorField, nd);
            s.HasADesyncBeenDetected = ReadBool(_detectedField, nd);
            s.IsHost = ReadBool(_isHostField, nd);
            s.LatestDesyncHashIndex = ReadInt(_hashIndexField, nd);
            s.LatestGameRandomDesyncIndex = ReadInt(_randomIndexField, nd);
            s.HashDataIndices = ReadIntKeys(_hashDataField, nd);
            s.GameRandomDataIndices = ReadIntKeys(_randomDataField, nd);
            return s;
        }

        private static bool ReadBool(FieldInfo field, object owner)
        {
            if (field == null) return false;
            object v = field.GetValue(owner);
            return v is bool b && b;
        }

        private static int ReadInt(FieldInfo field, object owner)
        {
            if (field == null) return -1;
            object v = field.GetValue(owner);
            return v is int i ? i : -1;
        }

        /// <summary>
        /// Pulls the int keys out of either debug dictionary without naming their value types —
        /// one is <c>Dictionary&lt;int,string&gt;</c>, the other <c>Dictionary&lt;int,(int,string)&gt;</c>,
        /// and only the keys matter here.
        /// </summary>
        private static IList<int> ReadIntKeys(FieldInfo field, object owner)
        {
            List<int> keys = new List<int>();
            if (field == null) return keys;

            if (!(field.GetValue(owner) is IDictionary dict)) return keys;

            foreach (object key in dict.Keys)
            {
                if (key is int i) keys.Add(i);
            }
            return keys;
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                // Resolved by name, never by typeof: this project deliberately compiles against no game
                // types so a renamed member degrades to "watcher inactive" instead of a plugin load failure.
                Type routerHelper = AccessTools.TypeByName("RouterHelper");
                if (routerHelper == null) return;

                _envProperty = routerHelper.GetProperty("Env", BindingFlags.Public | BindingFlags.Static);
                if (_envProperty == null) return;

                _networkDataField = _envProperty.PropertyType.GetField("NetworkData", BindingFlags.Public | BindingFlags.Instance);
                if (_networkDataField == null) return;

                Type nd = _networkDataField.FieldType;
                _playingOnlineField = nd.GetField("PlayingOnlineMultiplayer", BindingFlags.Public | BindingFlags.Instance);
                _monitorField = nd.GetField("DoMonitorForDesyncs", BindingFlags.Public | BindingFlags.Instance);
                _detectedField = nd.GetField("HasADesyncBeenDetected", BindingFlags.Public | BindingFlags.Instance);
                _isHostField = nd.GetField("IsHost", BindingFlags.Public | BindingFlags.Instance);
                _hashIndexField = nd.GetField("LatestDesyncHashIndex", BindingFlags.Public | BindingFlags.Instance);
                _randomIndexField = nd.GetField("LatestGameRandomDesyncIndex", BindingFlags.Public | BindingFlags.Instance);
                _hashDataField = nd.GetField("DebugDesyncHashData", BindingFlags.Public | BindingFlags.Instance);
                _randomDataField = nd.GetField("DebugDesyncGameRandomData", BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                _envProperty = null;
                _networkDataField = null;
            }
        }

        private static T SafeCall<T>(Func<T> f, T fallback)
        {
            try
            {
                return f();
            }
            catch
            {
                return fallback;
            }
        }
    }
}
