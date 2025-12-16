using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace IpfbTool.Core
{
    internal static class Transformers
    {
        static readonly ITransformer[] list =
        {
            new TBL(),
            new ISB(),
            new T32(),
            new TBM(),
            new PRT(),
            new FNT()
        };

        static readonly Dictionary<string, ITransformer> map = BuildMap();

        static volatile HashSet<string> enabled = new(map.Keys, StringComparer.OrdinalIgnoreCase);
        static int enabledVersion;

        static readonly ConcurrentDictionary<string, (int version, ITransformer? t)> packCache =
            new(StringComparer.OrdinalIgnoreCase);

        static Dictionary<string, ITransformer> BuildMap()
        {
            var d = new Dictionary<string, ITransformer>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in list) d[t.GetType().Name] = t;
            return d;
        }

        public static IReadOnlyList<string> Available
        {
            get
            {
                var a = new string[list.Length];
                for (int i = 0; i < list.Length; i++) a[i] = list[i].GetType().Name;
                return a;
            }
        }

        public static bool TryConfigure(string spec, out string error)
        {
            error = "";

            HashSet<string> set;

            if (string.IsNullOrWhiteSpace(spec) || spec.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                set = new HashSet<string>(map.Keys, StringComparer.OrdinalIgnoreCase);
                ApplyEnabled(set);
                return true;
            }

            if (spec.Equals("none", StringComparison.OrdinalIgnoreCase) || spec.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ApplyEnabled(set);
                return true;
            }

            var tokens = SplitTokens(spec);
            if (tokens.Count == 0)
            {
                error = "转换器参数为空";
                return false;
            }

            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool baseAll = false;

            if (tokens.Count > 0)
            {
                var first = tokens[0];
                if (first.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    baseAll = true;
                    tokens.RemoveAt(0);
                }
                else if (first.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    baseAll = false;
                    tokens.RemoveAt(0);
                }
                else
                {
                    for (int i = 0; i < tokens.Count; i++)
                    {
                        var t = tokens[i];
                        if (t.Length > 0 && (t[0] == '-' || t[0] == '!')) { baseAll = true; break; }
                    }
                }
            }

            if (baseAll) set.UnionWith(map.Keys);

            for (int i = 0; i < tokens.Count; i++)
            {
                var raw = tokens[i];
                if (string.IsNullOrWhiteSpace(raw)) continue;

                bool remove = raw[0] == '-' || raw[0] == '!';
                var name = remove ? raw[1..] : raw;

                if (!map.ContainsKey(name))
                {
                    error = $"未知转换器: {name}";
                    return false;
                }

                if (remove) set.Remove(name);
                else set.Add(name);
            }

            ApplyEnabled(set);
            return true;
        }

        static void ApplyEnabled(HashSet<string> set)
        {
            enabled = set;
            System.Threading.Interlocked.Increment(ref enabledVersion);
            packCache.Clear();
        }

        static List<string> SplitTokens(string spec)
        {
            var r = new List<string>();
            int i = 0;
            while (i < spec.Length)
            {
                while (i < spec.Length && IsSep(spec[i])) i++;
                if (i >= spec.Length) break;

                int j = i;
                while (j < spec.Length && !IsSep(spec[j])) j++;

                var tok = spec.AsSpan(i, j - i).Trim();
                if (!tok.IsEmpty) r.Add(tok.ToString());

                i = j;
            }
            return r;

            static bool IsSep(char c) => c == ',' || c == ';' || c == ' ' || c == '\t' || c == '\r' || c == '\n';
        }

        static bool IsEnabled(ITransformer t)
        {
            var e = enabled;
            return e.Contains(t.GetType().Name);
        }

        internal static bool TryGetPackTransformer(string name, out ITransformer transformer)
        {
            transformer = ResolvePackTransformer(name);
            return transformer != null;
        }

        public static (string name, string path) ProcessExtract(string name, string outPath, byte[] data, Manifest manifest)
        {
            for (int i = 0; i < list.Length; i++)
            {
                var t = list[i];
                if (!t.CanExtract) continue;
                if (!IsEnabled(t) || !t.CanTransformOnExtract(name)) continue;

                var (newName, outData) = t.OnExtract(data, name, manifest);
                if (outData == null)
                    return (newName, outPath);

                string dir = Path.GetDirectoryName(outPath) ?? "";
                string newPath = Path.Combine(dir, Path.GetFileName(newName));

                Directory.CreateDirectory(Path.GetDirectoryName(newPath) ?? ".");
                File.WriteAllBytes(newPath, outData);
                return (newName, newPath);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
            File.WriteAllBytes(outPath, data);
            return (name, outPath);
        }

        public static (string name, byte[] data) ProcessPack(string name, string srcPath)
        {
            var t = ResolvePackTransformer(name);
            if (t != null)
                return t.OnPack(srcPath, name);

            return (name, File.ReadAllBytes(srcPath));
        }

        static ITransformer? ResolvePackTransformer(string name)
        {
            int v = enabledVersion;
            string key = CacheKey(name);

            if (packCache.TryGetValue(key, out var hit) && hit.version == v)
            {
                var ht = hit.t;
                if (ht == null) return null;
                if (!IsEnabled(ht) || !ht.CanPack || !ht.CanTransformOnPack(name)) return null;
                return ht;
            }

            ITransformer? found = null;

            for (int i = 0; i < list.Length; i++)
            {
                var t = list[i];
                if (!t.CanPack) continue;
                if (!IsEnabled(t) || !t.CanTransformOnPack(name)) continue;
                found = t;
                break;
            }

            packCache[key] = (v, found);
            return found;
        }

        static string CacheKey(string name)
        {
            string ext = Path.GetExtension(name);
            if (!string.IsNullOrEmpty(ext))
                return ext;

            int slash = name.LastIndexOfAny(new[] { '/', '\\' });
            if (slash >= 0) name = name[(slash + 1)..];

            return name;
        }
    }
}