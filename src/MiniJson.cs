using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RdpTabs
{
    internal enum JsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>
    /// Minimal JSON reader/writer. Hand-written rather than using System.Text.Json /
    /// DataContractJsonSerializer so both build paths (net48 and net8) share one source tree with no packages.
    /// </summary>
    internal sealed class Json
    {
        private readonly JsonKind _kind;
        private readonly bool _bool;
        private readonly double _number;
        private readonly string _string;
        private readonly List<Json> _array;
        private readonly List<KeyValuePair<string, Json>> _object;

        private Json(JsonKind kind)
        {
            _kind = kind;
            if (kind == JsonKind.Array) _array = new List<Json>();
            if (kind == JsonKind.Object) _object = new List<KeyValuePair<string, Json>>();
        }

        private Json(bool value) : this(JsonKind.Bool) { _bool = value; }
        private Json(double value) : this(JsonKind.Number) { _number = value; }
        private Json(string value) : this(JsonKind.String) { _string = value; }

        public static readonly Json Null = new Json(JsonKind.Null);

        public static Json NewObject() { return new Json(JsonKind.Object); }
        public static Json NewArray() { return new Json(JsonKind.Array); }
        public static Json From(string value) { return value == null ? Null : new Json(value); }
        public static Json From(bool value) { return new Json(value); }
        public static Json From(int value) { return new Json((double)value); }
        public static Json From(double value) { return new Json(value); }

        public JsonKind Kind { get { return _kind; } }
        public bool IsObject { get { return _kind == JsonKind.Object; } }
        public bool IsArray { get { return _kind == JsonKind.Array; } }

        public int Count
        {
            get
            {
                if (_kind == JsonKind.Array) return _array.Count;
                if (_kind == JsonKind.Object) return _object.Count;
                return 0;
            }
        }

        public IEnumerable<Json> Items
        {
            get
            {
                if (_kind == JsonKind.Array)
                {
                    foreach (Json item in _array) yield return item;
                }
            }
        }

        public Json this[int index]
        {
            get
            {
                if (_kind != JsonKind.Array || index < 0 || index >= _array.Count) return Null;
                return _array[index];
            }
        }

        public Json this[string key]
        {
            get
            {
                if (_kind == JsonKind.Object)
                {
                    for (int i = 0; i < _object.Count; i++)
                        if (string.Equals(_object[i].Key, key, StringComparison.Ordinal))
                            return _object[i].Value;
                }
                return Null;
            }
            set
            {
                if (_kind != JsonKind.Object) throw new InvalidOperationException("not a JSON object");
                Json v = value ?? Null;
                for (int i = 0; i < _object.Count; i++)
                {
                    if (string.Equals(_object[i].Key, key, StringComparison.Ordinal))
                    {
                        _object[i] = new KeyValuePair<string, Json>(key, v);
                        return;
                    }
                }
                _object.Add(new KeyValuePair<string, Json>(key, v));
            }
        }

        public void Add(Json item)
        {
            if (_kind != JsonKind.Array) throw new InvalidOperationException("not a JSON array");
            _array.Add(item ?? Null);
        }

        public void Set(string key, string value) { this[key] = From(value); }
        public void Set(string key, bool value) { this[key] = From(value); }
        public void Set(string key, int value) { this[key] = From(value); }

        public string AsString(string fallback)
        {
            if (_kind == JsonKind.String) return _string;
            if (_kind == JsonKind.Number) return _number.ToString(CultureInfo.InvariantCulture);
            if (_kind == JsonKind.Bool) return _bool ? "true" : "false";
            return fallback;
        }

        public int AsInt(int fallback)
        {
            if (_kind == JsonKind.Number) return (int)Math.Round(_number);
            if (_kind == JsonKind.String)
            {
                int parsed;
                if (int.TryParse(_string, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)) return parsed;
            }
            return fallback;
        }

        public bool AsBool(bool fallback)
        {
            if (_kind == JsonKind.Bool) return _bool;
            if (_kind == JsonKind.Number) return Math.Abs(_number) > double.Epsilon;
            if (_kind == JsonKind.String)
            {
                bool parsed;
                if (bool.TryParse(_string, out parsed)) return parsed;
            }
            return fallback;
        }

        // ---------------- writing ----------------

        public string ToJson(bool indented)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, indented, 0);
            return sb.ToString();
        }

        private void Write(StringBuilder sb, bool indented, int depth)
        {
            switch (_kind)
            {
                case JsonKind.Null:
                    sb.Append("null");
                    break;
                case JsonKind.Bool:
                    sb.Append(_bool ? "true" : "false");
                    break;
                case JsonKind.Number:
                    if (_number == Math.Floor(_number) && Math.Abs(_number) < 1e15)
                        sb.Append(((long)_number).ToString(CultureInfo.InvariantCulture));
                    else
                        sb.Append(_number.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonKind.String:
                    WriteString(sb, _string);
                    break;
                case JsonKind.Array:
                    if (_array.Count == 0) { sb.Append("[]"); break; }
                    sb.Append('[');
                    for (int i = 0; i < _array.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        NewLine(sb, indented, depth + 1);
                        _array[i].Write(sb, indented, depth + 1);
                    }
                    NewLine(sb, indented, depth);
                    sb.Append(']');
                    break;
                case JsonKind.Object:
                    if (_object.Count == 0) { sb.Append("{}"); break; }
                    sb.Append('{');
                    for (int i = 0; i < _object.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        NewLine(sb, indented, depth + 1);
                        WriteString(sb, _object[i].Key);
                        sb.Append(':');
                        if (indented) sb.Append(' ');
                        _object[i].Value.Write(sb, indented, depth + 1);
                    }
                    NewLine(sb, indented, depth);
                    sb.Append('}');
                    break;
            }
        }

        private static void NewLine(StringBuilder sb, bool indented, int depth)
        {
            if (!indented) return;
            sb.Append('\n');
            sb.Append(' ', depth * 2);
        }

        private static void WriteString(StringBuilder sb, string value)
        {
            sb.Append('"');
            foreach (char c in value ?? string.Empty)
            {
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
                        if (c < ' ')
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- parsing ----------------

        public static Json Parse(string text)
        {
            int index = 0;
            Json value = ParseValue(text ?? string.Empty, ref index);
            SkipWhitespace(text, ref index);
            return value;
        }

        private static Json ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) throw Fail(i, "unexpected end of input");
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return From(ParseString(s, ref i));
                case 't': Expect(s, ref i, "true"); return From(true);
                case 'f': Expect(s, ref i, "false"); return From(false);
                case 'n': Expect(s, ref i, "null"); return Null;
                default: return From(ParseNumber(s, ref i));
            }
        }

        private static Json ParseObject(string s, ref int i)
        {
            Json result = NewObject();
            i++; // {
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return result; }
            while (true)
            {
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != '"') throw Fail(i, "expected a property name");
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length || s[i] != ':') throw Fail(i, "expected ':'");
                i++;
                result[key] = ParseValue(s, ref i);
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw Fail(i, "unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return result; }
                throw Fail(i, "expected ',' or '}'");
            }
        }

        private static Json ParseArray(string s, ref int i)
        {
            Json result = NewArray();
            i++; // [
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return result; }
            while (true)
            {
                result.Add(ParseValue(s, ref i));
                SkipWhitespace(s, ref i);
                if (i >= s.Length) throw Fail(i, "unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return result; }
                throw Fail(i, "expected ',' or ']'");
            }
        }

        private static string ParseString(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
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
                        if (i + 4 > s.Length) throw Fail(i, "incomplete \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw Fail(i, "unknown escape \\" + esc);
                }
            }
            throw Fail(i, "unterminated string");
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-.eE0123456789".IndexOf(s[i]) >= 0) i++;
            double value;
            if (i == start || !double.TryParse(s.Substring(start, i - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value))
                throw Fail(start, "invalid number");
            return value;
        }

        private static void Expect(string s, ref int i, string literal)
        {
            if (i + literal.Length > s.Length ||
                string.CompareOrdinal(s, i, literal, 0, literal.Length) != 0)
                throw Fail(i, "expected " + literal);
            i += literal.Length;
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static FormatException Fail(int index, string message)
        {
            return new FormatException("JSON parse error at offset " + index + ": " + message);
        }
    }
}
