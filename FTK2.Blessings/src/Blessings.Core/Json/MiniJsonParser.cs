using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Blessings.Core.Json
{
    /// <summary>
    /// A tiny, dependency-free, culture-invariant, STRICT JSON reader (RFC 8259 modulo a couple of
    /// intentionally-lenient escape rules shared with every other parser in this repo). Mirrors
    /// FTK2.DevKit/src/DevKit.Core/MiniJson.cs's "why hand-rolled" rationale (netstandard2.0, zero
    /// package references) but returns order-preserving <see cref="JsonValue"/> object members
    /// instead of a <see cref="Dictionary{TKey,TValue}"/> — see JsonValue's doc comment.
    /// Strict: trailing commas, comments, and NaN/Infinity are all rejected (never silently accepted),
    /// matching §4.1's "Strict JSON, UTF-8, no comments".
    /// </summary>
    internal static class MiniJsonParser
    {
        /// <summary>Parses <paramref name="text"/> into a <see cref="JsonValue"/> tree. Throws
        /// <see cref="FormatException"/> with a human-readable offset on any malformed input —
        /// never returns a partial/best-effort result.</summary>
        internal static JsonValue Parse(string text)
        {
            if (text == null) throw new FormatException("JSON text is null.");
            int i = 0;
            JsonValue value = ParseValue(text, ref i, 0);
            SkipWhitespace(text, ref i);
            if (i != text.Length)
                throw new FormatException("Trailing content at offset " + i.ToString(CultureInfo.InvariantCulture) + ".");
            return value;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
                else break;
            }
        }

        private static JsonValue ParseValue(string s, ref int i, int depth)
        {
            if (depth > 64) throw new FormatException("JSON nested too deeply (depth > 64).");
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON input.");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i, depth);
            if (c == '[') return ParseArray(s, ref i, depth);
            if (c == '"') return JsonValue.String(ParseString(s, ref i));
            if (Matches(s, i, "true")) { i += 4; return JsonValue.Bool(true); }
            if (Matches(s, i, "false")) { i += 5; return JsonValue.Bool(false); }
            if (Matches(s, i, "null")) { i += 4; return JsonValue.Null; }
            return ParseNumber(s, ref i);
        }

        private static JsonValue ParseObject(string s, ref int i, int depth)
        {
            i++; // '{'
            var members = new List<KeyValuePair<string, JsonValue>>();
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return JsonValue.Object(members); }
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"')
                    throw new FormatException("Expected object key (string) at offset " + i.ToString(CultureInfo.InvariantCulture) + ".");
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':')
                    throw new FormatException("Expected ':' at offset " + i.ToString(CultureInfo.InvariantCulture) + ".");
                i++;
                JsonValue v = ParseValue(s, ref i, depth + 1);
                members.Add(new KeyValuePair<string, JsonValue>(key, v));
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated object.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return JsonValue.Object(members); }
                throw new FormatException("Expected ',' or '}' at offset " + i.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }

        private static JsonValue ParseArray(string s, ref int i, int depth)
        {
            i++; // '['
            var items = new List<JsonValue>();
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return JsonValue.Array(items); }
            while (true)
            {
                items.Add(ParseValue(s, ref i, depth + 1));
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("Unterminated array.");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return JsonValue.Array(items); }
                throw new FormatException("Expected ',' or ']' at offset " + i.ToString(CultureInfo.InvariantCulture) + ".");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("Unterminated string.");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) throw new FormatException("Unterminated escape sequence.");
                char e = s[i++];
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
                        if (i + 4 > s.Length) throw new FormatException("Truncated \\u escape.");
                        string hex = s.Substring(i, 4);
                        int code;
                        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            throw new FormatException("Bad \\u escape '" + hex + "'.");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw new FormatException("Unknown escape '\\" + e + "'.");
                }
            }
        }

        private static JsonValue ParseNumber(string s, ref int i)
        {
            int start = i;
            if (i < s.Length && s[i] == '-') i++;
            if (i >= s.Length || s[i] < '0' || s[i] > '9')
                throw new FormatException("Unexpected character at offset " + start.ToString(CultureInfo.InvariantCulture) + ".");
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i < s.Length && s[i] == '.')
            {
                i++;
                if (i >= s.Length || s[i] < '0' || s[i] > '9') throw new FormatException("Malformed number (digits expected after '.').");
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                if (i >= s.Length || s[i] < '0' || s[i] > '9') throw new FormatException("Malformed number (digits expected in exponent).");
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            }
            return JsonValue.Number(s.Substring(start, i - start));
        }

        private static bool Matches(string s, int i, string literal)
        {
            if (i + literal.Length > s.Length) return false;
            return string.CompareOrdinal(s, i, literal, 0, literal.Length) == 0;
        }
    }
}
