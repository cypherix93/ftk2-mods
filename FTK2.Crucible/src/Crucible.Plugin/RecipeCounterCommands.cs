using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Reads ClassForge's live recipe-engine COUNTER values (<c>cf_bond</c>, <c>cf_gorged</c>, and any
    /// other name a pack's <c>COUNTER_ADD</c> effect writes) so counter-gated class traits stop being
    /// UNVERIFIABLE from the harness.
    ///
    /// <para><b>Why this lives in Crucible, not ClassForge.</b> Counter-gated traits need a console
    /// command reachable the same way every other <c>crucible_*</c> verb is: through
    /// <c>CommandLineHelper.RegisterCommand</c>, wired by <see cref="GameBridge"/>. Verified by reading
    /// every <c>.cs</c> file under <c>FTK2.ClassForge\src</c>: there is no call to
    /// <c>RegisterCommand</c>, <c>CommandLineHelper</c>, or <c>GameBridge</c> anywhere in that project
    /// — ClassForge registers zero console commands today, so there is no existing ClassForge
    /// registration path to extend. Putting the command here instead needs no
    /// <c>ProjectReference</c> from Crucible.Plugin to ClassForge.Plugin (confirmed absent from
    /// <c>Crucible.Plugin.csproj</c>'s <c>ItemGroup</c>) — every ClassForge type below is reached the
    /// same way <see cref="ReflectionCommands"/> reaches arbitrary game types: by name, through
    /// reflection over already-loaded assemblies, so a ClassForge internals change degrades this
    /// command to a reported "unreachable", never a Crucible build failure.
    ///
    /// <para><b>Where the counters actually live (read from
    /// <c>FTK2.ClassForge\src\ClassForge.Recipes\Runtime\CombatRuntime.cs</c>).</b> Line 42:
    /// <c>private readonly Dictionary&lt;string, int&gt; _counters = new Dictionary&lt;string, int&gt;(...)</c>,
    /// keyed (line 41 doc comment) <c>"(ownerGuid, counterName) → value"</c> and built via
    /// <c>Key3(ownerGuid, name, "")</c> (lines 118-134), where <c>Key3</c> (lines 59-62) joins its three
    /// parts with the US (U+001F) separator. So counters are PER-ENTITY (keyed by the owning
    /// entity's <c>Guid</c>), not global — an entity with no counters simply has no keys in the
    /// dictionary, it is never represented by a zero-valued entry.
    ///
    /// <para><b>Reachability — the single live <c>CombatRuntime</c>.</b>
    /// <c>FTK2.ClassForge\src\ClassForge.Plugin\Recipes\RecipeEngineHost.cs</c> line 50 declares
    /// <c>private static RecipeDispatcher _cachedDispatcher</c> (private, static, on an
    /// <c>internal static class RecipeEngineHost</c> — line 40). <c>RecipeDispatcher.State</c>
    /// (<c>ClassForge.Recipes\Runtime\RecipeDispatcher.cs</c> line 51) is a PUBLIC
    /// <c>RecipeStateStore</c> property, and <c>RecipeStateStore.Current</c>
    /// (<c>CombatRuntime.cs</c> line 187) is a PUBLIC <c>CombatRuntime</c> property that returns the
    /// runtime already synced for the live combat — <b>no method call is required</b> to read it.
    /// <c>RecipeEngineHost.PeekCounter</c> (lines 241-257) already demonstrates exactly this passive
    /// read path (<c>dispatcher.State.Current.GetCounter(...)</c>) for a single named counter without
    /// ever calling <c>RecipeStateStore.Sync</c>; this command generalizes that same path to enumerate
    /// every key currently in <c>_counters</c> instead of asking for one name at a time.
    ///
    /// <para><b>Correcting the earlier gap note.</b> The claim that <c>crucible_get</c> "only walks
    /// fields/properties and can call methods on instances resolvable via a static Instance/Current
    /// member... none of which apply" undersells what <c>ReflectionCommands.WalkSegments</c> already
    /// does: it resolves EVERY segment (including <c>_cachedDispatcher</c>, a private static field)
    /// via <c>AccessTools.Field</c>/<c>AccessTools.Property</c>, which search public AND non-public,
    /// static AND instance members — so a raw path like
    /// <c>ClassForge.Plugin.RecipeEngineHost._cachedDispatcher.State.Current</c> would in fact resolve.
    /// What <c>crucible_get</c> genuinely cannot do is render it usefully: its <c>Render</c> helper
    /// (<c>ReflectionCommands.cs</c> lines 910-929) treats any <c>IEnumerable</c> — including
    /// <c>Dictionary&lt;string,int&gt;</c> — as an opaque sampled list of up to 5
    /// <c>KeyValuePair.ToString()</c> entries, with no per-entity grouping and no decoding of the
    /// U+001F-joined <c>Key3</c> key shape, and it cannot distinguish "reached an empty
    /// dictionary" from "the path itself failed to resolve" the way this command's contract requires.
    /// That gap — not raw reachability — is what a dedicated command earns its keep on.
    /// </summary>
    internal static class RecipeCounterCommands
    {
        internal static string LastResult;
        private static ManualLogSource _log;
        private static bool _registered;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        internal static void TryRegister()
        {
            if (_registered) return;
            _registered = GameBridge.RegisterCommand("crucible_recipe_counters",
                typeof(RecipeCounterCommands).GetMethod("CrucibleRecipeCounters", BindingFlags.Public | BindingFlags.Static),
                new List<string> { "entityGuidOrIndex (optional, - for every entity)" });

            if (_registered && _log != null)
                _log.LogInfo("RecipeCounterCommands registered (crucible_recipe_counters).");
        }

        /// <summary>
        /// crucible_recipe_counters [entityGuidOrIndex] — lists every ClassForge recipe COUNTER
        /// currently held, optionally scoped to one entity. Single discrete string parameter for the
        /// same reason every other optional-arg command here uses one (see
        /// <see cref="ReflectionCommands.CrucibleGet"/>'s doc comment): the CommandLineHelper arg
        /// marshaller has no <c>string[]</c> overload.
        /// </summary>
        public static void CrucibleRecipeCounters(string pEntity)
        {
            LastResult = null;
            try
            {
                Type hostType = AccessTools.TypeByName("ClassForge.Plugin.RecipeEngineHost");
                if (hostType == null)
                {
                    LastResult = "unreachable: ClassForge.Plugin.RecipeEngineHost type not found — "
                        + "the ClassForge plugin is not loaded in this process.";
                    return;
                }

                FieldInfo dispatcherField = AccessTools.Field(hostType, "_cachedDispatcher");
                if (dispatcherField == null)
                {
                    LastResult = "unreachable: RecipeEngineHost._cachedDispatcher field not found "
                        + "(ClassForge internals changed since this command was written).";
                    return;
                }

                object dispatcher = dispatcherField.GetValue(null);
                if (dispatcher == null)
                {
                    LastResult = "unreachable: no live recipe dispatcher — the recipe engine has not "
                        + "synced to a combat yet this process (RecipeEngineHost.TryBegin never ran, "
                        + "or the last combat ended and ResetCombat cleared it). Enter combat and try again.";
                    return;
                }

                object stateStore = PartyAccess.ReadMember(dispatcher, "State");
                if (stateStore == null)
                {
                    LastResult = "unreachable: RecipeDispatcher.State was null (ClassForge internals changed).";
                    return;
                }

                object runtime = PartyAccess.ReadMember(stateStore, "Current");
                if (runtime == null)
                {
                    LastResult = "unreachable: RecipeStateStore.Current is null — no per-battle runtime "
                        + "has been allocated for the current combat yet.";
                    return;
                }

                FieldInfo countersField = AccessTools.Field(runtime.GetType(), "_counters");
                if (countersField == null)
                {
                    LastResult = "unreachable: CombatRuntime._counters field not found (ClassForge internals changed).";
                    return;
                }

                IDictionary counters = countersField.GetValue(runtime) as IDictionary;
                if (counters == null)
                {
                    LastResult = "unreachable: CombatRuntime._counters was not a Dictionary (ClassForge internals changed).";
                    return;
                }

                string wantedGuid = null;
                if (!string.IsNullOrEmpty(pEntity) && pEntity.Trim() != "-")
                {
                    string error;
                    if (!TryResolveEntityGuid(pEntity.Trim(), out wantedGuid, out error))
                    {
                        LastResult = "error: " + error;
                        return;
                    }
                }

                // Decode every "(ownerGuid, counterName) U+001F-joined" key (Key3 shape, CombatRuntime.cs
                // lines 59-62/118-134) into (ownerGuid, name, value), grouped by owner.
                var byOwner = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in counters)
                {
                    string key = entry.Key as string;
                    if (key == null) continue;
                    string[] parts = key.Split('\u001F');
                    if (parts.Length < 2) continue; // malformed key -- skip rather than misreport
                    string ownerGuid = parts[0];
                    string name = parts[1];

                    if (wantedGuid != null && !string.Equals(ownerGuid, wantedGuid, StringComparison.OrdinalIgnoreCase))
                        continue;

                    List<string> lines;
                    if (!byOwner.TryGetValue(ownerGuid, out lines))
                    {
                        lines = new List<string>();
                        byOwner[ownerGuid] = lines;
                    }
                    lines.Add(name + "=" + Convert.ToString(entry.Value, System.Globalization.CultureInfo.InvariantCulture));
                }

                if (byOwner.Count == 0)
                {
                    LastResult = wantedGuid != null
                        ? "reached OK: ClassForge recipe engine holds NO counters for entity '" + wantedGuid + "' this combat."
                        : "reached OK: ClassForge recipe engine holds NO counters this combat (0 entries in _counters).";
                    return;
                }

                var owners = new List<string>(byOwner.Keys);
                owners.Sort(StringComparer.Ordinal);

                var sb = new StringBuilder();
                sb.Append("ClassForge recipe counters (").Append(byOwner.Count).Append(" entity/ies):");
                for (int i = 0; i < owners.Count; i++)
                {
                    string owner = owners[i];
                    List<string> lines = byOwner[owner];
                    lines.Sort(StringComparer.Ordinal);
                    sb.Append(" | ").Append(owner).Append(": ").Append(string.Join(", ", lines.ToArray()));
                }

                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_recipe_counters threw: " + ex.Message;
                if (_log != null) _log.LogWarning("crucible_recipe_counters failed: " + ex);
            }
        }

        /// <summary>
        /// Resolves an <c>entityGuidOrIndex</c> argument against the live <c>CombatState.Entities</c>
        /// list, mirroring the guid-or-index convention <c>CombatDriveCommands.ResolveCombatant</c>
        /// already uses. A separate small helper rather than reusing that private method — it lives
        /// in a different file and this read is intentionally read-only, unlike the drive commands'
        /// action-taking resolvers.
        /// </summary>
        private static bool TryResolveEntityGuid(string guidOrIndex, out string guid, out string error)
        {
            guid = null;
            error = null;

            object env = GameBridge.GetEnv();
            object gameRun = PartyAccess.ReadMember(env, "GameRun");
            object combatState = PartyAccess.ReadMember(gameRun, "CombatState");
            if (combatState == null) { error = "no fight in progress (GameRunData.CombatState is null)"; return false; }

            IEnumerable entities = PartyAccess.ReadMember(combatState, "Entities") as IEnumerable;
            if (entities == null) { error = "CombatState.Entities is not enumerable"; return false; }

            var list = new List<object>();
            foreach (object e in entities) list.Add(e);

            int index;
            if (int.TryParse(guidOrIndex, out index))
            {
                if (index < 0 || index >= list.Count) { error = "entity index " + index + " out of range (0.." + (list.Count - 1) + ")"; return false; }
                object g = PartyAccess.ReadMember(list[index], "Guid");
                if (g == null) { error = "entity at index " + index + " has a null Guid"; return false; }
                guid = g.ToString();
                return true;
            }

            foreach (object candidate in list)
            {
                object g = PartyAccess.ReadMember(candidate, "Guid");
                if (g != null && string.Equals(g.ToString(), guidOrIndex, StringComparison.OrdinalIgnoreCase))
                {
                    guid = g.ToString();
                    return true;
                }
            }
            error = "no combatant with guid '" + guidOrIndex + "'";
            return false;
        }
    }
}
