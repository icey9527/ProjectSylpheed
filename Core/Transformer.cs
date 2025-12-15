using System;
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
        static HashSet<string> enabled = new(map.Keys, StringComparer.OrdinalIgnoreCase);

        static Dictionary<string, ITransformer> BuildMap()
        {
            var d = new Dictionary<string, ITransformer>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in list) d[t.GetType().Name] = t;
            return d;
        }

        public static IReadOnlyList<string> Available => Array.ConvertAll(list, t => t.GetType().Name);

        public static bool TryConfigure(string spec, out string error)
        {
            error = "";
            if (string.IsNullOrWhiteSpace(spec) || spec.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                enabled = new HashSet<string>(map.Keys, StringComparer.OrdinalIgnoreCase);
                return true;
            }

            if (spec.Equals("none", StringComparison.OrdinalIgnoreCase) || spec.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                return true;
            }

            var tokens = SplitTokens(spec);
            if (tokens.Count == 0)
            {
                error = "转换器参数为空";
                return false;
            }

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool baseAll = false;

            if (tokens.Count > 0)
            {
                if (tokens[0].Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    baseAll = true;
                    tokens.RemoveAt(0);
                }
                else if (tokens[0].Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    baseAll = false;
                    tokens.RemoveAt(0);
                }
                else
                {
                    foreach (var t in tokens)
                        if (t.Length > 0 && (t[0] == '-' || t[0] == '!'))
                            baseAll = true;
                }
            }

            if (baseAll) set.UnionWith(map.Keys);

            foreach (var raw in tokens)
            {
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

            enabled = set;
            return true;
        }

        static List<string> SplitTokens(string spec)
        {
            var r = new List<string>();
            foreach (var p in spec.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                r.Add(p.Trim());
            return r;
        }

        static bool IsEnabled(ITransformer t) => enabled.Contains(t.GetType().Name);

        internal static bool TryGetPackTransformer(string name, out ITransformer transformer)
        {
            foreach (var t in list)
            {
                if (!t.CanPack) continue;
                if (!IsEnabled(t) || !t.CanTransformOnPack(name)) continue;
                transformer = t;
                return true;
            }
            transformer = null!;
            return false;
        }

        public static (string name, string path) ProcessExtract(string name, string outPath, byte[] data, Manifest manifest)
        {
            foreach (var t in list)
            {
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
            foreach (var t in list)
            {
                if (!t.CanPack) continue;
                if (!IsEnabled(t) || !t.CanTransformOnPack(name)) continue;
                return t.OnPack(srcPath, name);
            }
            return (name, File.ReadAllBytes(srcPath));
        }
    }
}