// Offline Luban config reader. Loads Library/ScriptAssemblies/GamePlay.dll and parses
// Assets/GameRes/Config/<Name>Cfg.bytes with the generated TbXxx/XxxCfg classes.
// Built and run by cfg.ps1 with the Unity-bundled Roslyn and Mono; do not run directly.
//
// Usage: ConfigLookup.exe <projectRoot> <unityDataDir> <command> <table> [args...] [--full]
//   get   <table> <id...>             look up records by primary key
//   find  <table> <keyword> [max]     records whose string fields contain keyword
//   where <table> <field> <value>     records whose field equals value
//   tables                            list available tables

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

public static class ConfigLookup
{
    const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { "partner", "Partner" },
        { "item", "Item" },
        { "beauty", "BeautyInfo" },
        { "mall", "Mall" },
        { "goods", "Mall" },
        { "skin", "Appearance" },
        { "system", "GameSystem" },
        { "building", "BuildingInfo" },
        { "word", "LanguageWord" },
    };

    static string projectRoot;
    static string unityData;
    static Assembly gamePlay;
    static Type byteBufType;
    static bool full;

    public static int Main(string[] argv)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var args = argv.ToList();
        full = args.Remove("--full");
        if (args.Count < 3)
        {
            Fail("usage: <projectRoot> <unityDataDir> <get|find|where|tables> <table> [args...]");
            return 1;
        }

        projectRoot = args[0];
        unityData = args[1];
        string cmd = args[2].ToLowerInvariant();

        try
        {
            LoadAssemblies();
            if (cmd == "tables")
            {
                PrintTables();
                return 0;
            }

            if (args.Count < 4)
            {
                Fail("missing table name");
                return 1;
            }

            string table = Aliases.TryGetValue(args[3], out string real) ? real : args[3];
            if (table.StartsWith("Tb", StringComparison.Ordinal)) table = table.Substring(2);
            object tb = LoadTable(table);
            List<string> rest = args.Skip(4).ToList();

            switch (cmd)
            {
                case "get":
                    Get(tb, table, rest);
                    break;
                case "find":
                    if (rest.Count < 1) throw new ArgumentException("find requires keyword");
                    Find(tb, table, rest[0], rest.Count > 1 ? int.Parse(rest[1]) : 50);
                    break;
                case "where":
                    if (rest.Count < 2) throw new ArgumentException("where requires <field> <value>");
                    Where(tb, table, rest[0], rest[1]);
                    break;
                default:
                    throw new ArgumentException("unknown command " + cmd);
            }
            return 0;
        }
        catch (Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            Fail(e.GetType().Name + ": " + e.Message);
            return 1;
        }
    }

    // Load GamePlay.dll / GameCore.dll and resolve their dependencies from Unity folders
    static void LoadAssemblies()
    {
        string scriptAssemblies = Path.Combine(projectRoot, "Library", "ScriptAssemblies");
        var searchDirs = new[]
        {
            scriptAssemblies,
            Path.Combine(unityData, "Managed"),
            Path.Combine(unityData, "Managed", "UnityEngine"),
            Path.Combine(unityData, "MonoBleedingEdge", "lib", "mono", "4.5"),
            Path.Combine(unityData, "MonoBleedingEdge", "lib", "mono", "4.5", "Facades"),
        };

        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string file = new AssemblyName(e.Name).Name + ".dll";
            foreach (string dir in searchDirs)
            {
                string path = Path.Combine(dir, file);
                if (File.Exists(path)) return Assembly.LoadFrom(path);
            }
            return null;
        };

        string gamePlayPath = Path.Combine(scriptAssemblies, "GamePlay.dll");
        if (!File.Exists(gamePlayPath)) throw new FileNotFoundException("GamePlay.dll not found; open the project in Unity once to compile scripts", gamePlayPath);
        gamePlay = Assembly.LoadFrom(gamePlayPath);
        Assembly gameCore = Assembly.LoadFrom(Path.Combine(scriptAssemblies, "GameCore.dll"));
        byteBufType = gameCore.GetType("YQFramework.Helper.ByteBuf", true);
    }

    // Construct TbXxx from Assets/GameRes/Config/XxxCfg.bytes
    static object LoadTable(string table)
    {
        Type tbType = gamePlay.GetType("YQFramework.Config.Tb" + table);
        if (tbType == null) throw new ArgumentException("table Tb" + table + " not found; run 'tables' to list available tables");

        string bytesPath = Path.Combine(projectRoot, "Assets", "GameRes", "Config", table + "Cfg.bytes");
        if (!File.Exists(bytesPath)) throw new FileNotFoundException("config data not found", bytesPath);

        object buf = Activator.CreateInstance(byteBufType, File.ReadAllBytes(bytesPath));
        ConstructorInfo ctor = tbType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == byteBufType);
        if (ctor == null) throw new InvalidOperationException("Tb" + table + " has no ByteBuf constructor");

        object[] ctorArgs = ctor.GetParameters().Select((p, i) => i == 0 ? buf : (p.ParameterType == typeof(string) ? (object)(table + "Cfg") : null)).ToArray();
        return ctor.Invoke(ctorArgs);
    }

    // TbXxx.Get(key) method
    static MethodInfo GetMethod(Type tbType)
    {
        return tbType.GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(m => m.Name == "Get" && m.GetParameters().Length == 1);
    }

    // Primary key type of the table
    static Type KeyType(object tb)
    {
        return GetMethod(tb.GetType()).GetParameters()[0].ParameterType;
    }

    // All records via DataMap (forces a full read)
    static IEnumerable<KeyValuePair<object, object>> AllRecords(object tb)
    {
        var map = (IDictionary)tb.GetType().GetProperty("DataMap").GetValue(tb);
        foreach (DictionaryEntry e in map) yield return new KeyValuePair<object, object>(e.Key, e.Value);
    }

    static void Get(object tb, string table, List<string> ids)
    {
        if (ids.Count == 0) throw new ArgumentException("get requires at least one id");
        Type keyType = KeyType(tb);
        MethodInfo get = GetMethod(tb.GetType());
        var sb = new StringBuilder();
        sb.Append("{\"table\":").Append(Str("Tb" + table)).Append(",\"items\":[");
        bool first = true;
        foreach (string raw in ids.SelectMany(s => s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)))
        {
            if (!first) sb.Append(',');
            first = false;
            object key = Convert.ChangeType(raw.Trim(), keyType, CultureInfo.InvariantCulture);
            object cfg = get.Invoke(tb, new[] { key });
            WriteRecord(sb, key, cfg);
        }
        sb.Append("]}");
        Console.WriteLine(sb.ToString());
    }

    static void Find(object tb, string table, string keyword, int max)
    {
        var sb = new StringBuilder();
        sb.Append("{\"table\":").Append(Str("Tb" + table)).Append(",\"keyword\":").Append(Str(keyword)).Append(",\"items\":[");
        int count = 0;
        foreach (var kv in AllRecords(tb))
        {
            bool hit = Fields(kv.Value).Any(f => f.Value is string s && s.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
            if (!hit) continue;
            if (count >= max) break;
            if (count++ > 0) sb.Append(',');
            WriteRecord(sb, kv.Key, kv.Value);
        }
        sb.Append("],\"count\":").Append(count).Append('}');
        Console.WriteLine(sb.ToString());
    }

    static void Where(object tb, string table, string field, string value)
    {
        var sb = new StringBuilder();
        sb.Append("{\"table\":").Append(Str("Tb" + table)).Append(",\"items\":[");
        int count = 0;
        foreach (var kv in AllRecords(tb))
        {
            var match = Fields(kv.Value).FirstOrDefault(f => string.Equals(f.Key, field, StringComparison.OrdinalIgnoreCase));
            if (match.Key == null) throw new ArgumentException("field '" + field + "' not found; available: " + string.Join(", ", Fields(kv.Value).Select(f => f.Key)));
            if (!string.Equals(Text(match.Value), value, StringComparison.OrdinalIgnoreCase)) continue;
            if (count++ > 0) sb.Append(',');
            WriteRecord(sb, kv.Key, kv.Value);
        }
        sb.Append("],\"count\":").Append(count).Append('}');
        Console.WriteLine(sb.ToString());
    }

    static void PrintTables()
    {
        Type[] types;
        try
        {
            types = gamePlay.GetTypes();
        }
        catch (ReflectionTypeLoadException e)
        {
            types = e.Types.Where(t => t != null).ToArray();
        }

        var names = types
            .Where(t => t.Namespace == "YQFramework.Config" && t.Name.StartsWith("Tb", StringComparison.Ordinal) && GetMethod(t) != null)
            .Select(t => t.Name.Substring(2))
            .Where(n => File.Exists(Path.Combine(projectRoot, "Assets", "GameRes", "Config", n + "Cfg.bytes")))
            .OrderBy(n => n);
        Console.WriteLine("{\"aliases\":{" + string.Join(",", Aliases.Select(a => Str(a.Key) + ":" + Str(a.Value))) + "},\"tables\":[" + string.Join(",", names.Select(Str)) + "]}");
    }

    // id, found, name (simplified-Chinese source text) and optionally all fields
    static void WriteRecord(StringBuilder sb, object key, object cfg)
    {
        sb.Append("{\"id\":").Append(Str(Text(key)));
        if (cfg == null)
        {
            sb.Append(",\"found\":false}");
            return;
        }

        List<KeyValuePair<string, object>> fields = Fields(cfg);
        sb.Append(",\"found\":true,\"name\":").Append(Str(NameOf(cfg, fields)));
        if (full)
        {
            sb.Append(",\"fields\":{");
            sb.Append(string.Join(",", fields.Select(f => Str(f.Key) + ":" + Str(Text(f.Value)))));
            sb.Append('}');
        }
        sb.Append('}');
    }

    // Raw name stored in the table (not localized)
    static string NameOf(object cfg, List<KeyValuePair<string, object>> fields)
    {
        foreach (string candidate in new[] { "name", "Name_zh_0", "Name", "title", "Title" })
        {
            var f = fields.FirstOrDefault(x => x.Key == candidate);
            if (f.Key != null && f.Value is string s) return s;
        }
        return null;
    }

    // Declared instance fields; auto-property backing fields shown under the property name
    static List<KeyValuePair<string, object>> Fields(object cfg)
    {
        var result = new List<KeyValuePair<string, object>>();
        foreach (FieldInfo f in cfg.GetType().GetFields(Inst))
        {
            string name = f.Name;
            if (name.StartsWith("<", StringComparison.Ordinal) && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
            {
                name = name.Substring(1, name.IndexOf('>') - 1);
            }
            result.Add(new KeyValuePair<string, object>(name, f.GetValue(cfg)));
        }
        return result;
    }

    // Compact text form of a field value
    static string Text(object v)
    {
        if (v == null) return null;
        if (v is string s) return s;
        if (v is IFormattable fmt) return fmt.ToString(null, CultureInfo.InvariantCulture);
        if (v is IDictionary dict)
        {
            var parts = new List<string>();
            foreach (DictionaryEntry e in dict) parts.Add(Text(e.Key) + ":" + Text(e.Value));
            return "{" + string.Join(",", parts) + "}";
        }
        if (v is IEnumerable list)
        {
            var parts = new List<string>();
            foreach (object item in list) parts.Add(Text(item));
            return "[" + string.Join(",", parts) + "]";
        }
        var fields = Fields(v);
        if (fields.Count > 0) return "{" + string.Join(",", fields.Select(f => f.Key + "=" + Text(f.Value))) + "}";
        return v.ToString();
    }

    static string Str(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    static void Fail(string message)
    {
        Console.WriteLine("{\"ok\":false,\"error\":" + Str(message) + "}");
    }
}
