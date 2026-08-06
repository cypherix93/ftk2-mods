using System.Collections.Generic;

namespace ClassForge.Recipes.Loot
{
    /// <summary>
    /// Deterministic <c>Thing.Id</c> minting — verb spec §3.3.
    /// <c>Id = "cf-" + first 12 hex of SHA256("CF_LOOT_THING|" + GrantKey + "|" + opIndex)</c>,
    /// invariant culture. <paramref name="opIndex"/> is the op's own position within the authored
    /// <c>Ops[]</c> array (computation order == application order, verb spec §3.1/§4.3), so every peer
    /// (and, for <c>ADD_GOLD</c>'s conditional new-Thing case, the applier re-deriving it independently
    /// at apply time) mints the identical id without transmitting it.
    /// </summary>
    public static class LootThingId
    {
        public static string Mint(string grantKey, int opIndex)
        {
            string msg = "CF_LOOT_THING|" + (grantKey ?? string.Empty) + "|" + LootHash.IntInvariant(opIndex);
            return "cf-" + LootHash.ToHex(LootHash.Sha256Bytes(msg), 6); // 12 hex chars = 6 bytes
        }
    }

    /// <summary>
    /// <c>GrantKey</c> / <c>grantSeed</c> / <c>ListDigest</c> derivation — verb spec §3.4/§4.2, as
    /// resolved by the verification wave's GATE A (the shipped game's <c>GameRandom.NextCount</c> is
    /// dead code — never increments — so the spec's original <c>DrawMark</c> field cannot exist; it is
    /// replaced end to end by <see cref="ComputeListDigest"/>, whose fingerprint of the PRE-GRANT
    /// vanilla loot list supplies the per-combat entropy <c>DrawMark</c> was supposed to provide).
    /// </summary>
    public static class LootGrantKey
    {
        /// <summary>
        /// <c>ListDigest = "sha256:" + 64 hex of SHA256</c> over the canonical serialization of the
        /// PRE-GRANT vanilla loot list: rows <c>"ConfigName|Stack"</c> joined <c>"\n"</c>, IN LIST ORDER
        /// (not re-sorted — the vanilla list's own order is part of what is being fingerprinted),
        /// invariant culture.
        /// </summary>
        public static string ComputeListDigest(IReadOnlyList<ILootThingSnapshot> preGrantList)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            if (preGrantList != null)
            {
                for (int i = 0; i < preGrantList.Count; i++)
                {
                    if (i > 0) sb.Append('\n');
                    sb.Append(preGrantList[i].ConfigName ?? string.Empty).Append('|')
                      .Append(LootHash.IntInvariant(preGrantList[i].Stack));
                }
            }
            return LootHash.Sha256HexPrefixed(sb.ToString());
        }

        /// <summary>
        /// <c>GrantKey = first 16 hex of SHA256("CF_SYNC_LOOT_GRANT_V1|" + CombatSeed + "|" +
        /// join(",", enemy guids sorted ordinal) + "|" + ListDigest + "|" + join(",", owner guids sorted
        /// ordinal))</c>, invariant culture (GATE A #2). Enemy guids and <paramref name="listDigest"/>
        /// jointly supply the per-combat entropy <c>DrawMark</c> was meant to provide — both are
        /// replicated state, identical on every peer iff lockstep held.
        /// </summary>
        public static string ComputeGrantKey(int combatSeed, IEnumerable<string> enemyGuids, string listDigest, IEnumerable<string> ownerGuids)
        {
            List<string> sortedEnemies = LootHash.SortedOrdinal(enemyGuids);
            List<string> sortedOwners = LootHash.SortedOrdinal(ownerGuids);
            string msg = "CF_SYNC_LOOT_GRANT_V1|" + LootHash.IntInvariant(combatSeed) + "|" +
                         LootHash.JoinComma(sortedEnemies) + "|" + (listDigest ?? string.Empty) + "|" +
                         LootHash.JoinComma(sortedOwners);
            return LootHash.ToHex(LootHash.Sha256Bytes(msg), 8); // 16 hex chars = 8 bytes
        }

        /// <summary>
        /// <c>grantSeed = unchecked((int)(first 4 bytes big-endian of SHA256("CF_LOOT_GRANT_V1|" +
        /// CombatSeed + "|" + ListDigest + "|" + join(",", enemy guids sorted ordinal))))</c>, invariant
        /// culture (GATE A #3). Feeds the private grant stream's seed — never the shared combat stream.
        /// </summary>
        public static int ComputeGrantSeed(int combatSeed, string listDigest, IEnumerable<string> enemyGuids)
        {
            List<string> sortedEnemies = LootHash.SortedOrdinal(enemyGuids);
            string msg = "CF_LOOT_GRANT_V1|" + LootHash.IntInvariant(combatSeed) + "|" + (listDigest ?? string.Empty) +
                         "|" + LootHash.JoinComma(sortedEnemies);
            byte[] hash = LootHash.Sha256Bytes(msg);
            uint v = ((uint)hash[0] << 24) | ((uint)hash[1] << 16) | ((uint)hash[2] << 8) | hash[3];
            return unchecked((int)v);
        }
    }

    /// <summary>The minimal fact <see cref="LootGrantKey.ComputeListDigest"/> needs about one pre-grant
    /// <c>Thing</c> — deliberately narrower than <see cref="PendingThing"/> so a caller can feed it
    /// straight from a game <c>Thing</c> list without building the full pending-model type.</summary>
    public interface ILootThingSnapshot
    {
        string ConfigName { get; }
        int Stack { get; }
    }
}
