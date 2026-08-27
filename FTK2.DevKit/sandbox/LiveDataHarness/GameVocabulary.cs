using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace LiveDataHarness
{
    /// <summary>
    /// The allowlist that separates "this string is a content id that must resolve" from "this string is an
    /// enum member or a closed-vocabulary value". Measured against the live install 2026-08-23:
    /// CharacterConfig.BaseType has 32 distinct values, DefaultBodyType exactly two ("F","M"), character
    /// Tags 483 distinct, Thing Tags 496 distinct; FTK2.dll declares 395 enum types / 4182 member names.
    /// Checking BaseType against Configs.Characters instead produced 31 false findings (2026-08-08).
    /// </summary>
    public sealed class GameVocabulary
    {
        public ISet<string> EnumMembers { get; private set; }
        public ISet<string> BaseTypes { get; private set; }
        public ISet<string> BodyTypes { get; private set; }
        public ISet<string> CharacterTags { get; private set; }
        public ISet<string> ThingTags { get; private set; }
        public ISet<string> ThingClasses { get; private set; }
        public ISet<string> Rarities { get; private set; }
        public ISet<string> Materials { get; private set; }
        public ISet<string> ConsumableTypes { get; private set; }
        public ISet<string> StatusTypes { get; private set; }
        public ISet<string> Expansions { get; private set; }
        /// <summary>Every member name of the vanilla eStatusEffectsGroups enum. A custom status id must start
        /// with one of these or the game renders it nowhere — the "invisible status" failure class.</summary>
        public ISet<string> StatusGroups { get; private set; }

        public static GameVocabulary Build(GameData data)
        {
            HashSet<string> enums = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> statusGroups = new HashSet<string>(StringComparer.Ordinal);
            Assembly ftk2 = data.Configs.GetType().Assembly;
            foreach (Type t in SafeGetTypes(ftk2))
            {
                if (t == null || !t.IsEnum) continue;
                bool isStatusGroups = string.Equals(t.Name, "eStatusEffectsGroups", StringComparison.Ordinal);
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Static))
                {
                    enums.Add(f.Name);
                    if (isStatusGroups) statusGroups.Add(f.Name);
                }
            }

            HashSet<string> baseTypes = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> bodyTypes = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> charTags = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> expansions = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> charRarities = new HashSet<string>(StringComparer.Ordinal);

            foreach (object ch in data.Values("Characters"))
            {
                AddString(baseTypes, GameData.Field(ch, "BaseType"));
                AddString(bodyTypes, GameData.Field(ch, "DefaultBodyType"));
                AddAll(charTags, GameData.Field(ch, "Tags"));
                // Rarity and Expansion are enum-typed on CharacterConfig (eItemRarities / eExpansions);
                // their boxed values stringify to the member name, so learning them here gives one
                // vocabulary per field family without needing the enum type itself.
                AddString(expansions, GameData.Field(ch, "Expansion"));
                AddString(charRarities, GameData.Field(ch, "Rarity"));
            }

            HashSet<string> thingTags = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> thingClasses = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> rarities = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> materials = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> consumables = new HashSet<string>(StringComparer.Ordinal);

            foreach (object th in data.Values("Things"))
            {
                AddAll(thingTags, GameData.Field(th, "Tags"));
                AddString(thingClasses, GameData.Field(th, "Class"));
                AddString(rarities, GameData.Field(th, "Rarity"));
                AddString(materials, GameData.Field(th, "Material"));
                AddString(consumables, GameData.Field(th, "ConsumableType"));
            }

            HashSet<string> statusTypes = new HashSet<string>(StringComparer.Ordinal);
            foreach (object st in data.Values("StatusEffects"))
                AddString(statusTypes, GameData.Field(st, "Type"));

            rarities.UnionWith(charRarities);

            GameVocabulary v = new GameVocabulary();
            v.EnumMembers = enums;
            v.BaseTypes = baseTypes;
            v.BodyTypes = bodyTypes;
            v.CharacterTags = charTags;
            v.ThingTags = thingTags;
            v.ThingClasses = thingClasses;
            v.Rarities = rarities;
            v.Materials = materials;
            v.ConsumableTypes = consumables;
            v.StatusTypes = statusTypes;
            v.Expansions = expansions;
            v.StatusGroups = statusGroups;
            return v;
        }

        private static void AddString(HashSet<string> target, object value)
        {
            if (value == null) return;
            string s = value as string;
            if (s == null) s = value.ToString();
            if (!string.IsNullOrEmpty(s)) target.Add(s);
        }

        private static void AddAll(HashSet<string> target, object listValue)
        {
            IEnumerable list = listValue as IEnumerable;
            if (list == null) return;
            foreach (object item in list) AddString(target, item);
        }

        /// <summary>FTK2.dll references assemblies that may not resolve outside the player (editor-only
        /// modules); a partial type list is fine for enum harvesting, a thrown
        /// ReflectionTypeLoadException is not. Same posture as TypeProbe.GameAssembly.</summary>
        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(delegate (Type t) { return t != null; }); }
        }
    }
}
