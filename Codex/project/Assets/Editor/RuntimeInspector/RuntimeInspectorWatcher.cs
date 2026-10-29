using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace RuntimeInspector
{
    /// <summary>
    /// 运行时字段变化监听：按帧间隔序列化指定路径并与上次结果比较，保留最近的变化历史
    /// </summary>
    public static class RuntimeInspectorWatcher
    {
        /// <summary>
        /// 每个监听项保留的历史条数
        /// </summary>
        public const int HistoryCapacity = 30;

        private const int MaxChangesPerEntry = 100;
        private const int SerializeLimit = 200;
        private const int MaxDepth = 8;

        private static readonly List<WatchItem> items = new();
        private static int nextId = 1;

        /// <summary>
        /// 当前监听项数量
        /// </summary>
        public static int Count => items.Count;

        /// <summary>
        /// 添加监听，以当前值作为基线
        /// </summary>
        /// <param name="name">数据模块名</param>
        /// <param name="ui">界面名，非空时监听该界面的 UIModel</param>
        /// <param name="path">监听路径，空表示整个根对象</param>
        /// <param name="depth">序列化深度，超出部分只比较摘要</param>
        /// <param name="interval">检查间隔帧数</param>
        /// <returns>监听项信息及基线值 JSON</returns>
        public static string Add(string name, string ui, string path, int depth, int interval)
        {
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(ui)) throw new ArgumentException("需要 name（数据模块）或 ui（界面名）");

            var item = new WatchItem
            {
                Id = nextId++,
                Name = name,
                UI = ui,
                Path = path ?? "",
                Depth = Math.Max(1, Math.Min(MaxDepth, depth)),
                Interval = Math.Max(1, interval),
            };

            string json = Read(item, out string label, out string error);
            if (json == null) throw new InvalidOperationException(error);

            item.Label = label;
            item.LastJson = json;
            item.LastFrame = Time.frameCount;
            items.Add(item);

            var sb = new StringBuilder("{");
            WriteItemInfo(sb, item);
            sb.Append(",\"baseline\":").Append(json).Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 列出全部监听项
        /// </summary>
        public static string List()
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{');
                WriteItemInfo(sb, items[i]);
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>
        /// 读取监听项的变化历史
        /// </summary>
        /// <param name="id">监听项 ID</param>
        /// <param name="full">是否输出每条记录的完整旧值和新值</param>
        public static string History(int id, bool full)
        {
            WatchItem item = Find(id);
            var sb = new StringBuilder("{");
            WriteItemInfo(sb, item);
            sb.Append(",\"dropped\":").Append(Math.Max(0, item.TotalChanges - item.History.Count));
            sb.Append(",\"current\":").Append(item.LastJson ?? "null");
            sb.Append(",\"entries\":[");
            bool first = true;
            foreach (WatchEntry entry in item.History)
            {
                if (!first) sb.Append(',');
                first = false;
                WriteEntry(sb, entry, full);
            }
            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// 移除监听项
        /// </summary>
        public static void Remove(int id)
        {
            items.Remove(Find(id));
        }

        /// <summary>
        /// 移除全部监听项
        /// </summary>
        public static void Clear()
        {
            items.Clear();
        }

        /// <summary>
        /// 每帧由服务主循环调用，检查到期的监听项
        /// </summary>
        public static void Tick()
        {
            if (items.Count == 0 || !EditorApplication.isPlaying) return;

            int frame = Time.frameCount;
            foreach (WatchItem item in items)
            {
                if (frame - item.LastFrame < item.Interval) continue;
                item.LastFrame = frame;

                string json = Read(item, out string label, out string error);
                if (json == null)
                {
                    if (item.Error != error)
                    {
                        item.Error = error;
                        AddEntry(item, frame, null, error);
                    }
                    continue;
                }

                item.Error = null;
                item.Label = label;
                if (json == item.LastJson) continue;
                AddEntry(item, frame, json, null);
                item.LastJson = json;
            }
        }

        // 按 ID 查找监听项
        private static WatchItem Find(int id)
        {
            WatchItem item = items.Find(w => w.Id == id);
            if (item == null) throw new KeyNotFoundException($"不存在监听项 id={id}，可用 watch list 查看");
            return item;
        }

        // 解析根对象并序列化监听路径，失败时返回 null
        private static string Read(WatchItem item, out string label, out string error)
        {
            try
            {
                object root = RuntimeInspectorReflection.ResolveRoot(item.Name, item.UI, out label, out error);
                if (root == null) return null;
                List<PathNode> nodes = RuntimeInspectorReflection.Navigate(root, RuntimeInspectorReflection.ParsePath(item.Path));
                return new RuntimeInspectorSerializer(item.Depth, SerializeLimit, false).Serialize(nodes[nodes.Count - 1].Value);
            }
            catch (Exception e)
            {
                label = null;
                error = RuntimeInspectorSerializer.Unwrap(e).Message;
                return null;
            }
        }

        // 记录一条变化或错误，超出容量时丢弃最旧的记录
        private static void AddEntry(WatchItem item, int frame, string json, string error)
        {
            var entry = new WatchEntry
            {
                Seq = ++item.TotalChanges,
                Time = DateTime.Now,
                Frame = frame,
                Error = error,
                Old = item.LastJson,
                New = json,
            };

            if (error == null)
            {
                bool truncated = false;
                try
                {
                    RuntimeInspectorJson.Diff(RuntimeInspectorJson.Parse(item.LastJson), RuntimeInspectorJson.Parse(json), item.Path, entry.Changes, MaxChangesPerEntry, ref truncated);
                }
                catch (Exception e)
                {
                    entry.Error = "差异比较失败：" + e.Message;
                }
                entry.Truncated = truncated;
            }

            item.History.Enqueue(entry);
            while (item.History.Count > HistoryCapacity) item.History.Dequeue();
        }

        // 写入监听项基础信息（不含外层花括号）
        private static void WriteItemInfo(StringBuilder sb, WatchItem item)
        {
            sb.Append("\"id\":").Append(item.Id);
            sb.Append(",\"target\":");
            RuntimeInspectorJson.WriteString(sb, item.Label ?? item.UI ?? item.Name);
            if (!string.IsNullOrEmpty(item.UI))
            {
                sb.Append(",\"ui\":");
                RuntimeInspectorJson.WriteString(sb, item.UI);
            }
            sb.Append(",\"path\":");
            RuntimeInspectorJson.WriteString(sb, item.Path);
            sb.Append(",\"depth\":").Append(item.Depth);
            sb.Append(",\"interval\":").Append(item.Interval);
            sb.Append(",\"changes\":").Append(item.TotalChanges);
            sb.Append(",\"error\":");
            RuntimeInspectorJson.WriteString(sb, item.Error);
        }

        // 写入一条历史记录
        private static void WriteEntry(StringBuilder sb, WatchEntry entry, bool full)
        {
            sb.Append("{\"seq\":").Append(entry.Seq);
            sb.Append(",\"time\":");
            RuntimeInspectorJson.WriteString(sb, entry.Time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
            sb.Append(",\"frame\":").Append(entry.Frame);
            if (entry.Error != null)
            {
                sb.Append(",\"error\":");
                RuntimeInspectorJson.WriteString(sb, entry.Error);
            }
            if (entry.New != null)
            {
                sb.Append(",\"changes\":[").Append(string.Join(",", entry.Changes)).Append(']');
                if (entry.Truncated) sb.Append(",\"truncated\":true");
            }
            if (full)
            {
                sb.Append(",\"old\":").Append(entry.Old ?? "null");
                sb.Append(",\"new\":").Append(entry.New ?? "null");
            }
            sb.Append('}');
        }

        // 监听项
        private sealed class WatchItem
        {
            public int Id;
            public string Name;
            public string UI;
            public string Path;
            public int Depth;
            public int Interval;
            public string Label;
            public string LastJson;
            public int LastFrame;
            public string Error;
            public int TotalChanges;
            public readonly Queue<WatchEntry> History = new();
        }

        // 一条变化记录
        private sealed class WatchEntry
        {
            public int Seq;
            public DateTime Time;
            public int Frame;
            public string Error;
            public string Old;
            public string New;
            public bool Truncated;
            public readonly List<string> Changes = new();
        }
    }
}
