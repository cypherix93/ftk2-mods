using System.Collections.Generic;

namespace FTK2Mods.Crucible.Tests
{
    internal static class JsonTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("MiniJson");

            TestHarness.Run("Write emits sorted keys for determinism", delegate
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["zebra"] = 1;
                d["alpha"] = true;
                TestHarness.Equal("{\"alpha\":true,\"zebra\":1}", MiniJson.Write(d), "sorted object");
            });

            TestHarness.Run("Write round-trips through TryParse", delegate
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["name"] = "EndPhase";
                d["args"] = new List<object> { "a", "b" };
                object parsed;
                string error;
                TestHarness.True(MiniJson.TryParse(MiniJson.Write(d), out parsed, out error), "parse ok: " + error);
                TestHarness.Equal("EndPhase", MiniJson.AsString(MiniJson.AsObject(parsed)["name"]), "name");
                TestHarness.Equal(2, MiniJson.AsArray(MiniJson.AsObject(parsed)["args"]).Count, "args count");
            });

            TestHarness.Run("Write escapes quotes and newlines", delegate
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["k"] = "a\"b\nc";
                TestHarness.Equal("{\"k\":\"a\\\"b\\nc\"}", MiniJson.Write(d), "escaping");
            });

            TestHarness.Run("Write handles null and nested structures", delegate
            {
                Dictionary<string, object> inner = new Dictionary<string, object>();
                inner["deep"] = null;
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["outer"] = inner;
                d["list"] = new List<object> { 1, true, null };
                TestHarness.Equal("{\"list\":[1,true,null],\"outer\":{\"deep\":null}}", MiniJson.Write(d), "nested");
            });

            // Invariant-culture guard: a comma-decimal locale must not corrupt the digest input.
            TestHarness.RunInCulture("Write formats doubles invariantly", "de-DE", delegate
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["v"] = 1.5;
                TestHarness.Equal("{\"v\":1.5}", MiniJson.Write(d), "invariant double");
            });
        }
    }
}
