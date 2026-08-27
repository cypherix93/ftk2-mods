using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Reflects the live fight into a <see cref="CombatView"/>. The only file in S1 that touches
    /// game objects for combat data; everything it produces is plain data that Core shapes.
    ///
    /// Error posture (SPEC §3): nothing here throws outward. A member that has been renamed by a
    /// game update yields a null plus a warning inside the snapshot, so the next assertion reports
    /// it instead of a log nobody reads.
    /// </summary>
    internal static class CombatReader
    {
        private static Type _combatPhaseType;
        private static bool _typeProbed;

        internal static CombatView Read(object gameRun, TurnTracker tracker, WarningSink warnings)
        {
            CombatView view = new CombatView();

            object combatPhase = FindCombatPhase(warnings);

            // Primary path: the live phase object holds the fight. Fallback: the run data root
            // (GameRunData.CombatState, confirmed present by the Task 1 addendum).
            object combatState = combatPhase == null
                ? null
                : MemberResolver.GetMember(combatPhase, "_combatState", warnings);
            if (combatState == null && gameRun != null)
                combatState = MemberResolver.GetMember(gameRun, "CombatState", warnings);

            IList entities = null;
            if (combatState != null)
            {
                view.Round = MemberResolver.AsInt(MemberResolver.GetMember(combatState, "TotalRounds", warnings));
                view.Wave = MemberResolver.AsInt(MemberResolver.GetMember(combatState, "WaveIndex", warnings));
                entities = MemberResolver.GetMember(combatState, "Entities", warnings) as IList;
            }

            bool active = entities != null && entities.Count > 0;
            view.Active = active;

            // Object identity keys the episode: the engine offers no grounded combat-start callback,
            // so a different CombatState instance is the signal that this is a different fight.
            tracker.OnObserved(active, combatState == null ? 0 : RuntimeHelpers.GetHashCode(combatState));
            ApplyTracker(view, tracker, warnings);

            // A direct read beats the hook's cached value when the live phase object is reachable —
            // the field map resolves the active combatant to CombatPhase._activeCharacterEntity.
            if (combatPhase != null)
            {
                object activeEntity = MemberResolver.GetMember(combatPhase, "_activeCharacterEntity", warnings);
                if (activeEntity == null)
                    activeEntity = MemberResolver.GetMember(combatPhase, "_lastEngagedEntity", null);
                if (activeEntity != null)
                    view.ActiveEntityId = MemberResolver.AsString(
                        MemberResolver.GetMember(activeEntity, "Guid", warnings));
            }

            if (combatState == null) return view;          // not in a fight: 'active' says so already

            if (entities == null)
            {
                warnings.Note("combat.combatants unavailable: CombatState.Entities did not read as a list");
                return view;                                // CombatantsAvailable stays false -> null
            }

            view.CombatantsAvailable = true;
            for (int i = 0; i < entities.Count; i++)
            {
                // The list index IS the peer-stable identity (ClassForge.Core/Rng/EntityKey.cs), so it
                // is read from the same replicated list the determinism layer keys on, never re-derived.
                CombatantView combatant = ReadCombatant(entities[i], i, warnings);
                if (combatant != null) view.Combatants.Add(combatant);
            }

            ReadTiles(view, entities, warnings);
            return view;
        }

        private static void ApplyTracker(CombatView view, TurnTracker tracker, WarningSink warnings)
        {
            bool active; int turn; string phase; string entityId; string episode;
            tracker.Read(out active, out turn, out phase, out entityId, out episode);

            view.Episode = episode;
            view.ActiveEntityId = entityId;

            if (!tracker.HooksInstalled)
            {
                // Serving a permanently-zero counter as if it were a measurement is the failure this
                // whole plan exists to prevent. Say the value is unavailable instead.
                view.Turn = null;
                view.Phase = TurnTracker.PhaseUnknown;
                warnings.Note("combat.turn/phase unavailable: CombatPhase hooks are not installed");
                return;
            }

            view.Turn = turn < 0 ? (int?)null : turn;
            view.Phase = phase;
        }

        private static CombatantView ReadCombatant(object entity, int rosterOrdinal, WarningSink warnings)
        {
            if (entity == null) return null;

            CombatantView c = new CombatantView();
            c.Id = MemberResolver.AsString(MemberResolver.GetMember(entity, "Guid", warnings));
            c.RosterOrdinal = rosterOrdinal;

            object components = MemberResolver.GetMember(entity, "Components", warnings);

            // Venue tiles live in CombatState.Entities alongside actors, so they hold roster ordinals
            // and would otherwise be indistinguishable from a character with no CharacterComponent.
            c.IsTile = MemberResolver.FindComponentByTypeName(components, "VenueTileComponent", null) != null;
            ReadPosition(c, components, warnings);

            // isPlayer has no boolean field anywhere (field map) — it is component presence. Absence
            // is the normal answer for an enemy, so no sink: that is information, not a miss.
            c.IsPlayer = MemberResolver.FindComponentByTypeName(components, "PlayerComponent", null) != null;

            object character = MemberResolver.FindComponentByTypeName(components, "CharacterComponent", null);
            if (character == null)
            {
                warnings.Note("combatant has no CharacterComponent: " + (c.Id == null ? "<no guid>" : c.Id));
            }
            else
            {
                c.Name = MemberResolver.AsString(MemberResolver.GetMember(character, "DisplayName", warnings));
                c.ClassId = MemberResolver.AsString(MemberResolver.GetMember(character, "ConfigName", warnings));
                c.Hp = MemberResolver.AsInt(MemberResolver.GetMember(character, "CurrentHealth", warnings));
                c.GroupIndex = MemberResolver.AsInt(MemberResolver.GetMember(character, "GroupIndex", warnings));
                c.IsSummon = HasActorProperty(character, "SUMMON", warnings);
                ReadCustomData(character, "CharacterComponent", c.CustomData, ref c.CustomDataAvailable, warnings);
                ReadThings(c, character, warnings);
            }

            // maxHp is computed, not stored (field map: no MaxHealth field on CharacterComponent).
            // GetMaxHealth is grounded as binary (Entity, Boolean pCappedStat) — see CharacterHelperBridge.
            c.MaxHp = MemberResolver.AsInt(CharacterHelperBridge.InvokeMaxHealth(entity, warnings));
            c.Alive = Negate(MemberResolver.AsBool(
                CharacterHelperBridge.Invoke("IsDead", entity, character, warnings)));

            ReadStatuses(c, components, warnings);
            ReadStats(c, entity, character, warnings);
            return c;
        }

        /// <summary>
        /// <c>VenueComponent.TilePosition</c>, declared <c>(int x, int y)</c>
        /// (<c>VenueComponent.cs:5</c>). A ValueTuple's element names are compiler metadata, not real
        /// members, so the two fields are read as <c>Item1</c>/<c>Item2</c> -- Item1 IS <c>x</c>, the
        /// ROW/DEPTH axis (<c>VenueHelper.cs:988/998</c> takes Min/Max of <c>.x</c> for front/back row).
        ///
        /// Absence is normal: an entity with no VenueComponent is simply not on the board, so no
        /// warning is raised for the missing component.
        /// </summary>
        private static void ReadPosition(CombatantView c, object components, WarningSink warnings)
        {
            object venue = MemberResolver.FindComponentByTypeName(components, "VenueComponent", null);
            if (venue == null) return;

            int? x, y;
            ReadTilePosition(venue, warnings, out x, out y);
            c.TileX = x;
            c.TileY = y;
        }

        private static void ReadTilePosition(object venueComponent, WarningSink warnings, out int? x, out int? y)
        {
            x = null;
            y = null;
            object position = MemberResolver.GetMember(venueComponent, "TilePosition", warnings);
            if (position == null) return;
            x = MemberResolver.AsInt(MemberResolver.GetMember(position, "Item1", warnings));
            y = MemberResolver.AsInt(MemberResolver.GetMember(position, "Item2", warnings));
        }

        /// <summary>
        /// <c>CharacterHelper.ActorHasProperty</c> is exactly
        /// <c>Properties != null &amp;&amp; Properties.Contains(p)</c> (<c>CharacterHelper.cs:2010-2017</c>),
        /// so the membership test is done here rather than by resolving one of its two overloads --
        /// neither of which <see cref="MemberResolver.FindUnaryStatic"/> can pick (both take two
        /// parameters). Compared by enum NAME, since <c>eActorProperties</c> is not referenceable here.
        ///
        /// Null when the field itself could not be read; a null <c>Properties</c> list is the game's own
        /// "no properties" and yields false, matching the helper.
        /// </summary>
        private static bool? HasActorProperty(object character, string propertyName, WarningSink warnings)
        {
            if (!MemberExists(character, "Properties"))
            {
                warnings.MemberMissing("CharacterComponent", "Properties");
                return null;
            }

            IList properties = MemberResolver.GetMember(character, "Properties", warnings) as IList;
            if (properties == null) return false;

            for (int i = 0; i < properties.Count; i++)
            {
                object item = properties[i];
                if (item == null) continue;
                if (string.Equals(item.ToString(), propertyName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// A <c>Dictionary&lt;string,string&gt; CustomData</c>, copied verbatim. Deliberately NOT
        /// allow-listed: every ClassForge feature stores its state here (<c>CF_POKE_*</c>,
        /// <c>CF_COUNTER_*</c>, <c>CF_TRAINER_*</c>) and an allow-list would make each new feature need
        /// another harness change before its state became assertable.
        ///
        /// The member EXISTING with a null value is "there is none" (the game leaves it null until the
        /// first write) and serializes as an empty object; the member being absent is "could not read"
        /// and serializes as null.
        /// </summary>
        private static void ReadCustomData(object owner, string ownerTypeName,
            Dictionary<string, string> into, ref bool available, WarningSink warnings)
        {
            if (!MemberExists(owner, "CustomData"))
            {
                warnings.MemberMissing(ownerTypeName, "CustomData");
                return;
            }

            available = true;
            IDictionary map = MemberResolver.GetMember(owner, "CustomData", warnings) as IDictionary;
            if (map == null) return;

            foreach (DictionaryEntry entry in map)
            {
                if (entry.Key == null) continue;
                into[entry.Key.ToString()] = MemberResolver.AsString(entry.Value);
            }
        }

        /// <summary>
        /// <c>CharacterComponent.Things</c>, filtered to entries that actually carry custom data --
        /// the Trainer ball's <c>CF_POKE_HP</c>/<c>_MAXHP</c>/<c>_DOWNED</c>/<c>_CONFIG</c>/<c>_STAGE</c>/
        /// <c>_NICKNAME</c> record (<c>TrainerPartnerPersistence.cs:49-68</c>) among them. Dumping every
        /// item would bury those few under an inventory that never changes.
        /// </summary>
        private static void ReadThings(CombatantView c, object character, WarningSink warnings)
        {
            if (!MemberExists(character, "Things"))
            {
                warnings.MemberMissing("CharacterComponent", "Things");
                return;
            }

            c.ThingsAvailable = true;
            IList things = MemberResolver.GetMember(character, "Things", warnings) as IList;
            if (things == null) return;

            for (int i = 0; i < things.Count; i++)
            {
                object thing = things[i];
                if (thing == null) continue;

                ThingView t = new ThingView();
                bool hasCustomData = false;
                ReadCustomData(thing, "Thing", t.CustomData, ref hasCustomData, warnings);
                if (t.CustomData.Count == 0) continue;

                t.Id = MemberResolver.AsString(MemberResolver.GetMember(thing, "Id", warnings));
                t.ConfigName = MemberResolver.AsString(MemberResolver.GetMember(thing, "ConfigName", warnings));
                c.Things.Add(t);
            }
        }

        /// <summary>
        /// Tile entities, which live in the same <c>CombatState.Entities</c> roster as actors
        /// (<c>CombatHelper.cs:648</c> finds them with <c>e.Has&lt;VenueTileComponent&gt;()</c>).
        ///
        /// This is the read that did not exist: <c>VenueTileComponent.AuraStatuses</c> is where TILE
        /// effects live -- the Chaos Mage hazard tile among them -- and nothing in the harness read it,
        /// so a tile effect could only ever be evidenced by a decal in a screenshot.
        /// </summary>
        private static void ReadTiles(CombatView view, IList entities, WarningSink warnings)
        {
            // Occupancy first, so a tile can name who is standing on it. Matching is by position
            // alone, which is what the game itself does (CombatHelper.cs:901) -- tile coordinates are
            // unique across the whole board, not per group.
            Dictionary<string, string> occupantId = new Dictionary<string, string>();
            Dictionary<string, int> occupantOrdinal = new Dictionary<string, int>();

            for (int i = 0; i < entities.Count; i++)
            {
                object entity = entities[i];
                if (entity == null) continue;
                object components = MemberResolver.GetMember(entity, "Components", null);
                if (MemberResolver.FindComponentByTypeName(components, "VenueTileComponent", null) != null) continue;

                object venue = MemberResolver.FindComponentByTypeName(components, "VenueComponent", null);
                if (venue == null) continue;

                string id = MemberResolver.AsString(MemberResolver.GetMember(entity, "Guid", null));

                int? x, y;
                ReadTilePosition(venue, warnings, out x, out y);
                Occupy(occupantId, occupantOrdinal, x, y, id, i);

                // Large actors cover several tiles (VenueComponent.OccupiedTiles), so every covered
                // tile reports them, not only the anchor.
                IList occupied = MemberResolver.GetMember(venue, "OccupiedTiles", null) as IList;
                if (occupied == null) continue;
                for (int k = 0; k < occupied.Count; k++)
                {
                    object cell = occupied[k];
                    if (cell == null) continue;
                    Occupy(occupantId, occupantOrdinal,
                        MemberResolver.AsInt(MemberResolver.GetMember(cell, "Item1", null)),
                        MemberResolver.AsInt(MemberResolver.GetMember(cell, "Item2", null)),
                        id, i);
                }
            }

            view.TilesAvailable = true;

            for (int i = 0; i < entities.Count; i++)
            {
                object entity = entities[i];
                if (entity == null) continue;
                object components = MemberResolver.GetMember(entity, "Components", null);
                object tileComponent =
                    MemberResolver.FindComponentByTypeName(components, "VenueTileComponent", null);
                if (tileComponent == null) continue;

                TileView t = new TileView();
                t.RosterOrdinal = i;
                t.GroupIndex = MemberResolver.AsInt(
                    MemberResolver.GetMember(tileComponent, "GroupIndex", warnings));
                t.RowPositionsType = MemberResolver.AsString(
                    MemberResolver.GetMember(tileComponent, "RowPositionsType", warnings));
                ReadAuraStatuses(t, tileComponent, warnings);

                object venue = MemberResolver.FindComponentByTypeName(components, "VenueComponent", null);
                if (venue == null)
                {
                    warnings.Note("venue tile has no VenueComponent: roster ordinal "
                        + i.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    int? x, y;
                    ReadTilePosition(venue, warnings, out x, out y);
                    t.X = x;
                    t.Y = y;

                    string key = PositionKey(x, y);
                    string id;
                    if (key != null && occupantId.TryGetValue(key, out id))
                    {
                        t.OccupantId = id;
                        t.OccupantOrdinal = occupantOrdinal[key];
                    }
                }

                view.Tiles.Add(t);
            }
        }

        /// <summary>
        /// <c>VenueTileComponent.AuraStatuses</c>. A null list is the game's own "no aura on this tile"
        /// and serializes as an empty array; only an ABSENT member serializes as null, so a renamed
        /// field can never be misread as a clean tile.
        /// </summary>
        private static void ReadAuraStatuses(TileView t, object tileComponent, WarningSink warnings)
        {
            if (!MemberExists(tileComponent, "AuraStatuses"))
            {
                warnings.MemberMissing("VenueTileComponent", "AuraStatuses");
                return;
            }

            t.AuraStatusesAvailable = true;
            IList list = MemberResolver.GetMember(tileComponent, "AuraStatuses", warnings) as IList;
            if (list == null) return;

            for (int i = 0; i < list.Count; i++)
            {
                object item = list[i];
                if (item == null) continue;
                t.AuraStatuses.Add(item.ToString());
            }
        }

        private static void Occupy(Dictionary<string, string> ids, Dictionary<string, int> ordinals,
            int? x, int? y, string id, int ordinal)
        {
            string key = PositionKey(x, y);
            if (key == null || ids.ContainsKey(key)) return;   // first occupant wins; roster order is stable
            ids[key] = id;
            ordinals[key] = ordinal;
        }

        private static string PositionKey(int? x, int? y)
        {
            if (!x.HasValue || !y.HasValue) return null;
            return x.Value.ToString(CultureInfo.InvariantCulture) + ","
                 + y.Value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Whether the member is DECLARED, independent of its value. The whole *Available convention
        /// rests on telling "the field is null" (a real answer) from "the field is gone" (a game update
        /// broke us), and <see cref="MemberResolver.GetMember"/> returns null for both.
        /// </summary>
        private static bool MemberExists(object instance, string memberName)
        {
            if (instance == null) return false;
            Type type = instance.GetType();
            return MemberResolver.FindField(type, memberName) != null
                || MemberResolver.FindProperty(type, memberName) != null;
        }

        private static void ReadStatuses(CombatantView c, object components, WarningSink warnings)
        {
            object statusComponent =
                MemberResolver.FindComponentByTypeName(components, "StatusEffectComponent", null);
            if (statusComponent == null)
            {
                // No component means no statuses. That is a real answer, so the array is empty —
                // NOT null, which is reserved for "could not read".
                c.StatusesAvailable = true;
                return;
            }

            IDictionary map = MemberResolver.GetMember(statusComponent, "Statuses", warnings) as IDictionary;
            if (map == null)
            {
                warnings.Note("StatusEffectComponent.Statuses did not read as a dictionary");
                return;   // StatusesAvailable stays false -> serializes null
            }

            c.StatusesAvailable = true;
            foreach (DictionaryEntry entry in map)
            {
                StatusView s = new StatusView();
                // The dictionary key IS the status id (e.g. STATUS_ATTACKUP_00). No stacks field
                // exists on StatusEffectInfo and none is invented here — see the plan's stacks decision.
                s.Id = entry.Key == null ? null : entry.Key.ToString();
                object info = entry.Value;
                s.Duration = MemberResolver.AsInt(MemberResolver.GetMember(info, "Duration", warnings));
                s.InitialDuration = MemberResolver.AsInt(MemberResolver.GetMember(info, "InitialDuration", warnings));
                s.TickDuration = MemberResolver.AsInt(MemberResolver.GetMember(info, "TickDuration", warnings));
                s.OriginEntityId = MemberResolver.AsString(MemberResolver.GetMember(info, "OriginEntityId", warnings));
                c.Statuses.Add(s);
            }
        }

        /// <summary>
        /// Base stats, string-keyed: <c>eStats</c> does not exist (field map). <c>GetStat</c>'s 8
        /// overloads have no grounded signature, so effective/modified stats are out of S1's scope.
        /// </summary>
        private static void ReadStats(CombatantView c, object entity, object character, WarningSink warnings)
        {
            object raw = CharacterHelperBridge.Invoke("GetBaseStats", entity, character, warnings);
            IDictionary map = raw as IDictionary;
            if (map == null) return;   // StatsAvailable stays false -> serializes null

            c.StatsAvailable = true;
            foreach (DictionaryEntry entry in map)
            {
                if (entry.Key == null) continue;
                int? value = MemberResolver.AsInt(entry.Value);
                if (!value.HasValue) continue;
                c.Stats[entry.Key.ToString()] = value.Value;
            }
        }

        private static bool? Negate(bool? value)
        {
            return value.HasValue ? (bool?)(!value.Value) : null;
        }

        private static object FindCombatPhase(WarningSink warnings)
        {
            if (!_typeProbed)
            {
                _typeProbed = true;
                _combatPhaseType = AccessTools.TypeByName("CombatPhase");
            }
            if (_combatPhaseType == null)
            {
                warnings.TypeMissing("CombatPhase");
                return null;
            }

            try
            {
                // CombatPhase carries UIDocument/GameObject/camera members (field map), i.e. it is a
                // Unity component, so the live instance is found through the scene. FindObjectOfType
                // returns only enabled objects — a disabled CombatPhase would read as "no combat",
                // which Task 9 Step 3 checks against a real fight rather than assuming.
                UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(_combatPhaseType);
                return found == null ? null : (object)found;
            }
            catch (Exception ex)
            {
                warnings.Note("CombatPhase instance lookup failed: " + ex.Message);
                return null;
            }
        }
    }
}
