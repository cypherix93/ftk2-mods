using System.Collections.Generic;

namespace Blessings.Core.Json
{
    internal enum JsonKind
    {
        Object,
        Array,
        String,
        Number,
        Bool,
        Null
    }

    /// <summary>
    /// A parsed JSON node. Object members are kept in a <see cref="List{T}"/> of key/value pairs,
    /// NOT a <see cref="Dictionary{TKey,TValue}"/> — <c>blessings.json</c>'s authored key order is
    /// load-bearing (§3.4: "walk the roster in blessings.json authored order"), and .NET does not
    /// document <c>Dictionary&lt;,&gt;</c> enumeration order as stable across versions/platforms.
    /// </summary>
    internal sealed class JsonValue
    {
        internal JsonKind Kind;
        internal string StringValue;
        internal string RawNumber;
        internal bool BoolValue;
        internal List<KeyValuePair<string, JsonValue>> ObjectMembers;
        internal List<JsonValue> ArrayItems;

        internal static JsonValue Object(List<KeyValuePair<string, JsonValue>> members)
            => new JsonValue { Kind = JsonKind.Object, ObjectMembers = members };

        internal static JsonValue Array(List<JsonValue> items)
            => new JsonValue { Kind = JsonKind.Array, ArrayItems = items };

        internal static JsonValue String(string value)
            => new JsonValue { Kind = JsonKind.String, StringValue = value };

        internal static JsonValue Number(string raw)
            => new JsonValue { Kind = JsonKind.Number, RawNumber = raw };

        internal static JsonValue Bool(bool value)
            => new JsonValue { Kind = JsonKind.Bool, BoolValue = value };

        internal static readonly JsonValue Null = new JsonValue { Kind = JsonKind.Null };

        /// <summary>First member with this key (ordinal), or null. JSON permits duplicate object keys;
        /// "first wins" here rather than "last wins" is an arbitrary but documented choice — the parser
        /// layer above never expects a well-formed blessings.json to have duplicates in the first place.</summary>
        internal JsonValue GetMember(string key)
        {
            if (ObjectMembers == null) return null;
            for (int i = 0; i < ObjectMembers.Count; i++)
                if (string.Equals(ObjectMembers[i].Key, key, System.StringComparison.Ordinal))
                    return ObjectMembers[i].Value;
            return null;
        }
    }
}
