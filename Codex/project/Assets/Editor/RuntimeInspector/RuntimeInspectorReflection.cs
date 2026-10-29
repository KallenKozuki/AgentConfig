using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using YQFramework.FUI;
using YQFramework.Model;
using YQFramework.Observable;
using YQFramework.Singleton;

namespace RuntimeInspector
{
    /// <summary>
    /// 路径片段，成员名或方括号索引
    /// </summary>
    public readonly struct PathSegment
    {
        /// <summary>
        /// 片段文本，索引片段已去除引号
        /// </summary>
        public readonly string Text;

        /// <summary>
        /// 是否为方括号索引
        /// </summary>
        public readonly bool IsIndex;

        /// <summary>
        /// 构造路径片段
        /// </summary>
        public PathSegment(string text, bool isIndex)
        {
            Text = text;
            IsIndex = isIndex;
        }

        /// <summary>
        /// 输出路径片段的原始写法
        /// </summary>
        public override string ToString()
        {
            return IsIndex ? $"[{Text}]" : Text;
        }
    }

    /// <summary>
    /// 从容器读取或写入一个子值的访问器
    /// </summary>
    public abstract class ValueAccessor
    {
        /// <summary>
        /// 子值的声明类型
        /// </summary>
        public abstract Type ValueType { get; }

        /// <summary>
        /// 是否允许写入
        /// </summary>
        public abstract bool CanWrite(bool force, out string reason);

        /// <summary>
        /// 读取子值
        /// </summary>
        public abstract object Get(object container);

        /// <summary>
        /// 写入子值
        /// </summary>
        public abstract void Set(object container, object value);
    }

    /// <summary>
    /// 字段访问器
    /// </summary>
    public sealed class FieldAccessor : ValueAccessor
    {
        private readonly FieldInfo field;

        /// <summary>
        /// 构造字段访问器
        /// </summary>
        public FieldAccessor(FieldInfo field)
        {
            this.field = field;
        }

        /// <inheritdoc />
        public override Type ValueType => field.FieldType;

        /// <inheritdoc />
        public override bool CanWrite(bool force, out string reason)
        {
            if (field.IsLiteral)
            {
                reason = $"字段 {field.Name} 是 const，无法写入";
                return false;
            }
            if (field.IsInitOnly && !force)
            {
                reason = $"字段 {RuntimeInspectorReflection.DisplayName(field)} 是 readonly，需要 force=true 才能写入";
                return false;
            }
            reason = null;
            return true;
        }

        /// <inheritdoc />
        public override object Get(object container) => field.GetValue(container);

        /// <inheritdoc />
        public override void Set(object container, object value) => field.SetValue(container, value);
    }

    /// <summary>
    /// 属性访问器
    /// </summary>
    public sealed class PropertyAccessor : ValueAccessor
    {
        private readonly PropertyInfo property;

        /// <summary>
        /// 构造属性访问器
        /// </summary>
        public PropertyAccessor(PropertyInfo property)
        {
            this.property = property;
        }

        /// <inheritdoc />
        public override Type ValueType => property.PropertyType;

        /// <inheritdoc />
        public override bool CanWrite(bool force, out string reason)
        {
            if (property.GetSetMethod(true) == null)
            {
                reason = $"属性 {property.Name} 没有 setter，且未找到对应的可写字段";
                return false;
            }
            reason = null;
            return true;
        }

        /// <inheritdoc />
        public override object Get(object container)
        {
            MethodInfo getter = property.GetGetMethod(true);
            if (getter == null) throw new InvalidOperationException($"属性 {property.Name} 没有 getter");
            return getter.Invoke(container, null);
        }

        /// <inheritdoc />
        public override void Set(object container, object value)
        {
            property.GetSetMethod(true).Invoke(container, new[] { value });
        }
    }

    /// <summary>
    /// 数组或 IList 下标访问器
    /// </summary>
    public sealed class ListIndexAccessor : ValueAccessor
    {
        private readonly int index;
        private readonly Type elementType;

        /// <summary>
        /// 构造下标访问器
        /// </summary>
        public ListIndexAccessor(int index, Type elementType)
        {
            this.index = index;
            this.elementType = elementType;
        }

        /// <inheritdoc />
        public override Type ValueType => elementType;

        /// <inheritdoc />
        public override bool CanWrite(bool force, out string reason)
        {
            reason = null;
            return true;
        }

        /// <inheritdoc />
        public override object Get(object container)
        {
            var list = (IList)container;
            if (index < 0 || index >= list.Count)
            {
                throw new IndexOutOfRangeException($"下标 {index} 越界，当前数量 {list.Count}");
            }
            return list[index];
        }

        /// <inheritdoc />
        public override void Set(object container, object value)
        {
            var list = (IList)container;
            if (index < 0 || index >= list.Count)
            {
                throw new IndexOutOfRangeException($"下标 {index} 越界，当前数量 {list.Count}");
            }
            list[index] = value;
        }
    }

    /// <summary>
    /// 字典键访问器
    /// </summary>
    public sealed class DictionaryKeyAccessor : ValueAccessor
    {
        private readonly object key;
        private readonly Type valueType;

        /// <summary>
        /// 构造字典键访问器
        /// </summary>
        public DictionaryKeyAccessor(object key, Type valueType)
        {
            this.key = key;
            this.valueType = valueType;
        }

        /// <inheritdoc />
        public override Type ValueType => valueType;

        /// <inheritdoc />
        public override bool CanWrite(bool force, out string reason)
        {
            reason = null;
            return true;
        }

        /// <inheritdoc />
        public override object Get(object container)
        {
            var dict = (IDictionary)container;
            if (!dict.Contains(key))
            {
                throw new KeyNotFoundException($"字典中不存在 key={key}，当前数量 {dict.Count}，示例 key：{RuntimeInspectorReflection.SampleKeys(dict, 10)}");
            }
            return dict[key];
        }

        /// <inheritdoc />
        public override void Set(object container, object value)
        {
            ((IDictionary)container)[key] = value;
        }
    }

    /// <summary>
    /// 非 IList 集合（如 HashSet）的枚举序号访问器，只读
    /// </summary>
    public sealed class EnumerableIndexAccessor : ValueAccessor
    {
        private readonly int index;
        private readonly Type elementType;

        /// <summary>
        /// 构造枚举序号访问器
        /// </summary>
        public EnumerableIndexAccessor(int index, Type elementType)
        {
            this.index = index;
            this.elementType = elementType;
        }

        /// <inheritdoc />
        public override Type ValueType => elementType;

        /// <inheritdoc />
        public override bool CanWrite(bool force, out string reason)
        {
            reason = "该集合不支持按下标写入，请使用 /collection 增删元素";
            return false;
        }

        /// <inheritdoc />
        public override object Get(object container)
        {
            int i = 0;
            foreach (object item in (IEnumerable)container)
            {
                if (i++ == index) return item;
            }
            throw new IndexOutOfRangeException($"下标 {index} 越界，当前数量 {i}");
        }

        /// <inheritdoc />
        public override void Set(object container, object value)
        {
            throw new InvalidOperationException("该集合不支持按下标写入");
        }
    }

    /// <summary>
    /// 路径解析结果中的一个节点
    /// </summary>
    public sealed class PathNode
    {
        /// <summary>
        /// 节点当前值
        /// </summary>
        public object Value;

        /// <summary>
        /// 从父节点取得该值的访问器，根节点为 null
        /// </summary>
        public ValueAccessor Accessor;
    }

    /// <summary>
    /// 数据模块定位、路径解析及类型反射工具
    /// </summary>
    public static class RuntimeInspectorReflection
    {
        private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const string BackingFieldSuffix = ">k__BackingField";
        private const string ProtoPrefix = "__pbn__";

        private static readonly Dictionary<Type, List<KeyValuePair<string, ValueAccessor>>> displayMemberCache = new();
        private static readonly Dictionary<Type, List<KeyValuePair<string, ValueAccessor>>> displayMemberWithPropsCache = new();

        /// <summary>
        /// 获取 ModelManager 中已创建的数据模块，不会触发单例或模块的创建
        /// </summary>
        public static Dictionary<string, ModelBase> GetModelDic(out string error)
        {
            FieldInfo instanceField = typeof(SingletonMono<ModelManager>).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
            if (instanceField == null)
            {
                error = "未找到 SingletonMono<ModelManager>.instance 字段，请同步更新 RuntimeInspectorReflection";
                return null;
            }

            var manager = instanceField.GetValue(null) as ModelManager;
            if (manager == null)
            {
                error = "ModelManager 尚未创建或已销毁，请确认游戏已启动并完成登录";
                return null;
            }

            FieldInfo dicField = typeof(ModelManager).GetField("modelDic", BindingFlags.Instance | BindingFlags.NonPublic);
            if (dicField == null)
            {
                error = "未找到 ModelManager.modelDic 字段，请同步更新 RuntimeInspectorReflection";
                return null;
            }

            error = null;
            return dicField.GetValue(manager) as Dictionary<string, ModelBase>;
        }

        /// <summary>
        /// 读取 Singleton 单例的当前实例，未创建时返回 null，不会触发创建
        /// </summary>
        public static T GetSingleton<T>() where T : class, new()
        {
            FieldInfo field = typeof(Singleton<T>).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
            return field?.GetValue(null) as T;
        }

        /// <summary>
        /// 获取 UIManager 中已创建的界面，不会触发单例创建
        /// </summary>
        public static Dictionary<UIType, UICtrl> GetUIDic(out UIManager manager, out string error)
        {
            manager = GetSingleton<UIManager>();
            if (manager == null)
            {
                error = "UIManager 尚未创建，请确认游戏已启动";
                return null;
            }

            FieldInfo dicField = typeof(UIManager).GetField("uiDic", BindingFlags.Instance | BindingFlags.NonPublic);
            if (dicField == null)
            {
                error = "未找到 UIManager.uiDic 字段，请同步更新 RuntimeInspectorReflection";
                return null;
            }

            error = null;
            return dicField.GetValue(manager) as Dictionary<UIType, UICtrl>;
        }

        /// <summary>
        /// 界面是否可见，判断规则与 UIManager.IsVisible 一致
        /// </summary>
        public static bool IsUIVisible(UIManager manager, UIType uiType)
        {
            try
            {
                return manager.IsVisible(uiType);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// 按界面名查找已创建的界面：先匹配 UIType 枚举名或界面组件类名（忽略大小写），再按唯一的包含匹配
        /// </summary>
        public static UICtrl FindUI(string name, out UIType uiType, out string error)
        {
            uiType = default;
            if (string.IsNullOrEmpty(name))
            {
                error = "缺少界面名 ui";
                return null;
            }

            Dictionary<UIType, UICtrl> dic = GetUIDic(out UIManager manager, out error);
            if (dic == null) return null;

            foreach (var kv in dic)
            {
                if (kv.Value != null && string.Equals(kv.Key.ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    uiType = kv.Key;
                    return kv.Value;
                }
            }

            FieldInfo viewNameField = typeof(UIManager).GetField("viewNameToTypeDic", BindingFlags.Instance | BindingFlags.NonPublic);
            if (viewNameField?.GetValue(manager) is Dictionary<string, UIType> viewNameDic)
            {
                foreach (var kv in viewNameDic)
                {
                    if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase) && dic.TryGetValue(kv.Value, out UICtrl byView) && byView != null)
                    {
                        uiType = kv.Value;
                        return byView;
                    }
                }
            }

            List<KeyValuePair<UIType, UICtrl>> fuzzy = dic
                .Where(kv => kv.Value != null && kv.Key.ToString().IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            if (fuzzy.Count == 1)
            {
                uiType = fuzzy[0].Key;
                return fuzzy[0].Value;
            }

            if (fuzzy.Count > 1)
            {
                error = $"界面名 '{name}' 匹配到多个：{string.Join(", ", fuzzy.Select(kv => kv.Key))}";
            }
            else
            {
                IEnumerable<string> opened = dic.Where(kv => kv.Value != null)
                    .OrderByDescending(kv => IsUIVisible(manager, kv.Key))
                    .Select(kv => kv.Key.ToString())
                    .Take(40);
                error = $"界面 '{name}' 当前未创建（未打开过或已销毁）。已创建的界面：{string.Join(", ", opened)}";
            }
            return null;
        }

        /// <summary>
        /// 按数据模块名或界面名定位根对象；指定界面名时返回该界面的 UIModel
        /// </summary>
        /// <param name="name">数据模块名</param>
        /// <param name="ui">界面名，非空时优先使用</param>
        /// <param name="label">定位结果的展示名</param>
        /// <param name="error">失败原因</param>
        public static object ResolveRoot(string name, string ui, out string label, out string error)
        {
            label = null;
            if (!string.IsNullOrEmpty(ui))
            {
                UICtrl ctrl = FindUI(ui, out UIType uiType, out error);
                if (ctrl == null) return null;
                if (ctrl.UIModel == null)
                {
                    error = $"界面 {uiType} 没有 UIModel";
                    return null;
                }
                label = $"{uiType}/{ctrl.UIModel.GetType().Name}";
                return ctrl.UIModel;
            }

            Dictionary<string, ModelBase> dic = GetModelDic(out error);
            if (dic == null) return null;
            ModelBase model = FindModel(dic, name, out error);
            if (model == null) return null;
            label = model.GetType().Name;
            return model;
        }

        /// <summary>
        /// 按类型名查找模块：先精确匹配短名或全名（忽略大小写），再按唯一的包含匹配
        /// </summary>
        public static ModelBase FindModel(Dictionary<string, ModelBase> dic, string name, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(name))
            {
                error = "缺少模块名 name";
                return null;
            }

            List<ModelBase> models = dic.Values.Where(m => m != null).ToList();
            ModelBase exact = models.FirstOrDefault(m =>
                string.Equals(m.GetType().Name, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(m.GetType().FullName, name, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            List<ModelBase> fuzzy = models.Where(m => m.GetType().Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (fuzzy.Count == 1) return fuzzy[0];

            if (fuzzy.Count > 1)
            {
                error = $"模块名 '{name}' 匹配到多个：{string.Join(", ", fuzzy.Select(m => m.GetType().Name))}";
            }
            else
            {
                error = $"未找到模块 '{name}'。模块只在首次 GetModel 后才会创建，可先调用 /models 查看已创建列表";
            }
            return null;
        }

        /// <summary>
        /// 解析路径文本，支持 a.b[3].c["key"] 与 a.b[key]
        /// </summary>
        public static List<PathSegment> ParsePath(string path)
        {
            var result = new List<PathSegment>();
            if (string.IsNullOrWhiteSpace(path)) return result;

            int i = 0;
            while (i < path.Length)
            {
                char c = path[i];
                if (c == '.' || char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                if (c == '[')
                {
                    i++;
                    var sb = new StringBuilder();
                    if (i < path.Length && (path[i] == '"' || path[i] == '\''))
                    {
                        char quote = path[i++];
                        while (i < path.Length && path[i] != quote)
                        {
                            if (path[i] == '\\' && i + 1 < path.Length) i++;
                            sb.Append(path[i++]);
                        }
                        i++;
                        while (i < path.Length && path[i] != ']') i++;
                    }
                    else
                    {
                        while (i < path.Length && path[i] != ']') sb.Append(path[i++]);
                    }
                    if (i >= path.Length) throw new FormatException($"路径 '{path}' 缺少 ']'");
                    i++;
                    result.Add(new PathSegment(sb.ToString().Trim(), true));
                    continue;
                }

                int start = i;
                while (i < path.Length && path[i] != '.' && path[i] != '[') i++;
                result.Add(new PathSegment(path.Substring(start, i - start).Trim(), false));
            }
            return result;
        }

        /// <summary>
        /// 从根对象沿路径逐级解析，返回包含根节点在内的全部节点
        /// </summary>
        public static List<PathNode> Navigate(object root, IList<PathSegment> segments, bool preferSetter = false)
        {
            var nodes = new List<PathNode> { new PathNode { Value = root } };
            object current = root;
            string walked = "";
            foreach (PathSegment seg in segments)
            {
                if (current == null)
                {
                    throw new NullReferenceException($"路径 '{walked}' 的值为 null，无法继续访问 {seg}");
                }

                ValueAccessor accessor = CreateAccessor(current, seg, preferSetter);
                current = accessor.Get(current);
                walked += seg.IsIndex ? seg.ToString() : (walked.Length == 0 ? seg.Text : "." + seg.Text);
                nodes.Add(new PathNode { Value = current, Accessor = accessor });
            }
            return nodes;
        }

        /// <summary>
        /// 根据容器类型和路径片段创建访问器
        /// </summary>
        public static ValueAccessor CreateAccessor(object container, PathSegment seg, bool preferSetter = false)
        {
            Type type = container.GetType();
            if (seg.IsIndex)
            {
                if (container is IDictionary dict)
                {
                    GetDictionaryTypes(type, out Type keyType, out Type valueType);
                    object key = RuntimeInspectorWriter.ConvertTo(seg.Text, keyType);
                    return new DictionaryKeyAccessor(key, valueType);
                }

                if (!int.TryParse(seg.Text, out int index))
                {
                    throw new FormatException($"{FriendlyName(type)} 不是字典，下标 '{seg.Text}' 必须是整数");
                }
                if (container is IList)
                {
                    return new ListIndexAccessor(index, GetElementType(type));
                }
                if (container is IEnumerable && !(container is string))
                {
                    return new EnumerableIndexAccessor(index, GetElementType(type));
                }
                throw new InvalidOperationException($"{FriendlyName(type)} 不是集合，不能使用下标 [{seg.Text}]");
            }

            ValueAccessor member = FindMember(type, seg.Text, preferSetter);
            if (member == null)
            {
                string hint = container is IDictionary ? "；字典请使用 [key] 访问" : "";
                throw new MissingMemberException($"{FriendlyName(type)} 上找不到成员 '{seg.Text}'{hint}。可用成员：{string.Join(", ", GetDisplayMembers(type, false).Select(m => m.Key).Take(40))}");
            }
            return member;
        }

        /// <summary>
        /// 查找成员：字段、自动属性的隐藏字段、属性，最后忽略大小写重试
        /// </summary>
        public static ValueAccessor FindMember(Type type, string name, bool preferSetter = false)
        {
            return FindMember(type, name, preferSetter, StringComparison.Ordinal)
                ?? FindMember(type, name, preferSetter, StringComparison.OrdinalIgnoreCase);
        }

        // 按指定比较方式在继承链上查找成员
        private static ValueAccessor FindMember(Type type, string name, bool preferSetter, StringComparison comparison)
        {
            if (preferSetter)
            {
                PropertyInfo setterProp = FindProperty(type, name, comparison);
                if (setterProp != null && setterProp.GetSetMethod(true) != null) return new PropertyAccessor(setterProp);
            }

            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(InstanceFlags))
                {
                    if (string.Equals(f.Name, name, comparison) || string.Equals(f.Name, "<" + name + BackingFieldSuffix, comparison))
                    {
                        return new FieldAccessor(f);
                    }
                }
            }

            PropertyInfo prop = FindProperty(type, name, comparison);
            return prop != null ? new PropertyAccessor(prop) : null;
        }

        // 在继承链上查找无索引参数的属性
        private static PropertyInfo FindProperty(Type type, string name, StringComparison comparison)
        {
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (PropertyInfo p in t.GetProperties(InstanceFlags))
                {
                    if (p.GetIndexParameters().Length == 0 && string.Equals(p.Name, name, comparison)) return p;
                }
            }
            return null;
        }

        /// <summary>
        /// 获取用于展示的成员列表；protobuf 消息优先使用 ProtoMember 属性，普通对象使用字段
        /// </summary>
        public static List<KeyValuePair<string, ValueAccessor>> GetDisplayMembers(Type type, bool includeProps)
        {
            var cache = includeProps ? displayMemberWithPropsCache : displayMemberCache;
            if (cache.TryGetValue(type, out var cached)) return cached;

            var result = new List<KeyValuePair<string, ValueAccessor>>();
            var names = new HashSet<string>();
            bool isProto = IsProtoMessage(type);

            if (isProto)
            {
                foreach (PropertyInfo p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (p.GetIndexParameters().Length == 0 && HasAttribute(p, "ProtoMemberAttribute") && names.Add(p.Name))
                    {
                        result.Add(new KeyValuePair<string, ValueAccessor>(p.Name, new PropertyAccessor(p)));
                    }
                }
            }

            for (Type t = type; t != null && t != typeof(object) && t != typeof(ObservableObject); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(InstanceFlags))
                {
                    if (isProto && f.Name.StartsWith(ProtoPrefix, StringComparison.Ordinal)) continue;
                    string display = DisplayName(f);
                    if (!names.Add(display)) continue;
                    result.Add(new KeyValuePair<string, ValueAccessor>(display, new FieldAccessor(f)));
                }
            }

            if (includeProps)
            {
                for (Type t = type; t != null && t != typeof(object) && t != typeof(ObservableObject); t = t.BaseType)
                {
                    foreach (PropertyInfo p in t.GetProperties(InstanceFlags))
                    {
                        if (p.GetIndexParameters().Length > 0 || p.GetGetMethod(true) == null) continue;
                        if (!names.Add(p.Name)) continue;
                        result.Add(new KeyValuePair<string, ValueAccessor>(p.Name, new PropertyAccessor(p)));
                    }
                }
            }

            cache[type] = result;
            return result;
        }

        /// <summary>
        /// 字段展示名，自动属性的隐藏字段还原为属性名
        /// </summary>
        public static string DisplayName(FieldInfo field)
        {
            string name = field.Name;
            if (name.Length > 0 && name[0] == '<' && name.EndsWith(BackingFieldSuffix, StringComparison.Ordinal))
            {
                return name.Substring(1, name.Length - 1 - BackingFieldSuffix.Length);
            }
            return name;
        }

        /// <summary>
        /// 是否为 protobuf-net 生成的消息类型
        /// </summary>
        public static bool IsProtoMessage(Type type)
        {
            return type.GetInterfaces().Any(i => i.FullName == "ProtoBuf.IExtensible");
        }

        /// <summary>
        /// 获取集合元素类型，无法确定时返回 object
        /// </summary>
        public static Type GetElementType(Type type)
        {
            if (type.IsArray) return type.GetElementType();
            Type generic = FindGenericInterface(type, typeof(IEnumerable<>));
            return generic != null ? generic.GetGenericArguments()[0] : typeof(object);
        }

        /// <summary>
        /// 获取字典的键值类型，无法确定时返回 object
        /// </summary>
        public static void GetDictionaryTypes(Type type, out Type keyType, out Type valueType)
        {
            Type generic = FindGenericInterface(type, typeof(IDictionary<,>));
            if (generic != null)
            {
                Type[] args = generic.GetGenericArguments();
                keyType = args[0];
                valueType = args[1];
                return;
            }
            keyType = typeof(object);
            valueType = typeof(object);
        }

        /// <summary>
        /// 查找类型实现的指定泛型接口
        /// </summary>
        public static Type FindGenericInterface(Type type, Type genericDefinition)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition() == genericDefinition) return type;
            return type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == genericDefinition);
        }

        /// <summary>
        /// 输出易读的类型名，如 List&lt;Int32&gt;
        /// </summary>
        public static string FriendlyName(Type type)
        {
            if (type == null) return "null";
            if (type.IsArray) return FriendlyName(type.GetElementType()) + "[]";
            if (!type.IsGenericType) return type.Name;

            Type underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) return FriendlyName(underlying) + "?";

            string name = type.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            return name + "<" + string.Join(",", type.GetGenericArguments().Select(FriendlyName)) + ">";
        }

        /// <summary>
        /// 拼接字典的示例 key，用于错误提示
        /// </summary>
        public static string SampleKeys(IDictionary dict, int max)
        {
            var keys = new List<string>();
            foreach (object k in dict.Keys)
            {
                if (keys.Count >= max)
                {
                    keys.Add("...");
                    break;
                }
                keys.Add(Convert.ToString(k, System.Globalization.CultureInfo.InvariantCulture));
            }
            return string.Join(", ", keys);
        }

        // 判断成员是否带有指定名称的特性
        private static bool HasAttribute(MemberInfo member, string attributeName)
        {
            return member.GetCustomAttributes(true).Any(a => a.GetType().Name == attributeName);
        }
    }
}
