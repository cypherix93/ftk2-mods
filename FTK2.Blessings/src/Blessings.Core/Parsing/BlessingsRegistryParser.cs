using System;
using System.Collections.Generic;
using System.Globalization;
using Blessings.Core.Diagnostics;
using Blessings.Core.Json;
using Blessings.Core.Model;

namespace Blessings.Core.Parsing
{
    /// <summary>
    /// Strict parser + validator for <c>blessings.json</c> (SPEC §4.1, §8.2 item 1, §11 OQ7).
    /// "Strict" = malformed JSON, a non-object root, or a per-entry field of the wrong shape/type is an
    /// Error finding (that entry -- or the whole file, for a root-level problem -- is dropped, never
    /// guessed at). "Validator" = the softer pass layered on top: unknown top-level/per-entry JSON keys
    /// warn (forward-compatible authoring, never silently ignored), and a non-positive <c>Weight</c>
    /// warns but is NOT rejected -- it is accepted verbatim on the model and left for callers
    /// (<see cref="Resolution.BlessingResolver"/>) to clamp via <c>Max(1, Weight)</c> in the walk,
    /// mirroring EOR's own L832/L837 semantics (§11 OQ7).
    /// </summary>
    public static class BlessingsRegistryParser
    {
        public const string ExpectedSchemaVersion = "1.0";

        private static readonly HashSet<string> KnownTopLevelFields =
            new HashSet<string>(StringComparer.Ordinal) { "SchemaVersion", "Blessings" };

        // "_eor_source" ships on every M1-authored entry (provenance comment) -- it is a KNOWN,
        // allowed field, not an authoring mistake, so it must not trip the unknown-field warning.
        private static readonly HashSet<string> KnownEntryFields =
            new HashSet<string>(StringComparer.Ordinal) { "TraitId", "Weight", "Enabled", "Tags", "_eor_source" };

        public static ParseResult Parse(string json)
        {
            var result = new ParseResult();

            JsonValue root;
            try
            {
                root = MiniJsonParser.Parse(json);
            }
            catch (FormatException ex)
            {
                result.Findings.Add(Finding.Error(null, "blessings_json_malformed", "blessings.json failed to parse: " + ex.Message));
                return result;
            }

            if (root.Kind != JsonKind.Object)
            {
                result.Findings.Add(Finding.Error(null, "blessings_json_root_shape", "blessings.json root must be a JSON object."));
                return result;
            }

            foreach (var member in root.ObjectMembers)
            {
                if (!KnownTopLevelFields.Contains(member.Key))
                    result.Findings.Add(Finding.Warn(null, "unknown_top_level_field",
                        "Unrecognized top-level field '" + member.Key + "' in blessings.json -- ignored."));
            }

            var schemaVersionNode = root.GetMember("SchemaVersion");
            string schemaVersion = schemaVersionNode != null && schemaVersionNode.Kind == JsonKind.String
                ? schemaVersionNode.StringValue : null;
            if (schemaVersion == null)
            {
                result.Findings.Add(Finding.Error(null, "schema_version_missing", "blessings.json is missing a string 'SchemaVersion' field."));
                return result;
            }
            if (!string.Equals(schemaVersion, ExpectedSchemaVersion, StringComparison.Ordinal))
                result.Findings.Add(Finding.Warn(null, "schema_version_mismatch",
                    "blessings.json SchemaVersion='" + schemaVersion + "', expected '" + ExpectedSchemaVersion + "'."));

            var blessingsNode = root.GetMember("Blessings");
            if (blessingsNode == null || blessingsNode.Kind != JsonKind.Object)
            {
                result.Findings.Add(Finding.Error(null, "blessings_missing", "blessings.json is missing a 'Blessings' object."));
                return result;
            }

            var entries = new List<BlessingEntry>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var member in blessingsNode.ObjectMembers)
            {
                string id = member.Key;
                var entryNode = member.Value;

                if (!seenIds.Add(id))
                {
                    result.Findings.Add(Finding.Error(id, "duplicate_id", "duplicate blessing id '" + id + "' within blessings.json."));
                    continue;
                }

                if (entryNode == null || entryNode.Kind != JsonKind.Object)
                {
                    result.Findings.Add(Finding.Error(id, "entry_shape", "blessing entry '" + id + "' must be a JSON object."));
                    continue;
                }

                foreach (var field in entryNode.ObjectMembers)
                {
                    if (!KnownEntryFields.Contains(field.Key))
                        result.Findings.Add(Finding.Warn(id, "unknown_entry_field",
                            "Unrecognized field '" + field.Key + "' on blessing '" + id + "' -- ignored."));
                }

                var traitIdNode = entryNode.GetMember("TraitId");
                string traitId = traitIdNode != null && traitIdNode.Kind == JsonKind.String ? traitIdNode.StringValue : null;
                if (string.IsNullOrEmpty(traitId))
                {
                    result.Findings.Add(Finding.Error(id, "trait_id_missing", "blessing '" + id + "' is missing a non-empty string 'TraitId'."));
                    continue;
                }

                var weightNode = entryNode.GetMember("Weight");
                if (weightNode == null || weightNode.Kind != JsonKind.Number)
                {
                    result.Findings.Add(Finding.Error(id, "weight_missing", "blessing '" + id + "' is missing a numeric 'Weight'."));
                    continue;
                }
                int weight;
                if (!int.TryParse(weightNode.RawNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out weight))
                {
                    result.Findings.Add(Finding.Error(id, "weight_not_integer",
                        "blessing '" + id + "' Weight '" + weightNode.RawNumber + "' is not an integer."));
                    continue;
                }
                if (weight <= 0)
                    result.Findings.Add(Finding.Warn(id, "weight_non_positive",
                        "blessing '" + id + "' Weight=" + weight.ToString(CultureInfo.InvariantCulture) +
                        " <= 0; the weighted walk applies Max(1, Weight) (mirrors EOR L832/L837) -- authoring guidance is to avoid non-positive weights (§11 OQ7)."));

                var enabledNode = entryNode.GetMember("Enabled");
                if (enabledNode == null || enabledNode.Kind != JsonKind.Bool)
                {
                    result.Findings.Add(Finding.Error(id, "enabled_missing", "blessing '" + id + "' is missing a boolean 'Enabled'."));
                    continue;
                }
                bool enabled = enabledNode.BoolValue;

                var tags = new List<string>();
                var tagsNode = entryNode.GetMember("Tags");
                if (tagsNode != null)
                {
                    if (tagsNode.Kind != JsonKind.Array)
                    {
                        result.Findings.Add(Finding.Warn(id, "tags_shape", "blessing '" + id + "' Tags is present but not an array -- ignored."));
                    }
                    else
                    {
                        foreach (var tagValue in tagsNode.ArrayItems)
                        {
                            if (tagValue.Kind == JsonKind.String) tags.Add(tagValue.StringValue);
                            else result.Findings.Add(Finding.Warn(id, "tags_entry_shape", "blessing '" + id + "' has a non-string Tags entry -- skipped."));
                        }
                    }
                }

                entries.Add(new BlessingEntry(id, traitId, weight, enabled, tags));
            }

            result.Registry = new BlessingsRegistry(schemaVersion, entries);
            return result;
        }
    }
}
