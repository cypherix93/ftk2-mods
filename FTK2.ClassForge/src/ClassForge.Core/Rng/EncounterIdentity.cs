namespace ClassForge.Core.Rng
{
    /// <summary>
    /// The pure arithmetic behind "which encounter is this, in terms every peer agrees on" — the model the
    /// Plugin's <c>ReplicatedEncounterKey</c> feeds live game fields into.
    ///
    /// <para><b>Why the arithmetic lives here and not next to the field reads.</b> Same reason as
    /// <see cref="AiTargetingDraws"/>: nothing in <c>ClassForge.Plugin</c> can be unit-tested (it
    /// references FTK2, BepInEx and Unity), and the property that matters — <i>the result is a function of
    /// its arguments and of NOTHING else</i> — is precisely the property that has to be executable rather
    /// than asserted in a comment. The bug this replaces was a static counter
    /// (<c>VenueGridPatches._dioramaTurn</c>) whose value depended on how many fights the PROCESS had
    /// loaded, so two peers picked different battlefields, hence different tile counts, hence different
    /// <c>ShuffleList</c> draw counts off the shared stream on the first AI turn. A test that calls this
    /// a hundred times and gets the same answer is what makes "no hidden state" a fact rather than an
    /// intention.</para>
    ///
    /// <para><b>The inputs, and why these five.</b> Every one is a plain public field on
    /// <c>GameRunData</c>/<c>AdventureState</c> — inside the serialized graph the vendor MD5s at every
    /// save/init/end-turn checkpoint — and none is a guid, so none is normalised away by
    /// <c>NetworkDebuggingHelper</c>'s guid→ordinal rewrite before that hash is taken. Two peers that
    /// disagreed on any of them would already have tripped the game's OWN desync detector. That is the
    /// strongest evidence available anywhere in this codebase, and it is exactly the evidence
    /// <c>AdventureState.EncounterGUID</c> cannot offer: it holds an <c>Entity.Guid</c>, minted locally by
    /// <c>Guid.NewGuid()</c> on each peer (the map is regenerated from a replicated SEED, not shipped
    /// entity-by-entity), which is why the vendor rewrites guids before hashing in the first place.</para>
    ///
    /// <para><b>Not a counter, and that is the point.</b> Two fights in the same overworld round, same
    /// map, same biome produce the same key. A caller that needs them separated passes a discriminator it
    /// can also prove is replicated (the venue's own diorama name; a dungeon room's index in the
    /// serialized <c>OngoingVenues</c> list). Repetition is cosmetic; a counter is a desync.</para>
    /// </summary>
    public static class EncounterIdentity
    {
        /// <summary>
        /// FNV-1a 64 over <paramref name="purpose"/> and the five replicated encounter fields, in this
        /// fixed order. Strings are length-prefixed by <see cref="StableHash.AbsorbString"/>, so
        /// <c>("AB", "C")</c> and <c>("A", "BC")</c> cannot collide onto one value.
        /// </summary>
        public static ulong Hash(string purpose, int mapGenSeed, int totalRoundCount,
            string activeMapId, string biomeName, string dungeonName)
        {
            ulong h = StableHash.AbsorbString(StableHash.Fnv1aOffsetBasis, purpose);
            h = StableHash.AbsorbInt32(h, mapGenSeed);
            h = StableHash.AbsorbInt32(h, totalRoundCount);
            h = StableHash.AbsorbString(h, activeMapId);
            h = StableHash.AbsorbString(h, biomeName);
            h = StableHash.AbsorbString(h, dungeonName);
            return h;
        }

        /// <summary>The human-readable form: <c>"ENC-"</c> plus 16 lower-case hex digits, invariant
        /// culture. This is what the <c>ENCOUNTER_GUID</c> STATE_HASH_CHANCE token resolves to.</summary>
        public static string Text(ulong hash)
        {
            return "ENC-" + hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Folds two further replicated discriminators into <paramref name="hash"/> and reduces the result
        /// to <c>[0, count)</c>. Returns 0 when <paramref name="count"/> is not positive, so a caller with
        /// an empty choice list cannot index out of range.
        /// </summary>
        public static int IndexOf(ulong hash, int count, string extraText, int extraInt)
        {
            if (count <= 0) return 0;
            ulong h = StableHash.AbsorbString(hash, extraText);
            h = StableHash.AbsorbInt32(h, extraInt);
            return (int)(h % (ulong)count);
        }
    }
}
