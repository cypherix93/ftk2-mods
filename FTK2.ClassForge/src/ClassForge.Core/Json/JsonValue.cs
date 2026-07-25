using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ClassForge.Core.Json
{
    /// <summary>Discriminant for <see cref="JsonValue"/>.</summary>
    public enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// A minimal, allocation-light JSON value tree. Hand-rolled (see JsonParser) because ClassForge.Core
    /// must build with zero NuGet packages and zero game references, so the game's own JsonHelper /
    /// System.Text.Json is not available here (that only exists in the Plugin's game-referenced world).
    /// Object member order is preserved as parsed, but every consumer in this codebase re-sorts by key
    /// before using order for anything observable (SPEC/MULTIPLAYER.md R2 — never trust incidental order).
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind { get; }

        private readonly bool _bool;
        private readonly double _number;
        private readonly string _string;
        private readonly List<JsonValue> _array;
        private readonly List<KeyValuePair<string, JsonValue>> _object;

        private static readonly JsonValue NullInstance = new JsonValue(JsonKind.Null, false, 0, null, null, null);
        private static readonly JsonValue TrueInstance = new JsonValue(JsonKind.Bool, true, 0, null, null, null);
        private static readonly JsonValue FalseInstance = new JsonValue(JsonKind.Bool, false, 0, null, null, null);

        private JsonValue(JsonKind kind, bool b, double n, string s, List<JsonValue> a, List<KeyValuePair<string, JsonValue>> o)
        {
            Kind = kind;
            _bool = b;
            _number = n;
            _string = s;
            _array = a;
            _object = o;
        }

        public static JsonValue Null => NullInstance;
        public static JsonValue NewBool(bool b) => b ? TrueInstance : FalseInstance;
        public static JsonValue NewNumber(double d) => new JsonValue(JsonKind.Number, false, d, null, null, null);
        public static JsonValue NewString(string s) => new JsonValue(JsonKind.String, false, 0, s ?? string.Empty, null, null);
        public static JsonValue NewArray(List<JsonValue> items) => new JsonValue(JsonKind.Array, false, 0, null, items ?? new List<JsonValue>(), null);
        public static JsonValue NewObject(List<KeyValuePair<string, JsonValue>> members) => new JsonValue(JsonKind.Object, false, 0, null, null, members ?? new List<KeyValuePair<string, JsonValue>>());

        public bool IsNull => Kind == JsonKind.Null;

        public bool AsBool => Kind == JsonKind.Bool && _bool;
        public double AsNumber => Kind == JsonKind.Number ? _number : 0;
        public string AsString => Kind == JsonKind.String ? _string : null;
        public IReadOnlyList<JsonValue> AsArray => Kind == JsonKind.Array ? _array : EmptyArray;
        public IReadOnlyList<KeyValuePair<string, JsonValue>> AsObjectMembers => Kind == JsonKind.Object ? _object : EmptyObject;

        private static readonly List<JsonValue> EmptyArray = new List<JsonValue>();
        private static readonly List<KeyValuePair<string, JsonValue>> EmptyObject = new List<KeyValuePair<string, JsonValue>>();

        /// <summary>Object member lookup, last-wins on duplicate keys (matches JSON parse semantics). Returns Null (not C# null) if absent or not an object.</summary>
        public JsonValue Get(string key)
        {
            if (Kind != JsonKind.Object || key == null) return Null;
            JsonValue found = null;
            for (int i = 0; i < _object.Count; i++)
            {
                if (string.Equals(_object[i].Key, key, StringComparison.Ordinal))
                    found = _object[i].Value;
            }
            return found ?? Null;
        }

        public bool TryGet(string key, out JsonValue value)
        {
            value = Get(key);
            return !value.IsNull || (Kind == JsonKind.Object && HasKey(key));
        }

        private bool HasKey(string key)
        {
            if (Kind != JsonKind.Object) return false;
            for (int i = 0; i < _object.Count; i++)
                if (string.Equals(_object[i].Key, key, StringComparison.Ordinal)) return true;
            return false;
        }

        public string GetString(string key, string defaultValue = null)
        {
            var v = Get(key);
            return v.Kind == JsonKind.String ? v.AsString : defaultValue;
        }

        public double GetNumber(string key, double defaultValue = 0)
        {
            var v = Get(key);
            return v.Kind == JsonKind.Number ? v.AsNumber : defaultValue;
        }

        public bool GetBool(string key, bool defaultValue = false)
        {
            var v = Get(key);
            return v.Kind == JsonKind.Bool ? v.AsBool : defaultValue;
        }

        public List<string> GetStringArray(string key)
        {
            var v = Get(key);
            if (v.Kind != JsonKind.Array) return new List<string>();
            return v.AsArray.Where(x => x.Kind == JsonKind.String).Select(x => x.AsString).ToList();
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case JsonKind.Null: return "null";
                case JsonKind.Bool: return _bool ? "true" : "false";
                case JsonKind.Number: return _number.ToString(CultureInfo.InvariantCulture);
                case JsonKind.String: return "\"" + _string + "\"";
                case JsonKind.Array: return "[" + _array.Count + " items]";
                case JsonKind.Object: return "{" + _object.Count + " members}";
                default: return "?";
            }
        }

        /// <summary>
        /// Serializes back to compact, strictly-valid JSON text — used by ClassForge.Plugin to hand a merged
        /// entry to the game's own <c>System.Text.Json</c> deserializer (Core itself never depends on it).
        /// Whole-valued doubles are written without a trailing ".0" so integer-typed game fields (e.g.
        /// <c>CharacterConfig.Level</c>) round-trip cleanly through System.Text.Json's strict-by-default
        /// number handling.
        /// </summary>
        public string ToJsonString()
        {
            var sb = new StringBuilder();
            WriteTo(sb);
            return sb.ToString();
        }

        private void WriteTo(StringBuilder sb)
        {
            switch (Kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(_bool ? "true" : "false");
                    break;
                case JsonKind.Number:
                    sb.Append(FormatNumber(_number));
                    break;
                case JsonKind.String:
                    WriteString(sb, _string);
                    break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        _array[i].WriteTo(sb);
                    }
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    sb.Append('{');
                    for (int i = 0; i < _object.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteString(sb, _object[i].Key);
                        sb.Append(':');
                        _object[i].Value.WriteTo(sb);
                    }
                    sb.Append('}');
                    break;
            }
        }

        private static string FormatNumber(double d)
        {
            if (!double.IsNaN(d) && !double.IsInfinity(d) && d == Math.Truncate(d) && Math.Abs(d) < 1e15)
                return ((long)d).ToString(CultureInfo.InvariantCulture);
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
