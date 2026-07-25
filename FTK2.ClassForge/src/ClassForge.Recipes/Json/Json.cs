using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClassForge.Recipes.Json
{
    /// <summary>Kind discriminator for <see cref="JsonValue"/>.</summary>
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
    /// Minimal, allocation-honest JSON DOM. Hand-rolled because this assembly takes no
    /// NuGet dependency and cannot rely on the game's <c>System.Text.Json</c> being present
    /// (build rules, docs/research/build-template-notes.md §1).
    /// Object member order is preserved so parse/validate diagnostics are deterministic.
    /// </summary>
    public sealed class JsonValue
    {
        private readonly List<string> _keys;
        private readonly Dictionary<string, JsonValue> _members;
        private readonly List<JsonValue> _items;

        public JsonKind Kind { get; private set; }
        public bool BoolValue { get; private set; }
        public decimal NumberValue { get; private set; }
        public string StringValue { get; private set; }

        private JsonValue(JsonKind kind)
        {
            Kind = kind;
            if (kind == JsonKind.Object)
            {
                _keys = new List<string>();
                _members = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            }
            else if (kind == JsonKind.Array)
            {
                _items = new List<JsonValue>();
            }
        }

        public static JsonValue NewNull() { return new JsonValue(JsonKind.Null); }
        public static JsonValue NewBool(bool v) { var j = new JsonValue(JsonKind.Bool); j.BoolValue = v; return j; }
        public static JsonValue NewNumber(decimal v) { var j = new JsonValue(JsonKind.Number); j.NumberValue = v; return j; }
        public static JsonValue NewString(string v) { var j = new JsonValue(JsonKind.String); j.StringValue = v; return j; }
        public static JsonValue NewArray() { return new JsonValue(JsonKind.Array); }
        public static JsonValue NewObject() { return new JsonValue(JsonKind.Object); }

        /// <summary>Object member names in source order.</summary>
        public IReadOnlyList<string> Keys
        {
            get { return _keys != null ? (IReadOnlyList<string>)_keys : new string[0]; }
        }

        /// <summary>Array items in source order.</summary>
        public IReadOnlyList<JsonValue> Items
        {
            get { return _items != null ? (IReadOnlyList<JsonValue>)_items : new JsonValue[0]; }
        }

        public void Add(JsonValue item)
        {
            if (_items == null) throw new InvalidOperationException("not an array");
            _items.Add(item);
        }

        public void Set(string key, JsonValue value)
        {
            if (_members == null) throw new InvalidOperationException("not an object");
            if (!_members.ContainsKey(key)) _keys.Add(key);
            _members[key] = value;
        }

        public bool Has(string key)
        {
            return _members != null && _members.ContainsKey(key);
        }

        /// <summary>Member lookup; returns null when absent or when this is not an object.</summary>
        public JsonValue Get(string key)
        {
            JsonValue v;
            if (_members != null && _members.TryGetValue(key, out v)) return v;
            return null;
        }

        public override string ToString()
        {
            var sb = new StringBuilder();
            Write(sb);
            return sb.ToString();
        }

        private void Write(StringBuilder sb)
        {
            switch (Kind)
            {
                case JsonKind.Null: sb.Append("null"); break;
                case JsonKind.Bool: sb.Append(BoolValue ? "true" : "false"); break;
                case JsonKind.Number: sb.Append(NumberValue.ToString(CultureInfo.InvariantCulture)); break;
                case JsonKind.String: sb.Append('"').Append(StringValue).Append('"'); break;
                case JsonKind.Array:
                    sb.Append('[');
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        _items[i].Write(sb);
                    }
                    sb.Append(']');
                    break;
                default:
                    sb.Append('{');
                    for (int i = 0; i < _keys.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append('"').Append(_keys[i]).Append("\":");
                        _members[_keys[i]].Write(sb);
                    }
                    sb.Append('}');
                    break;
            }
        }
    }

    /// <summary>
    /// Recursive-descent JSON reader. Accepts the JSONC conveniences the SPEC's own examples use
    /// (<c>//</c> and <c>/* */</c> comments, trailing commas) so authored pack files copy-paste cleanly.
    /// <para>
    /// Never throws to callers: <see cref="TryParse"/> returns false plus a human-readable error.
    /// This is the outermost fail-safe required by SPEC-DELTA-v1.1 §"fail-safe" posture — a malformed
    /// pack file must disable recipes, never crash the host.
    /// </para>
    /// </summary>
    public static class JsonParser
    {
        private sealed class ParseError : Exception
        {
            public ParseError(string message) : base(message) { }
        }

        public static bool TryParse(string text, out JsonValue value, out string error)
        {
            value = null;
            error = null;
            if (text == null) { error = "input was null"; return false; }
            int i = 0;
            try
            {
                SkipTrivia(text, ref i);
                var v = ParseValue(text, ref i, 0);
                SkipTrivia(text, ref i);
                if (i < text.Length) throw new ParseError(Where(text, i) + ": trailing content after top-level value");
                value = v;
                return true;
            }
            catch (ParseError ex)
            {
                error = ex.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "unexpected reader failure: " + ex.Message;
                return false;
            }
        }

        private const int MaxDepth = 64;

        private static string Where(string s, int i)
        {
            int line = 1, col = 1;
            for (int k = 0; k < i && k < s.Length; k++)
            {
                if (s[k] == '\n') { line++; col = 1; }
                else col++;
            }
            return "line " + line.ToString(CultureInfo.InvariantCulture) + " col " + col.ToString(CultureInfo.InvariantCulture);
        }

        private static void SkipTrivia(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '﻿') { i++; continue; }
                if (c == '/' && i + 1 < s.Length)
                {
                    if (s[i + 1] == '/')
                    {
                        i += 2;
                        while (i < s.Length && s[i] != '\n') i++;
                        continue;
                    }
                    if (s[i + 1] == '*')
                    {
                        i += 2;
                        while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                        if (i + 1 >= s.Length) throw new ParseError("unterminated block comment");
                        i += 2;
                        continue;
                    }
                }
                return;
            }
        }

        private static JsonValue ParseValue(string s, ref int i, int depth)
        {
            if (depth > MaxDepth) throw new ParseError("nesting deeper than " + MaxDepth);
            if (i >= s.Length) throw new ParseError("unexpected end of input");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i, depth);
                case '[': return ParseArray(s, ref i, depth);
                case '"': return JsonValue.NewString(ParseString(s, ref i));
                case 't': Expect(s, ref i, "true"); return JsonValue.NewBool(true);
                case 'f': Expect(s, ref i, "false"); return JsonValue.NewBool(false);
                case 'n': Expect(s, ref i, "null"); return JsonValue.NewNull();
                default: return JsonValue.NewNumber(ParseNumber(s, ref i));
            }
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw new ParseError(Where(s, i) + ": expected '" + literal + "'");
            i += literal.Length;
        }

        private static JsonValue ParseObject(string s, ref int i, int depth)
        {
            var obj = JsonValue.NewObject();
            i++; // {
            SkipTrivia(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return obj; }
            while (true)
            {
                SkipTrivia(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return obj; } // trailing comma
                if (i >= s.Length || s[i] != '"') throw new ParseError(Where(s, i) + ": expected member name string");
                string key = ParseString(s, ref i);
                SkipTrivia(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new ParseError(Where(s, i) + ": expected ':' after member name");
                i++;
                SkipTrivia(s, ref i);
                var val = ParseValue(s, ref i, depth + 1);
                obj.Set(key, val);
                SkipTrivia(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return obj; }
                throw new ParseError(Where(s, i) + ": expected ',' or '}'");
            }
        }

        private static JsonValue ParseArray(string s, ref int i, int depth)
        {
            var arr = JsonValue.NewArray();
            i++; // [
            SkipTrivia(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return arr; }
            while (true)
            {
                SkipTrivia(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return arr; } // trailing comma
                arr.Add(ParseValue(s, ref i, depth + 1));
                SkipTrivia(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return arr; }
                throw new ParseError(Where(s, i) + ": expected ',' or ']'");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new ParseError("unterminated string");
                char c = s[i];
                if (c == '"') { i++; return sb.ToString(); }
                if (c == '\\')
                {
                    i++;
                    if (i >= s.Length) throw new ParseError("unterminated escape");
                    char e = s[i];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 >= s.Length) throw new ParseError("truncated \\u escape");
                            int code;
                            if (!int.TryParse(s.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                throw new ParseError(Where(s, i) + ": bad \\u escape");
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default: throw new ParseError(Where(s, i) + ": unknown escape '\\" + e + "'");
                    }
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
            }
        }

        private static decimal ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && (s[i] == '-' || s[i] == '+')) i++;
            bool any = false;
            while (i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' || s[i] == 'e' || s[i] == 'E' ||
                                    ((s[i] == '-' || s[i] == '+') && (s[i - 1] == 'e' || s[i - 1] == 'E'))))
            {
                any = true;
                i++;
            }
            if (!any) throw new ParseError(Where(s, start) + ": expected a value");
            string raw = s.Substring(start, i - start);
            decimal d;
            if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw new ParseError(Where(s, start) + ": '" + raw + "' is not a number");
            return d;
        }
    }
}
