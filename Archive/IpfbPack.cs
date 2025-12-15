using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IpfbTool.Core;

namespace IpfbTool.Archive
{
    internal static class IpfbPack
    {
        public static void Pack(string inputDir, string pakPath)
        {
            var manifest = Manifest.Load(Path.Combine(inputDir, "list.xml"));

            var texById = BuildTexIndexById(manifest);
            var managedPngFull = BuildManagedPngSet(inputDir, manifest);
            var builtRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 先做必须串行的“构建类产物”
            var fixedOutputs = new List<(uint hash, byte[] data)>(capacity: 1024);

            BuildStandaloneTextures(manifest, inputDir, fixedOutputs, builtRel);

            foreach (var kv in FNT.BuildAll(manifest, inputDir, texById))
            {
                uint hash = FileId.FromPath(kv.Key);
                byte[] data = ShouldCompress(kv.Key) ? CompressCustomBytes(kv.Value) : kv.Value;
                fixedOutputs.Add((hash, data));
                builtRel.Add(NormRel(kv.Key));
            }

            foreach (var kv in PRT.BuildAll(manifest, inputDir, texById))
            {
                uint hash = FileId.FromPath(kv.Key);
                byte[] data = ShouldCompress(kv.Key) ? CompressCustomBytes(kv.Value) : kv.Value;
                fixedOutputs.Add((hash, data));
                builtRel.Add(NormRel(kv.Key));
            }

            // 收集需要走插件/原始打包的文件
            var files = CollectFiles(inputDir, managedPngFull, builtRel);

            // 并行处理文件：用线程本地 list 收集，最后合并，避免 ConcurrentDictionary 热点
            var bag = new ConcurrentBag<List<(uint hash, byte[] data)>>();

            int dop = Math.Clamp(Environment.ProcessorCount, 1, 12);
            Parallel.ForEach(
                files,
                new ParallelOptions { MaxDegreeOfParallelism = dop },
                () => new List<(uint, byte[])>(capacity: 64),
                (rel, _, local) =>
                {
                    string fullPath = Path.Combine(inputDir, rel);

                    var (packedName, packedData) = Transformers.ProcessPack(rel, fullPath);

                    uint hash = FileId.FromPath(packedName);
                    byte[] chunk = ShouldCompress(packedName) ? CompressCustomBytes(packedData) : packedData;

                    local.Add((hash, chunk));
                    return local;
                },
                local => bag.Add(local)
            );

            // 合并所有输出
            var all = new List<(uint hash, byte[] data)>(fixedOutputs.Count + files.Count);
            all.AddRange(fixedOutputs);
            foreach (var local in bag) all.AddRange(local);

            // 去重：同 hash 后写覆盖前写（行为与 ConcurrentDictionary 最接近）
            // 若你希望“重复视为错误”，这里也可以改成 throw
            var map = new Dictionary<uint, byte[]>(all.Count);
            foreach (var (hash, data) in all) map[hash] = data;

            WritePak(map, pakPath);
        }

        static Dictionary<uint, Dictionary<string, string>> BuildTexIndexById(Manifest manifest)
        {
            var d = new Dictionary<uint, Dictionary<string, string>>();
            foreach (var t in manifest.T32)
            {
                uint id = TexId.Parse(t.TryGetValue("id", out var s) ? s : "");
                if (id != 0) d[id] = t;
            }
            return d;
        }

        static HashSet<string> BuildManagedPngSet(string root, Manifest manifest)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in manifest.T32)
            {
                if (!t.TryGetValue("png", out var rel) || string.IsNullOrWhiteSpace(rel)) continue;
                set.Add(Path.GetFullPath(Path.Combine(root, rel)));
            }
            return set;
        }

        static void BuildStandaloneTextures(
            Manifest manifest,
            string rootDir,
            List<(uint hash, byte[] data)> outputs,
            HashSet<string> builtRel)
        {
            foreach (var t in manifest.T32)
            {
                if (G(t, "type") != "0") continue;

                string name = G(t, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                byte[] data = BuildTextureBytes(t, rootDir);
                uint hash = FileId.FromPath(name);

                outputs.Add((hash, ShouldCompress(name) ? CompressCustomBytes(data) : data));
                builtRel.Add(NormRel(name));
            }
        }

        static byte[] BuildTextureBytes(Dictionary<string, string> texEntry, string rootDir)
        {
            string tex = G(texEntry, "tex");
            return tex.Equals("TBM", StringComparison.OrdinalIgnoreCase)
                ? TBM.Build(texEntry, rootDir)
                : T32.Build(texEntry, rootDir);
        }

        static List<string> CollectFiles(string root, HashSet<string> managedPngFull, HashSet<string> builtRel)
        {
            var list = new List<string>();

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file);
                string name = Path.GetFileName(rel);

                if (name.Equals("list.xml", StringComparison.OrdinalIgnoreCase)) continue;

                string relNorm = NormRel(rel);
                if (builtRel.Contains(relNorm)) continue;

                if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    string full = Path.GetFullPath(file);
                    if (managedPngFull.Contains(full)) continue;
                }

                list.Add(rel);
            }

            return list;
        }

        static bool ShouldCompress(string relPath)
        {
            string ext = Path.GetExtension(relPath);
            if (ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)) return false;
            if (ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        static void WritePak(Dictionary<uint, byte[]> processed, string pakPath)
        {
            var sorted = processed.Keys.OrderBy(h => h).ToList();

            string pakDir = Path.GetDirectoryName(pakPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(pakPath);
            string p00Path = Path.Combine(pakDir, $"{baseName}.p00");

            var entries = new List<Entry>(sorted.Count);

            // 写 p00：顺序写 + 一次性写 padding
            using (var p00 = new FileStream(p00Path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
            {
                for (int i = 0; i < sorted.Count; i++)
                {
                    uint hash = sorted[i];
                    byte[] chunk = processed[hash];

                    int offset = checked((int)p00.Position);
                    entries.Add(new Entry { Hash = hash, Offset = offset, Size = chunk.Length });

                    p00.Write(chunk, 0, chunk.Length);

                    if (i < sorted.Count - 1)
                    {
                        int pad = (int)((2048 - (p00.Position & 2047)) & 2047);
                        if (pad != 0)
                        {
                            Span<byte> zeros = pad <= 4096 ? stackalloc byte[pad] : new byte[pad];
                            zeros.Clear();
                            p00.Write(zeros);
                        }
                    }
                }
            }

            // 写 pak 头与索引
            using var pakFs = new FileStream(pakPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
            using var bw = new BinaryWriter(pakFs, Encoding.ASCII, leaveOpen: true);

            bw.Write("IPFB"u8);
            BeBinary.WriteInt32(bw, entries.Count);
            BeBinary.WriteInt32(bw, 0x800);
            BeBinary.WriteInt32(bw, 0x10000000);

            foreach (var e in entries)
            {
                BeBinary.WriteInt32(bw, unchecked((int)e.Hash));
                BeBinary.WriteInt32(bw, e.Offset);
                BeBinary.WriteInt32(bw, e.Size);
            }
        }

        static byte[] CompressCustomBytes(byte[] raw)
        {
            // Deflate 输出先到 MemoryStream，但避免 ToArray 的额外拷贝：TryGetBuffer + 精确复制
            using var compMs = new MemoryStream(raw.Length / 2);
            using (var ds = new DeflateStream(compMs, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                ds.Write(raw, 0, raw.Length);
            }

            if (!compMs.TryGetBuffer(out ArraySegment<byte> seg))
                seg = new ArraySegment<byte>(compMs.ToArray());

            int compLen = (int)compMs.Length;

            uint len = (uint)raw.Length;
            uint adler = Adler32(raw);

            int outLen = 12 + compLen + 4;
            byte[] result = GC.AllocateUninitializedArray<byte>(outLen);

            result[0] = (byte)'Z'; result[1] = (byte)'1';
            result[2] = (byte)(len >> 24); result[3] = (byte)(len >> 16);
            result[4] = (byte)(len >> 8); result[5] = (byte)len;
            result[6] = (byte)(adler >> 24); result[7] = (byte)(adler >> 16);
            result[8] = (byte)(adler >> 8); result[9] = (byte)adler;
            result[10] = 0x78; result[11] = 0xDA;

            Buffer.BlockCopy(seg.Array!, seg.Offset, result, 12, compLen);

            int p = 12 + compLen;
            result[p] = (byte)(adler >> 24); result[p + 1] = (byte)(adler >> 16);
            result[p + 2] = (byte)(adler >> 8); result[p + 3] = (byte)adler;

            return result;
        }

        static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        static string G(Dictionary<string, string> d, string k, string def = "")
            => d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;

        static string NormRel(string rel) => rel.Replace('\\', '/');

        sealed class Entry
        {
            public uint Hash;
            public int Offset, Size;
        }
    }
}