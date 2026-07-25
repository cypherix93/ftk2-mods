using System;

namespace FTK2Mods.DevKit.Tests
{
    internal static class ComparerTests
    {
        private const string HashA = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string HashB = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

        internal static void RunAll()
        {
            TestHarness.Section("ParityComparer verdicts");

            TestHarness.Run("verdict Match when guid/version/hash/features all agree", delegate
            {
                ParityVerdict v = One(
                    Reg("ftk2mods.warbrain", "1.0.0", HashA, new string[] { "B", "A" }),
                    Reg("ftk2mods.warbrain", "1.0.0", HashA, new string[] { "A", "B" }));
                TestHarness.EqualKind(ParityVerdictKind.Match, v.Kind, "kind");
                TestHarness.False(v.IsMismatch, "IsMismatch");
                TestHarness.True(v.Message.Contains("ftk2mods.warbrain"), "message names the mod");
            });

            TestHarness.Run("verdict VersionMismatch names the mod and both versions", delegate
            {
                ParityVerdict v = One(
                    Reg("ftk2mods.forge", "1.0.0", HashA, null),
                    Reg("ftk2mods.forge", "1.1.0", HashA, null));
                TestHarness.EqualKind(ParityVerdictKind.VersionMismatch, v.Kind, "kind");
                TestHarness.Equal("1.0.0", v.LocalValue, "local value");
                TestHarness.Equal("1.1.0", v.RemoteValue, "remote value");
                TestHarness.True(v.Message.Contains("ftk2mods.forge"), "message names the mod");
                TestHarness.True(v.Message.Contains("version diverged"), "message names the divergence kind");
                TestHarness.True(v.Message.Contains("1.0.0") && v.Message.Contains("1.1.0"), "message names both values");
                TestHarness.True(v.Message.Contains("peer-2"), "message names the peer");
            });

            TestHarness.Run("verdict DataMismatch names the mod and both hashes", delegate
            {
                ParityVerdict v = One(
                    Reg("ftk2mods.runeworks", "1.0.0", HashA, null),
                    Reg("ftk2mods.runeworks", "1.0.0", HashB, null));
                TestHarness.EqualKind(ParityVerdictKind.DataMismatch, v.Kind, "kind");
                TestHarness.Equal(HashA, v.LocalValue, "local value");
                TestHarness.Equal(HashB, v.RemoteValue, "remote value");
                TestHarness.True(v.Message.Contains("data diverged"), "message names the divergence kind");
            });

            TestHarness.Run("verdict FeaturesMismatch names both feature sets", delegate
            {
                ParityVerdict v = One(
                    Reg("ftk2mods.summoner", "1.0.0", HashA, new string[] { "Evolution" }),
                    Reg("ftk2mods.summoner", "1.0.0", HashA, new string[] { "Evolution", "Bonds" }));
                TestHarness.EqualKind(ParityVerdictKind.FeaturesMismatch, v.Kind, "kind");
                TestHarness.Equal("[Evolution]", v.LocalValue, "local value");
                TestHarness.Equal("[Bonds, Evolution]", v.RemoteValue, "remote value");
                TestHarness.True(v.Message.Contains("enabled features diverged"), "message names the divergence kind");
            });

            TestHarness.Run("verdict MissingRemote when only the local peer has the mod", delegate
            {
                ParityVerdict[] verdicts = ParityComparer.Compare(
                    new ParityRegistration[] { Reg("ftk2mods.venue", "1.0.0", HashA, null) },
                    new ParityRegistration[0], "peer-2");
                TestHarness.Equal(1, verdicts.Length, "verdict count");
                TestHarness.EqualKind(ParityVerdictKind.MissingRemote, verdicts[0].Kind, "kind");
                TestHarness.True(verdicts[0].Message.Contains("NOT installed on peer"), "message wording");
            });

            TestHarness.Run("verdict MissingLocal when only the remote peer has the mod", delegate
            {
                ParityVerdict[] verdicts = ParityComparer.Compare(
                    new ParityRegistration[0],
                    new ParityRegistration[] { Reg("ftk2mods.questsmith", "1.0.0", HashA, null) }, "peer-2");
                TestHarness.Equal(1, verdicts.Length, "verdict count");
                TestHarness.EqualKind(ParityVerdictKind.MissingLocal, verdicts[0].Kind, "kind");
                TestHarness.True(verdicts[0].Message.Contains("NOT installed locally"), "message wording");
            });

            TestHarness.Run("empty or malformed dataHash is a guaranteed mismatch, never a silent pass", delegate
            {
                ParityVerdict bothEmpty = One(
                    Reg("ftk2mods.x", "1.0.0", "", null),
                    Reg("ftk2mods.x", "1.0.0", "", null));
                TestHarness.EqualKind(ParityVerdictKind.DataMismatch, bothEmpty.Kind,
                    "two identical EMPTY hashes must still be a mismatch");
                TestHarness.True(bothEmpty.Message.Contains("missing or malformed"), "message explains why");

                ParityVerdict malformed = One(
                    Reg("ftk2mods.x", "1.0.0", "not-a-hash", null),
                    Reg("ftk2mods.x", "1.0.0", "not-a-hash", null));
                TestHarness.EqualKind(ParityVerdictKind.DataMismatch, malformed.Kind, "malformed hashes must mismatch");
            });

            TestHarness.Run("divergence precedence is version > data > features, message lists all parts", delegate
            {
                ParityVerdict v = One(
                    Reg("ftk2mods.x", "1.0.0", HashA, new string[] { "A" }),
                    Reg("ftk2mods.x", "2.0.0", HashB, new string[] { "B" }));
                TestHarness.EqualKind(ParityVerdictKind.VersionMismatch, v.Kind, "highest-precedence kind");
                TestHarness.True(v.Message.Contains("all diverging parts: version, data, features"),
                    "message must enumerate every diverging part");
            });

            TestHarness.Run("verdicts are ordered by guid and cover the union of both peers", delegate
            {
                ParityVerdict[] verdicts = ParityComparer.Compare(
                    new ParityRegistration[]
                    {
                        Reg("ftk2mods.zzz", "1", HashA, null),
                        Reg("ftk2mods.aaa", "1", HashA, null),
                    },
                    new ParityRegistration[]
                    {
                        Reg("ftk2mods.mmm", "1", HashA, null),
                        Reg("ftk2mods.aaa", "1", HashA, null),
                    }, "peer-2");
                TestHarness.Equal(3, verdicts.Length, "union size");
                TestHarness.Equal("ftk2mods.aaa", verdicts[0].Guid, "sorted[0]");
                TestHarness.Equal("ftk2mods.mmm", verdicts[1].Guid, "sorted[1]");
                TestHarness.Equal("ftk2mods.zzz", verdicts[2].Guid, "sorted[2]");
                TestHarness.Equal(2, ParityComparer.MismatchedGuids(verdicts).Length, "mismatched guids");
                TestHarness.True(ParityComparer.HasMismatch(verdicts), "HasMismatch");
            });

            TestHarness.Run("callback arg layout is [guid, kind, local, remote, peer, message]", delegate
            {
                ParityVerdict v = One(
                    Reg("ftk2mods.forge", "1.0.0", HashA, null),
                    Reg("ftk2mods.forge", "1.1.0", HashA, null));
                string[] args = v.ToCallbackArgs();
                TestHarness.Equal(6, args.Length, "arg count");
                TestHarness.Equal("ftk2mods.forge", args[0], "args[0] guid");
                TestHarness.Equal("VersionMismatch", args[1], "args[1] kind");
                TestHarness.Equal("1.0.0", args[2], "args[2] local");
                TestHarness.Equal("1.1.0", args[3], "args[3] remote");
                TestHarness.Equal("peer-2", args[4], "args[4] peer");
                TestHarness.Equal(v.Message, args[5], "args[5] message");
            });

            TestHarness.RunInCulture("comparison verdicts are unchanged under a Turkish-I culture", "tr-TR", delegate
            {
                // 'I' vs 'i' is the classic culture trap: these two feature names and versions differ
                // ONLY in case, so a culture-sensitive comparison would call them equal (or unequal)
                // depending on the peer's locale.
                ParityVerdict differsByCase = One(
                    Reg("ftk2mods.indexer", "1.0.0-i", HashA, new string[] { "Idle" }),
                    Reg("ftk2mods.indexer", "1.0.0-I", HashA, new string[] { "Idle" }));
                TestHarness.EqualKind(ParityVerdictKind.VersionMismatch, differsByCase.Kind,
                    "ordinal comparison must see a case-only version difference as a mismatch in every culture");

                ParityVerdict identical = One(
                    Reg("ftk2mods.indexer", "1.0.0-I", HashA, new string[] { "INDEX", "Idle" }),
                    Reg("ftk2mods.indexer", "1.0.0-I", HashA, new string[] { "Idle", "INDEX" }));
                TestHarness.EqualKind(ParityVerdictKind.Match, identical.Kind,
                    "identical registrations must match under tr-TR too");
            });
        }

        private static ParityVerdict One(ParityRegistration local, ParityRegistration remote)
        {
            ParityVerdict[] verdicts = ParityComparer.Compare(
                new ParityRegistration[] { local }, new ParityRegistration[] { remote }, "peer-2");
            if (verdicts.Length != 1) throw new Exception("expected exactly one verdict, got " + verdicts.Length);
            return verdicts[0];
        }

        private static ParityRegistration Reg(string guid, string version, string hash, string[] features)
        {
            return new ParityRegistration(guid, version, hash, features);
        }
    }
}
