using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LiveDataHarness
{
    /// <summary>
    /// The allowlist that separates "this string is a content id that must resolve" from "this string is an
    /// enum member or a closed-vocabulary value".
    ///
    /// Without it a reference checker reports every enum-valued JSON field (Rarity: "COMMON", Tags:
    /// ["RANGED"]) as a dangling id — measured at 131 false findings against the shipped packs.
    ///
    /// EnumMembers comes from FTK2.dll's own enum types. BaseTypes, BodyTypes and CharacterTags are instead
    /// *learned* from vanilla CharacterConfig values, because those fields are String/List&lt;string&gt; with a
    /// closed de-facto vocabulary rather than real enums — there is no type to reflect over, so the shipped
    /// content is the only authority on what a legal value looks like.
    /// </summary>
    public sealed class GameVocabulary
    {
        public ISet<string> EnumMembers { get; private set; }
        public ISet<string> BaseTypes { get; private set; }
        public ISet<string> BodyTypes { get; private set; }
        public ISet<string> CharacterTags { get; private set; }

        public static GameVocabulary Build(GameData data)
        {
            var enums = new HashSet<string>(StringComparer.Ordinal);
            var ftk2 = data.Configs.GetType().Assembly;
            foreach (var t in SafeGetTypes(ftk2))
            {
                if (t == null || !t.IsEnum) continue;
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                    enums.Add(f.Name);
            }

            var baseTypes = new HashSet<string>(StringComparer.Ordinal);
            var bodyTypes = new HashSet<string>(StringComparer.Ordinal);
            var tags = new HashSet<string>(StringComparer.Ordinal);

            foreach (var ch in data.Values("Characters"))
            {
                var bt = GameData.Field(ch, "BaseType") as string;
                if (!string.IsNullOrEmpty(bt)) baseTypes.Add(bt);

                var dbt = GameData.Field(ch, "DefaultBodyType") as string;
                if (!string.IsNullOrEmpty(dbt)) bodyTypes.Add(dbt);

                var tagList = GameData.Field(ch, "Tags") as IEnumerable;
                if (tagList != null)
                    foreach (var tg in tagList) { if (tg != null) tags.Add(tg.ToString()); }
            }

            return new GameVocabulary
            {
                EnumMembers = enums,
                BaseTypes = baseTypes,
                BodyTypes = bodyTypes,
                CharacterTags = tags,
            };
        }

        /// <summary>
        /// FTK2.dll references assemblies that need not resolve in a headless process (editor-only modules
        /// among them). A partial type list is fine for harvesting enum names; a thrown
        /// ReflectionTypeLoadException that aborts the whole build is not.
        /// </summary>
        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null); }
        }
    }
}
