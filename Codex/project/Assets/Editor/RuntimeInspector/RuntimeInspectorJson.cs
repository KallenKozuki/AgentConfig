using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace RuntimeInspector
{
    /// <summary>
    /// RuntimeInspector 使用的轻量 JSON 读写工具
    /// </summary>
    public static class RuntimeInspectorJson
    {
        /// <summary>
        /// 写入转义后的 JSON 字符串
        /// </summary>
        public static void WriteString(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        /// <summary>
        /// 将解析结果或基础值写为 JSON
        /// </summary>
        public static void WriteRaw(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    break;
                case string s:
                    WriteString(sb, s);
                    break;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    break;
                case IDictionary<string, object> dict:
                    sb.Append('{');
                    bool first = true;
                    foreach (var kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(sb, kv.Key);
                        sb.Append(':');
                        WriteRaw(sb, kv.Value);
                    }
                    sb.Append('}');
                    break;
                case IList<object> list:
                    sb.Append('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteRaw(sb, list[i]);
                    }
                    sb.Append(']');
                    break;
                case double d:
                    WriteDouble(sb, d);
                    break;
                case float f:
                    WriteDouble(sb, f);
                    break;
                case IFormattable formattable:
                    sb.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                    break;
                default:
                    WriteString(sb, value.ToString());
                    break;
            }
        }

        /// <summary>
        /// 写入浮点数，NaN 和 Infinity 以字符串输出
        /// </summary>
        public static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                WriteString(sb, d.ToString(CultureInfo.InvariantCulture));
                return;
            }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 比较两棵解析后的 JSON 树，按最深的差异路径输出条目：{"path","old","new"}，只有 old 表示被移除，只有 new 表示新增
        /// </summary>
        /// <param name="oldValue">旧值</param>
        /// <param name="newValue">新值</param>
        /// <param name="path">当前路径，作为条目路径的前缀</param>
        /// <param name="output">差异条目 JSON 列表</param>
        /// <param name="max">最多条目数</param>
        /// <param name="truncated">达到上限时置为 true</param>
        public static void Diff(object oldValue, object newValue, string path, List<string> output, int max, ref bool truncated)
        {
            if (output.Count >= max)
            {
                truncated = true;
                return;
            }

            if (oldValue is Dictionary<string, object> oldObj && newValue is Dictionary<string, object> newObj)
            {
                DiffObject(oldObj, newObj, path, output, max, ref truncated);
                return;
            }

            if (oldValue is List<object> oldList && newValue is List<object> newList)
            {
                int common = Math.Min(oldList.Count, newList.Count);
                for (int i = 0; i < common; i++) Diff(oldList[i], newList[i], $"{path}[{i}]", output, max, ref truncated);
                for (int i = common; i < oldList.Count; i++) AddDiff(output, max, ref truncated, $"{path}[{i}]", true, oldList[i], false, null);
                for (int i = common; i < newList.Count; i++) AddDiff(output, max, ref truncated, $"{path}[{i}]", false, null, true, newList[i]);
                return;
            }

            if (!LeafEquals(oldValue, newValue)) AddDiff(output, max, ref truncated, path, true, oldValue, true, newValue);
        }

        // 比较两个 JSON 对象；带 $type 的是普通对象（成员路径），否则是字典（[key] 路径），带 $items 的是截断后的集合
        private static void DiffObject(Dictionary<string, object> oldObj, Dictionary<string, object> newObj, string path, List<string> output, int max, ref bool truncated)
        {
            if (oldObj.TryGetValue("$items", out object oldItems) && newObj.TryGetValue("$items", out object newItems))
            {
                oldObj.TryGetValue("$count", out object oldCount);
                newObj.TryGetValue("$count", out object newCount);
                if (!LeafEquals(oldCount, newCount)) AddDiff(output, max, ref truncated, path + ".$count", true, oldCount, true, newCount);
                Diff(oldItems, newItems, path, output, max, ref truncated);
                return;
            }

            oldObj.TryGetValue("$type", out object oldType);
            newObj.TryGetValue("$type", out object newType);
            if (!LeafEquals(oldType, newType))
            {
                AddDiff(output, max, ref truncated, path, true, oldObj, true, newObj);
                return;
            }

            bool member = oldType != null;
            foreach (var kv in oldObj)
            {
                if (kv.Key == "$type") continue;
                string child = ChildPath(path, kv.Key, member);
                if (newObj.TryGetValue(kv.Key, out object nv)) Diff(kv.Value, nv, child, output, max, ref truncated);
                else AddDiff(output, max, ref truncated, child, true, kv.Value, false, null);
            }
            foreach (var kv in newObj)
            {
                if (kv.Key == "$type" || oldObj.ContainsKey(kv.Key)) continue;
                AddDiff(output, max, ref truncated, ChildPath(path, kv.Key, member), false, null, true, kv.Value);
            }
        }

        // 写入一条差异
        private static void AddDiff(List<string> output, int max, ref bool truncated, string path, bool hasOld, object oldValue, bool hasNew, object newValue)
        {
            if (output.Count >= max)
            {
                truncated = true;
                return;
            }

            var sb = new StringBuilder("{\"path\":");
            WriteString(sb, path);
            if (hasOld)
            {
                sb.Append(",\"old\":");
                WriteRaw(sb, oldValue);
            }
            if (hasNew)
            {
                sb.Append(",\"new\":");
                WriteRaw(sb, newValue);
            }
            sb.Append('}');
            output.Add(sb.ToString());
        }

        // 叶子值比较，容器与叶子混合时视为不同
        private static bool LeafEquals(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is Dictionary<string, object> || a is List<object> || b is Dictionary<string, object> || b is List<object>) return false;
            return a.Equals(b);
        }

        // 拼接子路径，成员用 .name，字典 key 用 [key]，含特殊字符时加引号
        private static string ChildPath(string path, string key, bool member)
        {
            if (member) return path.Length == 0 ? key : path + "." + key;
            bool plain = key.IndexOfAny(new[] { '.', '[', ']', '"', '\'', ' ' }) < 0;
            return path + "[" + (plain ? key : "\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"") + "]";
        }

        /// <summary>
        /// 解析 JSON 文本，对象为 Dictionary&lt;string, object&gt;，数组为 List&lt;object&gt;，整数为 long，小数为 double
        /// </summary>
        public static object Parse(string json)
        {
            var parser = new Parser(json);
            parser.SkipWhitespace();
            object value = parser.ParseValue();
            parser.SkipWhitespace();
            if (!parser.IsEnd)
            {
                throw new FormatException($"JSON 末尾存在多余内容，位置 {parser.Position}");
            }
            return value;
        }

        // 递归下降 JSON 解析器
        private class Parser
        {
            private readonly string text;
            private int pos;

            public Parser(string text)
            {
                this.text = text ?? string.Empty;
            }

            public bool IsEnd => pos >= text.Length;

            public int Position => pos;

            // 跳过空白字符
            public void SkipWhitespace()
            {
                while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
            }

            // 解析任意 JSON 值
            public object ParseValue()
            {
                if (IsEnd) throw new FormatException("JSON 意外结束");
                char c = text[pos];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || char.IsDigit(c)) return ParseNumber();
                        throw new FormatException($"无法识别的 JSON 字符 '{c}'，位置 {pos}");
                }
            }

            // 解析对象
            private Dictionary<string, object> ParseObject()
            {
                var result = new Dictionary<string, object>();
                pos++;
                SkipWhitespace();
                if (Peek('}'))
                {
                    pos++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    if (!Peek('"')) throw new FormatException($"对象键必须是字符串，位置 {pos}");
                    string key = ParseString();
                    SkipWhitespace();
                    if (!Peek(':')) throw new FormatException($"缺少 ':'，位置 {pos}");
                    pos++;
                    SkipWhitespace();
                    result[key] = ParseValue();
                    SkipWhitespace();
                    if (Peek(','))
                    {
                        pos++;
                        continue;
                    }
                    if (Peek('}'))
                    {
                        pos++;
                        return result;
                    }
                    throw new FormatException($"对象缺少 ',' 或 '}}'，位置 {pos}");
                }
            }

            // 解析数组
            private List<object> ParseArray()
            {
                var result = new List<object>();
                pos++;
                SkipWhitespace();
                if (Peek(']'))
                {
                    pos++;
                    return result;
                }

                while (true)
                {
                    SkipWhitespace();
                    result.Add(ParseValue());
                    SkipWhitespace();
                    if (Peek(','))
                    {
                        pos++;
                        continue;
                    }
                    if (Peek(']'))
                    {
                        pos++;
                        return result;
                    }
                    throw new FormatException($"数组缺少 ',' 或 ']'，位置 {pos}");
                }
            }

            // 解析字符串
            private string ParseString()
            {
                var sb = new StringBuilder();
                pos++;
                while (pos < text.Length)
                {
                    char c = text[pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\')
                    {
                        sb.Append(c);
                        continue;
                    }

                    if (pos >= text.Length) break;
                    char e = text[pos++];
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
                            if (pos + 4 > text.Length) throw new FormatException("\\u 转义不完整");
                            sb.Append((char)Convert.ToInt32(text.Substring(pos, 4), 16));
                            pos += 4;
                            break;
                        default:
                            throw new FormatException($"非法转义 '\\{e}'，位置 {pos}");
                    }
                }
                throw new FormatException("字符串未闭合");
            }

            // 解析数字，整数优先返回 long
            private object ParseNumber()
            {
                int start = pos;
                while (pos < text.Length && "+-0123456789.eE".IndexOf(text[pos]) >= 0) pos++;
                string s = text.Substring(start, pos - start);
                if (s.IndexOfAny(new[] { '.', 'e', 'E' }) < 0
                    && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                {
                    return l;
                }
                return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            // 校验固定字面量
            private void Expect(string literal)
            {
                if (string.CompareOrdinal(text, pos, literal, 0, literal.Length) != 0)
                {
                    throw new FormatException($"期望 '{literal}'，位置 {pos}");
                }
                pos += literal.Length;
            }

            // 判断当前字符
            private bool Peek(char c)
            {
                return pos < text.Length && text[pos] == c;
            }
        }
    }
}
