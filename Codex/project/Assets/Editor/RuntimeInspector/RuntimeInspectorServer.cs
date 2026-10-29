using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;
using YQFramework.FUI;
using YQFramework.Model;

namespace RuntimeInspector
{
    /// <summary>
    /// Editor Play 模式下的运行时检查服务，仅监听 127.0.0.1，供 agent 或人工读写数据模块和界面 UIModel、监听字段变化、派发事件
    /// </summary>
    [InitializeOnLoad]
    public static class RuntimeInspectorServer
    {
        /// <summary>
        /// 首选端口，避开 Unity 调试（56000+）和 Profiler（54998+）端口段
        /// </summary>
        public const int BasePort = 17890;

        /// <summary>
        /// 端口被占用时最多尝试的数量
        /// </summary>
        public const int PortRange = 10;

        private const string EnabledPrefKey = "RuntimeInspector.Enabled";
        private const string LogTag = "[RuntimeInspector]";
        private const int MainThreadTimeoutMs = 15000;

        private static readonly ConcurrentQueue<PendingRequest> pending = new();
        private static HttpListener listener;
        private static Thread listenThread;
        private static string portFilePath;

        /// <summary>
        /// 当前监听端口，未启动时为 0
        /// </summary>
        public static int Port { get; private set; }

        static RuntimeInspectorServer()
        {
            portFilePath = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Library", "RuntimeInspector", "port.txt");
            EditorApplication.update += Pump;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;

            if (EditorPrefs.GetBool(EnabledPrefKey, true)) Start();
        }

        /// <summary>
        /// 启动服务
        /// </summary>
        public static void Start()
        {
            if (listener != null) return;

            for (int port = BasePort; port < BasePort + PortRange; port++)
            {
                var candidate = new HttpListener();
                candidate.Prefixes.Add($"http://127.0.0.1:{port}/");
                try
                {
                    candidate.Start();
                }
                catch (Exception)
                {
                    candidate.Close();
                    continue;
                }

                listener = candidate;
                Port = port;
                break;
            }

            if (listener == null)
            {
                Debug.LogWarning($"{LogTag} 端口 {BasePort}-{BasePort + PortRange - 1} 均被占用，服务未启动");
                return;
            }

            WritePortFile();
            listenThread = new Thread(ListenLoop) { IsBackground = true, Name = "RuntimeInspectorServer" };
            listenThread.Start();
        }

        /// <summary>
        /// 停止服务
        /// </summary>
        public static void Stop()
        {
            HttpListener current = listener;
            listener = null;
            Port = 0;
            RuntimeInspectorEvent.Abort("服务已停止");
            if (current == null) return;

            try
            {
                current.Close();
            }
            catch (Exception)
            {
                // 关闭时监听线程会抛出异常并退出，无需处理
            }

            while (pending.TryDequeue(out PendingRequest request))
            {
                request.Complete(503, Error("服务已停止"));
            }

            try
            {
                if (File.Exists(portFilePath)) File.Delete(portFilePath);
            }
            catch (Exception)
            {
                // 端口文件删除失败不影响下次启动覆盖写入
            }
        }

        [MenuItem("Tools/RuntimeInspector/重启服务")]
        private static void MenuRestart()
        {
            EditorPrefs.SetBool(EnabledPrefKey, true);
            Stop();
            Start();
            Debug.Log(Port > 0 ? $"{LogTag} 服务已启动 http://127.0.0.1:{Port}/" : $"{LogTag} 服务启动失败");
        }

        [MenuItem("Tools/RuntimeInspector/停止服务")]
        private static void MenuStop()
        {
            EditorPrefs.SetBool(EnabledPrefKey, false);
            Stop();
            Debug.Log($"{LogTag} 服务已停止，可通过“重启服务”重新启用");
        }

        // 写入端口文件，供外部脚本读取
        private static void WritePortFile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(portFilePath));
                File.WriteAllText(portFilePath, Port.ToString());
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogTag} 写入端口文件失败：{e.Message}");
            }
        }

        // 进入 Play 模式时保持后台运行，否则 Editor 失焦后请求得不到主线程处理；退出时清理监听和派发
        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode) Application.runInBackground = true;
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                RuntimeInspectorWatcher.Clear();
                RuntimeInspectorEvent.Abort("已退出 Play 模式");
            }
        }

        // 后台线程：接收请求，转交主线程处理并等待结果
        private static void ListenLoop()
        {
            HttpListener current = listener;
            while (current != null && current.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = current.GetContext();
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    HandleContext(context);
                }
                catch (Exception)
                {
                    // 客户端提前断开等情况，忽略单个请求
                }
            }
        }

        // 读取请求、排队等待主线程处理，然后写回响应
        private static void HandleContext(HttpListenerContext context)
        {
            HttpListenerRequest req = context.Request;
            string body = null;
            if (req.HasEntityBody)
            {
                using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
                body = reader.ReadToEnd();
            }

            var request = new PendingRequest
            {
                Method = req.HttpMethod.ToUpperInvariant(),
                Route = req.Url.AbsolutePath.TrimEnd('/').ToLowerInvariant(),
                Query = ParseQuery(req.Url.Query),
                Body = body,
            };
            pending.Enqueue(request);

            int status;
            string json;
            if (request.Done.Wait(MainThreadTimeoutMs))
            {
                status = request.Status;
                json = request.ResultJson;
            }
            else
            {
                status = 504;
                json = Error("主线程处理超时：Unity 可能正被断点、弹窗或长耗时操作阻塞");
            }

            if (request.Query.TryGetValue("pretty", out string pretty) && IsTrue(pretty)) json = Prettify(json);

            byte[] bytes = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        // 主线程：处理排队中的请求
        private static void Pump()
        {
            while (pending.TryDequeue(out PendingRequest request))
            {
                try
                {
                    Route(request);
                }
                catch (Exception e)
                {
                    Exception inner = RuntimeInspectorSerializer.Unwrap(e);
                    request.Complete(400, Error($"{inner.GetType().Name}: {inner.Message}"));
                }
            }

            try
            {
                RuntimeInspectorWatcher.Tick();
                RuntimeInspectorEvent.Tick();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{LogTag} 监听或派发检查异常：{RuntimeInspectorSerializer.Unwrap(e)}");
            }
        }

        // 请求路由
        private static void Route(PendingRequest request)
        {
            if (request.Route == "" || request.Route == "/ping")
            {
                request.Complete(200, Ok(PingJson()));
                return;
            }

            if (!EditorApplication.isPlaying)
            {
                request.Complete(409, Error("Unity 当前不在 Play 模式"));
                return;
            }

            Dictionary<string, ModelBase> dic = RuntimeInspectorReflection.GetModelDic(out string error);
            if (dic == null)
            {
                request.Complete(409, Error(error));
                return;
            }

            switch (request.Route)
            {
                case "/models" when request.Method == "GET":
                    request.Complete(200, Ok(ModelsJson(dic, request.Get("filter"))));
                    return;
                case "/model" when request.Method == "GET":
                    HandleGetModel(request);
                    return;
                case "/search" when request.Method == "GET":
                    HandleSearch(request, dic);
                    return;
                case "/set" when request.Method == "POST":
                    HandleSet(request);
                    return;
                case "/collection" when request.Method == "POST":
                    HandleCollection(request);
                    return;
                case "/ui" when request.Method == "GET":
                    HandleUIList(request);
                    return;
                case "/watch/add" when request.Method == "POST":
                    HandleWatchAdd(request);
                    return;
                case "/watch/list" when request.Method == "GET":
                    request.Complete(200, Ok(RuntimeInspectorWatcher.List()));
                    return;
                case "/watch/history" when request.Method == "GET":
                    request.Complete(200, Ok(RuntimeInspectorWatcher.History(request.GetInt("id", 0), IsTrue(request.Get("full")))));
                    return;
                case "/watch/remove" when request.Method == "POST":
                    RuntimeInspectorWatcher.Remove(BodyInt(ParseBody(request), "id", 0));
                    request.Complete(200, Ok(RuntimeInspectorWatcher.List()));
                    return;
                case "/watch/clear" when request.Method == "POST":
                    RuntimeInspectorWatcher.Clear();
                    request.Complete(200, Ok("[]"));
                    return;
                case "/event/list" when request.Method == "GET":
                    request.Complete(200, Ok(RuntimeInspectorEvent.List(request.Get("filter"), request.GetInt("max", 100))));
                    return;
                case "/event/schema" when request.Method == "GET":
                    request.Complete(200, Ok(RuntimeInspectorEvent.Schema(request.Get("event"))));
                    return;
                case "/event/dispatch" when request.Method == "POST":
                    HandleEventDispatch(request);
                    return;
                default:
                    request.Complete(404, Error($"未知接口 {request.Method} {request.Route}，可用：GET /ping /models /model /search /ui /watch/list /watch/history /event/list /event/schema，" +
                                                "POST /set /collection /watch/add /watch/remove /watch/clear /event/dispatch"));
                    return;
            }
        }

        // 按 name（数据模块）或 ui（界面名）定位根对象，失败时直接以 404 结束请求
        private static object ResolveRoot(PendingRequest request, string name, string ui, out string label)
        {
            object root = RuntimeInspectorReflection.ResolveRoot(name, ui, out label, out string error);
            if (root == null) request.Complete(404, Error(error));
            return root;
        }

        // 服务与运行状态
        private static string PingJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"port\":").Append(Port);
            sb.Append(",\"playing\":").Append(EditorApplication.isPlaying ? "true" : "false");
            sb.Append(",\"paused\":").Append(EditorApplication.isPaused ? "true" : "false");
            int count = -1;
            if (EditorApplication.isPlaying)
            {
                Dictionary<string, ModelBase> dic = RuntimeInspectorReflection.GetModelDic(out _);
                if (dic != null) count = dic.Count;
            }
            sb.Append(",\"modelCount\":").Append(count);
            sb.Append('}');
            return sb.ToString();
        }

        // 已创建模块列表
        private static string ModelsJson(Dictionary<string, ModelBase> dic, string filter)
        {
            IEnumerable<ModelBase> models = dic.Values.Where(m => m != null).OrderBy(m => m.GetType().Name);
            if (!string.IsNullOrEmpty(filter))
            {
                models = models.Where(m => m.GetType().FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var sb = new StringBuilder("[");
            bool first = true;
            foreach (ModelBase model in models)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"name\":");
                RuntimeInspectorJson.WriteString(sb, model.GetType().Name);
                sb.Append(",\"fullName\":");
                RuntimeInspectorJson.WriteString(sb, model.GetType().FullName);
                sb.Append(",\"members\":").Append(RuntimeInspectorReflection.GetDisplayMembers(model.GetType(), false).Count);
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }

        // GET /model?name=|ui=&path=&depth=&limit=&props=
        private static void HandleGetModel(PendingRequest request)
        {
            object root = ResolveRoot(request, request.Get("name"), request.Get("ui"), out string label);
            if (root == null) return;

            string path = request.Get("path") ?? "";
            List<PathNode> nodes = RuntimeInspectorReflection.Navigate(root, RuntimeInspectorReflection.ParsePath(path), false);
            int depth = request.GetInt("depth", path.Length == 0 ? 2 : 3);
            var serializer = new RuntimeInspectorSerializer(depth, request.GetInt("limit", 50), IsTrue(request.Get("props")));

            var sb = new StringBuilder("{\"model\":");
            RuntimeInspectorJson.WriteString(sb, label);
            sb.Append(",\"path\":");
            RuntimeInspectorJson.WriteString(sb, path);
            sb.Append(",\"value\":");
            serializer.Write(sb, nodes[nodes.Count - 1].Value, 0);
            sb.Append('}');
            request.Complete(200, Ok(sb.ToString()));
        }

        // GET /search?name=|ui=&key=&value=&depth=&max=&limit=
        private static void HandleSearch(PendingRequest request, Dictionary<string, ModelBase> dic)
        {
            string key = request.Get("key");
            string value = request.Get("value");
            if (string.IsNullOrEmpty(key) && string.IsNullOrEmpty(value))
            {
                request.Complete(400, Error("需要 key（成员名关键字）或 value（值关键字）"));
                return;
            }

            var roots = new List<KeyValuePair<string, object>>();
            string name = request.Get("name");
            string ui = request.Get("ui");
            if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(ui))
            {
                foreach (ModelBase m in dic.Values.Where(m => m != null).OrderBy(m => m.GetType().Name))
                {
                    roots.Add(new KeyValuePair<string, object>(m.GetType().Name + ":", m));
                }
            }
            else
            {
                object root = ResolveRoot(request, name, ui, out _);
                if (root == null) return;
                string path = request.Get("path") ?? "";
                List<PathNode> nodes = RuntimeInspectorReflection.Navigate(root, RuntimeInspectorReflection.ParsePath(path), false);
                roots.Add(new KeyValuePair<string, object>(path, nodes[nodes.Count - 1].Value));
            }

            var serializer = new RuntimeInspectorSerializer(0, request.GetInt("limit", 500), IsTrue(request.Get("props")));
            string result = serializer.Search(roots, key, value, request.GetInt("depth", 6), request.GetInt("max", 100));
            request.Complete(200, Ok(result));
        }

        // POST /set {name|ui, path, value, force, merge, setter}
        private static void HandleSet(PendingRequest request)
        {
            Dictionary<string, object> body = ParseBody(request);
            object root = ResolveRoot(request, BodyString(body, "name"), BodyString(body, "ui"), out string label);
            if (root == null) return;
            if (!body.ContainsKey("value"))
            {
                request.Complete(400, Error("缺少 value"));
                return;
            }

            string path = BodyString(body, "path") ?? "";
            object newValue = RuntimeInspectorWriter.SetValue(root, path, body["value"],
                BodyBool(body, "force"), BodyBool(body, "merge"), BodyBool(body, "setter"), out object oldValue);

            var preview = new RuntimeInspectorSerializer(2, 20, false);
            string oldJson = preview.Serialize(oldValue);
            string newJson = preview.Serialize(newValue);
            Debug.Log($"{LogTag} SET {label}.{path}\nold: {oldJson}\nnew: {newJson}");

            var sb = new StringBuilder("{\"model\":");
            RuntimeInspectorJson.WriteString(sb, label);
            sb.Append(",\"path\":");
            RuntimeInspectorJson.WriteString(sb, path);
            sb.Append(",\"old\":").Append(oldJson);
            sb.Append(",\"new\":").Append(newJson);
            sb.Append('}');
            request.Complete(200, Ok(sb.ToString()));
        }

        // POST /collection {name|ui, path, op, key, index, value}
        private static void HandleCollection(PendingRequest request)
        {
            Dictionary<string, object> body = ParseBody(request);
            object root = ResolveRoot(request, BodyString(body, "name"), BodyString(body, "ui"), out string label);
            if (root == null) return;

            string path = BodyString(body, "path") ?? "";
            string op = BodyString(body, "op");
            body.TryGetValue("key", out object key);
            body.TryGetValue("value", out object value);
            int index = BodyInt(body, "index", -1);

            int count = RuntimeInspectorWriter.ModifyCollection(root, path, op, key, index, value, body.ContainsKey("value"));
            string valueJson = new RuntimeInspectorSerializer(2, 20, false).Serialize(value);
            Debug.Log($"{LogTag} COLLECTION {op} {label}.{path} key={key} index={index} value={valueJson} count={count}");

            var sb = new StringBuilder("{\"model\":");
            RuntimeInspectorJson.WriteString(sb, label);
            sb.Append(",\"path\":");
            RuntimeInspectorJson.WriteString(sb, path);
            sb.Append(",\"op\":");
            RuntimeInspectorJson.WriteString(sb, op);
            sb.Append(",\"count\":").Append(count);
            sb.Append('}');
            request.Complete(200, Ok(sb.ToString()));
        }

        // GET /ui?filter=
        private static void HandleUIList(PendingRequest request)
        {
            Dictionary<UIType, UICtrl> dic = RuntimeInspectorReflection.GetUIDic(out UIManager manager, out string error);
            if (dic == null)
            {
                request.Complete(409, Error(error));
                return;
            }

            string filter = request.Get("filter");
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var kv in dic.Where(kv => kv.Value != null).OrderBy(kv => kv.Key.ToString()))
            {
                if (filter != null && kv.Key.ToString().IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"ui\":");
                RuntimeInspectorJson.WriteString(sb, kv.Key.ToString());
                sb.Append(",\"visible\":").Append(RuntimeInspectorReflection.IsUIVisible(manager, kv.Key) ? "true" : "false");
                sb.Append(",\"ctrl\":");
                RuntimeInspectorJson.WriteString(sb, kv.Value.GetType().Name);
                sb.Append(",\"model\":");
                RuntimeInspectorJson.WriteString(sb, kv.Value.UIModel?.GetType().Name);
                sb.Append('}');
            }
            sb.Append(']');
            request.Complete(200, Ok(sb.ToString()));
        }

        // POST /watch/add {name|ui, path, depth, interval}
        private static void HandleWatchAdd(PendingRequest request)
        {
            Dictionary<string, object> body = ParseBody(request);
            string result = RuntimeInspectorWatcher.Add(BodyString(body, "name"), BodyString(body, "ui"), BodyString(body, "path"),
                BodyInt(body, "depth", 3), BodyInt(body, "interval", 1));
            request.Complete(200, Ok(result));
        }

        // POST /event/dispatch {event, value, wait, watch, depth}，等待结束后由派发模块完成请求
        private static void HandleEventDispatch(PendingRequest request)
        {
            Dictionary<string, object> body = ParseBody(request);
            body.TryGetValue("value", out object value);
            RuntimeInspectorEvent.Dispatch(BodyString(body, "event"), value, BodyInt(body, "wait", 30), BodyString(body, "watch"), BodyInt(body, "depth", 4),
                (data, error) => request.Complete(error == null ? 200 : 500, error == null ? Ok(data) : Error(error)));
        }

        // 读取请求体中的整数字段
        private static int BodyInt(Dictionary<string, object> body, string key, int defaultValue)
        {
            return body.TryGetValue(key, out object v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : defaultValue;
        }

        // 解析 POST JSON 请求体
        private static Dictionary<string, object> ParseBody(PendingRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Body)) throw new ArgumentException("POST 请求体不能为空");
            if (RuntimeInspectorJson.Parse(request.Body) is Dictionary<string, object> body) return body;
            throw new ArgumentException("POST 请求体必须是 JSON 对象");
        }

        // 读取请求体中的字符串字段
        private static string BodyString(Dictionary<string, object> body, string key)
        {
            return body.TryGetValue(key, out object v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null;
        }

        // 读取请求体中的布尔字段
        private static bool BodyBool(Dictionary<string, object> body, string key)
        {
            return body.TryGetValue(key, out object v) && v != null && IsTrue(Convert.ToString(v));
        }

        // 解析 URL 查询参数，按 UTF-8 解码
        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return result;

            foreach (string pair in query.TrimStart('?').Split('&'))
            {
                if (pair.Length == 0) continue;
                int eq = pair.IndexOf('=');
                string k = eq < 0 ? pair : pair.Substring(0, eq);
                string v = eq < 0 ? "" : pair.Substring(eq + 1);
                result[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
            }
            return result;
        }

        // 判断参数是否表示 true
        private static bool IsTrue(string value)
        {
            return value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }

        // 成功响应
        private static string Ok(string dataJson)
        {
            return "{\"ok\":true,\"data\":" + dataJson + "}";
        }

        // 失败响应
        private static string Error(string message)
        {
            var sb = new StringBuilder("{\"ok\":false,\"error\":");
            RuntimeInspectorJson.WriteString(sb, message);
            sb.Append('}');
            return sb.ToString();
        }

        // 为紧凑 JSON 添加缩进，便于人工阅读
        private static string Prettify(string json)
        {
            var sb = new StringBuilder(json.Length * 2);
            int indent = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < json.Length) sb.Append(json[++i]);
                    else if (c == '"') inString = false;
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inString = true;
                        sb.Append(c);
                        break;
                    case '{':
                    case '[':
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']'))
                        {
                            sb.Append(c).Append(json[++i]);
                            break;
                        }
                        sb.Append(c).Append('\n').Append(' ', ++indent * 2);
                        break;
                    case '}':
                    case ']':
                        sb.Append('\n').Append(' ', --indent * 2).Append(c);
                        break;
                    case ',':
                        sb.Append(c).Append('\n').Append(' ', indent * 2);
                        break;
                    case ':':
                        sb.Append(": ");
                        break;
                    default:
                        sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        // 跨线程传递的请求及结果
        private sealed class PendingRequest
        {
            public string Method;
            public string Route;
            public Dictionary<string, string> Query;
            public string Body;
            public int Status;
            public string ResultJson;
            public readonly ManualResetEventSlim Done = new(false);

            // 读取查询参数
            public string Get(string key)
            {
                return Query.TryGetValue(key, out string v) && v.Length > 0 ? v : null;
            }

            // 读取整数查询参数
            public int GetInt(string key, int defaultValue)
            {
                return int.TryParse(Get(key), out int v) ? v : defaultValue;
            }

            // 设置结果并唤醒等待线程
            public void Complete(int status, string json)
            {
                Status = status;
                ResultJson = json;
                Done.Set();
            }
        }
    }
}
