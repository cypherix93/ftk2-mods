using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClassForge.Core.Json
{
    /// <summary>
    /// Minimal hand-rolled recursive-descent JSON parser (strict — no comments, no trailing commas), matching
    /// the game's own JSON convention (SPEC.md §4: "no comments — the game's own JSON parser doesn't tolerate
    /// them"). Exists only because ClassForge.Core has zero NuGet packages and zero game references.
    /// </summary>
    public static class JsonParser
    {
        public static JsonValue Parse(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            int i = 0;
            SkipWhitespace(text, ref i);
            var value = ParseValue(text, ref i);
            SkipWhitespace(text, ref i);
            if (i != text.Length)
                throw new FormatException($"Unexpected trailing content at position {i}.");
            return value;
        }

        private static JsonValue ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON input.");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return JsonValue.NewString(ParseString(s, ref i));
                case 't': Expect(s, ref i, "true"); return JsonValue.NewBool(true);
                case 'f': Expect(s, ref i, "false"); return JsonValue.NewBool(false);
                case 'n': Expect(s, ref i, "null"); return JsonValue.Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref i);
                    throw new FormatException($"Unexpected character '{c}' at position {i}.");
            }
        }

        private static JsonValue ParseObject(string s, ref int i)
        {
            var members = new List<KeyValuePair<string, JsonValue>>();
            i++; // consume '{'
            SkipWhitespace(s, ref i);
            if (Peek(s, i) == '}') { i++; return JsonValue.NewObject(members); }
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (Peek(s, i) != '"')
                    throw new FormatException($"Expected string key at position {i}.");
                var key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (Peek(s, i) != ':')
                    throw new FormatException($"Expected ':' at position {i}.");
                i++; // consume ':'
                var value = ParseValue(s, ref i);
                members.Add(new KeyValuePair<string, JsonValue>(key, value));
                SkipWhitespace(s, ref i);
                char c = Peek(s, i);
                if (c == ',') { i++; continue; }
                if (c == '}') { i++; break; }
                throw new FormatException($"Expected ',' or '}}' at position {i}.");
            }
            return JsonValue.NewObject(members);
        }

        private static JsonValue ParseArray(string s, ref int i)
        {
            var items = new List<JsonValue>();
            i++; // consume '['
            SkipWhitespace(s, ref i);
            if (Peek(s, i) == ']') { i++; return JsonValue.NewArray(items); }
            while (true)
            {
                var value = ParseValue(s, ref i);
                items.Add(value);
                SkipWhitespace(s, ref i);
                char c = Peek(s, i);
                if (c == ',') { i++; continue; }
                if (c == ']') { i++; break; }
                throw new FormatException($"Expected ',' or ']' at position {i}.");
            }
            return JsonValue.NewArray(items);
        }

        private static string ParseString(string s, ref int i)
        {
            i++; // consume opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (i >= s.Length) throw new FormatException("Unterminated string literal.");
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\')
                {
                    if (i >= s.Length) throw new FormatException("Unterminated escape sequence.");
                    char esc = s[i++];
                    switch (esc)
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
                            var hex = s.Substring(i, 4);
                            i += 4;
                            sb.Append((char)ushort.Parse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                            break;
                        default:
                            throw new FormatException($"Invalid escape character '\\{esc}'.");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private static JsonValue ParseNumber(string s, ref int i)
        {
            int start = i;
            if (Peek(s, i) == '-') i++;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (Peek(s, i) == '.')
            {
                i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            }
            if (Peek(s, i) == 'e' || Peek(s, i) == 'E')
            {
                i++;
                if (Peek(s, i) == '+' || Peek(s, i) == '-') i++;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            }
            var text = s.Substring(start, i - start);
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                throw new FormatException($"Invalid number literal '{text}' at position {start}.");
            return JsonValue.NewNumber(d);
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length || string.CompareOrdinal(s.Substring(i, literal.Length), literal) != 0)
                throw new FormatException($"Expected '{literal}' at position {i}.");
            i += literal.Length;
        }

        private static char Peek(string s, int i) => i < s.Length ? s[i] : '\0';

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') i++;
                else break;
            }
        }
    }
}
