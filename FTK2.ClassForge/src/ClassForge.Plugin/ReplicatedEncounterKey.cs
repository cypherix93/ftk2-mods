using System;
using ClassForge.Core.Rng;

namespace ClassForge.Plugin
{
    /// <summary>
    /// The ONE place ClassForge turns "which encounter is this" into a value that is identical on every
    /// peer. Everything that needs to branch per-encounter — the battlefield rotation
    /// (<see cref="VenueGridPatches"/>), the <c>ENCOUNTER_GUID</c> STATE_HASH_CHANCE token
    /// (<c>GameAdapters</c>) — reads it from here, and nothing reads
    /// <c>AdventureState.EncounterGUID</c> for that purpose ever again.
    ///
    /// <para><b>Why <c>AdventureState.EncounterGUID</c> could never be that value.</b> It is assigned
    /// from an overworld <c>Entity.Guid</c> (<c>AdventureDirector.cs:6967, 10616, 10815, 11369, 11440,
    /// 11792, 11903</c>; <c>AdventureHelper.cs:244</c>), and <c>Entity.Create()</c> mints that with
    /// <c>System.Guid.NewGuid()</c>. The map is not shipped entity-by-entity to a joining peer — only
    /// the SEED is (<c>NetworkHelper.SyncEnvironment</c> sets <c>NetworkData.MapGenSeed</c>, and each
    /// peer runs <c>AdventureHelper.InitializeMap(..., new System.Random(GameRun.MapGenSeed), ...)</c>
    /// itself), so every peer mints its own guids for the same logical encounter. The vendor knows
    /// this: before the desync MD5 it rewrites EVERY guid in the graph to a first-occurrence ordinal
    /// (<c>NetworkDebuggingHelper._convertGuidsOfEntities</c> →
    /// <c>_tryTransformToIntIdFromGuid</c> → <c>_getIntIdFromGuid</c>, which hands out <c>1, 2, 3…</c>
    /// in encounter order). A raw guid is therefore never compared by anything, which is exactly why
    /// a guid-keyed divergence in OUR code has no detector of any kind.</para>
    ///
    /// <para><b>Why these five fields ARE that value.</b> Every one of them is a plain public field on
    /// <c>GameRunData</c>/<c>AdventureState</c>, i.e. inside the serialized <c>GameRunData</c> graph
    /// that <c>NetworkDebuggingHelper.RecordGameRunStateAndGetHash</c> MD5s at every
    /// save/init/end-turn checkpoint, and NOT a guid, so it is compared verbatim rather than
    /// normalised away. If two peers disagreed on any of them the VENDOR's own detector would already
    /// have declared a desync — which is the strongest available evidence, and the reason this is
    /// sound where <c>CombatState</c>-side reasoning is not (<c>CombatState</c> is
    /// <c>[JsonIgnore]</c>, so nothing in combat is in that hash).</para>
    ///
    /// <list type="bullet">
    ///   <item><c>GameRunData.MapGenSeed</c> — the map seed itself, explicitly replicated
    ///   (<c>NetworkHelper.SyncEnvironment</c> → <c>NetworkData.MapGenSeed</c>;
    ///   <c>PartyManagementDirector.cs:893</c> then seeds the run from it).</item>
    ///   <item><c>AdventureState.TotalRoundCount</c> — the overworld round counter. Advances once per
    ///   overworld round and is stable for the whole of a combat.</item>
    ///   <item><c>AdventureState.ActiveMapID</c>, <c>BiomeName</c>, <c>DungeonName</c> — where the
    ///   party is.</item>
    /// </list>
    ///
    /// <para><b>What this deliberately does NOT promise.</b> It is an encounter-IDENTITY key, not a
    /// counter: two fights inside the same overworld round, in the same biome, on the same map,
    /// produce the same key. That is a cosmetic repeat for the battlefield rotation and irrelevant to
    /// STATE_HASH_CHANCE (which is documented as correlated across identical inputs anyway,
    /// <c>StateHashChance</c> remarks). It is worth far more that the key is a pure function of
    /// replicated state than that it never repeats: a counter is what a process-local
    /// <c>_dioramaTurn</c> was, and that made the battlefield a function of how many fights THIS
    /// process happened to have loaded.</para>
    /// </summary>
    internal static class ReplicatedEncounterKey
    {
        /// <summary>
        /// The five replicated fields, absorbed into an FNV-1a 64 accumulator seeded with
        /// <paramref name="purpose"/>. Never throws: an unreadable <c>Env</c>/<c>GameRun</c> degrades to
        /// the purpose-only hash, which is still identical on every peer.
        /// </summary>
        internal static ulong Hash(string purpose)
        {
            int mapGenSeed = 0, totalRoundCount = 0;
            string activeMapId = null, biomeName = null, dungeonName = null;
            try
            {
                var run = RouterHelper.Env != null ? RouterHelper.Env.GameRun : null;
                var adv = run != null ? run.AdventureState : null;

                mapGenSeed = run != null ? run.MapGenSeed : 0;
                totalRoundCount = adv != null ? adv.TotalRoundCount : 0;
                activeMapId = adv != null ? adv.ActiveMapID : null;
                biomeName = adv != null ? adv.BiomeName : null;
                dungeonName = adv != null ? adv.DungeonName : null;
            }
            catch (Exception)
            {
                // Deliberately swallowed: the reads above are five plain public fields with no
                // allocation, and a peer that somehow threw here and a peer that did not would still have
                // to disagree about a serialized, MD5'd field to disagree about the result.
            }
            // The arithmetic itself is EncounterIdentity's, in ClassForge.Core, because it is unit-tested
            // there -- "the answer depends on the arguments and on nothing else" is exactly the property
            // the process-local counter this replaces did not have, so it must be executable.
            return EncounterIdentity.Hash(purpose, mapGenSeed, totalRoundCount, activeMapId, biomeName, dungeonName);
        }

        /// <summary>
        /// A peer-stable, human-readable encounter key: <c>"ENC-"</c> plus 16 hex of <see cref="Hash"/>.
        /// This is what the <c>ENCOUNTER_GUID</c> STATE_HASH_CHANCE token resolves to — the same
        /// treatment the three combatant <c>*_GUID</c> tokens got on 2026-08-26 (token NAME kept for
        /// authored-recipe compatibility, VALUE re-pointed at something peers can actually agree on).
        /// </summary>
        internal static string Text()
        {
            return EncounterIdentity.Text(Hash("CF_ENCOUNTER_IDENTITY_V1"));
        }

        /// <summary>
        /// A stable index into a list of <paramref name="count"/> choices, derived from the encounter
        /// key plus any extra replicated discriminators the caller supplies (the venue's own diorama
        /// name, a dungeon room index…). Returns 0 for a non-positive count.
        /// </summary>
        internal static int IndexOf(int count, string purpose, string extraText, int extraInt)
        {
            return EncounterIdentity.IndexOf(Hash(purpose), count, extraText, extraInt);
        }
    }
}
