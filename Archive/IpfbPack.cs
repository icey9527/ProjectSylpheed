using System;
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

            var processed = new ConcurrentDictionary<uint, byte[]>();

            BuildStandaloneTextures(manifest, inputDir, processed, builtRel);

            foreach (var kv in FNT.BuildAll(manifest, inputDir, texById))
            {
                uint hash = FileId.FromPath(kv.Key);
                processed[hash] = ShouldCompress(kv.Key) ? CompressCustomBytes(kv.Value) : kv.Value;
                builtRel.Add(NormRel(kv.Key));
            }

            foreach (var kv in PRT.BuildAll(manifest, inputDir, texById))
            {
                uint hash = FileId.FromPath(kv.Key);
                processed[hash] = ShouldCompress(kv.Key) ? CompressCustomBytes(kv.Value) : kv.Value;
                builtRel.Add(NormRel(kv.Key));
            }

            var files = CollectFiles(inputDir, managedPngFull, builtRel);

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, rel =>
            {
                string fullPath = Path.Combine(inputDir, rel);
                var (packedName, packedData) = Transformers.ProcessPack(rel, fullPath);
                uint hash = FileId.FromPath(packedName);
                byte[] chunk = ShouldCompress(packedName) ? CompressCustomBytes(packedData) : packedData;
                processed[hash] = chunk;
            });

            WritePak(processed, pakPath);
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

        static void BuildStandaloneTextures(Manifest manifest, string rootDir, ConcurrentDictionary<uint, byte[]> processed, HashSet<string> builtRel)
        {
            foreach (var t in manifest.T32)
            {
                if (G(t, "type") != "0") continue;

                string name = G(t, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                byte[] data = BuildTextureBytes(t, rootDir);
                uint hash = FileId.FromPath(name);
                processed[hash] = ShouldCompress(name) ? CompressCustomBytes(data) : data;

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

        static void WritePak(ConcurrentDictionary<uint, byte[]> processed, string pakPath)
        {
            var sortedHashes = processed.Keys.OrderBy(h => h).ToList();
            string pakDir = Path.GetDirectoryName(pakPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(pakPath);
            string p00Path = Path.Combine(pakDir, $"{baseName}.p00");

            var entries = new List<Entry>(sortedHashes.Count);
            byte[] padBuffer = new byte[2048];

            using (var p00 = new FileStream(p00Path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                for (int i = 0; i < sortedHashes.Count; i++)
                {
                    uint hash = sortedHashes[i];
                    byte[] chunk = processed[hash];

                    entries.Add(new Entry { Hash = hash, Offset = (int)p00.Position, Size = chunk.Length });
                    p00.Write(chunk);

                    if (i < sortedHashes.Count - 1)
                    {
                        long pad = (2048 - p00.Position % 2048) % 2048;
                        while (pad > 0)
                        {
                            int c = (int)Math.Min(pad, 2048);
                            p00.Write(padBuffer, 0, c);
                            pad -= c;
                        }
                    }
                }
            }

            using var pakFs = new FileStream(pakPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var bw = new BinaryWriter(pakFs, Encoding.ASCII, true);

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
            using var compMs = new MemoryStream();
            using (var ds = new DeflateStream(compMs, CompressionLevel.SmallestSize, true))
                ds.Write(raw);

            byte[] comp = compMs.ToArray();
            uint len = (uint)raw.Length;
            uint adler = Adler32(raw);

            byte[] result = new byte[12 + comp.Length + 4];
            result[0] = (byte)'Z'; result[1] = (byte)'1';
            result[2] = (byte)(len >> 24); result[3] = (byte)(len >> 16);
            result[4] = (byte)(len >> 8); result[5] = (byte)len;
            result[6] = (byte)(adler >> 24); result[7] = (byte)(adler >> 16);
            result[8] = (byte)(adler >> 8); result[9] = (byte)adler;
            result[10] = 0x78; result[11] = 0xDA;
            Buffer.BlockCopy(comp, 0, result, 12, comp.Length);
            int p = 12 + comp.Length;
            result[p] = (byte)(adler >> 24); result[p + 1] = (byte)(adler >> 16);
            result[p + 2] = (byte)(adler >> 8); result[p + 3] = (byte)adler;
            return result;
        }

        static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            foreach (byte t in data)
            {
                a = (a + t) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        static string G(Dictionary<string, string> d, string k, string def = "")
            => d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;

        static string NormRel(string rel) => rel.Replace('\\', '/');

        sealed class Entry { public uint Hash; public int Offset, Size; }
    }
}