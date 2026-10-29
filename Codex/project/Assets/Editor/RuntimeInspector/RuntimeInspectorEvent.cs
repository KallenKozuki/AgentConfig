using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEditor;
using UnityEngine;
using YQFramework.Event;
using YQFramework.FUI;
using YQFramework.Model;
using YQFramework.Pool.ReferencePool;

namespace RuntimeInspector
{
    /// <summary>
    /// 事件查询、参数结构生成，以及按 JSON 构造参数派发并生成响应报告
    /// </summary>
    public static class RuntimeInspectorEvent
    {
        private const int MaxWaitFrames = 300;
        private const double MaxWaitSeconds = 10;
        private const int MaxLogs = 100;
        private const int MaxMessageLength = 2000;
        private const int MaxStackLength = 4000;
        private const int MaxChangesPerRoot = 200;
        private const int SnapshotLimit = 200;
        private const int TemplateMaxDepth = 6;
        private const int MaxSchemaTypes = 200;
        private const string LogTag = "[RuntimeInspector]";

        private static List<Type> eventTypes;
        private static DispatchSession session;

        /// <summary>
        /// 按名称关键字列出事件类型
        /// </summary>
        /// <param name="filter">事件类名关键字，可空</param>
        /// <param name="max">最多返回条数</param>
        public static string List(string filter, int max)
        {
            var sb = new StringBuilder("{\"items\":[");
            int count = 0;
            int total = 0;
            foreach (Type type in GetEventTypes())
            {
                if (!string.IsNullOrEmpty(filter) && type.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                total++;
                if (count >= max) continue;
                if (count++ > 0) sb.Append(',');

                string eventId = GetEventId(type);
                sb.Append("{\"event\":");
                RuntimeInspectorJson.WriteString(sb, type.Name);
                if (type.Namespace != "YQFramework.Event")
                {
                    sb.Append(",\"fullName\":");
                    RuntimeInspectorJson.WriteString(sb, type.FullName);
                }
                if (eventId != type.FullName)
                {
                    sb.Append(",\"eventId\":");
                    RuntimeInspectorJson.WriteString(sb, eventId);
                }
                sb.Append(",\"protocol\":").Append(GetMessageType(type) != null ? "true" : "false");
                sb.Append(",\"listeners\":").Append(GetListeners(eventId).Length);
                sb.Append('}');
            }
            sb.Append("],\"total\":").Append(total).Append(",\"truncated\":").Append(total > count ? "true" : "false").Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 生成事件参数结构：成员类型表、枚举可选值、当前监听者及默认值 JSON 模板
        /// </summary>
        /// <param name="name">事件类名、全名或协议消息名</param>
        public static string Schema(string name)
        {
            Type type = FindEventType(name, out string error);
            if (type == null) throw new ArgumentException(error);

            var types = new List<Type>();
            var enums = new List<Type>();
            CollectTypes(type, types, enums);

            string eventId = GetEventId(type);
            var sb = new StringBuilder("{\"event\":");
            RuntimeInspectorJson.WriteString(sb, type.Name);
            sb.Append(",\"eventId\":");
            RuntimeInspectorJson.WriteString(sb, eventId);
            sb.Append(",\"protocol\":").Append(GetMessageType(type) != null ? "true" : "false");
            sb.Append(",\"listeners\":");
            WriteListeners(sb, GetListeners(eventId));

            sb.Append(",\"types\":{");
            for (int i = 0; i < types.Count; i++)
            {
                if (i > 0) sb.Append(',');
                RuntimeInspectorJson.WriteString(sb, RuntimeInspectorReflection.FriendlyName(types[i]));
                sb.Append(":{");
                List<KeyValuePair<string, Type>> members = GetSchemaMembers(types[i]);
                for (int j = 0; j < members.Count; j++)
                {
                    if (j > 0) sb.Append(',');
                    RuntimeInspectorJson.WriteString(sb, members[j].Key);
                    sb.Append(':');
                    RuntimeInspectorJson.WriteString(sb, RuntimeInspectorReflection.FriendlyName(members[j].Value));
                }
                sb.Append('}');
            }
            sb.Append('}');
            if (types.Count >= MaxSchemaTypes) sb.Append(",\"typesTruncated\":true");

            sb.Append(",\"enums\":{");
            for (int i = 0; i < enums.Count; i++)
            {
                if (i > 0) sb.Append(',');
                RuntimeInspectorJson.WriteString(sb, RuntimeInspectorReflection.FriendlyName(enums[i]));
                sb.Append(":[");
                string[] names = Enum.GetNames(enums[i]);
                for (int j = 0; j < names.Length; j++)
                {
                    if (j > 0) sb.Append(',');
                    RuntimeInspectorJson.WriteString(sb, names[j]);
                }
                sb.Append(']');
            }
            sb.Append('}');

            sb.Append(",\"template\":");
            RuntimeInspectorJson.WriteRaw(sb, Template(type, 0, new HashSet<Type>()));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 按 JSON 构造事件参数并通过 EventManager 派发，等待指定帧数后通过回调返回响应报告
        /// </summary>
        /// <param name="name">事件类名、全名或协议消息名</param>
        /// <param name="value">事件参数 JSON 对象，未给出的成员为默认值</param>
        /// <param name="waitFrames">派发后等待的帧数，用于覆盖异步刷新</param>
        /// <param name="watch">额外比较的根对象，逗号分隔；数据模块直接写名字，界面写 ui:界面名</param>
        /// <param name="depth">比较快照的序列化深度</param>
        /// <param name="complete">完成回调：报告 JSON，失败原因</param>
        public static void Dispatch(string name, object value, int waitFrames, string watch, int depth, Action<string, string> complete)
        {
            if (session != null) throw new InvalidOperationException("上一次派发尚未完成，请稍后重试");

            Type type = FindEventType(name, out string error);
            if (type == null) throw new ArgumentException(error);
            if (value != null && !(value is Dictionary<string, object>)) throw new ArgumentException("value 必须是 JSON 对象");

            EventManager manager = RuntimeInspectorReflection.GetSingleton<EventManager>();
            if (manager == null) throw new InvalidOperationException("EventManager 尚未创建，请确认游戏已启动");

            string eventId = GetEventId(type);
            Delegate[] listeners = GetListeners(eventId);
            var s = new DispatchSession
            {
                EventName = type.Name,
                EventId = eventId,
                Listeners = listeners,
                WaitFrames = Math.Max(0, Math.Min(MaxWaitFrames, waitFrames)),
                Depth = Math.Max(1, depth),
                Complete = complete,
            };
            CollectRoots(s, listeners, watch);

            GameEventArgs args = CreateArgs(type, value as Dictionary<string, object>);

            foreach (SnapshotRoot root in s.Roots) root.Before = Snapshot(root.Value, s.Depth);
            s.UIBefore = SnapshotUI();
            s.StartFrame = Time.frameCount;
            s.StartTime = EditorApplication.timeSinceStartup;
            Application.logMessageReceived += s.OnLog;
            session = s;

            try
            {
                manager.DispatchEvent(eventId, null, args);
            }
            catch (Exception e)
            {
                s.Exception = e;
            }

            if (s.WaitFrames == 0 || EditorApplication.isPaused) Finish();
        }

        /// <summary>
        /// 每帧由服务主循环调用，等待结束后生成报告
        /// </summary>
        public static void Tick()
        {
            DispatchSession s = session;
            if (s == null) return;

            bool done = !EditorApplication.isPlaying
                        || EditorApplication.isPaused
                        || Time.frameCount - s.StartFrame >= s.WaitFrames
                        || EditorApplication.timeSinceStartup - s.StartTime >= MaxWaitSeconds;
            if (done) Finish();
        }

        /// <summary>
        /// 中止进行中的派发等待，以失败结果返回
        /// </summary>
        public static void Abort(string reason)
        {
            DispatchSession s = session;
            if (s == null) return;
            session = null;
            Application.logMessageReceived -= s.OnLog;
            s.Complete(null, reason);
        }

        /// <summary>
        /// 按事件类名、全名、去掉 EventArgs 后缀的名字或协议消息名查找事件类型，最后按唯一的包含匹配
        /// </summary>
        public static Type FindEventType(string name, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(name))
            {
                error = "缺少事件名 event";
                return null;
            }

            List<Type> types = GetEventTypes();
            List<Type> exact = types.Where(t =>
                    string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.FullName, name, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(t.Name, name + "EventArgs", StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exact.Count == 1) return exact[0];

            if (exact.Count == 0)
            {
                List<Type> byMessage = types.Where(t => GetMessageType(t) is Type m && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (byMessage.Count == 1) return byMessage[0];
            }

            List<Type> candidates = exact.Count > 1
                ? exact
                : types.Where(t => t.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (candidates.Count == 1) return candidates[0];

            error = candidates.Count > 1
                ? $"事件名 '{name}' 匹配到 {candidates.Count} 个：{string.Join(", ", candidates.Take(20).Select(t => t.FullName))}"
                : $"未找到事件 '{name}'，可用 event list -Filter 关键字 搜索";
            return null;
        }

        // 所有非抽象的 GameEventArgs 子类，按类名排序
        private static List<Type> GetEventTypes()
        {
            return eventTypes ??= TypeCache.GetTypesDerivedFrom<GameEventArgs>()
                .Where(t => t.IsClass && !t.IsAbstract && !t.IsGenericTypeDefinition)
                .OrderBy(t => t.Name, StringComparer.Ordinal)
                .ToList();
        }

        // 事件 key：优先使用静态字段 EventId，否则为类型全名
        private static string GetEventId(Type type)
        {
            return type.GetField("EventId", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string ?? type.FullName;
        }

        // 协议事件的消息类型：名为 Message 且为 protobuf 消息的公有字段
        private static Type GetMessageType(Type eventType)
        {
            FieldInfo field = eventType.GetField("Message", BindingFlags.Public | BindingFlags.Instance);
            return field != null && RuntimeInspectorReflection.IsProtoMessage(field.FieldType) ? field.FieldType : null;
        }

        // 读取 EventManager 中该事件当前的订阅列表
        private static Delegate[] GetListeners(string eventId)
        {
            EventManager manager = RuntimeInspectorReflection.GetSingleton<EventManager>();
            if (manager == null) return Array.Empty<Delegate>();

            FieldInfo dicField = typeof(EventManager).GetField("eventDic", BindingFlags.Instance | BindingFlags.NonPublic)
                                 ?? throw new MissingFieldException("未找到 EventManager.eventDic 字段，请同步更新 RuntimeInspectorEvent");
            FieldInfo handlerField = typeof(EventNode).GetField("eventHandler", BindingFlags.Instance | BindingFlags.NonPublic)
                                     ?? throw new MissingFieldException("未找到 EventNode.eventHandler 字段，请同步更新 RuntimeInspectorEvent");

            if (!(dicField.GetValue(manager) is IDictionary dic) || !dic.Contains(eventId)) return Array.Empty<Delegate>();
            return (handlerField.GetValue(dic[eventId]) as Delegate)?.GetInvocationList() ?? Array.Empty<Delegate>();
        }

        // 从对象池获取事件参数并按 JSON 填充，填充失败时归还对象池
        private static GameEventArgs CreateArgs(Type type, Dictionary<string, object> value)
        {
            GameEventArgs args;
            if (type.GetConstructor(Type.EmptyTypes) != null)
            {
                MethodInfo acquire = typeof(ReferencePool).GetMethod(nameof(ReferencePool.Acquire), BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                args = (GameEventArgs)acquire.MakeGenericMethod(type).Invoke(null, null);
            }
            else
            {
                args = (GameEventArgs)Activator.CreateInstance(type, true);
            }

            if (value == null || value.Count == 0) return args;
            try
            {
                RuntimeInspectorWriter.ConvertTo(value, type, args);
                return args;
            }
            catch
            {
                ReferencePool.Release(args);
                throw;
            }
        }

        // 收集需要比较的根对象：监听者所属的数据模块或界面 UIModel，以及 watch 指定的根对象
        private static void CollectRoots(DispatchSession s, Delegate[] listeners, string watch)
        {
            foreach (Delegate d in listeners)
            {
                switch (ResolveOwner(d.Target))
                {
                    case ModelBase model:
                        AddRoot(s, model.GetType().Name, model);
                        break;
                    case UICtrl ctrl when ctrl.UIModel != null:
                        AddRoot(s, $"{ctrl.UIInfo.UIType}/{ctrl.UIModel.GetType().Name}", ctrl.UIModel);
                        break;
                }
            }

            if (string.IsNullOrEmpty(watch)) return;
            foreach (string raw in watch.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string token = raw.Trim();
                bool isUI = token.StartsWith("ui:", StringComparison.OrdinalIgnoreCase);
                object root = RuntimeInspectorReflection.ResolveRoot(isUI ? null : token, isUI ? token.Substring(3) : null, out string label, out string error);
                if (root == null) throw new ArgumentException($"watch '{token}'：{error}");
                AddRoot(s, label, root);
            }
        }

        // 添加比较根对象，按引用去重
        private static void AddRoot(DispatchSession s, string label, object value)
        {
            if (s.Roots.Any(r => ReferenceEquals(r.Value, value))) return;
            s.Roots.Add(new SnapshotRoot { Label = label, Value = value });
        }

        // 委托目标为闭包时，沿编译器生成的字段找到外层实例
        private static object ResolveOwner(object target)
        {
            for (int i = 0; i < 4 && target != null && IsCompilerGenerated(target.GetType()); i++)
            {
                object outer = null;
                foreach (FieldInfo f in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    object v = f.GetValue(target);
                    if (v == null) continue;
                    if (f.Name == "<>4__this")
                    {
                        outer = v;
                        break;
                    }
                    if (outer == null && IsCompilerGenerated(v.GetType())) outer = v;
                }
                target = outer;
            }
            return target;
        }

        // 是否为编译器生成的类型（闭包、迭代器等）
        private static bool IsCompilerGenerated(Type type)
        {
            return type.Name.StartsWith("<", StringComparison.Ordinal) || type.IsDefined(typeof(CompilerGeneratedAttribute), false);
        }

        // 序列化根对象作为快照
        private static string Snapshot(object value, int depth)
        {
            try
            {
                return new RuntimeInspectorSerializer(depth, SnapshotLimit, false).Serialize(value);
            }
            catch (Exception e)
            {
                var sb = new StringBuilder();
                RuntimeInspectorJson.WriteString(sb, "<error " + RuntimeInspectorSerializer.Unwrap(e).Message + ">");
                return sb.ToString();
            }
        }

        // 记录当前已创建界面及其可见状态
        private static Dictionary<UIType, bool> SnapshotUI()
        {
            var result = new Dictionary<UIType, bool>();
            Dictionary<UIType, UICtrl> dic = RuntimeInspectorReflection.GetUIDic(out UIManager manager, out _);
            if (dic == null) return result;
            foreach (var kv in dic)
            {
                if (kv.Value != null) result[kv.Key] = RuntimeInspectorReflection.IsUIVisible(manager, kv.Key);
            }
            return result;
        }

        // 结束等待，生成报告并回调
        private static void Finish()
        {
            DispatchSession s = session;
            session = null;
            Application.logMessageReceived -= s.OnLog;

            string report;
            try
            {
                report = BuildReport(s);
            }
            catch (Exception e)
            {
                s.Complete(null, "生成报告失败：" + RuntimeInspectorSerializer.Unwrap(e).Message);
                return;
            }
            finally
            {
                if (s.Exception != null) Debug.LogException(s.Exception);
            }
            s.Complete(report, null);
        }

        // 生成派发响应报告
        private static string BuildReport(DispatchSession s)
        {
            var sb = new StringBuilder("{\"event\":");
            RuntimeInspectorJson.WriteString(sb, s.EventName);
            sb.Append(",\"eventId\":");
            RuntimeInspectorJson.WriteString(sb, s.EventId);
            sb.Append(",\"framesWaited\":").Append(Time.frameCount - s.StartFrame);

            sb.Append(",\"listeners\":");
            WriteListeners(sb, s.Listeners);

            sb.Append(",\"exception\":");
            if (s.Exception == null)
            {
                sb.Append("null");
            }
            else
            {
                sb.Append("{\"type\":");
                RuntimeInspectorJson.WriteString(sb, s.Exception.GetType().Name);
                sb.Append(",\"message\":");
                RuntimeInspectorJson.WriteString(sb, s.Exception.Message);
                sb.Append(",\"stack\":");
                RuntimeInspectorJson.WriteString(sb, Truncate(s.Exception.StackTrace, MaxStackLength));
                sb.Append(",\"note\":\"抛出异常的回调之后的监听者不会执行\"}");
            }

            sb.Append(",\"logs\":[").Append(string.Join(",", s.Logs)).Append(']');
            if (s.LogsTruncated) sb.Append(",\"logsTruncated\":true");

            var unchanged = new List<string>();
            sb.Append(",\"modelChanges\":[");
            bool first = true;
            foreach (SnapshotRoot root in s.Roots)
            {
                string after = Snapshot(root.Value, s.Depth);
                if (after == root.Before)
                {
                    unchanged.Add(root.Label);
                    continue;
                }

                var changes = new List<string>();
                bool truncated = false;
                RuntimeInspectorJson.Diff(RuntimeInspectorJson.Parse(root.Before), RuntimeInspectorJson.Parse(after), "", changes, MaxChangesPerRoot, ref truncated);
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"target\":");
                RuntimeInspectorJson.WriteString(sb, root.Label);
                sb.Append(",\"changes\":[").Append(string.Join(",", changes)).Append(']');
                if (truncated) sb.Append(",\"truncated\":true");
                sb.Append('}');
            }
            sb.Append("],\"unchanged\":[");
            for (int i = 0; i < unchanged.Count; i++)
            {
                if (i > 0) sb.Append(',');
                RuntimeInspectorJson.WriteString(sb, unchanged[i]);
            }
            sb.Append(']');

            Dictionary<UIType, bool> uiAfter = SnapshotUI();
            sb.Append(",\"ui\":{\"opened\":");
            WriteUIList(sb, uiAfter.Where(kv => !s.UIBefore.ContainsKey(kv.Key)).Select(kv => kv.Key));
            sb.Append(",\"destroyed\":");
            WriteUIList(sb, s.UIBefore.Keys.Where(k => !uiAfter.ContainsKey(k)));
            sb.Append(",\"shown\":");
            WriteUIList(sb, uiAfter.Where(kv => kv.Value && s.UIBefore.TryGetValue(kv.Key, out bool v) && !v).Select(kv => kv.Key));
            sb.Append(",\"hidden\":");
            WriteUIList(sb, uiAfter.Where(kv => !kv.Value && s.UIBefore.TryGetValue(kv.Key, out bool v) && v).Select(kv => kv.Key));
            sb.Append("}}");
            return sb.ToString();
        }

        // 写入界面名数组
        private static void WriteUIList(StringBuilder sb, IEnumerable<UIType> list)
        {
            sb.Append('[');
            bool first = true;
            foreach (UIType uiType in list)
            {
                if (!first) sb.Append(',');
                first = false;
                RuntimeInspectorJson.WriteString(sb, uiType.ToString());
            }
            sb.Append(']');
        }

        // 写入监听者列表：所属类、方法名、委托目标类型
        private static void WriteListeners(StringBuilder sb, Delegate[] listeners)
        {
            sb.Append('[');
            for (int i = 0; i < listeners.Length; i++)
            {
                if (i > 0) sb.Append(',');
                MethodInfo method = listeners[i].Method;
                Type owner = method.DeclaringType;
                while (owner != null && owner.DeclaringType != null && IsCompilerGenerated(owner)) owner = owner.DeclaringType;

                string methodName = method.Name;
                if (methodName.StartsWith("<", StringComparison.Ordinal))
                {
                    int end = methodName.IndexOf('>');
                    methodName = (end > 1 ? methodName.Substring(1, end - 1) : methodName) + " 内的匿名函数";
                }

                sb.Append("{\"class\":");
                RuntimeInspectorJson.WriteString(sb, owner?.Name);
                sb.Append(",\"method\":");
                RuntimeInspectorJson.WriteString(sb, methodName);
                object target = ResolveOwner(listeners[i].Target);
                if (target != null && target.GetType() != owner)
                {
                    sb.Append(",\"target\":");
                    RuntimeInspectorJson.WriteString(sb, target.GetType().Name);
                }
                sb.Append('}');
            }
            sb.Append(']');
        }

        // 参数结构中列出的成员：protobuf 消息取 ProtoMember 属性，其他类型取公有字段和可写属性
        private static List<KeyValuePair<string, Type>> GetSchemaMembers(Type type)
        {
            var result = new List<KeyValuePair<string, Type>>();
            if (RuntimeInspectorReflection.IsProtoMessage(type))
            {
                foreach (PropertyInfo p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (p.GetIndexParameters().Length == 0 && p.GetCustomAttributes(true).Any(a => a.GetType().Name == "ProtoMemberAttribute"))
                    {
                        result.Add(new KeyValuePair<string, Type>(p.Name, p.PropertyType));
                    }
                }
                return result;
            }

            var names = new HashSet<string>();
            for (Type t = type; t != null && t != typeof(GameEventArgs) && t != typeof(EventArgs) && t != typeof(object); t = t.BaseType)
            {
                foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (names.Add(f.Name)) result.Add(new KeyValuePair<string, Type>(f.Name, f.FieldType));
                }
                foreach (PropertyInfo p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (p.GetIndexParameters().Length == 0 && p.GetSetMethod() != null && names.Add(p.Name))
                    {
                        result.Add(new KeyValuePair<string, Type>(p.Name, p.PropertyType));
                    }
                }
            }
            return result;
        }

        // 递归收集参数结构中出现的复合类型和枚举
        private static void CollectTypes(Type type, List<Type> types, List<Type> enums)
        {
            type = Unwrap(type);
            if (type.IsEnum)
            {
                if (!enums.Contains(type)) enums.Add(type);
                return;
            }
            if (!IsComplex(type) || types.Contains(type) || types.Count >= MaxSchemaTypes) return;

            types.Add(type);
            foreach (var member in GetSchemaMembers(type)) CollectTypes(member.Value, types, enums);
        }

        // 去掉 Nullable 和集合包装，得到元素或值类型
        private static Type Unwrap(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string)) return type;
            if (RuntimeInspectorReflection.FindGenericInterface(type, typeof(IDictionary<,>)) != null)
            {
                RuntimeInspectorReflection.GetDictionaryTypes(type, out _, out Type valueType);
                return Unwrap(valueType);
            }
            if (typeof(IEnumerable).IsAssignableFrom(type)) return Unwrap(RuntimeInspectorReflection.GetElementType(type));
            return type;
        }

        // 是否为需要展开成员的复合类型
        private static bool IsComplex(Type type)
        {
            return !type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(decimal) && type != typeof(object)
                   && !typeof(IEnumerable).IsAssignableFrom(type)
                   && !typeof(UnityEngine.Object).IsAssignableFrom(type)
                   && !typeof(Delegate).IsAssignableFrom(type);
        }

        // 生成默认值模板；对象列表附带一个元素模板以展示结构
        private static object Template(Type type, int depth, HashSet<Type> visiting)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (type == typeof(string)) return "";
            if (type == typeof(bool)) return false;
            if (type.IsEnum)
            {
                string[] names = Enum.GetNames(type);
                return Enum.GetName(type, Activator.CreateInstance(type)) ?? (names.Length > 0 ? names[0] : (object)0L);
            }
            if (type.IsPrimitive || type == typeof(decimal)) return 0L;
            if (typeof(UnityEngine.Object).IsAssignableFrom(type) || typeof(Delegate).IsAssignableFrom(type) || type == typeof(object)) return null;

            if (RuntimeInspectorReflection.FindGenericInterface(type, typeof(IDictionary<,>)) != null) return new Dictionary<string, object>();
            if (typeof(IEnumerable).IsAssignableFrom(type))
            {
                Type element = RuntimeInspectorReflection.GetElementType(type);
                var list = new List<object>();
                if (IsComplex(Nullable.GetUnderlyingType(element) ?? element) && depth < TemplateMaxDepth && !visiting.Contains(element))
                {
                    list.Add(Template(element, depth + 1, visiting));
                }
                return list;
            }

            if (depth >= TemplateMaxDepth || !visiting.Add(type)) return null;
            var obj = new Dictionary<string, object>();
            foreach (var member in GetSchemaMembers(type)) obj[member.Key] = Template(member.Value, depth + 1, visiting);
            visiting.Remove(type);
            return obj;
        }

        // 截断过长文本
        private static string Truncate(string text, int max)
        {
            if (text == null || text.Length <= max) return text;
            return text.Substring(0, max) + "...";
        }

        // 派发前快照的根对象
        private sealed class SnapshotRoot
        {
            public string Label;
            public object Value;
            public string Before;
        }

        // 一次派发的等待状态及收集到的信息
        private sealed class DispatchSession
        {
            public string EventName;
            public string EventId;
            public Delegate[] Listeners;
            public int WaitFrames;
            public int Depth;
            public int StartFrame;
            public double StartTime;
            public Exception Exception;
            public Action<string, string> Complete;
            public Dictionary<UIType, bool> UIBefore;
            public readonly List<SnapshotRoot> Roots = new();
            public readonly List<string> Logs = new();
            public bool LogsTruncated;

            // 收集等待期间的 Unity 日志，错误类附带堆栈
            public void OnLog(string condition, string stackTrace, LogType type)
            {
                if (condition != null && condition.StartsWith(LogTag, StringComparison.Ordinal)) return;
                if (Logs.Count >= MaxLogs)
                {
                    LogsTruncated = true;
                    return;
                }

                var sb = new StringBuilder("{\"type\":");
                RuntimeInspectorJson.WriteString(sb, type.ToString());
                sb.Append(",\"message\":");
                RuntimeInspectorJson.WriteString(sb, Truncate(condition, MaxMessageLength));
                if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                {
                    sb.Append(",\"stack\":");
                    RuntimeInspectorJson.WriteString(sb, Truncate(stackTrace, MaxStackLength));
                }
                sb.Append('}');
                Logs.Add(sb.ToString());
            }
        }
    }
}
