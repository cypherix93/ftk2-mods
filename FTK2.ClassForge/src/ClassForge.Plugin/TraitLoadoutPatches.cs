using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClassForge.Core;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Trait selection via vanilla loadout-pool injection — SPEC-DELTA-v1.1 §1 OQ#1 item 3.
    ///
    /// <para><b>Verified target</b> (re-read from <c>tools/out/decompile/FTK2/LootDropHelper.cs</c> L336, as
    /// SPEC-DELTA-v1.1 §9 risk 2 required, because this method was cited via the trait-mechanism doc rather
    /// than via PSN):</para>
    /// <code>public static List&lt;Thing&gt; GetAdventureLoadOut(string pAdventureName, GameRandom pRandom)</code>
    /// <para>Exactly one overload. The body ends in
    /// <c>(from thing3 in list2 orderby ... select thing3).Cast&lt;Thing&gt;().ToList()</c>, i.e. it returns a
    /// <b>freshly allocated</b> <c>List&lt;Thing&gt;</c> on every call — never a cached or shared instance —
    /// so a postfix appending to <c>__result</c> cannot corrupt engine state.</para>
    ///
    /// <para><b>Why a patch is required at all</b> rather than just minting a <c>LoreStore</c> entry: the
    /// vanilla body replicates each LoreStore key <c>StatsHelper.GetStat(key, GLOBAL)</c> times, so a modded
    /// trait with no global unlock stat yields <b>zero</b> pool copies. The postfix bypasses that gate.</para>
    ///
    /// <para><b>MP determinism (the load-bearing constraint).</b>
    /// <c>PartyManagementDirector</c> serializes a pick as
    /// <c>_loadOutItemThingsPool.IndexOf(pThing)</c> and the receiving peer resolves it as
    /// <c>_loadOutItemThingsPool[thingIndex]</c> — the wire format is a raw <b>index</b>, not an id. A
    /// deterministic <c>Thing.Id</c> alone does NOT make this safe. Therefore this postfix appends
    /// <b>unconditionally and in a fixed ordinal-sorted order</b>, with no state-dependent branch that could
    /// make one peer's pool differ in length or ordering from another's. Every skip reason below is a pure
    /// function of parity-hashed pack data (which R1 already guarantees is byte-identical across peers), never
    /// of local/session state.</para>
    ///
    /// <para><b>Deterministic <c>Thing.Id</c></b> (SPEC-DELTA-v1.1 §1 OQ#1 item 5): <c>InventoryHelper.CreateThing</c>
    /// assigns <c>Id = Guid.NewGuid().ToString()</c> — a runtime GUID, different on every peer. <c>Thing.Id</c>
    /// is a public field, so it is overwritten immediately after creation with
    /// <c>SHA256("CF_THING_ID|" + packId + "|" + traitId)</c>, first 12 hex chars, invariant culture.</para>
    ///
    /// <para><b>Known vanilla behavior deliberately not fought</b> (SPEC-DELTA-v1.1 §1 OQ#1 item 6):
    /// <c>PartyManagementDirector._takeAndEquip</c> calls <c>CharacterHelper.RemoveAllTraits</c> before
    /// <c>GiveTrait</c>, so picking any trait strips every other <c>TRAIT_</c>-prefixed Thing on that
    /// character — pack traits are mutually exclusive with each other and with vanilla traits. That is
    /// vanilla behavior for vanilla traits too; ClassForge does not patch around it.</para>
    ///
    /// <para><b>MP review B3 — session gate.</b> Everything above is correct in isolation, but its correctness
    /// rests entirely on R1 (parity), and R1 has no enforcement point that runs before this postfix: DevKit
    /// runs the whole handshake from an <c>AdventureDirector.Initialize</c> postfix, which fires AFTER
    /// <c>PartyManagementDirector</c> may already have built and serialized the pool via this exact method.
    /// So "ClassForge is not yet Blocked" is not sufficient evidence that injection is safe online — there may
    /// simply be no verdict yet. The fix applied here: read
    /// <see cref="NetworkSessionState.IsOnlineMultiplayer"/> (reflective, cached, fail-closed = treat as
    /// online). Offline/single-player: inject unconditionally, exactly as before. Online multiplayer:
    /// injection additionally requires <see cref="ParityBridge.HasVerifiedMatch"/> — a POSITIVE verdict, not
    /// merely "not blocked" — so the pool cannot diverge in length between peers during the window before the
    /// handshake resolves. This means MP trait injection may legitimately be off for the first loadout build
    /// of an online session; that is the fail-closed, SP-safe-today posture (see <see cref="SessionInjectionGate"/>).
    /// The day-one MP gap this used to leave is CLOSED by the task-#11 pair: DevKit runs the handshake during
    /// party management (979bd17) and <see cref="TraitLoadoutRefresh"/> rebuilds the cached pool when the
    /// Match verdict lands, re-entering this postfix with the gate now open.</para>
    /// </summary>
    public static class TraitLoadoutPatches
    {
        /// <summary>The native trait prefix. <c>InventoryHelper.TRAIT_CONFIG_PREFIX</c> declares this same
        /// literal, but every runtime site hardcodes the string, so this is matched by value.</summary>
        internal const string TraitPrefix = "TRAIT_";

        private static readonly HashSet<string> WarnedNonPrefixed = new HashSet<string>(StringComparer.Ordinal);
        private static bool _warnedMissingConfig;

        // MP review B3: log the online/offline + allow/deny decision once per session, not once per
        // GetAdventureLoadOut call (party setup / join-in-progress rebuild can both call this repeatedly).
        private static bool _loggedSessionGateDecision;

        /// <summary>
        /// Postfix for <c>LootDropHelper.GetAdventureLoadOut(string, GameRandom)</c>. Appends one
        /// <c>Thing</c> per eligible pack trait not already present in the pool.
        /// </summary>
        public static void GetAdventureLoadOut_Postfix(string pAdventureName, ref List<Thing> __result)
        {
            if (!ClassForgePlugin.FeaturesActive) return;
            if (!ClassForgePlugin.EnableTraitLoadoutInjection.Value) return;
            if (__result == null) return;
            if (!SessionInjectionGate()) return;

            var plan = ClassForgePlugin.CurrentMergePlan;
            if (plan == null || plan.TraitIds.Count == 0) return;

            try
            {
                // Ordinal sort: SPEC-DELTA-v1.1 §5.2 invariant 4 ("never Dictionary/HashSet enumeration
                // order, never filesystem order"). MergePlan.TraitIds is already deterministic, but sorting
                // here makes the pool ordering independent of any future change to how it is built.
                var traitIds = new List<string>(plan.TraitIds);
                traitIds.Sort(StringComparer.Ordinal);

                var packByThingId = BuildPackLookup(plan);

                // Already-present ConfigNames, so a re-entrant call (loadout screen reopened) cannot double-add.
                var present = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < __result.Count; i++)
                {
                    var existing = __result[i];
                    if (existing != null && !string.IsNullOrEmpty(existing.ConfigName))
                        present.Add(existing.ConfigName);
                }

                int injected = 0;
                int skippedPrefix = 0;
                int skippedUnmerged = 0;

                for (int i = 0; i < traitIds.Count; i++)
                {
                    string traitId = traitIds[i];

                    // (1) Prefix enforcement. The "TRAIT_" prefix is not cosmetic — it IS the mechanism
                    //     (CharacterHelper.RemoveAllTraits, InventoryHelper.GetTraits,
                    //     EquipmentHelper.GetEquippedThingsNonAlloc all key on StartsWith("TRAIT_")).
                    //     A CF_TRAIT_* id would merge into Configs.Things fine and grant its Equippable.Stats
                    //     via a normal equip, but would be invisible to every native trait query — so it is
                    //     NOT injected as a trait. Mirrors ClassForge.Core's CF_TRAIT_PREFIX parser warning.
                    if (!traitId.StartsWith(TraitPrefix, StringComparison.Ordinal))
                    {
                        skippedPrefix++;
                        if (WarnedNonPrefixed.Add(traitId))
                        {
                            ClassForgePlugin.Log.LogWarning(
                                "[ClassForge] Trait '" + traitId + "' does not start with '" + TraitPrefix +
                                "' — NOT injected into the adventure loadout pool. The native trait substrate " +
                                "keys entirely on that prefix (CharacterHelper/InventoryHelper/EquipmentHelper " +
                                "all use ConfigName.StartsWith(\"TRAIT_\")), so a differently-prefixed id can " +
                                "never behave as a trait. Rename it to '" + TraitPrefix + "...' in traits.json.");
                        }
                        continue;
                    }

                    // (2) The ThingConfig must actually be in Env.Configs.Things. InventoryHelper.GetThingConfig
                    //     is `return Env.Configs.Things[name];` (raw index, throws KeyNotFoundException), and
                    //     Thing.TryIncreaseStack reads Env.Configs.Things[ConfigName].Stacks. Injecting an
                    //     unmerged id would crash the loadout screen rather than degrade.
                    //     Parity-safe: whether an id merged is a pure function of pack data, identical on all peers.
                    if (!IsThingConfigPresent(traitId))
                    {
                        skippedUnmerged++;
                        continue;
                    }

                    if (present.Contains(traitId)) continue;

                    string packId;
                    if (!packByThingId.TryGetValue(traitId, out packId)) packId = "";

                    var thing = InventoryHelper.CreateThing(traitId);
                    if (thing == null) continue;

                    // Overwrite the runtime GUID CreateThing assigned with the deterministic id.
                    thing.Id = DeterministicThingId(packId, traitId);

                    __result.Add(thing);
                    present.Add(traitId);
                    injected++;
                }

                if (skippedUnmerged > 0 && !_warnedMissingConfig)
                {
                    _warnedMissingConfig = true;
                    ClassForgePlugin.Log.LogWarning(
                        "[ClassForge] " + skippedUnmerged.ToString(CultureInfo.InvariantCulture) +
                        " trait(s) were skipped for the loadout pool because their ThingConfig is not present " +
                        "in Env.Configs.Things — the pack merge has not run yet, or the entry failed to " +
                        "deserialize. Injecting them would throw inside InventoryHelper.GetThingConfig.");
                }

                if (injected > 0 || ClassForgePlugin.VerboseLogging.Value)
                {
                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] GetAdventureLoadOut('" + (pAdventureName ?? "(null)") + "'): injected " +
                        injected.ToString(CultureInfo.InvariantCulture) + " pack trait(s); pool now " +
                        __result.Count.ToString(CultureInfo.InvariantCulture) + " item(s). " +
                        "skipped: " + skippedPrefix.ToString(CultureInfo.InvariantCulture) + " non-'" + TraitPrefix +
                        "'-prefixed, " + skippedUnmerged.ToString(CultureInfo.InvariantCulture) + " unmerged.");
                }
            }
            catch (Exception ex)
            {
                // Fail-safe (CONVENTIONS.md): the loadout screen keeps working with the vanilla pool.
                ClassForgePlugin.Log.LogError(
                    "[ClassForge] Trait loadout injection failed (fail-safe, vanilla pool unchanged): " + ex);
            }
        }

        /// <summary>
        /// MP review B3 session gate. Offline/single-player: always true. Online multiplayer: true only once
        /// <see cref="ParityBridge.HasVerifiedMatch"/> — DevKit's handshake round-trip has actually produced a
        /// <c>Match</c> verdict for ClassForge this session — fail CLOSED (false) otherwise, including the
        /// window before the handshake has had a chance to run at all. Logs the decision exactly once per
        /// session (the first call), not once per invocation.
        /// </summary>
        private static bool SessionInjectionGate()
        {
            bool online = NetworkSessionState.IsOnlineMultiplayer();
            bool allowed = !online || ParityBridge.HasVerifiedMatch();

            if (!_loggedSessionGateDecision)
            {
                _loggedSessionGateDecision = true;
                if (!online)
                {
                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] Trait loadout injection: offline/single-player session — injecting unconditionally.");
                }
                else
                {
                    ClassForgePlugin.Log.LogInfo(
                        "[ClassForge] Trait loadout injection: online multiplayer session — " +
                        (allowed
                            ? "DevKit parity handshake already verified Match; injecting."
                            : "no verified parity Match yet (handshake may not have completed) — injection " +
                              "FAILS CLOSED this call (MP review B3). Expected on the very first loadout " +
                              "build of an online session; the party-phase handshake delivers a verdict " +
                              "shortly and TraitLoadoutRefresh rebuilds the pool automatically on Match."));
                }
            }

            return allowed;
        }

        /// <summary>thingId -&gt; owning packId, so the deterministic id can be namespaced per SPEC-DELTA-v1.1 OQ#1.5.</summary>
        private static Dictionary<string, string> BuildPackLookup(MergePlan plan)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < plan.Things.Count; i++)
            {
                var op = plan.Things[i];
                if (op == null || string.IsNullOrEmpty(op.Id)) continue;
                map[op.Id] = op.SourcePackId ?? "";
            }
            return map;
        }

        private static bool IsThingConfigPresent(string thingId)
        {
            try
            {
                var configs = Env.Configs;
                if (configs == null || configs.Things == null) return false;
                return configs.Things.ContainsKey(thingId);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// <c>SHA256("CF_THING_ID|" + packId + "|" + traitId)</c>, first 12 hex chars, invariant culture —
        /// SPEC-DELTA-v1.1 §1 OQ#1 item 5. Never a runtime GUID: the same pack trait must resolve to the
        /// same <c>Thing.Id</c> on every peer.
        /// </summary>
        internal static string DeterministicThingId(string packId, string traitId)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = Encoding.UTF8.GetBytes("CF_THING_ID|" + (packId ?? "") + "|" + (traitId ?? ""));
                var hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(12);
                for (int i = 0; i < 6; i++) sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
    }
}
