using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassForge.Core
{
    /// <summary>
    /// Builds the final <see cref="MergePlan"/> from a resolved pack order + their parsed content (SPEC.md §3
    /// runtime flow). Id collisions across packs resolve last-pack-wins (the later pack in resolved load order),
    /// logging both pack ids (SPEC.md §3, §8 edge cases). Iteration is always over keys sorted ordinally —
    /// Dictionary enumeration order is never relied on for anything observable (MULTIPLAYER.md R2).
    ///
    /// <para><b>MP review M0 — adds-only enforcement against LIVE ids.</b> The pack-vs-pack collision check
    /// above is the only thing the original implementation had; it says nothing about a pack that defines an
    /// id which already exists in the live game (e.g. a pack shipping <c>KNIGHT</c> and silently replacing the
    /// vanilla Knight <c>CharacterConfig</c>). <see cref="LiveIdSets"/> (supplied by the Plugin, snapshotted
    /// from <c>Configs</c> immediately before the merge runs) closes that gap: a candidate id already present
    /// in the live set is refused outright with a <see cref="FindingSeverity.Error"/> Finding and never enters
    /// the target dictionary — it never even reaches the pack-vs-pack collision check below, and a second pack
    /// defining the same live-colliding id is refused independently, the same way, every time.</para>
    /// </summary>
    public static class MergePlanner
    {
        public static MergePlan Build(List<(DiscoveredPack Pack, ParsedPack Content)> orderedContents, List<Finding> findings, LiveIdSets liveIds = null)
        {
            var live = liveIds ?? LiveIdSets.Empty;
            var plan = new MergePlan();
            var characters = new Dictionary<string, MergeOp>(StringComparer.Ordinal);
            var things = new Dictionary<string, MergeOp>(StringComparer.Ordinal);
            var abilities = new Dictionary<string, MergeOp>(StringComparer.Ordinal);
            var statusEffects = new Dictionary<string, MergeOp>(StringComparer.Ordinal);

            foreach (var entry in orderedContents)
            {
                var packId = entry.Pack.Manifest.Id;
                var content = entry.Content;

                MergeCategory(characters, packId, content.Classes, findings, "Character", live.Characters);
                MergeCategory(things, packId, content.Traits, findings, "Trait/Thing", live.Things);
                MergeCategory(things, packId, content.Items, findings, "Item/Thing", live.Things);
                MergeCategory(abilities, packId, content.Abilities, findings, "Ability", live.Abilities);
                MergeCategory(statusEffects, packId, content.Statuses, findings, "StatusEffect", live.StatusEffects);

                MergeStringDict(plan.Localization, packId, content.Localization, findings, "CF_LOC_OVERRIDE", "Localization key");
                MergeStringDict(plan.Icons, packId, content.Icons, findings, "CF_ICON_OVERRIDE", "Icon id");
                MergeStringDict(plan.Portraits, packId, content.Portraits, findings, "CF_PORTRAIT_OVERRIDE", "Portrait id");
                MergeStringDict(plan.VisualFallbacks, packId, content.VisualFallbacks, findings, "CF_VISUALFALLBACK_OVERRIDE", "Visual fallback");

                // modifiers.json is ClassForge's own registry (like skillrecipes.json), not a Configs.* merge
                // category (Encounter Modifiers spec §3.2/§3.4) — collected in resolved pack load order,
                // authored Modifiers array order preserved untouched within each table.
                if (content.ModifierTable != null)
                    plan.ModifierTables.Add(content.ModifierTable);
            }

            plan.Characters = characters.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
            plan.Things = things.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
            plan.Abilities = abilities.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();
            plan.StatusEffects = statusEffects.Values.OrderBy(m => m.Id, StringComparer.Ordinal).ToList();

            // Class:"TRAIT" entries register with the trait registry (SPEC.md §3), regardless of which source
            // file (traits.json vs. items.json) they came from.
            plan.TraitIds = plan.Things
                .Where(m => string.Equals(m.Value.GetString("Class"), "TRAIT", StringComparison.Ordinal))
                .Select(m => m.Id)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            // Visual-fallback donor resolution (task #8). Only when a live Things id set was actually
            // supplied — offline tools (PackCheck, tests) have no live game to resolve against, and a
            // vanilla donor id is unknowable there; warning on every vanilla donor would be pure noise.
            // (LiveIdSets coalesces null to an empty set, so "supplied" is Count > 0 — a real game always
            // has vanilla Things.)
            if (live.Things != null && live.Things.Count > 0)
            {
                var mergedThingIds = new HashSet<string>(things.Keys, StringComparer.Ordinal);
                foreach (var kv in plan.VisualFallbacks.OrderBy(e => e.Key, StringComparer.Ordinal))
                {
                    if (!live.Things.Contains(kv.Value) && !mergedThingIds.Contains(kv.Value))
                    {
                        findings.Add(Finding.Warning("CF_VISUALFALLBACK_DANGLING",
                            $"Visual fallback for '{kv.Key}' names donor '{kv.Value}', which is neither a live " +
                            "Configs.Things id nor a merged pack Thing — the item will render without a 3D model " +
                            "(the runtime finalizer degrades this safely, but the fallback is doing nothing).", null));
                    }
                }
            }

            plan.Findings = findings;
            return plan;
        }

        private static void MergeCategory(
            Dictionary<string, MergeOp> target,
            string packId,
            Dictionary<string, ClassForge.Core.Json.JsonValue> entries,
            List<Finding> findings,
            string label,
            ISet<string> liveIds)
        {
            foreach (var kv in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                // M0: refuse outright — packs are adds-only and must never overwrite something the live game
                // (or a prior, non-pack source) already defines. This is checked BEFORE the pack-vs-pack
                // collision logic below, so a live-colliding id never enters `target` at all.
                if (liveIds != null && liveIds.Contains(kv.Key))
                {
                    findings.Add(Finding.Error("CF_LIVE_ID_COLLISION",
                        $"{label} id '{kv.Key}' from pack '{packId}' collides with an id already present in the " +
                        "live game data and was REFUSED (packs are adds-only; a pack must never overwrite " +
                        "pre-existing content). Rename the id in the pack.", packId));
                    continue;
                }

                if (target.TryGetValue(kv.Key, out var existing) && !string.Equals(existing.SourcePackId, packId, StringComparison.Ordinal))
                {
                    findings.Add(Finding.Warning("CF_ID_COLLISION",
                        $"{label} id '{kv.Key}' is defined by both pack '{existing.SourcePackId}' and pack '{packId}' — " +
                        $"'{packId}' wins (later in resolved load order).", packId));
                }
                target[kv.Key] = new MergeOp(kv.Key, kv.Value, packId);
            }
        }

        private static void MergeStringDict(
            Dictionary<string, string> target,
            string packId,
            Dictionary<string, string> entries,
            List<Finding> findings,
            string findingCode,
            string label)
        {
            foreach (var kv in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                if (target.ContainsKey(kv.Key) && !string.Equals(target[kv.Key], kv.Value, StringComparison.Ordinal))
                {
                    findings.Add(Finding.Info(findingCode, $"{label} '{kv.Key}' overridden by pack '{packId}'.", packId));
                }
                target[kv.Key] = kv.Value;
            }
        }
    }
}
