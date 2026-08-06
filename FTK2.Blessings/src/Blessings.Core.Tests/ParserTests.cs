using System.Linq;
using Blessings.Core.Diagnostics;
using Blessings.Core.Parsing;

namespace Blessings.Core.Tests
{
    public static class ParserTests
    {
        private const string Minimal = @"{
          ""SchemaVersion"": ""1.0"",
          ""Blessings"": {
            ""BLSS_A"": { ""TraitId"": ""TRAIT_BLSS_A"", ""Weight"": 6, ""Enabled"": true, ""Tags"": [""X""], ""_eor_source"": ""L1"" },
            ""BLSS_B"": { ""TraitId"": ""TRAIT_BLSS_B"", ""Weight"": 10, ""Enabled"": false, ""Tags"": [] }
          }
        }";

        public static void ParsesMinimalRegistry_PreservesAuthoredOrder()
        {
            var result = BlessingsRegistryParser.Parse(Minimal);
            Check.True(result.Success, "minimal registry should parse");
            Check.Equal("1.0", result.Registry.SchemaVersion, "SchemaVersion");
            Check.Equal(2, result.Registry.Blessings.Count, "entry count");
            Check.SequenceEqual(new[] { "BLSS_A", "BLSS_B" }, result.Registry.Blessings.Select(b => b.Id), "authored order");
            Check.Equal("TRAIT_BLSS_A", result.Registry.Blessings[0].TraitId, "TraitId");
            Check.Equal(6, result.Registry.Blessings[0].Weight, "Weight");
            Check.True(result.Registry.Blessings[0].Enabled, "Enabled true");
            Check.False(result.Registry.Blessings[1].Enabled, "Enabled false");
        }

        public static void UnknownTopLevelField_ProducesWarning_NotError()
        {
            const string json = @"{ ""SchemaVersion"": ""1.0"", ""Blessings"": {}, ""RogueField"": 1 }";
            var result = BlessingsRegistryParser.Parse(json);
            Check.True(result.Success, "should still parse");
            Check.Contains(result.Findings, f => f.Severity == FindingSeverity.Warn && f.Check == "unknown_top_level_field",
                "expected an unknown_top_level_field warning");
        }

        public static void UnknownEntryField_ProducesWarning_NotError()
        {
            const string json = @"{
              ""SchemaVersion"": ""1.0"",
              ""Blessings"": { ""BLSS_A"": { ""TraitId"": ""TRAIT_BLSS_A"", ""Weight"": 1, ""Enabled"": true, ""RogueField"": true } }
            }";
            var result = BlessingsRegistryParser.Parse(json);
            Check.True(result.Success, "should still parse");
            Check.Contains(result.Findings, f => f.Severity == FindingSeverity.Warn && f.Check == "unknown_entry_field",
                "expected an unknown_entry_field warning");
            Check.Equal(1, result.Registry.Blessings.Count, "entry with an unknown field is still kept");
        }

        public static void EorSourceField_IsKnown_NoWarning()
        {
            var result = BlessingsRegistryParser.Parse(Minimal);
            Check.DoesNotContain(result.Findings, f => f.Check == "unknown_entry_field", "_eor_source must not warn -- it's an authored, allowed field");
        }

        public static void NonPositiveWeight_WarnsButIsAcceptedVerbatim()
        {
            const string json = @"{
              ""SchemaVersion"": ""1.0"",
              ""Blessings"": { ""BLSS_ZERO"": { ""TraitId"": ""TRAIT_BLSS_ZERO"", ""Weight"": 0, ""Enabled"": true } }
            }";
            var result = BlessingsRegistryParser.Parse(json);
            Check.True(result.Success, "should still parse");
            Check.Contains(result.Findings, f => f.Severity == FindingSeverity.Warn && f.Check == "weight_non_positive",
                "expected a weight_non_positive warning");
            Check.Equal(0, result.Registry.Blessings[0].Weight, "raw Weight stays 0 on the model -- clamping is the resolver's job");
        }

        public static void MalformedJson_ReturnsNoRegistry_WithErrorFinding()
        {
            var result = BlessingsRegistryParser.Parse("{ this is not json");
            Check.False(result.Success, "malformed JSON must not produce a registry");
            Check.Contains(result.Findings, f => f.Severity == FindingSeverity.Error, "expected an error finding");
        }

        public static void MissingRequiredField_DropsEntry_ButKeepsRestOfRegistry()
        {
            const string json = @"{
              ""SchemaVersion"": ""1.0"",
              ""Blessings"": {
                ""BLSS_BAD"": { ""Weight"": 1, ""Enabled"": true },
                ""BLSS_GOOD"": { ""TraitId"": ""TRAIT_BLSS_GOOD"", ""Weight"": 1, ""Enabled"": true }
              }
            }";
            var result = BlessingsRegistryParser.Parse(json);
            Check.True(result.Success, "the file as a whole still parses");
            Check.Equal(1, result.Registry.Blessings.Count, "only the valid entry survives");
            Check.Equal("BLSS_GOOD", result.Registry.Blessings[0].Id, "the good entry is the one kept");
            Check.Contains(result.Findings, f => f.Severity == FindingSeverity.Error && f.Check == "trait_id_missing", "expected trait_id_missing error");
        }
    }
}
