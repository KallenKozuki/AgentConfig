using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace RuntimeInspector
{
    /// <summary>
    /// 将运行时对象按深度和数量限制序列化为 JSON，并提供字段搜索
    /// </summary>
    public sealed class RuntimeInspectorSerializer
    {
        private readonly int maxDepth;
        private readonly int limit;
        private readonly bool includeProps;
        private readonly HashSet<object> visiting = new(ReferenceComparer.Instance);

        /// <summary>
        /// 构造序列化器
        /// </summary>
        /// <param name="maxDepth">展开深度，超出后输出摘要</param>
        /// <param name="limit">单个集合最多输出的元素数</param>
        /// <param name="includeProps">是否读取普通属性（getter 可能有副作用）</param>
        public RuntimeInspectorSerializer(int maxDepth, int limit, bool includeProps)
        {
            this.maxDepth = Math.Max(0, maxDepth);
            this.limit = Math.Max(1, limit);
            this.includeProps = includeProps;
        }

        /// <summary>
        /// 序列化对象为 JSON 字符串
        /// </summary>
        public string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value, 0);
            return sb.ToString();
        }

        /// <summary>
        /// 将对象写入 StringBuilder
        /// </summary>
        public void Write(StringBuilder sb, object value, int depth)
        {
            if (TryWriteLeaf(sb, value)) return;

            Type type = value.GetType();
            if (depth >= maxDepth)
            {
                RuntimeInspectorJson.WriteString(sb, Summary(value));
                return;
            }

            bool isReference = !type.IsValueType;
            if (isReference && !visiting.Add(value))
            {
                RuntimeInspectorJson.WriteString(sb, $"<cycle {RuntimeInspectorReflection.FriendlyName(type)}>");
                return;
            }

            try
            {
                if (value is IDictionary dict)
                {
                    WriteDictionary(sb, dict, depth);
                }
                else if (value is IEnumerable enumerable)
                {
                    WriteEnumerable(sb, enumerable, depth);
                }
                else
                {
                    WriteObject(sb, value, type, depth);
                }
            }
            finally
            {
                if (isReference) visiting.Remove(value);
            }
        }

        /// <summary>
        /// 生成值的单行摘要
        /// </summary>
        public static string Summary(object value)
        {
            if (value == null) return "null";
            Type type = value.GetType();
            string typeName = RuntimeInspectorReflection.FriendlyName(type);
            if (value is ICollection collection) return $"<{typeName} count={collection.Count}>";
            if (IsLeaf(value)) return LeafText(value);
            if (value is IEnumerable && type.GetProperty("Count")?.GetValue(value) is int count) return $"<{typeName} count={count}>";
            return $"<{typeName}>";
        }

        /// <summary>
        /// 是否为可直接输出的叶子值
        /// </summary>
        public static bool IsLeaf(object value)
        {
            if (value == null) return true;
            Type type = value.GetType();
            return type.IsPrimitive || type.IsEnum || value is string || value is decimal
                   || value is DateTime || value is TimeSpan || value is Guid || value is Type
                   || value is Delegate || value is UnityEngine.Object;
        }

        /// <summary>
        /// 叶子值的文本形式，用于搜索和日志
        /// </summary>
        public static string LeafText(object value)
        {
            switch (value)
            {
                case null: return "null";
                case Delegate d: return $"<delegate {RuntimeInspectorReflection.FriendlyName(d.GetType())} x{d.GetInvocationList().Length}>";
                case Type t: return RuntimeInspectorReflection.FriendlyName(t);
                case UnityEngine.Object uo: return uo == null ? "<destroyed>" : $"<{uo.GetType().Name} {uo.name}>";
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return value.ToString();
            }
        }

        // 写入叶子值，非叶子返回 false
        private static bool TryWriteLeaf(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null:
                    sb.Append("null");
                    return true;
                case string s:
                    RuntimeInspectorJson.WriteString(sb, s);
                    return true;
                case bool b:
                    sb.Append(b ? "true" : "false");
                    return true;
                case char c:
                    RuntimeInspectorJson.WriteString(sb, c.ToString());
                    return true;
                case Enum e:
                    RuntimeInspectorJson.WriteString(sb, e.ToString());
                    return true;
                case float f:
                    RuntimeInspectorJson.WriteDouble(sb, f);
                    return true;
                case double d:
                    RuntimeInspectorJson.WriteDouble(sb, d);
                    return true;
                case decimal m:
                    sb.Append(m.ToString(CultureInfo.InvariantCulture));
                    return true;
                case UnityEngine.Object uo:
                    if (uo == null)
                    {
                        RuntimeInspectorJson.WriteString(sb, "<destroyed>");
                    }
                    else
                    {
                        sb.Append("{\"$unity\":");
                        RuntimeInspectorJson.WriteString(sb, uo.GetType().Name);
                        sb.Append(",\"name\":");
                        RuntimeInspectorJson.WriteString(sb, uo.name);
                        sb.Append('}');
                    }
                    return true;
            }

            if (value.GetType().IsPrimitive)
            {
                sb.Append(((IFormattable)value).ToString(null, CultureInfo.InvariantCulture));
                return true;
            }
            if (IsLeaf(value))
            {
                RuntimeInspectorJson.WriteString(sb, LeafText(value));
                return true;
            }
            return false;
        }

        // 写入字典，超出数量时包装为带 $count 的对象
        private void WriteDictionary(StringBuilder sb, IDictionary dict, int depth)
        {
            bool truncated = dict.Count > limit;
            if (truncated)
            {
                sb.Append("{\"$count\":").Append(dict.Count).Append(",\"$truncated\":true,\"$items\":");
            }

            sb.Append('{');
            int i = 0;
            foreach (DictionaryEntry entry in dict)
            {
                if (i >= limit) break;
                if (i++ > 0) sb.Append(',');
                RuntimeInspectorJson.WriteString(sb, IsLeaf(entry.Key) ? LeafText(entry.Key) : entry.Key.ToString());
                sb.Append(':');
                WriteSafe(sb, entry.Value, depth + 1);
            }
            sb.Append('}');

            if (truncated) sb.Append('}');
        }

        // 写入列表或其他可枚举集合，超出数量时包装为带 $count 的对象
        private void WriteEnumerable(StringBuilder sb, IEnumerable enumerable, int depth)
        {
            var items = new StringBuilder();
            int count = 0;
            items.Append('[');
            foreach (object item in enumerable)
            {
                if (count < limit)
                {
                    if (count > 0) items.Append(',');
                    WriteSafe(items, item, depth + 1);
                }
                count++;
            }
            items.Append(']');

            if (count > limit)
            {
                sb.Append("{\"$count\":").Append(count).Append(",\"$truncated\":true,\"$items\":").Append(items).Append('}');
            }
            else
            {
                sb.Append(items);
            }
        }

        // 写入普通对象，带 $type
        private void WriteObject(StringBuilder sb, object value, Type type, int depth)
        {
            sb.Append("{\"$type\":");
            RuntimeInspectorJson.WriteString(sb, RuntimeInspectorReflection.FriendlyName(type));
            foreach (var member in RuntimeInspectorReflection.GetDisplayMembers(type, includeProps))
            {
                sb.Append(',');
                RuntimeInspectorJson.WriteString(sb, member.Key);
                sb.Append(':');
                object memberValue;
                try
                {
                    memberValue = member.Value.Get(value);
                }
                catch (Exception e)
                {
                    RuntimeInspectorJson.WriteString(sb, "<error " + Unwrap(e).Message + ">");
                    continue;
                }
                WriteSafe(sb, memberValue, depth + 1);
            }
            sb.Append('}');
        }

        // 写入子值，异常时输出错误文本而不中断整体序列化
        private void WriteSafe(StringBuilder sb, object value, int depth)
        {
            int mark = sb.Length;
            try
            {
                Write(sb, value, depth);
            }
            catch (Exception e)
            {
                sb.Length = mark;
                RuntimeInspectorJson.WriteString(sb, "<error " + Unwrap(e).Message + ">");
            }
        }

        /// <summary>
        /// 解开反射调用包装的异常
        /// </summary>
        public static Exception Unwrap(Exception e)
        {
            while (e is System.Reflection.TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        /// <summary>
        /// 按成员名或叶子值搜索，返回匹配项 JSON 数组
        /// </summary>
        /// <param name="roots">搜索起点及其路径前缀</param>
        /// <param name="key">成员名或字典 key 包含的关键字，可空</param>
        /// <param name="valueText">叶子值文本包含的关键字，可空</param>
        /// <param name="searchDepth">最大搜索深度</param>
        /// <param name="maxResults">最多返回条数</param>
        public string Search(IEnumerable<KeyValuePair<string, object>> roots, string key, string valueText, int searchDepth, int maxResults)
        {
            var results = new List<string>();
            var visited = new HashSet<object>(ReferenceComparer.Instance);
            foreach (var root in roots)
            {
                SearchNode(root.Value, root.Key, null, key, valueText, 0, searchDepth, maxResults, results, visited);
                if (results.Count >= maxResults) break;
            }

            var sb = new StringBuilder();
            sb.Append("{\"count\":").Append(results.Count).Append(",\"reachedMax\":").Append(results.Count >= maxResults ? "true" : "false").Append(",\"items\":[");
            sb.Append(string.Join(",", results));
            sb.Append("]}");
            return sb.ToString();
        }

        // 深度优先搜索单个节点
        private void SearchNode(object value, string path, string memberName, string key, string valueText,
            int depth, int searchDepth, int maxResults, List<string> results, HashSet<object> visited)
        {
            if (results.Count >= maxResults) return;

            bool keyHit = !string.IsNullOrEmpty(key) && memberName != null && memberName.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
            bool leaf = IsLeaf(value);
            bool valueHit = !string.IsNullOrEmpty(valueText) && leaf && value != null
                            && LeafText(value).IndexOf(valueText, StringComparison.OrdinalIgnoreCase) >= 0;
            bool hit = string.IsNullOrEmpty(key) ? valueHit
                : string.IsNullOrEmpty(valueText) ? keyHit
                : keyHit && valueHit;

            if (hit)
            {
                var sb = new StringBuilder();
                sb.Append("{\"path\":");
                RuntimeInspectorJson.WriteString(sb, path);
                sb.Append(",\"type\":");
                RuntimeInspectorJson.WriteString(sb, value == null ? "null" : RuntimeInspectorReflection.FriendlyName(value.GetType()));
                sb.Append(",\"value\":");
                if (leaf)
                {
                    TryWriteLeaf(sb, value);
                }
                else
                {
                    RuntimeInspectorJson.WriteString(sb, Summary(value));
                }
                sb.Append('}');
                results.Add(sb.ToString());
            }

            if (leaf || depth >= searchDepth) return;
            if (!value.GetType().IsValueType && !visited.Add(value)) return;

            try
            {
                if (value is IDictionary dict)
                {
                    int i = 0;
                    foreach (DictionaryEntry entry in dict)
                    {
                        if (i++ >= limit) break;
                        string k = IsLeaf(entry.Key) ? LeafText(entry.Key) : entry.Key.ToString();
                        SearchNode(entry.Value, $"{path}[{QuoteKey(k)}]", k, key, valueText, depth + 1, searchDepth, maxResults, results, visited);
                    }
                }
                else if (value is IEnumerable enumerable)
                {
                    int i = 0;
                    foreach (object item in enumerable)
                    {
                        if (i >= limit) break;
                        SearchNode(item, $"{path}[{i}]", null, key, valueText, depth + 1, searchDepth, maxResults, results, visited);
                        i++;
                    }
                }
                else
                {
                    foreach (var member in RuntimeInspectorReflection.GetDisplayMembers(value.GetType(), includeProps))
                    {
                        object memberValue;
                        try
                        {
                            memberValue = member.Value.Get(value);
                        }
                        catch
                        {
                            continue;
                        }
                        string childPath = path.Length == 0 || path.EndsWith(":", StringComparison.Ordinal) ? path + member.Key : path + "." + member.Key;
                        SearchNode(memberValue, childPath, member.Key, key, valueText, depth + 1, searchDepth, maxResults, results, visited);
                    }
                }
            }
            catch
            {
                // 集合在遍历期间被修改等情况，跳过该节点
            }
        }

        // 字典 key 含特殊字符时加引号，保证路径可回填
        private static string QuoteKey(string key)
        {
            if (key.IndexOfAny(new[] { '.', '[', ']', '"', '\'', ' ' }) < 0) return key;
            return "\"" + key.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        // 按引用比较对象，用于循环检测
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new();

            public new bool Equals(object x, object y) => ReferenceEquals(x, y);

            public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
        }
    }
}
