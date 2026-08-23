using System;

namespace FTK2Mods.Crucible.Tests
{
    internal static class MemberAccessTests
    {
        internal class SamplePoco
        {
            public int PublicField = 5;
            public string PublicProp { get; set; } = "hi";
            public readonly int ReadOnlyField = 7;
            public int GetOnlyProp { get { return 9; } }
            public static int StaticField = 3;
            private int _privateField = 1;
        }

        internal static void RunAll()
        {
            TestHarness.Section("MemberAccess.TryResolveMember");

            TestHarness.Run("resolves a public instance field", delegate
            {
                MemberInfoResult r = Resolve(typeof(SamplePoco), "PublicField");
                TestHarness.True(r.Ok, "resolved: " + r.Error);
                TestHarness.False(r.IsStatic, "not static");
                TestHarness.False(r.IsReadOnly, "not read-only");
                TestHarness.Equal("Int32", r.MemberType.Name, "member type");
            });

            TestHarness.Run("resolves a public instance property", delegate
            {
                MemberInfoResult r = Resolve(typeof(SamplePoco), "PublicProp");
                TestHarness.True(r.Ok, "resolved: " + r.Error);
                TestHarness.False(r.IsReadOnly, "has a setter");
            });

            TestHarness.Run("resolves a private field", delegate
            {
                MemberInfoResult r = Resolve(typeof(SamplePoco), "_privateField");
                TestHarness.True(r.Ok, "resolved: " + r.Error);
            });

            TestHarness.Run("resolves a static field as static", delegate
            {
                MemberInfoResult r = Resolve(typeof(SamplePoco), "StaticField");
                TestHarness.True(r.Ok, "resolved: " + r.Error);
                TestHarness.True(r.IsStatic, "is static");
            });

            TestHarness.Run("flags a readonly field as read-only", delegate
            {
                MemberInfoResult r = Resolve(typeof(SamplePoco), "ReadOnlyField");
                TestHarness.True(r.Ok, "resolved: " + r.Error);
                TestHarness.True(r.IsReadOnly, "readonly field is read-only");
            });

            TestHarness.Run("flags a get-only property as read-only", delegate
            {
                MemberInfoResult r = Resolve(typeof(SamplePoco), "GetOnlyProp");
                TestHarness.True(r.Ok, "resolved: " + r.Error);
                TestHarness.True(r.IsReadOnly, "get-only property is read-only");
            });

            TestHarness.Run("fails to resolve an unknown member", delegate
            {
                MemberInfoResult r = Resolve(typeof(SamplePoco), "DoesNotExist");
                TestHarness.False(r.Ok, "should fail");
                TestHarness.True(r.Error != null && r.Error.Length > 0, "error present");
            });

            TestHarness.Run("fails on a null owner type", delegate
            {
                MemberInfoResult r = Resolve(null, "PublicField");
                TestHarness.False(r.Ok, "should fail");
            });

            TestHarness.Run("fails on an empty member name", delegate
            {
                MemberInfoResult r = Resolve(typeof(SamplePoco), "");
                TestHarness.False(r.Ok, "should fail");
            });

            TestHarness.Section("MemberAccess.TryGetValue / TrySetValue");

            TestHarness.Run("gets and sets a public instance field", delegate
            {
                SamplePoco poco = new SamplePoco();
                MemberInfoResult r = Resolve(typeof(SamplePoco), "PublicField");
                object oldValue; string e;
                TestHarness.True(MemberAccess.TryGetValue(r.Member, poco, r.IsStatic, out oldValue, out e), "get: " + e);
                TestHarness.Equal(5, (int)oldValue, "old value");

                TestHarness.True(MemberAccess.TrySetValue(r.Member, poco, r.IsStatic, r.IsReadOnly, 42, out e), "set: " + e);
                TestHarness.Equal(42, poco.PublicField, "new value applied");
            });

            TestHarness.Run("gets and sets a public instance property", delegate
            {
                SamplePoco poco = new SamplePoco();
                MemberInfoResult r = Resolve(typeof(SamplePoco), "PublicProp");
                string e;
                TestHarness.True(MemberAccess.TrySetValue(r.Member, poco, r.IsStatic, r.IsReadOnly, "bye", out e), "set: " + e);
                TestHarness.Equal("bye", poco.PublicProp, "new value applied");
            });

            TestHarness.Run("refuses to set a read-only field (does not silently no-op)", delegate
            {
                SamplePoco poco = new SamplePoco();
                MemberInfoResult r = Resolve(typeof(SamplePoco), "ReadOnlyField");
                string e;
                TestHarness.False(MemberAccess.TrySetValue(r.Member, poco, r.IsStatic, r.IsReadOnly, 99, out e), "should fail");
                TestHarness.True(e != null && e.Length > 0, "error present");
                TestHarness.Equal(7, poco.ReadOnlyField, "value unchanged");
            });

            TestHarness.Run("refuses to set a get-only property", delegate
            {
                SamplePoco poco = new SamplePoco();
                MemberInfoResult r = Resolve(typeof(SamplePoco), "GetOnlyProp");
                string e;
                TestHarness.False(MemberAccess.TrySetValue(r.Member, poco, r.IsStatic, r.IsReadOnly, 1, out e), "should fail");
            });

            TestHarness.Run("gets and sets a static field", delegate
            {
                MemberInfoResult r = Resolve(typeof(SamplePoco), "StaticField");
                object oldValue; string e;
                TestHarness.True(MemberAccess.TryGetValue(r.Member, null, r.IsStatic, out oldValue, out e), "get: " + e);
                TestHarness.Equal(3, (int)oldValue, "old value");

                TestHarness.True(MemberAccess.TrySetValue(r.Member, null, r.IsStatic, r.IsReadOnly, 11, out e), "set: " + e);
                TestHarness.Equal(11, SamplePoco.StaticField, "new value applied");
                SamplePoco.StaticField = 3; // restore for test isolation
            });
        }

        private struct MemberInfoResult
        {
            internal bool Ok;
            internal System.Reflection.MemberInfo Member;
            internal Type MemberType;
            internal bool IsStatic;
            internal bool IsReadOnly;
            internal string Error;
        }

        private static MemberInfoResult Resolve(Type ownerType, string memberName)
        {
            System.Reflection.MemberInfo member; Type memberType; bool isStatic; bool isReadOnly; string error;
            bool ok = MemberAccess.TryResolveMember(ownerType, memberName, out member, out memberType, out isStatic, out isReadOnly, out error);
            MemberInfoResult r = new MemberInfoResult();
            r.Ok = ok;
            r.Member = member;
            r.MemberType = memberType;
            r.IsStatic = isStatic;
            r.IsReadOnly = isReadOnly;
            r.Error = error;
            return r;
        }
    }
}
