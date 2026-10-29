using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;

namespace RuntimeInspector
{
    /// <summary>
    /// 运行时数据写入：路径赋值、集合增删及 JSON 到目标类型的转换
    /// </summary>
    public static class RuntimeInspectorWriter
    {
        /// <summary>
        /// 按路径写入值，路径经过 struct 时逐级写回
        /// </summary>
        /// <param name="root">模块实例</param>
        /// <param name="path">字段路径，不能为空</param>
        /// <param name="json">解析后的 JSON 值</param>
        /// <param name="force">是否允许写 readonly 字段</param>
        /// <param name="merge">对象类型是否在现有实例上合并，而不是整体替换</param>
        /// <param name="useSetter">是否优先调用属性 setter（会触发属性变更通知等逻辑）</param>
        /// <param name="oldValue">写入前的值</param>
        /// <returns>写入后的值</returns>
        public static object SetValue(object root, string path, object json, bool force, bool merge, bool useSetter, out object oldValue)
        {
            List<PathSegment> segments = RuntimeInspectorReflection.ParsePath(path);
            if (segments.Count == 0) throw new ArgumentException("path 不能为空，不允许替换整个模块");

            List<PathNode> parents = RuntimeInspectorReflection.Navigate(root, segments.GetRange(0, segments.Count - 1));
            object container = parents[parents.Count - 1].Value;
            if (container == null) throw new NullReferenceException($"'{path}' 的父级为 null");

            ValueAccessor accessor = RuntimeInspectorReflection.CreateAccessor(container, segments[segments.Count - 1], useSetter);
            if (!accessor.CanWrite(force, out string reason)) throw new InvalidOperationException(reason);

            try
            {
                oldValue = accessor.Get(container);
            }
            catch (KeyNotFoundException)
            {
                oldValue = null;
            }

            object newValue = ConvertTo(json, accessor.ValueType, merge ? oldValue : null);
            accessor.Set(container, newValue);
            WriteBackStructs(parents, force);
            return accessor.Get(container);
        }

        /// <summary>
        /// 对集合执行 add / remove / clear
        /// </summary>
        /// <param name="root">模块实例</param>
        /// <param name="path">集合路径</param>
        /// <param name="op">add、remove 或 clear</param>
        /// <param name="key">字典 key，可空</param>
        /// <param name="index">列表下标，小于 0 表示未指定</param>
        /// <param name="json">元素值</param>
        /// <param name="hasValue">请求是否携带 value</param>
        /// <returns>操作后的集合数量</returns>
        public static int ModifyCollection(object root, string path, string op, object key, int index, object json, bool hasValue)
        {
            List<PathNode> nodes = RuntimeInspectorReflection.Navigate(root, RuntimeInspectorReflection.ParsePath(path));
            object target = nodes[nodes.Count - 1].Value;
            if (target == null) throw new NullReferenceException($"'{path}' 为 null，可先用 /set 赋值为 [] 或 {{}}");

            Type type = target.GetType();
            op = (op ?? "").ToLowerInvariant();

            if (target is IDictionary dict)
            {
                RuntimeInspectorReflection.GetDictionaryTypes(type, out Type keyType, out Type valueType);
                switch (op)
                {
                    case "add":
                    case "set":
                        if (key == null) throw new ArgumentException("字典 add 需要 key");
                        dict[ConvertTo(key, keyType)] = ConvertTo(json, valueType);
                        break;
                    case "remove":
                        if (key == null) throw new ArgumentException("字典 remove 需要 key");
                        dict.Remove(ConvertTo(key, keyType));
                        break;
                    case "clear":
                        dict.Clear();
                        break;
                    default:
                        throw new ArgumentException($"不支持的 op '{op}'，可选 add/remove/clear");
                }
                return dict.Count;
            }

            if (type.IsArray) throw new InvalidOperationException("数组长度固定，请用 /set 整体替换");

            Type elementType = RuntimeInspectorReflection.GetElementType(type);
            if (target is IList list)
            {
                switch (op)
                {
                    case "add":
                        object item = ConvertTo(json, elementType);
                        if (index >= 0) list.Insert(index, item);
                        else list.Add(item);
                        break;
                    case "remove":
                        if (index >= 0) list.RemoveAt(index);
                        else if (hasValue) list.Remove(ConvertTo(json, elementType));
                        else throw new ArgumentException("列表 remove 需要 index 或 value");
                        break;
                    case "clear":
                        list.Clear();
                        break;
                    default:
                        throw new ArgumentException($"不支持的 op '{op}'，可选 add/remove/clear");
                }
                return list.Count;
            }

            string methodName = op switch
            {
                "add" => "Add",
                "remove" => "Remove",
                "clear" => "Clear",
                _ => throw new ArgumentException($"不支持的 op '{op}'，可选 add/remove/clear"),
            };
            MethodInfo method = op == "clear"
                ? type.GetMethod(methodName, Type.EmptyTypes)
                : type.GetMethod(methodName, new[] { elementType });
            if (method == null) throw new InvalidOperationException($"{RuntimeInspectorReflection.FriendlyName(type)} 不支持 {methodName}");
            method.Invoke(target, op == "clear" ? null : new[] { ConvertTo(json, elementType) });

            PropertyInfo countProp = type.GetProperty("Count");
            return countProp != null ? (int)countProp.GetValue(target) : -1;
        }

        /// <summary>
        /// 将解析后的 JSON 值转换为目标类型
        /// </summary>
        /// <param name="json">JSON 值或路径中的字符串</param>
        /// <param name="target">目标类型</param>
        /// <param name="existing">合并模式下的现有实例，可空</param>
        public static object ConvertTo(object json, Type target, object existing = null)
        {
            if (target == typeof(object)) return json;

            if (json == null)
            {
                if (!target.IsValueType || Nullable.GetUnderlyingType(target) != null) return null;
                throw new InvalidCastException($"{RuntimeInspectorReflection.FriendlyName(target)} 不能为 null");
            }

            Type underlying = Nullable.GetUnderlyingType(target);
            if (underlying != null) target = underlying;

            if (target.IsInstanceOfType(json) && !(json is IDictionary<string, object>) && !(json is List<object>)) return json;

            if (target == typeof(string)) return Convert.ToString(json, CultureInfo.InvariantCulture);

            if (target.IsEnum)
            {
                if (json is string s)
                {
                    return long.TryParse(s, out long n) ? Enum.ToObject(target, n) : Enum.Parse(target, s, true);
                }
                return Enum.ToObject(target, Convert.ToInt64(json, CultureInfo.InvariantCulture));
            }

            if (target == typeof(bool) && json is string boolText) return bool.Parse(boolText);
            if (target == typeof(char)) return Convert.ToString(json, CultureInfo.InvariantCulture)[0];
            if (target.IsPrimitive || target == typeof(decimal))
            {
                return Convert.ChangeType(json, target, CultureInfo.InvariantCulture);
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(target) || typeof(Delegate).IsAssignableFrom(target))
            {
                throw new InvalidOperationException($"不支持写入 {RuntimeInspectorReflection.FriendlyName(target)}");
            }

            if (target.IsArray)
            {
                List<object> items = RequireList(json, target);
                Type elementType = target.GetElementType();
                Array array = Array.CreateInstance(elementType, items.Count);
                for (int i = 0; i < items.Count; i++) array.SetValue(ConvertTo(items[i], elementType), i);
                return array;
            }

            if (RuntimeInspectorReflection.FindGenericInterface(target, typeof(IDictionary<,>)) != null)
            {
                var obj = RequireObject(json, target);
                var dict = (IDictionary)CreateInstance(target);
                RuntimeInspectorReflection.GetDictionaryTypes(target, out Type keyType, out Type valueType);
                foreach (var kv in obj) dict[ConvertTo(kv.Key, keyType)] = ConvertTo(kv.Value, valueType);
                return dict;
            }

            if (typeof(IEnumerable).IsAssignableFrom(target))
            {
                List<object> items = RequireList(json, target);
                object collection = CreateInstance(target);
                FillCollection(collection, items);
                return collection;
            }

            return ConvertObject(RequireObject(json, target), target, existing);
        }

        // 将 JSON 对象写入新实例或现有实例的成员
        private static object ConvertObject(Dictionary<string, object> obj, Type target, object existing)
        {
            object instance = existing != null && target.IsInstanceOfType(existing) ? existing : CreateInstance(target);
            foreach (var kv in obj)
            {
                if (kv.Key.StartsWith("$", StringComparison.Ordinal)) continue;

                ValueAccessor accessor = RuntimeInspectorReflection.FindMember(instance.GetType(), kv.Key);
                if (accessor == null)
                {
                    throw new MissingMemberException($"{RuntimeInspectorReflection.FriendlyName(target)} 上找不到成员 '{kv.Key}'");
                }

                object current = accessor.Get(instance);
                if (accessor.CanWrite(true, out _))
                {
                    bool nestedMerge = existing != null && current != null && !(current is IEnumerable);
                    accessor.Set(instance, ConvertTo(kv.Value, accessor.ValueType, nestedMerge ? current : null));
                }
                else if (current is IDictionary currentDict && kv.Value is Dictionary<string, object> dictJson)
                {
                    RuntimeInspectorReflection.GetDictionaryTypes(current.GetType(), out Type keyType, out Type valueType);
                    currentDict.Clear();
                    foreach (var item in dictJson) currentDict[ConvertTo(item.Key, keyType)] = ConvertTo(item.Value, valueType);
                }
                else if (current is IEnumerable && !(current is string) && kv.Value is List<object> listJson)
                {
                    FillCollection(current, listJson, true);
                }
                else
                {
                    throw new InvalidOperationException($"成员 '{kv.Key}' 不可写");
                }
            }
            return instance;
        }

        // 向集合实例填充元素，通过 Add 方法兼容 List、HashSet 等
        private static void FillCollection(object collection, List<object> items, bool clearFirst = false)
        {
            Type type = collection.GetType();
            Type elementType = RuntimeInspectorReflection.GetElementType(type);
            if (clearFirst) type.GetMethod("Clear", Type.EmptyTypes)?.Invoke(collection, null);

            MethodInfo add = type.GetMethod("Add", new[] { elementType });
            if (add == null) throw new InvalidOperationException($"{RuntimeInspectorReflection.FriendlyName(type)} 没有 Add 方法");
            foreach (object item in items) add.Invoke(collection, new[] { ConvertTo(item, elementType) });
        }

        // 路径经过的 struct 是装箱副本，需要从叶子向上逐级写回父容器
        private static void WriteBackStructs(List<PathNode> nodes, bool force)
        {
            for (int i = nodes.Count - 1; i >= 1; i--)
            {
                object value = nodes[i].Value;
                if (value == null || !value.GetType().IsValueType) return;

                ValueAccessor accessor = nodes[i].Accessor;
                if (!accessor.CanWrite(force, out string reason))
                {
                    throw new InvalidOperationException($"路径经过 struct，需要写回父级但失败：{reason}");
                }
                accessor.Set(nodes[i - 1].Value, value);
            }
        }

        // 创建实例，无无参构造时跳过构造函数
        private static object CreateInstance(Type type)
        {
            if (type.IsInterface || type.IsAbstract)
            {
                throw new InvalidOperationException($"无法创建抽象类型 {RuntimeInspectorReflection.FriendlyName(type)}");
            }
            if (type.IsValueType || type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null) != null)
            {
                return Activator.CreateInstance(type, true);
            }
            return FormatterServices.GetUninitializedObject(type);
        }

        // 校验并返回 JSON 数组
        private static List<object> RequireList(object json, Type target)
        {
            if (json is List<object> list) return list;
            throw new InvalidCastException($"{RuntimeInspectorReflection.FriendlyName(target)} 需要 JSON 数组");
        }

        // 校验并返回 JSON 对象
        private static Dictionary<string, object> RequireObject(object json, Type target)
        {
            if (json is Dictionary<string, object> obj) return obj;
            throw new InvalidCastException($"{RuntimeInspectorReflection.FriendlyName(target)} 需要 JSON 对象");
        }
    }
}
