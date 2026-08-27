using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using ClassForge.Recipes.Abstractions;
using ClassForge.Recipes.Runtime;

namespace ClassForge.Plugin
{
    /// <summary>
    /// Adapts one game <c>Entity</c> onto <see cref="ICombatEntity"/>. Every member is a pure read of
    /// replicated state (SPEC-DELTA-v1.1 §3) and every one is individually try/caught: a recipe condition
    /// that cannot be answered degrades to a neutral value rather than throwing out of a Harmony patch.
    /// </summary>
    internal sealed class EntityAdapter : ICombatEntity
    {
        /// <summary>The <c>eConfigTags</c> member marking a Thing as a toolbelt item — the slotless class
        /// of item that can never be equipped (InventoryHelper.cs:975).</summary>
        private const string ToolbeltTag = "TOOLBELT";

        /// <summary>The one VIRTUAL <c>ENTITY_TAG</c> value: not an <c>eConfigTags</c> member but the
        /// <see cref="TrainerCaptureRules.CanCapture"/> verdict, so the capture gate can be asked as a
        /// recipe condition. See <see cref="HasTag"/> for why.</summary>
        internal const string VirtualTagCapturable = "CF_CAPTURABLE";

        internal readonly Entity Native;
        private readonly CombatContextAdapter _ctx;
        private readonly int _rosterOrdinal;

        private IReadOnlyList<string> _statuses;
        private IReadOnlyList<string> _passives;
        private string _weaponClass;
        private bool _weaponClassResolved;

        internal EntityAdapter(Entity native, CombatContextAdapter ctx, int rosterOrdinal)
        {
            Native = native;
            _ctx = ctx;
            _rosterOrdinal = rosterOrdinal;
        }

        public string Guid
        {
            // Entity.Guid is `public string Guid { get; private set; }` (Entity.cs L16) — a string property,
            // not a System.Guid. It is PEER-LOCAL: Entity.Create() mints it with System.Guid.NewGuid()
            // (Entity.cs:119-125), so the same logical combatant carries a different value on every peer.
            // Legal ONLY as a same-peer round trip - a dictionary key this peer both writes and reads, and
            // EngineAction.TargetGuid, which NativeByGuid resolves right back on this same peer. It is NOT
            // the universal tiebreak any more; that clause is withdrawn. See RosterOrdinal below.
            get { try { return Native != null ? (Native.Guid ?? "") : ""; } catch { return ""; } }
        }

        /// <summary>
        /// Index in <c>CombatState.Entities</c>, captured once when this adapter was created for THIS
        /// dispatch - see <see cref="ICombatEntity.RosterOrdinal"/>. Captured rather than recomputed on
        /// every read because the roster mutates during a fight (revives insert mid-list, wave changes
        /// rebuild it): an ordering opened at one lockstep point must see one consistent ordinal for the
        /// whole of that point, and this adapter's lifetime IS that point.
        /// <para><see cref="PeerOrder.Unknown"/> when the entity is not on the roster, or the roster could
        /// not be read at all.</para>
        /// </summary>
        public int RosterOrdinal { get { return _rosterOrdinal; } }

        /// <summary><c>CharacterHelper.IsDead</c> is <c>CurrentHealth &lt; 1</c> (CharacterHelper.cs L1469).</summary>
        public bool IsAlive
        {
            get { try { return Native != null && !CharacterHelper.IsDead(Native); } catch { return false; } }
        }

        /// <summary>
        /// Selects <c>AiProcChance</c> over <c>ProcChance</c>. There is no "is this entity player-driven"
        /// flag in the decompile; the engine's own discriminator is <c>CharacterComponent.GroupIndex</c>
        /// (<c>CharacterHelper.IsFriendly</c> = <c>GroupIndex == 0</c>, L1483), so anything outside the
        /// player group is treated as AI-controlled.
        /// </summary>
        public bool IsAiControlled
        {
            get { try { return Native == null || !CharacterHelper.IsFriendly(Native); } catch { return false; } }
        }

        /// <summary>
        /// <c>CharacterHelper.GetStat(Entity, string, bool)</c> — L396, the one overload SPEC-DELTA-v1.1
        /// §3.1 standardises on out of the eight available (PSN "Notable surprises" #6) — for every stat
        /// key except <c>HP</c>/<c>MXHP</c>.
        /// <para>
        /// <c>HP</c> and <c>MXHP</c> are special-cased onto <b>live combat state</b>:
        /// <c>CharacterHelper.GetHealth</c> (CharacterHelper.cs L1439, <c>CharacterComponent.CurrentHealth</c>)
        /// and <c>CharacterHelper.GetMaxHealth</c> (CharacterHelper.cs L1422). The class config's static
        /// base-stat table (what <c>GetCharacterBaseStat</c> — and therefore the fallback <c>GetStat</c> call
        /// below — reads) carries an "HP" key but no "MXHP" key, so every HP-percentage recipe token
        /// (<c>RankValue</c>'s <c>HP_PCT</c>, <c>HP_THRESHOLD.Percent</c>, <c>TARGET_HP_PCT</c>) divided by a
        /// permanent 0 and always resolved to 0. Fixing it here — once, at the adapter — repairs every
        /// caller at once and leaves the authored recipe JSON untouched.
        /// </para>
        /// <para>
        /// Both live calls are guarded independently: a non-combat caller (character creation, config
        /// validation) hands in an <c>Entity</c> that may lack the components <c>GetHealth</c>/<c>GetMaxHealth</c>
        /// need, or may simply throw inside the game's own health math. On failure this falls back to the
        /// same static base-stat lookup every other stat key uses — which, for "MXHP", reproduces today's
        /// safe (if uninformative) 0 rather than throwing out of a Harmony patch.
        /// </para>
        /// </summary>
        public int GetStat(string statKey)
        {
            try
            {
                if (Native == null || string.IsNullOrEmpty(statKey)) return 0;

                if (string.Equals(statKey, "HP", StringComparison.Ordinal))
                {
                    try { return CharacterHelper.GetHealth(Native); }
                    catch { return CharacterHelper.GetStat(Native, statKey, false); }
                }

                if (string.Equals(statKey, "MXHP", StringComparison.Ordinal))
                {
                    try { return CharacterHelper.GetMaxHealth(Native); }
                    catch { return CharacterHelper.GetStat(Native, statKey, false); }
                }

                return CharacterHelper.GetStat(Native, statKey, false);
            }
            catch { return 0; }
        }

        /// <summary>
        /// <c>StatusEffectComponent.Statuses</c> is a <c>Dictionary&lt;string, StatusEffectInfo&gt;</c>
        /// (StatusEffectComponent.cs) — <b>not</b> a list. Keys are materialised and sorted ordinal here so
        /// the engine never sees Dictionary enumeration order (§5.2 invariant 4).
        /// </summary>
        public IReadOnlyList<string> Statuses
        {
            get
            {
                if (_statuses != null) return _statuses;
                var list = new List<string>();
                try
                {
                    StatusEffectComponent comp;
                    if (Native != null && Native.TryGet<StatusEffectComponent>(out comp) && comp != null && comp.Statuses != null)
                    {
                        foreach (var kv in comp.Statuses)
                            if (!string.IsNullOrEmpty(kv.Key)) list.Add(kv.Key);
                    }
                }
                catch { }
                list.Sort(StringComparer.Ordinal);
                _statuses = list;
                return _statuses;
            }
        }

        /// <summary>
        /// Row bucket. Characters carry <c>VenueComponent</c> (position); the row bucket lives on the
        /// <b>tile</b> entity's <c>VenueTileComponent.RowPositionsType</c> (<c>eTileRowPositions</c>), reached
        /// exactly the way the engine reaches it —
        /// <c>VenueHelper.GetTileEntityOfCharacter(entity, CombatState.Entities)</c> (VenueHelper.cs L103).
        /// </summary>
        public EntityRow Row
        {
            get
            {
                try
                {
                    var state = _ctx != null ? _ctx.State : null;
                    if (Native == null || state == null || state.Entities == null) return EntityRow.UNKNOWN;
                    var tile = VenueHelper.GetTileEntityOfCharacter(Native, state.Entities);
                    VenueTileComponent tc;
                    if (tile == null || !tile.TryGet<VenueTileComponent>(out tc) || tc == null) return EntityRow.UNKNOWN;
                    switch (tc.RowPositionsType)
                    {
                        case eTileRowPositions.FRONT: return EntityRow.FRONT;
                        case eTileRowPositions.BACK: return EntityRow.BACK;
                        default: return EntityRow.UNKNOWN;
                    }
                }
                catch { return EntityRow.UNKNOWN; }
            }
        }

        /// <summary>
        /// Board column — <c>VenueComponent.TilePosition.x</c> (VenueComponent.cs, a
        /// <c>(int x, int y)</c> tuple field). <c>int.MinValue</c> when the character carries no
        /// VenueComponent or the read throws, per <c>ICombatEntity.TileX</c>; ALLY_IN_FRONT treats the
        /// sentinel as "unknown position" and evaluates false.
        /// </summary>
        public int TileX
        {
            get
            {
                try
                {
                    VenueComponent vc;
                    if (Native == null || !Native.TryGet<VenueComponent>(out vc) || vc == null) return int.MinValue;
                    return vc.TilePosition.x;
                }
                catch { return int.MinValue; }
            }
        }

        /// <summary>Board row-line — <c>VenueComponent.TilePosition.y</c>. Same sentinel contract as
        /// <see cref="TileX"/>.</summary>
        public int TileY
        {
            get
            {
                try
                {
                    VenueComponent vc;
                    if (Native == null || !Native.TryGet<VenueComponent>(out vc) || vc == null) return int.MinValue;
                    return vc.TilePosition.y;
                }
                catch { return int.MinValue; }
            }
        }

        /// <summary>
        /// THIS character's own progression level -- <c>ProgressionHelper.GetEntityLevel</c>
        /// (ProgressionHelper.cs:565), which derives the level from the entity's "XP" inventory Thing
        /// (<c>GetEntityXP</c>, ProgressionHelper.cs:554: <c>Things.Find(t =&gt; t.ConfigName == "XP")
        /// ?.StackCount ?? 0</c>) against PLAYER_XP_LEVELS / COMPANION_XP_LEVELS.
        ///
        /// <para>Returns <see cref="EntityReads.UnknownLevel"/> rather than a fabricated 0 whenever a
        /// real per-entity level is not readable. XP progression is player-side only: an enemy carries
        /// no "XP" Thing, so <c>GetEntityLevel</c> reports a flat 0 for every one of them while their
        /// real level lives in the static <c>CharacterConfig.Level</c> -- a DIFFERENT quantity. The
        /// <c>PlayerComponent</c> guard mirrors the game's own precedent in
        /// <c>CharacterHelper.GetLevelUpAttackBonusStat</c> (CharacterHelper.cs:878-884), which returns
        /// 0 for anything without one instead of calling GetEntityLevel.</para>
        /// </summary>
        public int Level
        {
            get
            {
                try
                {
                    if (Native == null) return EntityReads.UnknownLevel;
                    if (!Native.Has<CharacterComponent>()) return EntityReads.UnknownLevel;
                    if (!Native.Has<PlayerComponent>()) return EntityReads.UnknownLevel;
                    return ProgressionHelper.GetEntityLevel(Native);
                }
                catch { return EntityReads.UnknownLevel; }
            }
        }

        /// <summary>
        /// v1.4 <c>HAS_ITEM</c>: does this character CARRY <paramref name="thingConfigName"/>?
        /// Ordinal, case-sensitive scan of <c>CharacterComponent.Things</c> -- the read
        /// <c>InventoryHelper.HasItemByName</c> performs (InventoryHelper.cs:424-427) and the read
        /// <c>ProgressionHelper.GetEntityXP</c> uses for "XP" (ProgressionHelper.cs:557).
        ///
        /// <para>POSSESSION, not equipment. Toolbelt Things (<c>eConfigTags.TOOLBELT</c>,
        /// InventoryHelper.cs:975) have NO <c>eEquipmentSlots</c> slot and can never be equipped, and
        /// an equipped Thing is still resolved back out of <c>Things</c>
        /// (<c>EquipmentHelper.GetEquippedThingBySlot</c>, EquipmentHelper.cs:165-177), so possession
        /// covers both. <c>ParentId</c> is deliberately NOT filtered: a Thing nested in a container
        /// is still carried.</para>
        ///
        /// <para>Returns <c>null</c> -- the inventory-read "unknown" -- when the inventory cannot be
        /// read AT ALL. <c>false</c> means the inventory WAS read and genuinely lacks the item. The
        /// engine turns <c>null</c> into a NON-negatable false, exactly as for
        /// <see cref="EntityReads.UnknownLevel"/>; never collapse the two by returning false.</para>
        /// </summary>
        public bool? HasItem(string thingConfigName)
        {
            try
            {
                if (Native == null || string.IsNullOrEmpty(thingConfigName)) return null;
                CharacterComponent cc;
                if (!Native.TryGet<CharacterComponent>(out cc) || cc == null) return null;
                var things = cc.Things;
                if (things == null) return null;
                for (int i = 0; i < things.Count; i++)
                {
                    var thing = things[i];
                    if (thing != null && string.Equals(thing.ConfigName, thingConfigName, StringComparison.Ordinal))
                        return true;
                }
                return false;
            }
            catch { return null; }
        }

        /// <summary>Equipped main-hand weapon's <c>ThingConfig.Class</c> (EGT §5 free-string space).</summary>
        public string WeaponClass
        {
            get
            {
                if (_weaponClassResolved) return _weaponClass;
                _weaponClassResolved = true;
                _weaponClass = "";
                try
                {
                    CharacterComponent cc;
                    if (Native == null || !Native.TryGet<CharacterComponent>(out cc) || cc == null) return _weaponClass;
                    var weapon = EquipmentHelper.GetEquippedWeaponThing(cc);
                    if (weapon == null || string.IsNullOrEmpty(weapon.ConfigName)) return _weaponClass;
                    var cfg = GameLookups.ThingConfig(weapon.ConfigName);
                    if (cfg != null) _weaponClass = cfg.Class ?? "";
                }
                catch { }
                return _weaponClass;
            }
        }

        /// <summary><c>CharacterComponent.CharacterType</c> (<c>eCharacterTypes</c>, EGT §11).</summary>
        public string CharacterType
        {
            get
            {
                try
                {
                    CharacterComponent cc;
                    if (Native == null || !Native.TryGet<CharacterComponent>(out cc) || cc == null) return "";
                    return cc.CharacterType.ToString();
                }
                catch { return ""; }
            }
        }

        /// <summary>The character config's <c>BaseType</c> (CharacterConfig.cs).</summary>
        public string BaseType
        {
            get
            {
                try
                {
                    CharacterComponent cc;
                    if (Native == null || !Native.TryGet<CharacterComponent>(out cc) || cc == null) return "";
                    var cfg = GameLookups.CharacterConfig(cc.ConfigName);
                    return cfg != null ? (cfg.BaseType ?? "") : "";
                }
                catch { return ""; }
            }
        }

        /// <summary>
        /// Skill ids this entity carries — a recipe only evaluates for owners holding its <c>SKILL_*</c> id.
        /// Sources, per SPEC-DELTA-v1.1 OQ#1: the class config's own <c>Passives</c>, plus
        /// <c>Equippable.Passives</c> of every equipped Thing. <c>EquipmentHelper.GetEquippedThings(entity,
        /// pVisualOnly: false, pIncludeTraits: true)</c> (EquipmentHelper.cs L341) is the native call that
        /// includes <c>TRAIT_</c>-prefixed Things regardless of slot — which is exactly why a ClassForge
        /// trait with <c>Equippable.Slots: []</c> still contributes its passives with zero patches.
        /// <b>Plus</b>, per Encounter Modifiers spec §4.4: for each entry of this entity's
        /// <c>StatusEffectComponent.Statuses</c>, <c>Configs.StatusEffects[key].Passives</c> filtered to
        /// <c>SKILL_</c>-prefixed entries — mirroring native <c>CharacterHelper.GetPassiveSkills</c>
        /// (L936-968). This is what lets a status-attached recipe (e.g. <c>SKILL_CF_ENCMOD_REGEN_TICK</c>
        /// on <c>STATUS_CF_ENCMOD_REGENERATING</c>) route through the ordinary owned-trigger path with no
        /// bespoke combat-scoped machinery.
        /// <para>The native calls return unordered collections, so the result is sorted ordinal and
        /// de-duplicated before the engine sees it (§5.2 invariant 4). Adapter instances are per-hook-
        /// invocation (see remarks below), so this cannot go stale across status changes.</para>
        /// </summary>
        public IReadOnlyList<string> Passives
        {
            get
            {
                if (_passives != null) return _passives;
                var set = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    CharacterComponent cc;
                    if (Native != null && Native.TryGet<CharacterComponent>(out cc) && cc != null)
                    {
                        var charCfg = GameLookups.CharacterConfig(cc.ConfigName);
                        if (charCfg != null && charCfg.Passives != null)
                            for (int i = 0; i < charCfg.Passives.Count; i++)
                                if (!string.IsNullOrEmpty(charCfg.Passives[i])) set.Add(charCfg.Passives[i]);

                        var equipped = EquipmentHelper.GetEquippedThings(Native, false, true);
                        if (equipped != null)
                        {
                            foreach (var thing in equipped)
                            {
                                if (thing == null || string.IsNullOrEmpty(thing.ConfigName)) continue;
                                if (thing.PassiveModifiers != null)
                                    for (int i = 0; i < thing.PassiveModifiers.Count; i++)
                                        if (!string.IsNullOrEmpty(thing.PassiveModifiers[i])) set.Add(thing.PassiveModifiers[i]);

                                var tc = GameLookups.ThingConfig(thing.ConfigName);
                                if (tc == null || tc.Equippable == null || tc.Equippable.Passives == null) continue;
                                for (int i = 0; i < tc.Equippable.Passives.Count; i++)
                                    if (!string.IsNullOrEmpty(tc.Equippable.Passives[i])) set.Add(tc.Equippable.Passives[i]);
                            }
                        }

                        // --- TOOLBELT possession passives (capture spec, v1.5) ---
                        // A TOOLBELT Thing has NO eEquipmentSlots slot (InventoryHelper.cs:975) and can
                        // therefore NEVER appear in CharacterComponent.Equipped, so
                        // EquipmentHelper.GetEquippedThingsNonAlloc (EquipmentHelper.cs:312-339) — which
                        // admits only TRAIT_-prefixed Things and Things whose Id matches an Equipped slot —
                        // can never see one. Its Equippable.Passives are, today, unreachable dead data.
                        //
                        // That is exactly the hole a class-agnostic item has to fill: the capture ball is
                        // meant to work for ANY class that carries it, and a recipe is only evaluated for an
                        // owner whose Passives contain its SKILL_ id (RecipeDispatcher.Holds). Granting the
                        // ball's passives by POSSESSION is the same rule HAS_ITEM already reasons by, and is
                        // the ONLY way "any class can capture" is expressible without hardcoding the class
                        // list (docs/CONVENTIONS.md: the engine hardcodes no content).
                        //
                        // Provably inert for everything that shipped before it: measured 2026-08-25, ZERO of
                        // the 8 TOOLBELT-tagged Things in the game's Things/*.json and ZERO in any pack's
                        // items.json declare Equippable.Passives at all. The set this adds is empty unless
                        // an author opts in by writing one.
                        if (cc.Things != null)
                        {
                            for (int i = 0; i < cc.Things.Count; i++)
                            {
                                var t = cc.Things[i];
                                if (t == null || string.IsNullOrEmpty(t.ConfigName)) continue;
                                var tcfg = GameLookups.ThingConfig(t.ConfigName);
                                if (tcfg == null || tcfg.Equippable == null || tcfg.Equippable.Passives == null) continue;
                                if (tcfg.Tags == null || !tcfg.Tags.Contains(ToolbeltTag)) continue;
                                for (int k = 0; k < tcfg.Equippable.Passives.Count; k++)
                                    if (!string.IsNullOrEmpty(tcfg.Equippable.Passives[k]))
                                        set.Add(tcfg.Equippable.Passives[k]);
                            }
                        }
                    }

                    // Encounter Modifiers spec §4.4 — status-attached SKILL_ passives.
                    StatusEffectComponent statusComp;
                    if (Native != null && Native.TryGet<StatusEffectComponent>(out statusComp) && statusComp != null && statusComp.Statuses != null)
                    {
                        foreach (var kv in statusComp.Statuses)
                        {
                            if (string.IsNullOrEmpty(kv.Key)) continue;
                            var statusCfg = GameLookups.StatusConfig(kv.Key);
                            if (statusCfg == null || statusCfg.Passives == null) continue;
                            for (int i = 0; i < statusCfg.Passives.Count; i++)
                            {
                                var p = statusCfg.Passives[i];
                                if (!string.IsNullOrEmpty(p) && p.StartsWith("SKILL_", StringComparison.Ordinal))
                                    set.Add(p);
                            }
                        }
                    }
                }
                catch { }
                var list = new List<string>(set);
                list.Sort(StringComparer.Ordinal);
                _passives = list;
                return _passives;
            }
        }

        /// <summary>GATE D: <c>CharacterHelper.IsEnemy(entity)</c> — <c>CharacterComponent.GroupIndex == 1</c>
        /// (verified CharacterHelper.cs:1492).</summary>
        public bool IsEnemy
        {
            get { try { return Native != null && CharacterHelper.IsEnemy(Native); } catch { return false; } }
        }

        /// <summary><c>CharacterHelper.ActorHasTag(entity, eConfigTags.&lt;tagName&gt;)</c> — parses the enum
        /// member name from <paramref name="tagName"/>; an unparseable name is a fail-safe false.</summary>
        public bool HasTag(string tagName)
        {
            try
            {
                if (Native == null || string.IsNullOrEmpty(tagName)) return false;

                // --- the ONE virtual tag: CF_CAPTURABLE (Gary, the capture Trainer) -------------------
                // "Is this entity capturable?" cannot be written as recipe conditions: the answer is an OR
                // over the 20-member PLAYTHING_* tag family (conditions are ANDed, there is no OR) plus a
                // graph walk over the target's own Things -> Interactable.Abilities -> Actions for the
                // no-stun rule, plus a dCharacter composition lookup. It therefore lived in
                // TrainerCaptureRules and was only asked INSIDE the CAPTURE effect -- i.e. AFTER
                // RecipeDispatcher had already spent the recipe's ONCE_PER_COMBAT budget (ConsumeOn.PROC,
                // RecipeDispatcher.cs:466-467). One throw at an untagged enemy then blocked every further
                // capture in that fight, silently. Exposing the existing predicate as a tag moves it into
                // Conditions, which are evaluated BEFORE the budget is touched (RecipeDispatcher.cs:407 vs
                // :420-467), so a refused target no longer costs the fight's attempt.
                //
                // Safe as an extension of this adapter: "CF_CAPTURABLE" is not an eConfigTags member, so
                // before this branch it parsed as nothing and answered false -- no existing recipe, pack or
                // generated modifier can change meaning. It reads only replicated state (config Tags,
                // config kit, CharacterComponent.CharacterType, IsDead) and takes zero random draws, so it
                // is as parity-safe as the ActorHasTag read below.
                if (string.Equals(tagName, VirtualTagCapturable, StringComparison.Ordinal))
                {
                    string ignored;
                    return TrainerCaptureRules.CanCapture(Native, out ignored);
                }

                eConfigTags tag;
                if (!Enum.TryParse(tagName, out tag)) return false;
                return CharacterHelper.ActorHasTag(Native, tag);
            }
            catch { return false; }
        }

        /// <summary><c>CharacterComponent.ConfigName</c>.</summary>
        public string ConfigName
        {
            get
            {
                try
                {
                    CharacterComponent cc;
                    if (Native == null || !Native.TryGet<CharacterComponent>(out cc) || cc == null) return "";
                    return cc.ConfigName ?? "";
                }
                catch { return ""; }
            }
        }
    }

    /// <summary>
    /// Adapts the live combat onto <see cref="ICombatContext"/>. One instance is built per hook invocation;
    /// it is a thin view over <c>Env.GameRun.CombatState</c>, not a copy.
    /// </summary>
    /// <summary>
    /// Adapts one venue TILE <c>Entity</c> onto <see cref="ICombatTile"/> (v1.4 <c>RANDOM_TILE</c>).
    /// <para>Coordinates are snapshotted at construction rather than read lazily: the tile list is built
    /// once per dispatch and the draw index must mean the same square for the whole of that dispatch. A
    /// tile's <c>VenueComponent.TilePosition</c> never moves anyway (only characters move between tiles),
    /// so the snapshot is also free of staleness risk.</para>
    /// </summary>
    internal sealed class TileAdapter : ICombatTile
    {
        internal readonly Entity Native;
        private readonly int _x;
        private readonly int _y;
        private readonly string _guid;

        internal TileAdapter(Entity native, int x, int y, string guid)
        {
            Native = native;
            _x = x;
            _y = y;
            _guid = guid ?? "";
        }

        public int X { get { return _x; } }
        public int Y { get { return _y; } }
        public string LocalGuid { get { return _guid; } }
    }

    internal sealed class CombatContextAdapter : ICombatContext
    {
        internal readonly CombatState State;
        internal readonly GameRandom Random;
        internal readonly Env Env;

        private readonly Dictionary<string, EntityAdapter> _byGuid = new Dictionary<string, EntityAdapter>(StringComparer.Ordinal);
        private readonly List<ICombatEntity> _entities = new List<ICombatEntity>();
        private List<ICombatTile> _tiles;
        private readonly string _identity;

        /// <summary>Actions the dispatcher streamed out via <see cref="EmitAction"/>, in plan order.</summary>
        internal readonly List<EngineAction> Emitted = new List<EngineAction>();

        internal CombatContextAdapter(Env env, CombatState state)
        {
            Env = env;
            State = state;
            Random = state != null ? state.Random : null;

            // SPEC-DELTA-v1.1 §6: CombatKey := (ReferenceEquals-identity of CombatState, CombatState.Random.Seed).
            // GameRandom.Seed is `public readonly int` (PSN §10) and is identical on every peer in an online
            // session. RuntimeHelpers.GetHashCode is the identity hash (never an overridden GetHashCode).
            int identityHash = state != null ? RuntimeHelpers.GetHashCode(state) : 0;
            int seed = Random != null ? Random.Seed : 0;
            _identity = identityHash.ToString(CultureInfo.InvariantCulture) + "|" + seed.ToString(CultureInfo.InvariantCulture);

            try
            {
                if (state != null && state.Entities != null)
                {
                    // Materialise in COMBAT-ROSTER order: index i here IS roster ordinal i, which is what
                    // ICombatContext.Entities now promises and what every engine-side ordering keys on.
                    //
                    // This used to be `natives.Sort(CompareByGuid)`, and that was a co-op divergence.
                    // Entity.Guid is minted per peer by Guid.NewGuid(), so sorting on it handed each peer a
                    // DIFFERENT entity order - and RANK / ALLY_* resolution tie-breaks off exactly that
                    // order, so two peers could pick two different allies for the same effect. It takes no
                    // extra RNG draw, so the vendor's GameRandomNextInt probe would never have flagged it.
                    // The roster's own index is the vendor's own cross-peer identity mechanism (every peer
                    // appends to CombatState.Entities in the same sequence off the same shared-seed
                    // stream) - see ClassForge.Core.Rng.EntityKey for the full argument.
                    var natives = state.Entities;
                    for (int i = 0; i < natives.Count; i++)
                    {
                        var n = natives[i];
                        if (n == null) continue;
                        var adapter = Wrap(n, i);
                        if (adapter != null) _entities.Add(adapter);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// Stable adapter per Entity guid, so reference equality holds within one dispatch. The guid is fine
        /// as the memo key here and ONLY here: it is minted, written and read entirely inside this peer's
        /// process, which is the one use <see cref="ICombatEntity.Guid"/> is still legal for.
        /// <para><paramref name="rosterOrdinal"/> is supplied by the roster walk in the constructor.</para>
        /// </summary>
        internal EntityAdapter Wrap(Entity native, int rosterOrdinal)
        {
            if (native == null) return null;
            string guid;
            try { guid = native.Guid ?? ""; } catch { return null; }
            EntityAdapter existing;
            if (_byGuid.TryGetValue(guid, out existing)) return existing;
            var created = new EntityAdapter(native, this, rosterOrdinal);
            _byGuid[guid] = created;
            return created;
        }

        /// <summary>
        /// Wraps an entity whose roster ordinal is not already known - resolved against the live roster, or
        /// <see cref="PeerOrder.Unknown"/> when there is no roster to resolve against (the loot postfix runs
        /// after the fight, so its party wraps can legitimately land here). Callers in that position must
        /// NOT depend on the ordinal for ordering; the loot path deliberately keys on the caller's own list
        /// position instead - see <c>LootDeltaComputer.EmitEffect</c>.
        /// </summary>
        internal EntityAdapter Wrap(Entity native)
        {
            return Wrap(native, RosterOrdinalOf(native));
        }

        /// <summary>Index of <paramref name="native"/> in the live combat roster, by reference identity, or
        /// <see cref="PeerOrder.Unknown"/>.</summary>
        private int RosterOrdinalOf(Entity native)
        {
            try
            {
                if (native == null || State == null || State.Entities == null) return PeerOrder.Unknown;
                var list = State.Entities;
                for (int i = 0; i < list.Count; i++)
                    if (ReferenceEquals(list[i], native)) return i;
                return PeerOrder.Unknown;
            }
            catch { return PeerOrder.Unknown; }
        }

        /// <summary>Resolves a planned action's guid back to the native <c>Entity</c>. Only entities present
        /// in this combat resolve; anything else returns null and the executor skips that action.</summary>
        internal Entity NativeByGuid(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            EntityAdapter adapter;
            return _byGuid.TryGetValue(guid, out adapter) && adapter != null ? adapter.Native : null;
        }

        public string CombatIdentity { get { return _identity; } }

        /// <summary>
        /// <c>CombatState.TotalRounds</c> (CombatState.cs L8) — a real, verified field, incremented inside
        /// <c>CombatHelper.NextTurn</c>'s <c>pIsNewRound</c> branch (CombatHelper.cs L793-838) right after the
        /// <c>"## NEW ROUND"</c> log. SPEC-DELTA-v1.1 §6 named the <c>NextTurn</c> postfix as the round source
        /// of truth specifically because <c>TotalRounds</c> was "unverified" at the time; it is now verified,
        /// so ClassForge reads it directly. That is strictly safer than mirroring it into plugin state: a
        /// pure read of replicated state cannot drift if a hook is ever missed, and it needs no hook at all.
        /// </summary>
        public int Round
        {
            get { try { return State != null ? State.TotalRounds : 0; } catch { return 0; } }
        }

        public IReadOnlyList<ICombatEntity> Entities { get { return _entities; } }

        /// <summary>
        /// v1.4 <c>RANDOM_TILE</c> — every PLAYABLE board tile, ordered ascending by <c>(Y, X)</c>.
        ///
        /// <para><b>Where the tiles come from.</b> Tile entities are created by
        /// <c>VenueHelper.CreateVenueTileEntities(string[] pMap)</c> (VenueHelper.cs:69), one per character
        /// of a static venue map string, and are pushed into the combat's own entity list by
        /// <c>CombatPhase.cs:314</c> (<c>_combatState.Entities.AddRange(_gameObjectMaps.FromTile.Keys)</c>).
        /// So the tiles are already right here in <c>CombatState.Entities</c> — no new game API, no
        /// <c>VenueDirector</c> reach-through, and (importantly) no second source of truth that could hold
        /// a different board than the one the game is actually fighting on.</para>
        ///
        /// <para><b>The filter is the game's own.</b> <c>e.Has&lt;VenueTileComponent&gt;() &amp;&amp;
        /// GroupIndex &gt; -1</c> is copied verbatim from the rain and chaos weather ticks
        /// (CombatPhase.cs:2009 / :2021), which build exactly this pool before dropping
        /// <c>STATUS_WATER_00</c> on a random member of it. <c>CreateVenueTileEntities</c> assigns
        /// <c>GroupIndex = -1</c> to every non-letter map cell (the <c>+ - | .</c> border/floor
        /// characters), so the predicate keeps precisely the ally- and enemy-side squares.</para>
        ///
        /// <para><b>Why the sort is load-bearing.</b> <c>FromTile</c> is a <c>Dictionary&lt;Entity,
        /// GameObject&gt;</c>; its enumeration order is an implementation detail of the CLR's hash layout
        /// and is NOT a cross-peer contract. If the engine drew an index into that order, two peers could
        /// draw the same NUMBER and hit different squares — a desync that no amount of seed discipline
        /// would catch. Sorting by <c>(Y, X)</c> re-derives the venue map's own reading order, which is a
        /// pure function of a compiled-in <c>string[]</c> and therefore identical everywhere.</para>
        ///
        /// <para><b>Coordinates live on <c>VenueComponent</c>, not <c>VenueTileComponent</c>.</b>
        /// <c>VenueTileComponent</c> carries only <c>GroupIndex</c>, <c>RowPositionsType</c> and
        /// <c>AuraStatuses</c>; the <c>(int x, int y) TilePosition</c> is on <c>VenueComponent</c>, the same
        /// component a CHARACTER uses for its own square (which is how
        /// <c>VenueHelper.GetTileEntityOfCharacter</c> matches the two, VenueHelper.cs:103).</para>
        ///
        /// <para><b>Registration for the executor.</b> Each tile is passed through <see cref="Wrap"/> so its
        /// guid is in <c>_byGuid</c>. That is what lets <c>RecipeActionExecutor</c> resolve a planned tile
        /// action with the ordinary <c>NativeByGuid</c> call and hand it to
        /// <c>InteractableHelper.ApplyStatus</c> — the very call CombatPhase.cs:2013 makes against a tile —
        /// with no executor change at all. Tiles are normally in <c>State.Entities</c> and therefore already
        /// wrapped by the constructor; the call here is idempotent and covers the case where they are not.</para>
        ///
        /// <para><b>Fail-safe:</b> every failure path yields an EMPTY list, never null and never a throw, so
        /// a <c>RANDOM_TILE</c> effect degrades to a logged no-op that takes zero draws.</para>
        /// </summary>
        public IReadOnlyList<ICombatTile> Tiles
        {
            get
            {
                if (_tiles != null) return _tiles;
                var built = new List<ICombatTile>();
                try
                {
                    if (State != null && State.Entities != null)
                    {
                        var natives = State.Entities;
                        for (int i = 0; i < natives.Count; i++)
                        {
                            var n = natives[i];
                            if (n == null) continue;
                            VenueTileComponent tc;
                            if (!n.TryGet<VenueTileComponent>(out tc) || tc == null) continue;
                            if (tc.GroupIndex <= -1) continue;   // border/floor cell, not part of the board
                            VenueComponent vc;
                            if (!n.TryGet<VenueComponent>(out vc) || vc == null) continue;

                            string guid;
                            try { guid = n.Guid ?? ""; } catch { continue; }
                            if (guid.Length == 0) continue;

                            Wrap(n);   // idempotent; puts the tile's guid in _byGuid for NativeByGuid
                            built.Add(new TileAdapter(n, vc.TilePosition.x, vc.TilePosition.y, guid));
                        }
                    }
                }
                catch { built.Clear(); }

                // ONE comparator, shared with the venue-placement sites in SummonVisuals and
                // RecipeActionExecutor. It used to be a private copy here; two hand-rolled orderings that
                // are meant to agree is exactly the shape that drifts. See TileOrder for why the ordering
                // is a replication contract.
                built.Sort(TileOrder.Compare);
                _tiles = built;
                return _tiles;
            }
        }

        /// <summary><c>CharacterHelper.IsOpponent</c> = differing <c>CharacterComponent.GroupIndex</c> (L1497).</summary>
        public bool AreOpponents(ICombatEntity a, ICombatEntity b)
        {
            try
            {
                var ea = a as EntityAdapter;
                var eb = b as EntityAdapter;
                if (ea == null || eb == null || ea.Native == null || eb.Native == null) return false;
                return CharacterHelper.IsOpponent(ea.Native, eb.Native);
            }
            catch { return false; }
        }

        public IAbilityInfo GetAbility(string abilityId)
        {
            try
            {
                if (string.IsNullOrEmpty(abilityId)) return null;
                var cfg = GameLookups.AbilityConfig(abilityId);
                if (cfg == null) return null;
                return new AbilityInfoAdapter(abilityId, cfg, this);
            }
            catch { return null; }
        }

        public IStatusInfo GetStatus(string statusId)
        {
            try
            {
                if (string.IsNullOrEmpty(statusId)) return null;
                var cfg = GameLookups.StatusConfig(statusId);
                if (cfg == null) return null;
                return new StatusInfoAdapter(statusId, cfg.Type.ToString());
            }
            catch { return null; }
        }

        public IItemInfo GetItem(string thingConfigName)
        {
            try
            {
                if (string.IsNullOrEmpty(thingConfigName)) return null;
                var cfg = GameLookups.ThingConfig(thingConfigName);
                if (cfg == null) return null;
                return new ItemInfoAdapter(thingConfigName, cfg.Class ?? "", cfg.ConsumableType != eConsumableTypes.NONE);
            }
            catch { return null; }
        }

        /// <summary>Encounter Modifiers spec §5 <c>PARTY_AVG_LEVEL</c>: <c>ProgressionHelper.
        /// GetAveragePartyLevel</c> over <c>GameRun.Entities</c> filtered to
        /// <c>Has&lt;PlayerComponent&gt;() &amp;&amp; Has&lt;CharacterComponent&gt;()</c> — mirrors EOR's
        /// caller-side filter (L22744); the extra CharacterComponent filter is a safety net against a
        /// PlayerComponent entity that (abnormally) lacks one, which the native helper would otherwise throw on.</summary>
        public int PartyAverageLevel
        {
            get
            {
                try
                {
                    var run = Env != null ? Env.GameRun : null;
                    if (run == null || run.Entities == null) return 0;
                    var filtered = new List<Entity>();
                    foreach (var e in run.Entities)
                    {
                        if (e == null) continue;
                        PlayerComponent pc; CharacterComponent cc;
                        if (e.TryGet<PlayerComponent>(out pc) && e.TryGet<CharacterComponent>(out cc))
                            filtered.Add(e);
                    }
                    return ProgressionHelper.GetAveragePartyLevel(filtered);
                }
                catch { return 0; }
            }
        }

        /// <summary>Encounter Modifiers spec §5 <c>IS_DUNGEON</c>: <c>CombatState.IsDungeon</c>.</summary>
        public bool IsDungeon
        {
            get { try { return State != null && State.IsDungeon; } catch { return false; } }
        }

        /// <summary>Encounter Modifiers spec §5 <c>BOSS_FIGHT</c>: <c>CombatState.BossFightState != null</c>.</summary>
        public bool IsBossFight
        {
            get { try { return State != null && State.BossFightState != null; } catch { return false; } }
        }

        /// <summary>Encounter Modifiers spec §5 <c>ENCOUNTER_PROPERTY</c>: resolves the encounter entity via
        /// <c>GameRun.AdventureState.EncounterGUID</c>, then <c>EncounterComponent.HasProperty</c>. No
        /// encounter entity resolved ⇒ false (matches EOR's default, L22772-3).</summary>
        public bool HasEncounterProperty(string propertyName)
        {
            try
            {
                if (string.IsNullOrEmpty(propertyName)) return false;
                eEncounterProperties prop;
                if (!Enum.TryParse(propertyName, out prop)) return false;

                var run = Env != null ? Env.GameRun : null;
                var adv = run != null ? run.AdventureState : null;
                string guid = adv != null ? adv.EncounterGUID : null;
                if (string.IsNullOrEmpty(guid) || run.Entities == null) return false;

                foreach (var e in run.Entities)
                {
                    if (e == null || !string.Equals(e.Guid, guid, StringComparison.Ordinal)) continue;
                    EncounterComponent ec;
                    if (e.TryGet<EncounterComponent>(out ec) && ec != null)
                        return ec.HasProperty(prop);
                    return false;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>STATE_HASH_CHANCE spec §2.1 <c>COMBAT_SEED</c>: <c>CombatState.Random.Seed</c> —
        /// <c>public readonly int</c> (PSN §10), identical on every peer. Deliberately NOT
        /// <see cref="CombatIdentity"/>: its ReferenceEquals half is process-local and must never feed a
        /// cross-peer hash.</summary>
        public int CombatSeed
        {
            get { try { return Random != null ? Random.Seed : 0; } catch { return 0; } }
        }

        /// <summary>STATE_HASH_CHANCE spec §2.1 <c>RUN_SEED</c>: <c>GameRunData.MapGenSeed</c>.</summary>
        public int RunSeed
        {
            get
            {
                try
                {
                    var run = Env != null ? Env.GameRun : null;
                    return run != null ? run.MapGenSeed : 0;
                }
                catch { return 0; }
            }
        }

        /// <summary>
        /// STATE_HASH_CHANCE spec §2.1 <c>ENCOUNTER_GUID</c> — the LAST of the four <c>*_GUID</c> tokens
        /// to be re-pointed off a raw guid, and the one with no detector at all.
        ///
        /// <para>It used to return <c>GameRun.AdventureState.EncounterGUID</c> verbatim. That field is
        /// assigned an overworld <c>Entity.Guid</c>, which <c>Entity.Create()</c> mints locally with
        /// <c>Guid.NewGuid()</c> — a joining peer is sent the map SEED, not the entities, and regenerates
        /// them itself. So the token's value differed on every peer for the same encounter. Because
        /// STATE_HASH_CHANCE takes ZERO draws by design, that divergence never perturbs the shared
        /// stream (so the vendor's <c>GameRandomNextInt</c> probe cannot see it) and the vendor's desync
        /// MD5 rewrites every guid to a first-occurrence ordinal before hashing (so that cannot see it
        /// either): it would have surfaced only as two players watching different outcomes.</para>
        ///
        /// <para>It now resolves to <see cref="ReplicatedEncounterKey.Text"/> — a hash of five
        /// serialized, MD5'd, non-guid fields — exactly as <c>SELF_GUID</c> /
        /// <c>TRIGGER_SOURCE_GUID</c> / <c>TRIGGER_TARGET_GUID</c> were re-pointed at
        /// <c>PeerOrder.KeyOf</c>. The token NAME is unchanged for authored-recipe compatibility. No
        /// shipped recipe uses it (the one shipped <c>STATE_HASH_CHANCE</c> uses <c>SELF_GUID</c>), so
        /// this changes no shipped behaviour.</para>
        /// </summary>
        public string EncounterGuid
        {
            get
            {
                try { return ReplicatedEncounterKey.Text(); }
                catch { return ""; }
            }
        }

        /// <summary>Action sink. The plan is collected here and executed by <see cref="RecipeActionExecutor"/>
        /// after the dispatcher returns, so nothing mutates game state mid-evaluation.</summary>
        public void EmitAction(EngineAction action)
        {
            if (action != null) Emitted.Add(action);
        }

        /// <summary>
        /// The acting Thing carrying an ability, used for <c>ABILITY_STAT</c> (C3):
        /// <c>Interactable.Abilities[abilityId].Stat</c> (<c>SkillRollData.Stat</c>).
        /// Set by whichever hook has a <c>pThing</c> parameter.
        /// </summary>
        internal Thing ActingThing;
    }

    internal sealed class AbilityInfoAdapter : IAbilityInfo
    {
        private readonly string _id;
        private readonly CombatAbilityConfig _cfg;
        private readonly CombatContextAdapter _ctx;

        internal AbilityInfoAdapter(string id, CombatAbilityConfig cfg, CombatContextAdapter ctx)
        {
            _id = id; _cfg = cfg; _ctx = ctx;
        }

        public string Id { get { return _id; } }
        public bool IsRanged { get { try { return _cfg.IsRanged; } catch { return false; } } }

        /// <summary>C2 <c>HOSTILE_ACTION</c>: <c>Configs.Abilities[id].Target == eTargets.ENEMY</c>
        /// (PSN §6 L1351 reads exactly this comparison).</summary>
        public bool TargetsEnemy { get { try { return _cfg.Target == eTargets.ENEMY; } catch { return false; } } }

        /// <summary>C3 <c>ABILITY_STAT</c>: the acting Thing's
        /// <c>Interactable.Abilities[abilityId].Stat</c> (<c>SkillRollData.Stat</c>, a free string ∩
        /// <c>eCharacterStats</c>). Empty when the trigger carried no Thing.</summary>
        public string Stat
        {
            get
            {
                try
                {
                    var thing = _ctx != null ? _ctx.ActingThing : null;
                    if (thing == null || string.IsNullOrEmpty(thing.ConfigName)) return "";
                    var tc = GameLookups.ThingConfig(thing.ConfigName);
                    if (tc == null || tc.Interactable == null || tc.Interactable.Abilities == null) return "";
                    SkillRollData roll;
                    if (!tc.Interactable.Abilities.TryGetValue(_id, out roll) || roll == null) return "";
                    return roll.Stat ?? "";
                }
                catch { return ""; }
            }
        }

        public IReadOnlyList<string> Tags
        {
            get
            {
                try
                {
                    if (_cfg.Tags == null) return EmptyStrings;
                    var list = new List<string>(_cfg.Tags);
                    list.Sort(StringComparer.Ordinal);
                    return list;
                }
                catch { return EmptyStrings; }
            }
        }

        private static readonly string[] EmptyStrings = new string[0];
    }

    internal sealed class StatusInfoAdapter : IStatusInfo
    {
        private readonly string _id;
        private readonly string _type;
        internal StatusInfoAdapter(string id, string type) { _id = id; _type = type; }
        public string Id { get { return _id; } }
        public string Type { get { return _type; } }
    }

    internal sealed class ItemInfoAdapter : IItemInfo
    {
        private readonly string _name;
        private readonly string _class;
        private readonly bool _consumable;
        internal ItemInfoAdapter(string name, string cls, bool consumable) { _name = name; _class = cls; _consumable = consumable; }
        public string ConfigName { get { return _name; } }
        public string Class { get { return _class; } }
        public bool IsConsumable { get { return _consumable; } }
    }

    /// <summary>
    /// The shared combat RNG (SPEC-DELTA-v1.1 §5.2 invariant 1). Wraps <c>CombatState.Random</c> and
    /// <b>nothing else</b> — <c>System.Random</c>, <c>UnityEngine.Random</c> and freshly constructed
    /// <c>GameRandom</c> instances are all forbidden, because <c>GameRandom</c> is backed by a single seeded
    /// <c>System.Random</c> whose call order and call count must stay identical across peers (PSN §10).
    /// </summary>
    internal sealed class GameRandomSource : IRandomSource
    {
        private readonly GameRandom _rng;
        internal GameRandomSource(GameRandom rng) { _rng = rng; }

        /// <summary><c>GameRandom.NextChance(decimal)</c> — one draw.</summary>
        public bool NextChance(decimal chance) { return _rng.NextChance(chance); }

        /// <summary><c>GameRandom.NextInt(min, max, pMaxInclusive: false)</c> — one draw. Exactly what
        /// <c>GetRandomElementFromList&lt;T&gt;</c> does internally, which is what <c>StatusOneOf</c> needs.</summary>
        public int NextInt(int minInclusive, int maxExclusive) { return _rng.NextInt(minInclusive, maxExclusive, false); }

        /// <summary><c>GameRandom.NextInt(min, max, pMaxInclusive: true)</c> — one draw. Used by
        /// <c>SELECTION_SET</c> (Encounter Modifiers spec §5/§6.3), EOR's weighted-pick draw verbatim (L22814).</summary>
        public int NextIntInclusive(int minInclusive, int maxInclusive) { return _rng.NextInt(minInclusive, maxInclusive, true); }
    }

    /// <summary>Routes the engine's diagnostics into BepInEx, gated on <c>[General] VerboseLogging</c>.</summary>
    internal sealed class RecipeLogAdapter : IRecipeLog
    {
        public void Info(string message)
        {
            if (ClassForgePlugin.VerboseLogging != null && ClassForgePlugin.VerboseLogging.Value)
                ClassForgePlugin.Log.LogDebug(message);
        }

        public void Warn(string message)
        {
            ClassForgePlugin.Log.LogWarning(message);
        }
    }

    /// <summary>
    /// Null-safe config lookups. <c>Env.Configs</c> is a <c>public static</c> field (Env.cs L6) and the
    /// dictionaries are <c>SerializedSortedDictionary&lt;string, T&gt;</c>. Every native accessor the game
    /// uses is a raw indexer — <c>InventoryHelper.GetThingConfig</c> is literally
    /// <c>return Env.Configs.Things[pThingName];</c>, which throws <c>KeyNotFoundException</c> on a miss —
    /// so ClassForge never indexes directly.
    /// </summary>
    internal static class GameLookups
    {
        internal static ThingConfig ThingConfig(string id)
        {
            try
            {
                var c = global::Env.Configs;
                if (c == null || c.Things == null || string.IsNullOrEmpty(id)) return null;
                return c.Things.ContainsKey(id) ? c.Things[id] : null;
            }
            catch { return null; }
        }

        internal static CharacterConfig CharacterConfig(string id)
        {
            try
            {
                var c = global::Env.Configs;
                if (c == null || c.Characters == null || string.IsNullOrEmpty(id)) return null;
                return c.Characters.ContainsKey(id) ? c.Characters[id] : null;
            }
            catch { return null; }
        }

        internal static CombatAbilityConfig AbilityConfig(string id)
        {
            try
            {
                var c = global::Env.Configs;
                if (c == null || c.Abilities == null || string.IsNullOrEmpty(id)) return null;
                return c.Abilities.ContainsKey(id) ? c.Abilities[id] : null;
            }
            catch { return null; }
        }

        internal static StatusEffectConfig StatusConfig(string id)
        {
            try
            {
                var c = global::Env.Configs;
                if (c == null || c.StatusEffects == null || string.IsNullOrEmpty(id)) return null;
                return c.StatusEffects.ContainsKey(id) ? c.StatusEffects[id] : null;
            }
            catch { return null; }
        }
    }
}
