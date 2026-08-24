using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;

namespace ClassForge.Plugin
{
    /// <summary>
    /// MP trait-loadout refresh — the ClassForge half of the task-#11 fix (DevKit half: commit 979bd17,
    /// party-phase parity handshake + <c>ParityService.RegisterVerdictCallback</c>).
    ///
    /// <para><b>The gap this closes.</b> Online, <see cref="TraitLoadoutPatches"/> fails CLOSED until the
    /// parity handshake produces a verified <c>Match</c> (MP review B3). DevKit can now complete that
    /// handshake during party management, but the game builds the loadout pool ONCE at
    /// <c>PartyManagementDirector.Initialize</c> (decompile L176) and caches it in
    /// <c>_loadOutItemThingsPool</c> — nothing rebuilds it when the verdict lands a moment later. This class
    /// listens for the verdict transition (via <see cref="ParityBridge"/>) and re-runs the game's own
    /// rebuild path, mirroring what the vanilla <c>JIP_UPDATE_STATS</c> network handler already does
    /// (decompile L4262-4277): rebuild the pool with <c>LootDropHelper.GetAdventureLoadOut</c> — which
    /// re-enters our injection postfix, now seeing the verified Match — then
    /// <c>_reloadAssignedLoadoutForParty(pool, _getLoadoutIndicesForPlayers())</c> (the L354/L2575 shape)
    /// to refresh assignments and the open loadout UI.</para>
    ///
    /// <para><b>Threading.</b> Verdict callbacks fire synchronously from DevKit's receive prefix on the
    /// director's <c>_handleNetworkAction</c>, which the game's action pump drives on the Unity main thread
    /// (UnitySynchronizationContext — the same context every director method runs on), so UI work here is
    /// main-thread-safe with no marshalling.</para>
    ///
    /// <para><b>MP safety.</b> Three legs, each verified in the decompile:</para>
    /// <list type="number">
    /// <item><b>RNG:</b> <c>GetAdventureLoadOut</c> draws from its <c>GameRandom</c> ONLY for
    /// <c>LoadOuts.ExtraItems</c> quantity rolls (LootDropHelper.cs L365), and no adventure in the current
    /// game data ships a non-empty <c>ExtraItems</c> (the sole <c>LoadOuts.json</c> entry,
    /// SIDE_ADVENTURE_DARK_CARNIVAL, has zero) — so this rebuild takes <b>zero draws</b> today. The
    /// director's own <c>_gameRandom</c> is still passed, exactly as the vanilla JIP rebuild does, so if a
    /// future game update adds ExtraItems the behavior stays whatever vanilla's own repeated-rebuild
    /// behavior is, rather than a divergent stream of our invention.</item>
    /// <item><b>Pick replication:</b> picks serialize as raw pool indices
    /// (<c>_loadOutItemThingsPool.IndexOf</c>, see TraitLoadoutPatches header). Our traits append AFTER the
    /// sorted vanilla block, so vanilla indices are identical before and after a refresh; a TRAIT pick can
    /// only be issued by a peer whose pool already contains traits, and the parity action that triggers each
    /// counterpart's own refresh was broadcast BEFORE any such pick could be made — the game's per-session
    /// ordered action pump therefore rebuilds every peer's pool before a trait pick can reach it, the same
    /// ordering guarantee the vanilla JIP rebuild relies on.</item>
    /// <item><b>Symmetry:</b> every peer transitions its verdict once per remote peer (DevKit m13 dedupe),
    /// so refresh counts — and with them any future ExtraItems draws — stay symmetric across peers.</item>
    /// </list>
    ///
    /// <para>Fail-safe throughout: any miss (no live director, reflection failure, a dead UI) is a logged
    /// no-op; the pool keeps its pre-refresh content, which is exactly the pre-#11 behavior.</para>
    /// </summary>
    internal static class TraitLoadoutRefresh
    {
        private static PartyManagementDirector _liveDirector;

        private static FieldInfo _gameRandomField;
        private static FieldInfo _poolField;
        private static MethodInfo _getIndicesMethod;
        private static MethodInfo _reloadMethod;
        private static bool _accessorsResolved;

        /// <summary>Postfix for <c>PartyManagementDirector.Initialize</c> (single overload, async Task —
        /// this runs at kickoff, which is all the caching needs). Marks the party screen live.</summary>
        public static void PartyInitialize_Postfix(PartyManagementDirector __instance)
        {
            _liveDirector = __instance;
        }

        /// <summary>Postfix for <c>AdventureDirector.Initialize</c> — the party phase is over; a verdict
        /// landing mid-adventure must do nothing here.</summary>
        public static void AdventureStarted_Postfix()
        {
            _liveDirector = null;
        }

        /// <summary>
        /// Called by <see cref="ParityBridge"/> when a verdict transition reports <c>Match</c>.
        /// No-ops unless the party-management screen is live and trait injection is enabled; the actual
        /// "may we inject" decision stays inside <see cref="TraitLoadoutPatches"/>'s own session gate,
        /// which the rebuilt <c>GetAdventureLoadOut</c> call re-evaluates.
        /// </summary>
        internal static void OnVerifiedMatch()
        {
            try
            {
                var director = _liveDirector;
                if (director == null) return;
                if (!ClassForgePlugin.FeaturesActive) return;
                if (ClassForgePlugin.EnableTraitLoadoutInjection == null ||
                    !ClassForgePlugin.EnableTraitLoadoutInjection.Value) return;

                if (!ResolveAccessors(director)) return;

                var env = GameSurfaceEnv(director);
                var random = (GameRandom)_gameRandomField.GetValue(director);
                if (env == null || random == null || string.IsNullOrEmpty(env.SelectedAdventureConfig)) return;

                // The vanilla rebuild shape (decompile L354/L2575, and the JIP handler at L4262-4277):
                // rebuild the pool — our GetAdventureLoadOut postfix runs inside this call and now sees
                // the verified Match — then push it through the game's own assignment/UI reload.
                var pool = LootDropHelper.GetAdventureLoadOut(env.SelectedAdventureConfig, random);
                _poolField.SetValue(director, pool);
                var indices = _getIndicesMethod.Invoke(director, null);
                _reloadMethod.Invoke(director, new object[] { pool, indices });

                ClassForgePlugin.Log.LogInfo(
                    "[ClassForge] MP parity verified — trait loadout pool refreshed (" +
                    CountPackTraits(pool).ToString(CultureInfo.InvariantCulture) + " trait(s) injected, pool now " +
                    pool.Count.ToString(CultureInfo.InvariantCulture) + " item(s)).");
            }
            catch (Exception ex)
            {
                // Fail-safe: the pool keeps its pre-refresh content (pre-#11 behavior); the player can
                // still force a rebuild by re-entering party management.
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] MP trait loadout refresh failed (non-fatal, pool unchanged): " + ex);
            }
        }

        private static bool ResolveAccessors(PartyManagementDirector director)
        {
            if (_accessorsResolved) return true;

            var type = director.GetType();
            _gameRandomField = AccessTools.Field(type, "_gameRandom");
            _poolField = AccessTools.Field(type, "_loadOutItemThingsPool");
            _getIndicesMethod = AccessTools.Method(type, "_getLoadoutIndicesForPlayers");
            _reloadMethod = AccessTools.Method(type, "_reloadAssignedLoadoutForParty");

            if (_gameRandomField == null || _poolField == null || _getIndicesMethod == null || _reloadMethod == null)
            {
                ClassForgePlugin.Log.LogWarning(
                    "[ClassForge] MP trait loadout refresh disabled: PartyManagementDirector members not " +
                    "resolvable (game update drift?) — _gameRandom=" + (_gameRandomField != null) +
                    " _loadOutItemThingsPool=" + (_poolField != null) +
                    " _getLoadoutIndicesForPlayers=" + (_getIndicesMethod != null) +
                    " _reloadAssignedLoadoutForParty=" + (_reloadMethod != null) +
                    ". Traits still appear after leaving and re-entering party management.");
                return false;
            }
            _accessorsResolved = true;
            return true;
        }

        /// <summary><c>DirectorBase._env</c> is <c>private protected</c>; AccessTools walks base types.</summary>
        private static Env GameSurfaceEnv(PartyManagementDirector director)
        {
            var field = AccessTools.Field(director.GetType(), "_env");
            return field != null ? field.GetValue(director) as Env : null;
        }

        private static int CountPackTraits(List<Thing> pool)
        {
            var plan = ClassForgePlugin.CurrentMergePlan;
            if (plan == null || pool == null) return 0;
            var traitIds = new HashSet<string>(plan.TraitIds, StringComparer.Ordinal);
            int count = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                var thing = pool[i];
                if (thing != null && !string.IsNullOrEmpty(thing.ConfigName) && traitIds.Contains(thing.ConfigName))
                    count++;
            }
            return count;
        }
    }
}
