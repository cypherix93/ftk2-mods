using System;

namespace ClassForge.Core.Rng
{
    /// <summary>
    /// A cross-peer stable identity for a combat entity. It carries exactly one field: an
    /// <see cref="Ordinal"/> -- the entity's index within a deterministically ordered, replicated
    /// entity list.
    ///
    /// <para><b>Why not the entity GUID.</b> <c>Entity.Guid</c> is LOCAL. <c>Entity.Create()</c> assigns
    /// <c>System.Guid.NewGuid().ToString()</c> (<c>Entity.cs:119-125</c>), so the same logical enemy carries a
    /// different GUID on every peer in a co-op session. The vendor's own desync hasher acknowledges this:
    /// before it MD5s a run snapshot it walks every entity and rewrites each GUID to a positional ordinal
    /// (<c>NetworkDebuggingHelper._convertGuidsOfEntities</c> -> <c>_tryTransformToIntIdFromGuid</c> ->
    /// <c>_getIntIdFromGuid</c>), because otherwise two in-sync peers would hash to different digests. A GUID
    /// is therefore never a legal RNG seed input and never a legal persisted key. This type is what you use
    /// instead.</para>
    ///
    /// <para><b>Why ordinal and nothing else.</b> <c>_getIntIdFromGuid</c> is a bare monotonic counter over a
    /// per-hash-call dictionary -- it assigns 1, 2, 3, ... in first-encounter order and consults nothing about
    /// the entity. Positional ordinal in a replicated list is the ONLY cross-peer identity the vendor's own
    /// code demonstrates, and it is the whole of it. An ordinal is a list index, so it is already unique by
    /// construction: no additional field can improve its discriminating power, and every candidate field
    /// measurably reduces its safety, because every one of them is mutable during a fight:</para>
    /// <list type="bullet">
    ///   <item><description><b>Grid position.</b> It does exist -- <c>VenueComponent.TilePosition</c>, declared
    ///   <c>(int x, int y)</c> (<c>VenueComponent.cs:5</c>) and carried by both character and tile entities --
    ///   but <c>VenueHelper.SetTilePosition</c> (<c>VenueHelper.cs:851</c>) reassigns it on every move,
    ///   knockback, swap and summon placement, and <c>AIHelper.cs:319</c> mutates it inside AI evaluation
    ///   itself. A stream keyed on position derives different values on two peers that evaluate it at points
    ///   straddling a movement -- draw counts stay equal so lockstep survives, but a downstream
    ///   branch-on-value can then change draw counts. That is a latent desync, so position is banned from the
    ///   key rather than merely omitted from it.</description></item>
    ///   <item><description><b><c>CharacterComponent.ConfigName</c>.</b> Swapped on NPC level progression
    ///   (<c>CharacterHelper.cs:2050</c>, <c>GetCharacterConfigAtLevel</c>) and on player class rebuild
    ///   (<c>PartyManagementDirector._rebuildCharactertAsNewConfigType</c>, <c>:1846</c>).</description></item>
    ///   <item><description><b><c>CharacterComponent.GroupIndex</c>.</b> Reassigned mid-run when an entity
    ///   changes side -- <c>CombatHelper.cs:2219</c> (companion) and <c>AdventureHelper.cs:748</c>
    ///   (follower).</description></item>
    /// </list>
    ///
    /// <para><b>Why a roster index is peer-identical.</b> FTK2 co-op is deterministic lockstep: the wire
    /// carries inputs, never entity identities. <c>CombatPhase._sendPerformAbilityActionToNetwork</c>
    /// (<c>CombatPhase.cs:9003-9034</c>) broadcasts an <c>AbilityActionIndex</c> and a tile <c>Position</c>;
    /// the receiver resolves the target by scanning for a matching <c>TilePosition</c>
    /// (<c>CombatPhase.cs:8574-8588</c>). Sibling actions carry <c>PlayerIndex</c> (<c>:8540</c>),
    /// <c>ThingIndex</c> (<c>:8554</c>) and <c>EntityIndex</c> (<c>:9106</c>). Zero GUIDs cross the wire --
    /// <b>an index into a well-known list IS the vendor's own cross-peer identity mechanism</b>, and this type
    /// is that mechanism applied to the combat roster. Every peer then re-runs the same simulation code
    /// against the same shared-seed <c>GameRandom</c> (<c>CombatPhase.cs:271</c>,
    /// <c>NetworkHelper.cs:1001/1083</c>), so the roster is built and appended to in the same sequence
    /// everywhere.</para>
    ///
    /// <para><b>The ordinal is peer-stable, NOT time-stable, and that distinction is the whole contract.</b>
    /// It indexes a list the vendor mutates constantly: <c>CombatPhase.cs:1538</c> removes a departing entity,
    /// <c>:3849</c> and <c>:4285</c> insert revived characters mid-list, <c>:669-674</c> clears and rebuilds
    /// the whole roster on a wave change, <c>:7958</c> destroys every tile entity on a grid swap. So one
    /// entity's ordinal can shift when a DIFFERENT entity dies or is summoned. That is acceptable and is why
    /// <see cref="CFSeedInputs"/> also absorbs round and turn: a stream is opened at a single lockstep point,
    /// and the property that must hold is only that both peers compute the same ordinal AT THAT POINT. It is
    /// also why callers must derive the ordinal from the same replicated list every time; see
    /// <see cref="FromCombatRosterIndex"/>.</para>
    ///
    /// <para><b>Mid-combat summons -- the case that breaks GUIDs and survives ordinals.</b> A summon is
    /// created independently on every peer, not replicated: <c>CombatHelper.cs:2233</c> calls
    /// <c>TryCreateSummon</c> with <c>pGuid = null</c>, which reaches <c>Entity.Create()</c> and a fresh
    /// <c>System.Guid.NewGuid()</c>, so the same summon carries a different GUID on each peer -- by
    /// construction it cannot be otherwise. What IS identical is the append: the same code path runs on every
    /// peer off the same seeded stream and appends via <c>CombatState.Entities.Add(pSummonEntity)</c>
    /// (<c>CombatHelper.cs:2299</c>), landing the summon at the same index everywhere. The hard case therefore
    /// argues FOR the ordinal and against every identity derived from the entity itself.</para>
    ///
    /// <para><b>Known soft spot, deliberately not papered over.</b> The roster is seeded
    /// <c>_combatState.Entities.AddRange(base._gameObjectMaps.FromTile.Keys)</c>
    /// (<c>CombatPhase.cs:314</c>, again at <c>:674</c>) -- that is <c>Dictionary</c> key enumeration order,
    /// which .NET does not contractually define. In practice the dictionary is insertion-ordered and the tiles
    /// are generated row-major from a fixed ASCII venue map (<c>VenueHelper.cs:65-84</c>), so it is stable in
    /// fact; it is simply not stable by contract. Note also that <c>CombatState</c> is <c>[JsonIgnore]</c> on
    /// <c>GameRunData</c> (<c>GameRunData.cs:54-55</c>), so the vendor's GameRun desync MD5 does NOT cover the
    /// combat roster and will not catch a roster-order divergence for us. Do not key a stream on a TILE
    /// entity's ordinal.
    /// <br/>
    /// Restated so nobody has to rediscover it: <b>EVERY ordinal-based fix in this codebase rests on this
    /// one unguaranteed property</b> -- PeerOrder, TileOrder.SelectPlacement, LootGrantKey's roster keys,
    /// the loot-grant owner iteration order, the three combatant <c>*_GUID</c> STATE_HASH_CHANCE tokens.
    /// If .NET ever changed <c>Dictionary</c> enumeration order, or another mod's patch inserted into
    /// <c>FromTile</c> in a different order on one peer, all of them would move together and NOTHING --
    /// not the vendor MD5 (CombatState is [JsonIgnore]), not the GameRandomNextInt probe (the counts stay
    /// equal), not our own audits -- would report it. This is a VENDOR property, not ours; it is not
    /// fixable from a mod, and it is recorded rather than papered over. The determinism self-check in
    /// docs/research/VERIFICATION-METHOD.md §6 (same fixture, pinned seed, twice, diff the digest and the
    /// draw sequence) is the only instrument that would catch it, and it only catches it locally.</para>
    ///
    /// <para><b>Hashing.</b> <see cref="StableValue"/> is FNV-1a 64 over the field bytes, NOT
    /// <c>string.GetHashCode()</c> -- see <see cref="StableHash"/> for why that would be a silent
    /// reintroduction of the bug. <see cref="GetHashCode"/> folds the same stable value, so even the CLR
    /// dictionary bucketing of a key set is peer-identical.</para>
    /// </summary>
    public struct EntityKey : IEquatable<EntityKey>
    {
        /// <summary>
        /// Position within a deterministically ordered, replicated entity list. Peer-identical because the
        /// ordering is. This is the entire key -- see the type remarks for why nothing else belongs here.
        /// </summary>
        public readonly int Ordinal;

        /// <summary>Builds a key from an already-computed ordinal. Prefer <see cref="FromCombatRosterIndex"/>,
        /// which names the list the ordinal must come from.</summary>
        public EntityKey(int ordinal)
        {
            Ordinal = ordinal;
        }

        /// <summary>
        /// Builds a key from an entity's index in <c>CombatState.Entities</c> -- the replicated combat roster
        /// (<c>CombatState.cs:46</c>). Named rather than inlined because WHICH list the index came from is the
        /// load-bearing part of the contract: indexing a different, locally-derived list
        /// (<c>CombatState.RoundEntities</c>, a filtered <c>FindAll</c> result, an <c>OrderBy</c> projection)
        /// yields ordinals that collide with these and quietly share streams.
        /// </summary>
        public static EntityKey FromCombatRosterIndex(int index)
        {
            return new EntityKey(index);
        }

        /// <summary>
        /// The peer-stable 64-bit hash of this key. Fixed algorithm (FNV-1a 64 over length-prefixed field
        /// bytes) with no process salt and no culture-dependent formatting. The <c>"EntityKey/2"</c> domain tag
        /// namespaces the value and is versioned: any change to the field set must bump it so the pinned
        /// known-answer constant in the test suite fails loudly rather than drifting.
        /// </summary>
        public ulong StableValue
        {
            get
            {
                ulong h = StableHash.Fnv1aOffsetBasis;
                h = StableHash.AbsorbString(h, "EntityKey/2");
                h = StableHash.AbsorbInt32(h, Ordinal);
                return h;
            }
        }

        /// <summary>Stable, culture-invariant text form. Safe to log, safe to write into
        /// <c>Thing.CustomData</c> (which IS inside the vendor desync MD5 -- it is absent from
        /// <c>NetworkDebuggingHelper._ignorePropertyNames</c> -- so anything stashed there must be
        /// byte-identical across peers).</summary>
        public override string ToString()
        {
            return "E" + StableHash.Format(Ordinal);
        }

        public bool Equals(EntityKey other)
        {
            return Ordinal == other.Ordinal;
        }

        public override bool Equals(object obj)
        {
            return obj is EntityKey && Equals((EntityKey)obj);
        }

        /// <summary>Folded <see cref="StableValue"/>, so bucketing is peer-identical rather than
        /// implementation-defined.</summary>
        public override int GetHashCode()
        {
            ulong v = StableValue;
            return unchecked((int)(v ^ (v >> 32)));
        }

        public static bool operator ==(EntityKey a, EntityKey b) { return a.Equals(b); }
        public static bool operator !=(EntityKey a, EntityKey b) { return !a.Equals(b); }
    }
}
