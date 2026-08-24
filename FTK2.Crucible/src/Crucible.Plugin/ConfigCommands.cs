using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using FTK2Mods.Crucible;
using HarmonyLib;

namespace Crucible.Plugin
{
    /// <summary>
    /// Reads the LIVE, merged game configs — the authoritative answer to "did our content actually
    /// reach the game?"
    ///
    /// This is a different question from what the JSON on disk says, and the difference is the whole
    /// point. A pack that fails to merge, an id that collides, or a status id that resolves to
    /// nothing all look perfect on disk. Two authored recipes shipped applying <c>"CURSE"</c> — an
    /// <c>eStatusEffectTypes</c> member rather than a <c>Configs.StatusEffects</c> id — and did
    /// nothing; reading the live config is how that class of defect becomes visible.
    ///
    /// It also matters that custom skills are NOT reachable through
    /// <c>CharacterHelper.GetPassiveSkills</c>: that maps to the game's <c>eSkills</c> enum, which
    /// cannot gain members at runtime, so an authored <c>SKILL_CF_*</c> id will never appear there
    /// even when it is working perfectly. ClassForge's recipe engine instead reads the character
    /// config's <c>Passives</c> as raw strings. So the config is where a custom skill's presence must
    /// be asserted, and its EFFECT in combat is where its behaviour must be asserted. Asserting the
    /// wrong one produces a confident false negative.
    ///
    /// Verified API surface (TypeProbe --signatures, 2026-08-23):
    ///   Env.Configs : Configs
    ///   Configs.Characters / .StatusEffects / .Abilities : SerializedSortedDictionary
    /// </summary>
    internal static class ConfigCommands
    {
        internal static string LastResult;
        private static ManualLogSource _log;
        private static bool _classRegistered;
        private static bool _statusRegistered;
        private static bool _thingRegistered;

        internal static void Initialize(ManualLogSource log)
        {
            _log = log;
        }

        private static bool _locRegistered;

        internal static void TryRegister()
        {
            if (!_locRegistered)
                _locRegistered = GameBridge.RegisterCommand("crucible_loc",
                    typeof(ConfigCommands).GetMethod("CrucibleLoc", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "key or prefix" });

            if (!_classRegistered)
                _classRegistered = GameBridge.RegisterCommand("crucible_class_config",
                    typeof(ConfigCommands).GetMethod("CrucibleClassConfig", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "classId" });

            if (!_statusRegistered)
                _statusRegistered = GameBridge.RegisterCommand("crucible_status_config",
                    typeof(ConfigCommands).GetMethod("CrucibleStatusConfig", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "statusId" });

            if (!_thingRegistered)
                _thingRegistered = GameBridge.RegisterCommand("crucible_thing_config",
                    typeof(ConfigCommands).GetMethod("CrucibleThingConfig", BindingFlags.Public | BindingFlags.Static),
                    new List<string> { "thingId" });
        }

        /// <summary>
        /// crucible_class_config &lt;classId&gt; — the live merged character config: passives, things,
        /// stats. Reports NOT PRESENT explicitly when the id is absent, which is the finding that
        /// matters most (it means the pack did not merge).
        /// </summary>
        public static void CrucibleClassConfig(string classId)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(classId))
                {
                    LastResult = "error: usage: crucible_class_config <classId>";
                    return;
                }
                classId = classId.Trim();

                object map;
                string error;
                if (!TryGetConfigMap("Characters", out map, out error)) { LastResult = "error: " + error; return; }

                object config = LookUp(map, classId);
                if (config == null)
                {
                    LastResult = "classId=" + classId + " PRESENT=False"
                        + "\nThe id is NOT in the live Configs.Characters map. The pack did not merge,"
                        + "\nor the id differs from what the JSON declares. Total entries=" + CountOf(map);
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("classId=").Append(classId).Append(" PRESENT=True");
                sb.Append("\npassives: ").Append(RenderStrings(PartyAccess.ReadMember(config, "Passives")));
                sb.Append("\nthings: ").Append(RenderKeys(PartyAccess.ReadMember(config, "Things")));
                sb.Append("\nstats: ").Append(RenderKeyValues(PartyAccess.ReadMember(config, "Stats")));
                sb.Append("\nbaseType=").Append(Str(PartyAccess.ReadMember(config, "BaseType")))
                  .Append(" rarity=").Append(Str(PartyAccess.ReadMember(config, "Rarity")))
                  .Append(" locKey=").Append(Str(PartyAccess.ReadMember(config, "LocKey")));
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_class_config threw: " + ex.Message;
            }
        }

        /// <summary>
        /// crucible_status_config &lt;statusId&gt; — proves a status id resolves in the live
        /// Configs.StatusEffects map. This is the exact check that would have caught the shipped
        /// <c>"CURSE"</c> bug: an eStatusEffectTypes member is not a StatusEffects key.
        /// </summary>
        public static void CrucibleStatusConfig(string statusId)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(statusId))
                {
                    LastResult = "error: usage: crucible_status_config <statusId>";
                    return;
                }
                statusId = statusId.Trim();

                object map;
                string error;
                if (!TryGetConfigMap("StatusEffects", out map, out error)) { LastResult = "error: " + error; return; }

                object config = LookUp(map, statusId);
                if (config == null)
                {
                    LastResult = "statusId=" + statusId + " PRESENT=False"
                        + "\nNOT a key in the live Configs.StatusEffects map, so anything applying it"
                        + "\nsilently does nothing. Total entries=" + CountOf(map);
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("statusId=").Append(statusId).Append(" PRESENT=True");
                sb.Append("\ntype=").Append(Str(PartyAccess.ReadMember(config, "Type")));
                sb.Append(" duration=").Append(Str(PartyAccess.ReadMember(config, "Duration")));
                sb.Append(" tickCombat=").Append(Str(PartyAccess.ReadMember(config, "TickCombat")));
                sb.Append(" tickOverworld=").Append(Str(PartyAccess.ReadMember(config, "TickOverworld")));
                sb.Append("\npassives: ").Append(RenderStrings(PartyAccess.ReadMember(config, "Passives")));
                sb.Append("\nstats: ").Append(RenderKeyValues(PartyAccess.ReadMember(config, "Stats")));
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_status_config threw: " + ex.Message;
            }
        }

        /// <summary>
        /// crucible_thing_config &lt;thingId&gt; — proves an item id resolves in the live
        /// Configs.Things map.
        ///
        /// An absent id is not a cosmetic problem: EquipmentHelper.Equip throws a
        /// NullReferenceException on one, which reads as a broken command rather than as missing
        /// content. Measured 2026-08-23: every ARM_EOR_STARTER_* weapon was absent because the
        /// Armory plugin had never been deployed to the game at all.
        /// </summary>
        public static void CrucibleThingConfig(string thingId)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(thingId))
                {
                    LastResult = "error: usage: crucible_thing_config <thingId|prefix*>";
                    return;
                }
                thingId = thingId.Trim();

                object map;
                string error;
                if (!TryGetConfigMap("Things", out map, out error)) { LastResult = "error: " + error; return; }

                // A trailing * lists matching ids instead of describing one. Item ids are not
                // guessable -- there are 2418 of them and no naming convention that survives
                // contact with the shipped data -- so without a way to SEARCH, every item test
                // starts by guessing a key and getting KeyNotFoundException.
                if (thingId.EndsWith("*", StringComparison.Ordinal))
                {
                    LastResult = ListThingIds(map, thingId.Substring(0, thingId.Length - 1));
                    return;
                }

                object config = LookUp(map, thingId);
                if (config == null)
                {
                    LastResult = "thingId=" + thingId + " PRESENT=False"
                        + "\nNOT a key in the live Configs.Things map. Equipping or granting it throws."
                        + " Total entries=" + CountOf(map);
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("thingId=").Append(thingId).Append(" PRESENT=True");
                sb.Append("\ntype=").Append(Str(PartyAccess.ReadMember(config, "Type")));
                sb.Append(" rarity=").Append(Str(PartyAccess.ReadMember(config, "Rarity")));
                sb.Append("\nstats: ").Append(RenderKeyValues(PartyAccess.ReadMember(config, "Stats")));
                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_thing_config threw: " + ex.Message;
            }
        }


        /// <summary>Ids in <paramref name="map"/> starting with <paramref name="prefix"/>, case-insensitively.</summary>
        private static string ListThingIds(object map, string prefix)
        {
            var keys = PartyAccess.ReadMember(map, "Keys") as IEnumerable;
            if (keys == null) return "error: Configs.Things exposes no Keys";

            var hits = new List<string>();
            foreach (object k in keys)
            {
                string key = Str(k);
                if (key == null) continue;
                if (prefix.Length > 0 &&
                    key.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) != 0) continue;
                hits.Add(key);
            }
            hits.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.Append("prefix=").Append(prefix).Append("* matches=").Append(hits.Count);
            for (int i = 0; i < hits.Count && i < 60; i++) sb.Append("\n  ").Append(hits[i]);
            if (hits.Count > 60) sb.Append("\n  ... ").Append(hits.Count - 60).Append(" more");
            return sb.ToString();
        }

        // ============================================================== helpers

        private static bool TryGetConfigMap(string mapName, out object map, out string error)
        {
            map = null;
            error = null;

            object env = GameBridge.GetEnv();
            if (env == null) { error = "Env unavailable"; return false; }

            object configs = PartyAccess.ReadMember(env, "Configs");
            if (configs == null) { error = "Env.Configs is null"; return false; }

            map = PartyAccess.ReadMember(configs, mapName);
            if (map == null) { error = "Configs." + mapName + " is null"; return false; }
            return true;
        }

        /// <summary>
        /// SerializedSortedDictionary is not necessarily an IDictionary, so this tries the indexer
        /// and falls back to enumerating key/value pairs rather than assuming a shape.
        /// </summary>
        private static object LookUp(object map, string key)
        {
            IDictionary dict = map as IDictionary;
            if (dict != null)
            {
                if (dict.Contains(key)) return dict[key];
                return null;
            }

            try
            {
                PropertyInfo indexer = AccessTools.Property(map.GetType(), "Item");
                if (indexer != null)
                {
                    try { return indexer.GetValue(map, new object[] { key }); }
                    catch (Exception) { /* missing key throws; fall through to enumeration */ }
                }
            }
            catch (Exception) { }

            IEnumerable pairs = map as IEnumerable;
            if (pairs == null) return null;
            foreach (object pair in pairs)
            {
                object k = PartyAccess.ReadMember(pair, "Key");
                if (k != null && string.Equals(k.ToString(), key, StringComparison.Ordinal))
                    return PartyAccess.ReadMember(pair, "Value");
            }
            return null;
        }

        private static int CountOf(object map)
        {
            ICollection c = map as ICollection;
            if (c != null) return c.Count;
            object count = PartyAccess.ReadMember(map, "Count");
            return count is int ? (int)count : -1;
        }

        private static string RenderStrings(object value)
        {
            if (value == null) return "(null)";
            IEnumerable list = value as IEnumerable;
            if (list == null) return value.ToString();
            List<string> parts = new List<string>();
            foreach (object item in list) parts.Add(item == null ? "(null)" : item.ToString());
            return "count=" + parts.Count + " [" + string.Join(", ", parts.ToArray()) + "]";
        }

        private static string RenderKeys(object value)
        {
            if (value == null) return "(null)";
            IDictionary dict = value as IDictionary;
            if (dict == null) return RenderStrings(value);
            List<string> parts = new List<string>();
            foreach (object k in dict.Keys) parts.Add(k == null ? "(null)" : k.ToString());
            parts.Sort(StringComparer.Ordinal);
            return "count=" + parts.Count + " [" + string.Join(", ", parts.ToArray()) + "]";
        }

        private static string RenderKeyValues(object value)
        {
            if (value == null) return "(null)";
            IDictionary dict = value as IDictionary;
            if (dict == null) return RenderStrings(value);
            List<string> parts = new List<string>();
            foreach (DictionaryEntry e in dict)
                parts.Add((e.Key == null ? "?" : e.Key.ToString()) + "=" + (e.Value == null ? "?" : e.Value.ToString()));
            parts.Sort(StringComparer.Ordinal);
            return "count=" + parts.Count + " [" + string.Join(", ", parts.ToArray()) + "]";
        }

        private static string Str(object value)
        {
            return value == null ? "(null)" : value.ToString();
        }
        // ============================================================== crucible_loc

        /// <summary>
        /// crucible_loc &lt;key|prefix&gt; — resolve a localization key exactly as the game's UI does.
        ///
        /// Reads <c>Lang.__translations</c>, the dictionary every UI surface funnels through, so a key
        /// that answers here is a key the player will actually see. This exists because "the label
        /// looked right in a screenshot" only proves the NAME resolved: descriptions live behind
        /// hover tooltips and encyclopedia panels that a harness cannot easily open, and an
        /// unresolved key silently renders as the raw id or as nothing at all.
        ///
        /// A trailing '*' matches by prefix, which is how a whole pack's keys get checked in one call
        /// (e.g. <c>crucible_loc SKILL_CF_VAMPIRIC*</c>).
        /// </summary>
        public static void CrucibleLoc(string key)
        {
            LastResult = null;
            try
            {
                if (string.IsNullOrEmpty(key))
                {
                    LastResult = "error: usage: crucible_loc <key|prefix*>";
                    return;
                }
                key = key.Trim();

                Type lang = AccessTools.TypeByName("Lang");
                FieldInfo field = lang == null ? null : AccessTools.Field(lang, "__translations");
                IDictionary translations = field == null ? null : field.GetValue(null) as IDictionary;
                if (translations == null)
                {
                    LastResult = "error: Lang.__translations unavailable (game may still be loading)";
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("totalKeys=").Append(translations.Count);

                if (key.EndsWith("*", StringComparison.Ordinal))
                {
                    string prefix = key.Substring(0, key.Length - 1);
                    int found = 0;
                    foreach (DictionaryEntry entry in translations)
                    {
                        string k = entry.Key as string;
                        if (k == null || !k.StartsWith(prefix, StringComparison.Ordinal)) continue;
                        found++;
                        sb.Append("\n  ").Append(k).Append(" = ").Append(Preview(entry.Value));
                    }
                    sb.Append("\nmatched=").Append(found);
                    if (found == 0)
                        sb.Append("  <-- NOTHING matched '").Append(prefix)
                          .Append("'; the pack's localization file is not loaded");
                }
                else
                {
                    bool present = translations.Contains(key);
                    sb.Append(" key=").Append(key).Append(" PRESENT=").Append(present);
                    if (present) sb.Append("\nvalue: ").Append(Preview(translations[key]));
                    else sb.Append("\nMISSING: the UI will render the raw key or nothing at all");
                }

                LastResult = sb.ToString();
            }
            catch (Exception ex)
            {
                LastResult = "error: crucible_loc threw: " + ex.Message;
            }
        }

        private static string Preview(object value)
        {
            string text = value as string;
            if (text == null) return "(null)";
            text = text.Replace("\n", " ");
            return text.Length <= 300 ? text : text.Substring(0, 300) + "...";
        }

    }
}
