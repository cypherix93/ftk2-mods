using System;
using System.Reflection;
using HarmonyLib;

namespace DevKit.Plugin
{
    /// <summary>
    /// Cached reflective accessors for the two session-state flags the parity handshake needs.
    /// Nothing here takes a compile-time dependency on a game type, so a game update that renames or
    /// moves a member degrades to "Target NOT found" + feature off, never a load failure.
    ///
    /// <b>Evidence for every member below</b> (MP review M3 — DevKit previously hardcoded
    /// <c>SetIsHost(false)</c> on the stated grounds that "no networking/session-state flag has been
    /// identified", which the repo's own decompile refutes):
    ///  - <c>DirectorBase._env</c> — <c>tools/out/decompile/FTK2/DirectorBase.cs:13</c>,
    ///    <c>private protected Env _env;</c>. <c>AdventureDirector</c> inherits it via
    ///    <c>GameplayDirectorBase</c>; <see cref="AccessTools.Field(Type,string)"/> walks base types.
    ///  - <c>Env.NetworkData</c> — <c>tools/out/decompile/FTK2/Env.cs:20</c>,
    ///    <c>public NetworkData NetworkData;</c> (a plain field, not a property).
    ///  - <c>NetworkData.IsHost</c> — <c>tools/out/decompile/FTK2/NetworkData.cs:24</c>,
    ///    <c>public bool IsHost;</c>.
    ///  - <c>NetworkData.PlayingOnlineMultiplayer</c> — <c>tools/out/decompile/FTK2/NetworkData.cs:61</c>,
    ///    <c>public bool PlayingOnlineMultiplayer;</c>. The game gates its own send path on exactly
    ///    this flag (<c>AdventureDirector.cs:15137</c>), which is why DevKit uses it to decide whether
    ///    a session is a multiplayer session at all.
    /// </summary>
    internal static class GameSurface
    {
        private const string EnvFieldName = "_env";
        private const string NetworkDataFieldName = "NetworkData";
        private const string IsHostFieldName = "IsHost";
        private const string PlayingOnlineFieldName = "PlayingOnlineMultiplayer";

        private static FieldInfo _envField;
        private static FieldInfo _networkDataField;
        private static FieldInfo _isHostField;
        private static FieldInfo _playingOnlineField;
        private static bool _resolveAttempted;
        private static bool _resolved;

        /// <summary>True once every accessor above resolved against the live game assembly.</summary>
        internal static bool Resolved { get { return _resolved; } }

        /// <summary>
        /// Reads <c>NetworkData.PlayingOnlineMultiplayer</c> from the live director.
        /// Returns false if anything is unresolved or null — DevKit then treats the session as
        /// single-player and takes no network code path at all (M3).
        /// </summary>
        internal static bool IsOnlineMultiplayer(object directorInstance)
        {
            return ReadFlag(directorInstance, _playingOnlineField);
        }

        /// <summary>
        /// Reads <c>NetworkData.IsHost</c> from the live director. Returns false when unresolved,
        /// which is the safe default: a peer that wrongly believes it is not the host merely declines
        /// to answer a late-join REQUEST, while still broadcasting its own snapshot.
        /// </summary>
        internal static bool IsHost(object directorInstance)
        {
            return ReadFlag(directorInstance, _isHostField);
        }

        private static bool ReadFlag(object directorInstance, FieldInfo flagField)
        {
            try
            {
                if (directorInstance == null) return false;
                if (!Resolve(directorInstance.GetType())) return false;
                if (flagField == null) return false;

                object env = _envField.GetValue(directorInstance);
                if (env == null) return false;
                object networkData = _networkDataField.GetValue(env);
                if (networkData == null) return false;
                object value = flagField.GetValue(networkData);
                return value is bool && (bool)value;
            }
            catch (Exception ex)
            {
                DevKitPlugin.Verbose("ParityService: session-flag read failed (treated as false): " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// One-shot resolution of the whole chain. Logs one "Target found"/"Target NOT found" line
        /// per the repo convention and never throws.
        /// </summary>
        private static bool Resolve(Type directorType)
        {
            if (_resolved) return true;
            if (_resolveAttempted) return false;
            _resolveAttempted = true;

            try
            {
                _envField = AccessTools.Field(directorType, EnvFieldName);
                if (_envField == null)
                {
                    DevKitPlugin.Log.LogError("Target NOT found: " + directorType.Name + "." + EnvFieldName
                        + " (DirectorBase.cs:13) - host/MP detection disabled, parity handshake will not run.");
                    return false;
                }

                Type envType = _envField.FieldType;
                _networkDataField = AccessTools.Field(envType, NetworkDataFieldName);
                if (_networkDataField == null)
                {
                    DevKitPlugin.Log.LogError("Target NOT found: " + envType.Name + "." + NetworkDataFieldName
                        + " (Env.cs:20) - host/MP detection disabled, parity handshake will not run.");
                    return false;
                }

                Type networkDataType = _networkDataField.FieldType;
                _isHostField = AccessTools.Field(networkDataType, IsHostFieldName);
                _playingOnlineField = AccessTools.Field(networkDataType, PlayingOnlineFieldName);
                if (_isHostField == null || _playingOnlineField == null)
                {
                    DevKitPlugin.Log.LogError("Target NOT found: " + networkDataType.Name + "."
                        + (_isHostField == null ? IsHostFieldName : PlayingOnlineFieldName)
                        + " (NetworkData.cs:24/:61) - host/MP detection disabled, parity handshake will not run.");
                    return false;
                }
                if (_isHostField.FieldType != typeof(bool) || _playingOnlineField.FieldType != typeof(bool))
                {
                    DevKitPlugin.Log.LogError("Target found but unusable: " + networkDataType.Name
                        + ".IsHost/.PlayingOnlineMultiplayer are not bool - host/MP detection disabled.");
                    return false;
                }

                DevKitPlugin.Log.LogInfo("Target found: " + directorType.Name + "._env -> " + envType.Name
                    + ".NetworkData -> " + networkDataType.Name + ".{IsHost, PlayingOnlineMultiplayer}");
                _resolved = true;
                return true;
            }
            catch (Exception ex)
            {
                DevKitPlugin.Log.LogError("Target NOT found: session-state flags could not be resolved - "
                    + "host/MP detection disabled, parity handshake will not run: " + ex);
                return false;
            }
        }
    }
}
