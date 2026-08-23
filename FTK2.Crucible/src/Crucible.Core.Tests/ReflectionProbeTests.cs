using System;

namespace FTK2Mods.Crucible.Tests
{
    internal static class ReflectionProbeTests
    {
        private enum SampleEnum
        {
            Alpha,
            Beta
        }

        internal static void RunAll()
        {

            TestHarness.Run("object parameter accepts the null tokens", delegate
            {
                object v; string err;
                TestHarness.True(ArgCoercion.TryCoerce("null", typeof(object), out v, out err), "'null' must coerce: " + err);
                TestHarness.True(v == null, "'null' must yield a null value");
                TestHarness.True(ArgCoercion.TryCoerce("-", typeof(object), out v, out err), "'-' must coerce: " + err);
                TestHarness.True(v == null, "'-' must yield a null value");
            });

            // NEGATIVE CONTROL: a non-null string must be REFUSED for an object parameter. Passing it
            // through as a string would appear to work and then diverge from the real call site.
            TestHarness.Run("NEGATIVE: object parameter refuses a non-null value", delegate
            {
                object v; string err;
                bool ok = ArgCoercion.TryCoerce("someValue", typeof(object), out v, out err);
                TestHarness.True(!ok, "a non-null string must not coerce to an object parameter");
                TestHarness.True(err != null && err.Length > 0, "refusal must explain itself");
            });

            TestHarness.Section("PathParser.TryParse");

            TestHarness.Run("parses a single segment", delegate
            {
                string[] segments; string e;
                TestHarness.True(PathParser.TryParse("RouterMono", out segments, out e), "parsed: " + e);
                TestHarness.Equal(1, segments.Length, "segment count");
                TestHarness.Equal("RouterMono", segments[0], "segment 0");
            });

            TestHarness.Run("parses a.b.c into three segments", delegate
            {
                string[] segments; string e;
                TestHarness.True(PathParser.TryParse("RouterHelper.Env.GameRuns", out segments, out e), "parsed: " + e);
                TestHarness.Equal(3, segments.Length, "segment count");
                TestHarness.Equal("RouterHelper", segments[0], "segment 0");
                TestHarness.Equal("Env", segments[1], "segment 1");
                TestHarness.Equal("GameRuns", segments[2], "segment 2");
            });

            TestHarness.Run("parses a deep path", delegate
            {
                string[] segments; string e;
                TestHarness.True(PathParser.TryParse("RouterHelper.Env.NetworkData.IsHost", out segments, out e), "parsed: " + e);
                TestHarness.Equal(4, segments.Length, "segment count");
            });

            TestHarness.Run("rejects an empty path", delegate
            {
                string[] segments; string e;
                TestHarness.False(PathParser.TryParse("", out segments, out e), "should reject");
                TestHarness.True(e != null && e.Length > 0, "error message present");
            });

            TestHarness.Run("rejects null path", delegate
            {
                string[] segments; string e;
                TestHarness.False(PathParser.TryParse(null, out segments, out e), "should reject");
            });

            TestHarness.Run("rejects a whitespace-only path", delegate
            {
                string[] segments; string e;
                TestHarness.False(PathParser.TryParse("   ", out segments, out e), "should reject");
            });

            TestHarness.Run("rejects an empty segment in the middle (a..b)", delegate
            {
                string[] segments; string e;
                TestHarness.False(PathParser.TryParse("a..b", out segments, out e), "should reject");
                TestHarness.True(e.IndexOf("index 1", StringComparison.Ordinal) >= 0, "error names the segment index: " + e);
            });

            TestHarness.Run("rejects a leading dot", delegate
            {
                string[] segments; string e;
                TestHarness.False(PathParser.TryParse(".RouterMono", out segments, out e), "should reject");
            });

            TestHarness.Run("rejects a trailing dot", delegate
            {
                string[] segments; string e;
                TestHarness.False(PathParser.TryParse("RouterMono.", out segments, out e), "should reject");
            });

            TestHarness.Section("ArgCoercion.TryCoerce");

            TestHarness.Run("coerces string to string", delegate
            {
                object value; string e;
                TestHarness.True(ArgCoercion.TryCoerce("hello", typeof(string), out value, out e), "coerced: " + e);
                TestHarness.Equal("hello", (string)value, "value");
            });

            TestHarness.Run("coerces a valid int", delegate
            {
                object value; string e;
                TestHarness.True(ArgCoercion.TryCoerce("42", typeof(int), out value, out e), "coerced: " + e);
                TestHarness.Equal(42, (int)value, "value");
            });

            TestHarness.Run("coerces a valid float", delegate
            {
                object value; string e;
                TestHarness.True(ArgCoercion.TryCoerce("3.5", typeof(float), out value, out e), "coerced: " + e);
                TestHarness.True(Math.Abs((float)value - 3.5f) < 0.0001f, "value close to 3.5");
            });

            TestHarness.Run("coerces a valid double", delegate
            {
                object value; string e;
                TestHarness.True(ArgCoercion.TryCoerce("2.25", typeof(double), out value, out e), "coerced: " + e);
                TestHarness.True(Math.Abs((double)value - 2.25) < 0.0001, "value close to 2.25");
            });

            TestHarness.Run("coerces a valid bool", delegate
            {
                object value; string e;
                TestHarness.True(ArgCoercion.TryCoerce("true", typeof(bool), out value, out e), "coerced: " + e);
                TestHarness.Equal(true, (bool)value, "value");
            });

            TestHarness.Run("coerces an enum by name, case-insensitively", delegate
            {
                object value; string e;
                TestHarness.True(ArgCoercion.TryCoerce("beta", typeof(SampleEnum), out value, out e), "coerced: " + e);
                TestHarness.True(SampleEnum.Beta.Equals(value), "value is Beta");
            });

            TestHarness.Run("rejects a non-numeric string to int (does not silently yield 0)", delegate
            {
                object value; string e;
                TestHarness.False(ArgCoercion.TryCoerce("not-a-number", typeof(int), out value, out e), "should fail");
                TestHarness.True(value == null, "value stays null on failure");
                TestHarness.True(e != null && e.Length > 0, "error message present");
            });

            TestHarness.Run("rejects a non-numeric string to float", delegate
            {
                object value; string e;
                TestHarness.False(ArgCoercion.TryCoerce("abc", typeof(float), out value, out e), "should fail");
            });

            TestHarness.Run("rejects an invalid bool", delegate
            {
                object value; string e;
                TestHarness.False(ArgCoercion.TryCoerce("maybe", typeof(bool), out value, out e), "should fail");
            });

            TestHarness.Run("rejects an invalid enum value", delegate
            {
                object value; string e;
                TestHarness.False(ArgCoercion.TryCoerce("Gamma", typeof(SampleEnum), out value, out e), "should fail");
            });

            TestHarness.Run("rejects a null target type", delegate
            {
                object value; string e;
                TestHarness.False(ArgCoercion.TryCoerce("1", null, out value, out e), "should fail");
            });

            TestHarness.Run("rejects a null raw value", delegate
            {
                object value; string e;
                TestHarness.False(ArgCoercion.TryCoerce(null, typeof(int), out value, out e), "should fail");
            });

            TestHarness.Run("rejects an unsupported target type", delegate
            {
                object value; string e;
                TestHarness.False(ArgCoercion.TryCoerce("x", typeof(DateTime), out value, out e), "should fail");
            });

            TestHarness.Run("CancellationToken parameter accepts '-' and yields CancellationToken.None", delegate
            {
                object value; string e;
                TestHarness.True(ArgCoercion.TryCoerce("-", typeof(System.Threading.CancellationToken), out value, out e), "coerced: " + e);
                TestHarness.True(value is System.Threading.CancellationToken, "value is a CancellationToken");
                TestHarness.True(((System.Threading.CancellationToken)value) == System.Threading.CancellationToken.None, "value is CancellationToken.None");
            });

            // NEGATIVE CONTROL: a CancellationToken parameter must REFUSE any value other than the
            // placeholder. There is no string that could legitimately change what gets passed, so a
            // caller typing something that looks meaningful must be told it does nothing, not have it
            // silently accepted and ignored.
            TestHarness.Run("NEGATIVE: CancellationToken parameter refuses a non-placeholder value", delegate
            {
                object value; string e;
                bool ok = ArgCoercion.TryCoerce("someToken", typeof(System.Threading.CancellationToken), out value, out e);
                TestHarness.True(!ok, "a non-'-' string must not coerce to a CancellationToken parameter");
                TestHarness.True(e != null && e.Length > 0, "refusal must explain itself");
            });
        }
    }
}
