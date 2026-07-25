using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FTK2Mods.DevKit
{
    /// <summary>
    /// A deliberately tiny, dependency-free, culture-invariant JSON reader/writer.
    ///
    /// Why hand-rolled: DevKit.Core is netstandard2.0 with ZERO package references (the repo build
    /// rules bar NuGet), and the parity payload schema is closed and trivial — objects, arrays and
    /// strings only (SPEC §4 has no numeric or boolean fields). A full JSON stack would be dead
    /// weight and, worse, a determinism risk (serializer settings differing between peers).
    ///
    /// Determinism guarantees:
    ///  - the writer emits keys in the exact order the caller writes them, with no whitespace;
    ///  - every character outside printable ASCII is escaped as <c>\uXXXX</c> with the invariant
    ///    culture, so the payload is pure ASCII regardless of the peer's encoding/locale;
    ///  - the reader accepts numbers/bools/null for forward compatibility but the parity schema
    ///    never emits them.
    /// </summary>
    internal static class MiniJson
    {
        internal static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            if (value != null)
            {
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < ' ' || c > '~')
                            {
                                sb.Append("\\u");
                                sb.Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                sb.Append(c);
                            }
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        /// <summary>
        /// Parses <paramref name="text"/> into nested
        /// <see cref="Dictionary{TKey,TValue}"/> / <see cref="List{T}"/> / <see cref="string"/> /
        /// bool / null. Never throws: returns false with a human-readable <paramref name="error"/>.
        /// </summary>
        internal static bool TryParse(string text, out object value, out string error)
        {
            value = null;
            error = null;
            if (text == null)
            {
                error = "payload is null";
                return false;
            }
            int index = 0;
            try
            {
                object parsed = ParseValue(text, ref index, 0);
                SkipWhitespace(text, ref index);
                if (index != text.Length)
                {
                    error = "trailing content at offset " + index.ToString(CultureInfo.InvariantCulture);
                    return false;
                }
                value = parsed;
                return true;
            }
            catch (FormatException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static string AsString(object value)
        {
            return value as string;
        }

        internal static Dictionary<string, object> AsObject(object value)
        {
            return value as Dictionary<string, object>;
        }

        internal static List<object> AsArray(object value)
        {
            return value as List<object>;
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

        private static object ParseValue(string s, ref int i, int depth)
        {
            if (depth > 32) throw new FormatException("payload nested too deeply");
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end of payload");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i, depth);
            if (c == '[') return ParseArray(s, ref i, depth);
            if (c == '"') return ParseString(s, ref i);
            if (Matches(s, i, "true")) { i += 4; return BoxedTrue; }
            if (Matches(s, i, "false")) { i += 5; return BoxedFalse; }
            if (Matches(s, i, "null")) { i += 4; return null; }
            return ParseNumberToken(s, ref i);
        }

        private static readonly object BoxedTrue = true;
        private static readonly object BoxedFalse = false;

        private static Dictionary<string, object> ParseObject(string s, ref int i, int depth)
        {
            i++; // '{'
            Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return map; }
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw new FormatException("expected object key at offset " + i.ToString(CultureInfo.InvariantCulture));
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at offset " + i.ToString(CultureInfo.InvariantCulture));
                i++;
                object v = ParseValue(s, ref i, depth + 1);
                map[key] = v;
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return map; }
                throw new FormatException("expected ',' or '}' at offset " + i.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static List<object> ParseArray(string s, ref int i, int depth)
        {
            i++; // '['
            List<object> list = new List<object>();
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (true)
            {
                object v = ParseValue(s, ref i, depth + 1);
                list.Add(v);
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return list; }
                throw new FormatException("expected ',' or ']' at offset " + i.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // opening quote
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("unterminated string");
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (i >= s.Length) throw new FormatException("unterminated escape");
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
                        if (i + 4 > s.Length) throw new FormatException("truncated \\u escape");
                        string hex = s.Substring(i, 4);
                        int code;
                        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            throw new FormatException("bad \\u escape '" + hex + "'");
                        sb.Append((char)code);
                        i += 4;
                        break;
                    default:
                        throw new FormatException("unknown escape '\\" + e + "'");
                }
            }
        }

        /// <summary>
        /// A JSON number, kept as its raw token text (invariant by construction) and wrapped so it
        /// can never be mistaken for a JSON string by <see cref="AsString"/>. The parity schema has
        /// no numeric fields; this exists only so an unknown future field can't make a whole payload
        /// unreadable — and so <c>{"Action":123}</c> is rejected rather than read as "123".
        /// </summary>
        internal sealed class JsonNumber
        {
            internal JsonNumber(string raw) { Raw = raw; }
            internal string Raw { get; private set; }
            public override string ToString() { return Raw; }
        }

        private static JsonNumber ParseNumberToken(string s, ref int i)
        {
            int start = i;
            while (i < s.Length)
            {
                char c = s[i];
                bool isNumberChar = (c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E';
                if (!isNumberChar) break;
                i++;
            }
            if (i == start) throw new FormatException("unexpected character '" + s[i] + "' at offset " + i.ToString(CultureInfo.InvariantCulture));
            return new JsonNumber(s.Substring(start, i - start));
        }

        private static bool Matches(string s, int i, string literal)
        {
            if (i + literal.Length > s.Length) return false;
            return string.CompareOrdinal(s, i, literal, 0, literal.Length) == 0;
        }
    }
}
