using System;
using System.Reflection;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Reflective, cached read of <c>RouterHelper.Env.NetworkData.PlayingOnlineMultiplayer</c> (MP review B3).
    ///
    /// <para><b>Why reflective</b> rather than the direct compile-time chain other patches in this project use
    /// (e.g. <c>Env.Configs</c> in <see cref="TraitLoadoutPatches"/>): this is the one read that gates whether
    /// MP-unsafe content can reach a live multiplayer session at all, so it is deliberately built to degrade to
    /// "assume online" on ANY resolution failure — a future rename/relocation of <c>NetworkData</c> or its
    /// field must never silently compile away the gate; it must fail closed instead. The lookup is cached
    /// after the first attempt (whether it succeeds or fails) since <see cref="IsOnlineMultiplayer"/> is read
    /// from <c>LootDropHelper.GetAdventureLoadOut</c>, a party-setup-screen path that can run more than once
    /// per session.</para>
    ///
    /// <para><c>Env.NetworkData</c> is an INSTANCE field on the live <c>Env</c> object (unlike the static
    /// <c>Env.Configs</c>), reached the same way <c>RecipeEngineHost</c> reaches <c>Env.GameRun</c> — via the
    /// <c>RouterHelper.Env</c> static property.</para>
    /// </summary>
    internal static class NetworkSessionState
    {
        private static bool _resolved;
        private static PropertyInfo _envProperty;
        private static FieldInfo _networkDataField;
        private static FieldInfo _playingOnlineField;
        private static FieldInfo _isHostField;

        /// <summary>
        /// True if this session is (or this method cannot prove it is not) an online multiplayer session.
        /// Fail-closed throughout: a missing member, a null <c>Env</c>/<c>NetworkData</c> instance, or any
        /// exception all return <c>true</c>, so a broken read can never silently let MP-unsafe trait injection
        /// through unguarded.
        /// </summary>
        internal static bool IsOnlineMultiplayer()
        {
            try
            {
                EnsureResolved();
                if (_envProperty == null || _networkDataField == null || _playingOnlineField == null) return true;

                var env = _envProperty.GetValue(null);
                if (env == null) return true;

                var networkData = _networkDataField.GetValue(env);
                if (networkData == null) return true;

                var value = _playingOnlineField.GetValue(networkData);
                return !(value is bool online) || online;
            }
            catch
            {
                return true; // fail-closed.
            }
        }

        /// <summary>
        /// True only if <c>NetworkData.IsHost</c> can be read AND is true. Deliberately the OPPOSITE
        /// fail-closed direction from <see cref="IsOnlineMultiplayer"/> (M-LG3, loot-grant verb spec §7
        /// "non-host peers never send"): a peer that cannot prove it is host must not broadcast the
        /// <c>CF_SYNC_LOOT_GRANT_V1</c> host-push, so any resolution failure or exception here reads as
        /// "not host", never "assume host". Grant COMPUTATION is unaffected either way — every peer
        /// computes the identical Mode-M delta regardless of send eligibility (verb spec §2).
        /// </summary>
        internal static bool IsHost()
        {
            try
            {
                EnsureResolved();
                if (_envProperty == null || _networkDataField == null || _isHostField == null) return false;

                var env = _envProperty.GetValue(null);
                if (env == null) return false;

                var networkData = _networkDataField.GetValue(env);
                if (networkData == null) return false;

                var value = _isHostField.GetValue(networkData);
                return value is bool isHost && isHost;
            }
            catch
            {
                return false; // fail-closed (opposite direction from IsOnlineMultiplayer, see above).
            }
        }

        private static void EnsureResolved()
        {
            if (_resolved) return;
            _resolved = true;

            try
            {
                var routerHelperType = typeof(RouterHelper);
                _envProperty = routerHelperType.GetProperty("Env", BindingFlags.Public | BindingFlags.Static);
                if (_envProperty == null) return;

                _networkDataField = _envProperty.PropertyType.GetField("NetworkData", BindingFlags.Public | BindingFlags.Instance);
                if (_networkDataField == null) return;

                _playingOnlineField = _networkDataField.FieldType.GetField("PlayingOnlineMultiplayer", BindingFlags.Public | BindingFlags.Instance);
                _isHostField = _networkDataField.FieldType.GetField("IsHost", BindingFlags.Public | BindingFlags.Instance);
            }
            catch
            {
                _envProperty = null;
                _networkDataField = null;
                _playingOnlineField = null;
                _isHostField = null;
            }
        }
    }
}
