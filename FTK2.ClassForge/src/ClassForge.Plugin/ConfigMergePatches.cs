using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ClassForge.Core;
using ClassForge.Core.IO;

namespace ClassForge.Plugin
{
    /// <summary>
    /// The real M1 work: postfix <c>ConfigsHelper.LoadConfigs</c>/<c>ReloadConfigs</c> (SPEC-DELTA-v1.1 §1
    /// OQ#3 — the only viable merge mechanism, since neither entry point accepts extra source directories and
    /// both rebuild <c>Configs</c> from scratch, which is also why a re-run here is safe/idempotent) and merge
    /// ClassForge.Core's resolved <see cref="MergePlan"/> into the just-populated <c>Configs</c> dictionaries.
    /// </summary>
    public static class ConfigMergePatches
    {
        /// <summary>Postfix for <c>public static Configs LoadConfigs(string basePath)</c> — mutates the method's return value before the caller (which assigns it to Env.Configs) resumes.</summary>
        public static void LoadConfigs_Postfix(string basePath, ref Configs __result)
        {
            if (__result == null) return;
            RunMerge(__result, "LoadConfigs");
        }

        /// <summary>Postfix for <c>public static void ReloadConfigs(ref Configs configs, string basePath)</c>.</summary>
        public static void ReloadConfigs_Postfix(ref Configs configs, string basePath)
        {
            if (configs == null) return;
            RunMerge(configs, "ReloadConfigs");
        }

        private static void RunMerge(Configs configs, string caller)
        {
            if (!ClassForgePlugin.Enabled.Value)
            {
                ClassForgePlugin.Log.LogInfo($"[ClassForge] {caller}: Enabled=false — no packs scanned, vanilla Configs untouched.");
                return;
            }

            // MP review M2 — honest Block semantics, documented here rather than pretended in code:
            // ClassForge's Block policy (SPEC.md §9.5 / SPEC-DELTA-v1.1 §5.3) turns off RUNTIME features
            // (recipe engine, trait-loadout injection, class-select injection — everything gated behind
            // ClassForgePlugin.FeaturesActive), not the content merge. There used to be an
            // `if (ParityBridge.Blocked) return;` guard right here; it was dead code in practice (this method
            // only runs from ConfigsHelper.LoadConfigs at boot, before any multiplayer handshake could
            // possibly have latched Blocked, and ConfigsHelper.ReloadConfigs has no vanilla caller at all — see
            // the MP review), and worse, it actively misrepresented the real posture as "merge stops too".
            // The honest posture is: merged Configs entries (classes/traits/items/abilities/localization/
            // icons/portraits) are inert DATA. With FeaturesActive false, nothing exercises that data — no
            // class-select/trait-loadout injection offers it, and the recipe engine never fires — so
            // re-running (or having already run) the merge under Block cannot itself cause an asymmetric
            // simulation. Re-running the merge is also always safe: ApplyPlan is idempotent, and this is the
            // same data-only content ConfigsHelper.LoadConfigs/ReloadConfigs already rebuilds from scratch.
            try
            {
                ClassForgePlugin.EnsurePackKnobsBound();

                var fs = new FileSystemFileSource();
                var roots = ClassForgePlugin.GetRoots();
                var loader = new PackLoader();

                // MP review M0 — adds-only enforcement against LIVE ids. Snapshot the ids already present in
                // `configs` (vanilla content the native LoadConfigs/ReloadConfigs body just built, BEFORE this
                // postfix applies anything) so MergePlanner can refuse a pack entry that would silently
                // overwrite pre-existing content (e.g. a pack shipping an id named "KNIGHT").
                var liveIds = new LiveIdSets(configs.Characters?.Keys, configs.Things?.Keys, configs.Abilities?.Keys, configs.StatusEffects?.Keys);
                var result = loader.Load(fs, roots, ClassForgePlugin.IsPackEnabled, liveIds);

                foreach (var finding in result.Findings)
                    LogFinding(finding);

                ApplyPlan(configs, result.MergePlan);

                // Feed the visual-remap layer the current pack class ids (full rebuild each merge,
                // so hot-reload / disabled packs never leave a stale remap behind).
                VisualRemapPatches.SetPackClassIds(result.MergePlan.Characters.Select(op => op.Id));

                ClassForgePlugin.CurrentMergePlan = result.MergePlan;
                ClassForgePlugin.CurrentDataHash = result.DataHash;

                // Icon/portrait paths may have changed (or their PNGs been edited) — drop the texture cache
                // so a hot-reload actually shows the new art.
                AssetPatches.InvalidateCache();

                // Rebuild the skill-recipe book from every enabled pack's skillrecipes.json. Full rebuild,
                // never incremental, so a hot-reload cannot leave a stale recipe behind; this also drops the
                // per-battle recipe runtime (budgets/cooldowns keyed to the old book).
                RecipeEngineHost.LoadBook(result);

                ClassForgePlugin.Log.LogInfo(
                    $"[ClassForge] {caller}: merged {result.EnabledOrderedPacks.Count} pack(s) " +
                    $"[{string.Join(", ", result.EnabledOrderedPacks.Select(p => p.Id))}] -> " +
                    $"{result.MergePlan.Characters.Count} classes, {result.MergePlan.Things.Count} things " +
                    $"({result.MergePlan.TraitIds.Count} traits), {result.MergePlan.Abilities.Count} abilities, " +
                    $"{result.MergePlan.StatusEffects.Count} statuses, " +
                    $"{result.MergePlan.Localization.Count} loc keys, {result.MergePlan.Icons.Count} icons, " +
                    $"{result.MergePlan.Portraits.Count} portraits. dataHash={result.DataHash}.");

                // MP review B4: hand the real, current knob values to the registration builder so a knob
                // difference between peers becomes a genuine parity divergence instead of silent "Match".
                var payload = ParityRegistrationBuilder.Build(
                    result, ClassForgePlugin.Guid, ClassForgePlugin.Version,
                    ClassForgePlugin.EnableRecipeEngine.Value, ClassForgePlugin.EnableTraitLoadoutInjection.Value);
                ClassForgePlugin.RegisterParity(payload);
            }
            catch (Exception ex)
            {
                // Total fail-safe (CONVENTIONS.md): never let a bad pack, or any unexpected exception in the
                // pipeline, escape a Harmony postfix. Vanilla Configs are left as-is beyond whatever succeeded
                // before the failure.
                ClassForgePlugin.Log.LogError($"[ClassForge] {caller}: pack merge failed unexpectedly: {ex}");
            }
        }

        private static void LogFinding(Finding finding)
        {
            switch (finding.Severity)
            {
                case FindingSeverity.Error:
                    ClassForgePlugin.Log.LogError($"[ClassForge] {finding}");
                    break;
                case FindingSeverity.Warning:
                    ClassForgePlugin.Log.LogWarning($"[ClassForge] {finding}");
                    break;
                default:
                    if (ClassForgePlugin.VerboseLogging.Value)
                        ClassForgePlugin.Log.LogDebug($"[ClassForge] {finding}");
                    break;
            }
        }

        private static void ApplyPlan(Configs configs, MergePlan plan)
        {
            foreach (var op in plan.Characters)
            {
                try
                {
                    configs.Characters[op.Id] = DeserializeGameConfig<CharacterConfig>(op.Value.ToJsonString());
                    LogApplied("class", op);
                }
                catch (Exception ex) { LogApplyFailed("class", op, ex); }
            }

            foreach (var op in plan.Things)
            {
                try
                {
                    configs.Things[op.Id] = DeserializeGameConfig<ThingConfig>(op.Value.ToJsonString());
                    LogApplied("thing", op);
                }
                catch (Exception ex) { LogApplyFailed("thing", op, ex); }
            }

            foreach (var op in plan.Abilities)
            {
                try
                {
                    configs.Abilities[op.Id] = DeserializeGameConfig<CombatAbilityConfig>(op.Value.ToJsonString());
                    LogApplied("ability", op);
                }
                catch (Exception ex) { LogApplyFailed("ability", op, ex); }
            }

            // Encounter Modifiers spec §3.4/M-EM2 (deferred from M-EM1): statuses.json -> Configs.StatusEffects.
            // Same idempotent from-scratch-rebuild pass, same DeserializeGameConfig<T> pathway (game-type
            // deserialization rule) as Characters/Things/Abilities above — StatusEffectConfig is
            // field-based (Type/Duration/TickFrequency/TickOverworld/TickCombat/TickExpire/TileSync/
            // GroupSync/Passives/AddProperties/Stats/CustomStats), so the same ImportOptions +
            // NormalizeNullCollections pass that already handles the other three categories applies
            // unchanged (Stats/CustomStats are SerializedSortedDictionary<string,int>, a collection type
            // NormalizeNullCollections already knows how to default when a pack entry omits it).
            foreach (var op in plan.StatusEffects)
            {
                try
                {
                    configs.StatusEffects[op.Id] = DeserializeGameConfig<StatusEffectConfig>(op.Value.ToJsonString());
                    LogApplied("status", op);
                }
                catch (Exception ex) { LogApplyFailed("status", op, ex); }
            }
        }

        /// <summary>
        /// Game config classes are field-based (public fields, no properties), so they MUST be
        /// deserialized with the game's own <c>JsonHelper.importOptions</c> (IncludeFields=true,
        /// string enums, comments/trailing commas) — default STJ options silently ignore every
        /// field and produce a hollow config (day-one in-game finding: null <c>Tags</c> on a merged
        /// Thing crashed <c>SkillHelper.Initialize</c> at boot).
        /// </summary>
        private static T DeserializeGameConfig<T>(string json) where T : class
        {
            var obj = JsonSerializer.Deserialize<T>(json, ImportOptions);
            if (obj != null) NormalizeNullCollections(obj);
            return obj;
        }

        private static JsonSerializerOptions _importOptions;

        /// <summary>
        /// The game's own <c>JsonHelper.importOptions</c> (fields, comments, trailing commas) with
        /// one addition: enum parsing tolerates the empty string, mapping it to the enum's default
        /// (day-one in-game finding: pack JSON writes <c>"Expansion": ""</c> / <c>"Material": ""</c>
        /// for "unset", which the stock <c>JsonStringEnumConverter</c> rejects, skipping the entry).
        /// </summary>
        private static JsonSerializerOptions ImportOptions
        {
            get
            {
                if (_importOptions == null)
                {
                    var o = new JsonSerializerOptions(JsonHelper.importOptions);
                    o.Converters.Insert(0, new LenientEnumConverterFactory());
                    _importOptions = o;
                }
                return _importOptions;
            }
        }

        private sealed class LenientEnumConverterFactory : System.Text.Json.Serialization.JsonConverterFactory
        {
            public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

            public override System.Text.Json.Serialization.JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
                => (System.Text.Json.Serialization.JsonConverter)Activator.CreateInstance(
                    typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert));
        }

        private sealed class LenientEnumConverter<T> : System.Text.Json.Serialization.JsonConverter<T>
            where T : struct, Enum
        {
            public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                if (reader.TokenType == JsonTokenType.Number)
                    return (T)Enum.ToObject(typeof(T), reader.GetInt64());
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s)) return default;
                return (T)Enum.Parse(typeof(T), s, ignoreCase: true);
            }

            public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
                => writer.WriteStringValue(value.ToString());
        }

        /// <summary>
        /// Vanilla config entries always carry their collection fields, and vanilla code indexes
        /// them without null checks (e.g. <c>SkillHelper.Initialize</c> runs <c>Tags.Contains</c>
        /// over every Thing). A pack entry that omits a collection must land as an empty instance,
        /// not null. Non-collection reference fields (Equippable, LocKey, ...) are left null —
        /// null is meaningful there ("not equippable") and vanilla JSON omits them too.
        /// </summary>
        private static void NormalizeNullCollections(object obj)
        {
            foreach (var f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (f.GetValue(obj) != null) continue;
                var t = f.FieldType;
                if (!t.IsClass || t.IsAbstract || t == typeof(string)) continue;
                if (!typeof(IEnumerable).IsAssignableFrom(t)) continue;
                var ctor = t.GetConstructor(Type.EmptyTypes);
                if (ctor != null) f.SetValue(obj, ctor.Invoke(null));
            }
        }

        private static void LogApplied(string kind, MergeOp op)
        {
            if (ClassForgePlugin.VerboseLogging.Value)
                ClassForgePlugin.Log.LogDebug($"[ClassForge] merged {kind} '{op.Id}' from pack '{op.SourcePackId}'.");
        }

        private static void LogApplyFailed(string kind, MergeOp op, Exception ex)
        {
            // Fail-safe per entry: one malformed id doesn't take down the rest of the pack's merge.
            ClassForgePlugin.Log.LogError($"[ClassForge] Failed to apply {kind} '{op.Id}' from pack '{op.SourcePackId}': {ex.Message} — entry skipped.");
        }
    }
}
