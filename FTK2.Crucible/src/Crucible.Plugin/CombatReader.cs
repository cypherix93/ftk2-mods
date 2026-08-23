using System;
using System.Collections;
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
                CombatantView combatant = ReadCombatant(entities[i], warnings);
                if (combatant != null) view.Combatants.Add(combatant);
            }
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

        private static CombatantView ReadCombatant(object entity, WarningSink warnings)
        {
            if (entity == null) return null;

            CombatantView c = new CombatantView();
            c.Id = MemberResolver.AsString(MemberResolver.GetMember(entity, "Guid", warnings));

            object components = MemberResolver.GetMember(entity, "Components", warnings);

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
