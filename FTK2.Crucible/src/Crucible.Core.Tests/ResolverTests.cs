using System;
using System.Collections.Generic;
using System.Reflection;

namespace FTK2Mods.Crucible.Tests
{
    // ---- fakes standing in for game types (no game, no Unity) ----

    internal interface IFirst { }
    internal interface ISecond { }

    internal class FakeBase
    {
        private int _baseOnlyField = 7;
        public int ReadBaseOnlyField() { return _baseOnlyField; }
    }

    internal sealed class FakeEntity : FakeBase, IFirst, ISecond
    {
        public string Name = "goblin";
        private string BackingOnly { get { return "prop-value"; } }
        public string Guid { get { return "e-1"; } }
        public string ReadBackingOnly() { return BackingOnly; }
    }

    internal sealed class ThrowingHolder
    {
        public int Boom { get { throw new InvalidOperationException("kaboom"); } }
    }

    internal sealed class CharacterComponent { public string DisplayName = "Bard"; }
    internal class ComponentBase { }
    internal sealed class PlayerComponent : ComponentBase { }
    internal sealed class PlayerComponentExtra { }

    internal static class FakeStatics
    {
        public static int Unary(FakeBase x) { return 1; }
        public static int Unary(FakeEntity x) { return 2; }
        public static int Ambiguous(IFirst x) { return 1; }
        public static int Ambiguous(ISecond x) { return 2; }
        public static int Binary(FakeEntity x, int y) { return 3; }
        public static int Throws(FakeEntity x) { throw new InvalidOperationException("nope"); }
    }

    internal static class ResolverTests
    {
        internal static void RunAll()
        {
            TestHarness.Section("MemberResolver — members");

            TestHarness.Run("reads a public field", delegate
            {
                WarningSink w = new WarningSink();
                object v = MemberResolver.GetMember(new FakeEntity(), "Name", w);
                TestHarness.Equal("goblin", v as string, "field value");
                TestHarness.Equal(0, w.Count, "no warnings");
            });

            TestHarness.Run("falls through to a property when no field matches", delegate
            {
                WarningSink w = new WarningSink();
                object v = MemberResolver.GetMember(new FakeEntity(), "Guid", w);
                TestHarness.Equal("e-1", v as string, "property value");
                TestHarness.Equal(0, w.Count, "no warnings");
            });

            TestHarness.Run("reads a non-public property", delegate
            {
                object v = MemberResolver.GetMember(new FakeEntity(), "BackingOnly", null);
                TestHarness.Equal("prop-value", v as string, "private property");
            });

            TestHarness.Run("reads a private field declared on a base type", delegate
            {
                object v = MemberResolver.GetMember(new FakeEntity(), "_baseOnlyField", null);
                TestHarness.Equal(7, MemberResolver.AsInt(v).Value, "base-declared private field");
            });

            TestHarness.Run("a missing member warns and returns null", delegate
            {
                WarningSink w = new WarningSink();
                object v = MemberResolver.GetMember(new FakeEntity(), "PlayerCount", w);
                TestHarness.True(v == null, "null");
                TestHarness.Equal("member_missing: FakeEntity.PlayerCount", w.ToArray()[0], "loud");
            });

            // Negative control: a member that IS there must not be reported as missing.
            TestHarness.Run("an existing member emits no warning", delegate
            {
                WarningSink w = new WarningSink();
                MemberResolver.GetMember(new FakeEntity(), "Name", w);
                TestHarness.Equal(0, w.Count, "silent on success");
            });

            TestHarness.Run("a throwing property warns and returns null", delegate
            {
                WarningSink w = new WarningSink();
                object v = MemberResolver.GetMember(new ThrowingHolder(), "Boom", w);
                TestHarness.True(v == null, "null");
                TestHarness.True(w.ToArray()[0].StartsWith("member_threw: ThrowingHolder.Boom"), "loud");
            });

            TestHarness.Run("a null instance is not an error", delegate
            {
                WarningSink w = new WarningSink();
                TestHarness.True(MemberResolver.GetMember(null, "Name", w) == null, "null in, null out");
                TestHarness.Equal(0, w.Count, "no warning for an absent parent");
            });

            TestHarness.Section("MemberResolver — static overloads");

            TestHarness.Run("picks the most derived unary overload", delegate
            {
                WarningSink w = new WarningSink();
                MethodInfo m = MemberResolver.FindUnaryStatic(typeof(FakeStatics), "Unary", new FakeEntity(), w);
                TestHarness.True(m != null, "resolved");
                TestHarness.Equal("FakeEntity", m.GetParameters()[0].ParameterType.Name, "most derived wins");
                TestHarness.Equal(0, w.Count, "no warnings");
            });

            // Negative control: unrelated interface overloads are genuinely ambiguous and must refuse.
            TestHarness.Run("refuses unrelated ambiguous overloads and says so", delegate
            {
                WarningSink w = new WarningSink();
                MethodInfo m = MemberResolver.FindUnaryStatic(typeof(FakeStatics), "Ambiguous", new FakeEntity(), w);
                TestHarness.True(m == null, "refused");
                TestHarness.True(w.ToArray()[0].StartsWith("member_ambiguous: FakeStatics.Ambiguous"), "loud");
            });

            TestHarness.Run("ignores methods of the wrong arity", delegate
            {
                WarningSink w = new WarningSink();
                MethodInfo m = MemberResolver.FindUnaryStatic(typeof(FakeStatics), "Binary", new FakeEntity(), w);
                TestHarness.True(m == null, "a two-parameter overload is not unary");
                TestHarness.True(w.ToArray()[0].StartsWith("member_missing: FakeStatics.Binary"), "loud");
            });

            TestHarness.Run("reports a throwing target instead of propagating", delegate
            {
                WarningSink w = new WarningSink();
                MethodInfo m = MemberResolver.FindUnaryStatic(typeof(FakeStatics), "Throws", new FakeEntity(), w);
                object v = MemberResolver.InvokeStatic(m, new FakeEntity(), "FakeStatics.Throws", w);
                TestHarness.True(v == null, "null");
                TestHarness.True(w.ToArray()[0].StartsWith("invoke_failed: FakeStatics.Throws"), "unwrapped");
            });

            TestHarness.Section("MemberResolver — components");

            TestHarness.Run("matches a component by exact type name", delegate
            {
                object v = MemberResolver.FindComponentByTypeName(Components(), "CharacterComponent", null);
                TestHarness.True(v is CharacterComponent, "found");
            });

            TestHarness.Run("matches a component by a base type name", delegate
            {
                object v = MemberResolver.FindComponentByTypeName(Components(), "ComponentBase", null);
                TestHarness.True(v is PlayerComponent, "found via base type");
            });

            // Negative control: a similarly-named type must not satisfy the check.
            TestHarness.Run("does not match a similarly named type", delegate
            {
                Dictionary<string, object> map = new Dictionary<string, object>();
                map["x"] = new PlayerComponentExtra();
                TestHarness.True(MemberResolver.FindComponentByTypeName(map, "PlayerComponent", null) == null,
                    "PlayerComponentExtra is not PlayerComponent");
            });

            TestHarness.Run("warns when the component map is not a dictionary", delegate
            {
                WarningSink w = new WarningSink();
                TestHarness.True(MemberResolver.FindComponentByTypeName("not a map", "PlayerComponent", w) == null,
                    "null");
                TestHarness.True(w.ToArray()[0].StartsWith("note: component map unavailable"), "loud");
            });
        }

        private static Dictionary<string, object> Components()
        {
            Dictionary<string, object> map = new Dictionary<string, object>();
            map["character"] = new CharacterComponent();
            map["player"] = new PlayerComponent();
            return map;
        }
    }
}
