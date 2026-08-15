using System.Collections.Generic;

namespace FTK2Mods.Crucible.Tests
{
    internal static class DigestTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("StateDigest");

            TestHarness.Run("identical snapshots hash identically", delegate
            {
                TestHarness.Equal(StateDigest.Compute(Sample(), null), StateDigest.Compute(Sample(), null), "stable");
            });

            TestHarness.Run("key insertion order does not matter", delegate
            {
                Dictionary<string, object> a = new Dictionary<string, object>();
                a["x"] = 1; a["y"] = 2;
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["y"] = 2; b["x"] = 1;
                TestHarness.Equal(StateDigest.Compute(a, null), StateDigest.Compute(b, null), "order-independent");
            });

            TestHarness.Run("float noise below the quantum does not change the digest", delegate
            {
                Dictionary<string, object> a = new Dictionary<string, object>();
                a["hp"] = 10.000001;
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["hp"] = 10.0000012;
                TestHarness.Equal(StateDigest.Compute(a, null), StateDigest.Compute(b, null), "quantized");
            });

            TestHarness.Run("a real difference does change the digest", delegate
            {
                Dictionary<string, object> a = new Dictionary<string, object>();
                a["hp"] = 10.0;
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["hp"] = 11.0;
                TestHarness.NotEqual(StateDigest.Compute(a, null), StateDigest.Compute(b, null), "differs");
            });

            TestHarness.Run("redacted fields do not affect the digest", delegate
            {
                string[] globs = new string[] { "frame*" };
                Dictionary<string, object> a = new Dictionary<string, object>();
                a["frameCount"] = 1; a["round"] = 3;
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["frameCount"] = 99999; b["round"] = 3;
                TestHarness.Equal(StateDigest.Compute(a, globs), StateDigest.Compute(b, globs), "redacted");
            });

            TestHarness.Run("redaction applies to nested paths", delegate
            {
                string[] globs = new string[] { "combat.elapsed*" };
                Dictionary<string, object> inner1 = new Dictionary<string, object>();
                inner1["elapsedMs"] = 5; inner1["round"] = 1;
                Dictionary<string, object> a = new Dictionary<string, object>();
                a["combat"] = inner1;
                Dictionary<string, object> inner2 = new Dictionary<string, object>();
                inner2["elapsedMs"] = 900; inner2["round"] = 1;
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["combat"] = inner2;
                TestHarness.Equal(StateDigest.Compute(a, globs), StateDigest.Compute(b, globs), "nested redaction");
            });

            TestHarness.Run("redaction does not hide a real divergence elsewhere", delegate
            {
                string[] globs = new string[] { "frame*" };
                Dictionary<string, object> a = new Dictionary<string, object>();
                a["frameCount"] = 1; a["round"] = 3;
                Dictionary<string, object> b = new Dictionary<string, object>();
                b["frameCount"] = 1; b["round"] = 4;
                TestHarness.NotEqual(StateDigest.Compute(a, globs), StateDigest.Compute(b, globs), "round still counts");
            });

            TestHarness.Run("digest is well-formed", delegate
            {
                string d = StateDigest.Compute(Sample(), null);
                TestHarness.True(d.StartsWith("sha256:"), "prefix");
                TestHarness.Equal(71, d.Length, "sha256: + 64 hex");
            });

            TestHarness.Run("null snapshot does not throw", delegate
            {
                string d = StateDigest.Compute(null, null);
                TestHarness.True(d.StartsWith("sha256:"), "still a digest");
            });

            // Two peers on different locales must agree, or every session reads as a false desync.
            // The expectation is computed HERE, under the ambient culture, before the de-DE block
            // runs — otherwise the comparison would be against a value produced by the very culture
            // it is meant to catch, and the test would pass no matter what.
            Dictionary<string, object> floatSample = new Dictionary<string, object>();
            floatSample["hp"] = 10.5;
            string ambientDigest = StateDigest.Compute(floatSample, null);

            TestHarness.RunInCulture("digest is culture-invariant", "de-DE", delegate
            {
                Dictionary<string, object> a = new Dictionary<string, object>();
                a["hp"] = 10.5;
                TestHarness.Equal(ambientDigest, StateDigest.Compute(a, null), "same digest under de-DE");
            });

            TestHarness.RunInCulture("digest is culture-invariant under Turkish casing", "tr-TR", delegate
            {
                Dictionary<string, object> a = new Dictionary<string, object>();
                a["hp"] = 10.5;
                TestHarness.Equal(ambientDigest, StateDigest.Compute(a, null), "same digest under tr-TR");
            });
        }

        private static Dictionary<string, object> Sample()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["round"] = 3;
            d["partyAlive"] = true;
            d["name"] = "hero";
            return d;
        }
    }
}
