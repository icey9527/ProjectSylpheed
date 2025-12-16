using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IpfbTool.Core;
using ICSharpCode.SharpZipLib.Zip.Compression;
using ICSharpCode.SharpZipLib.Zip.Compression.Streams;

namespace IpfbTool.Archive
{
    internal static class IpfbPack
    {
        public static void Pack(string inputDir, string pakPath)
        {
            using var log = new AsyncLog();

            var manifest = Manifest.Load(Path.Combine(inputDir, "list.xml"));

            var texById = BuildTexIndexById(manifest);
            var managedPngFull = BuildManagedPngSet(inputDir, manifest);
            var builtRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var fixedOutputs = new List<(uint hash, byte[] data)>(capacity: 1024);

            BuildStandaloneTextures(manifest, inputDir, fixedOutputs, builtRel, log);

            foreach (var kv in FNT.BuildAll(manifest, inputDir, texById))
            {
                log.Write(kv.Key);
                uint hash = FileId.FromPath(kv.Key);
                byte[] data = ShouldCompress(kv.Key) ? CompressCustomBytes(kv.Value) : kv.Value;
                fixedOutputs.Add((hash, data));
                builtRel.Add(NormRel(kv.Key));
            }

            foreach (var kv in PRT.BuildAll(manifest, inputDir, texById))
            {
                log.Write(kv.Key);
                uint hash = FileId.FromPath(kv.Key);
                byte[] data = ShouldCompress(kv.Key) ? CompressCustomBytes(kv.Value) : kv.Value;
                fixedOutputs.Add((hash, data));
                builtRel.Add(NormRel(kv.Key));
            }

            var skipDirs = new List<string>();
            foreach (var d in manifest.PRT)
            {
                if (d.TryGetValue("kind", out var k) &&
                    k.Equals("unpack_dir", StringComparison.OrdinalIgnoreCase) &&
                    d.TryGetValue("dir", out var dir) &&
                    !string.IsNullOrWhiteSpace(dir))
                {
                    skipDirs.Add(NormRel(dir).TrimEnd('/') + "/");
                }
            }
            skipDirs.Sort(StringComparer.OrdinalIgnoreCase);

            var files = CollectFiles(inputDir, managedPngFull, builtRel, skipDirs);

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
                    Console.WriteLine(packedName);

                    uint hash = FileId.FromPath(packedName);
                    byte[] chunk = ShouldCompress(packedName) ? CompressCustomBytes(packedData) : packedData;

                    local.Add((hash, chunk));
                    return local;
                },
                local => bag.Add(local)
            );

            var all = new List<(uint hash, byte[] data)>(fixedOutputs.Count + files.Count);
            all.AddRange(fixedOutputs);
            foreach (var local in bag) all.AddRange(local);

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
            HashSet<string> builtRel,
            AsyncLog log)
        {
            foreach (var t in manifest.T32)
            {
                if (G(t, "type") != "0") continue;

                string name = G(t, "name");
                if (string.IsNullOrWhiteSpace(name)) continue;

                log.Write(name);

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

        static List<string> CollectFiles(
            string root,
            HashSet<string> managedPngFull,
            HashSet<string> builtRel,
            List<string> skipDirs)
        {
            var list = new List<string>(8192);

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(root, file);
                string name = Path.GetFileName(rel);

                if (name.Equals("list.xml", StringComparison.OrdinalIgnoreCase)) continue;

                string relNorm = NormRel(rel);
                if (builtRel.Contains(relNorm)) continue;

                if (skipDirs.Count != 0 && IsInSkipDir(relNorm, skipDirs)) continue;

                if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    string full = Path.GetFullPath(file);
                    if (managedPngFull.Contains(full)) continue;
                }

                list.Add(rel);
            }

            return list;
        }

        static bool IsInSkipDir(string relNorm, List<string> skipDirs)
        {
            for (int i = 0; i < skipDirs.Count; i++)
            {
                if (relNorm.StartsWith(skipDirs[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
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

            byte[] padBuf = new byte[2048];

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
                        if (pad != 0) p00.Write(padBuf, 0, pad);
                    }
                }
            }

            using var pakFs = new FileStream(pakPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
            using var bw = new BinaryWriter(pakFs, Encoding.ASCII, leaveOpen: true);

            bw.Write("IPFB"u8);
            BeBinary.WriteInt32(bw, entries.Count);
            BeBinary.WriteInt32(bw, 0x800);  // size_padding
            BeBinary.WriteInt32(bw, 0x10000000); // lim_container

            foreach (var e in entries)
            {
                BeBinary.WriteInt32(bw, unchecked((int)e.Hash));
                BeBinary.WriteInt32(bw, e.Offset);
                BeBinary.WriteInt32(bw, e.Size);
            }
        }

        static readonly ThreadLocal<Deflater> s_deflater = new(() => new Deflater(7, noZlibHeaderOrFooter: true));

        static byte[] CompressCustomBytes(byte[] raw)
        {
            var deflater = s_deflater.Value!;
            deflater.Reset();
            deflater.SetLevel(7);

            using var ms = new MemoryStream(raw.Length / 2);
            using (var ds = new DeflaterOutputStream(ms, deflater))
            {
                ds.IsStreamOwner = false;
                ds.Write(raw, 0, raw.Length);
                ds.Finish();
            }

            byte[] def = ms.ToArray();
            int compLen = def.Length;

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

            Buffer.BlockCopy(def, 0, result, 12, compLen);

            int p = 12 + compLen;
            result[p] = (byte)(adler >> 24);
            result[p + 1] = (byte)(adler >> 16);
            result[p + 2] = (byte)(adler >> 8);
            result[p + 3] = (byte)adler;

            return result;
        }

        static uint Adler32(ReadOnlySpan<byte> data)
        {
            const uint MOD = 65521;
            uint a = 1, b = 0;

            int i = 0;
            int len = data.Length;
            const int NMAX = 5552;

            while (len > 0)
            {
                int n = len < NMAX ? len : NMAX;
                len -= n;

                for (int j = 0; j < n; j++)
                {
                    a += data[i++];
                    b += a;
                }

                a %= MOD;
                b %= MOD;
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

        sealed class AsyncLog : IDisposable
        {
            readonly BlockingCollection<string> q = new(new ConcurrentQueue<string>(), boundedCapacity: 4096);
            readonly Thread t;

            public AsyncLog()
            {
                t = new Thread(Consume) { IsBackground = true, Name = "pack-log" };
                t.Start();
            }

            public void Write(string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                q.TryAdd(s);
            }

            void Consume()
            {
                foreach (var s in q.GetConsumingEnumerable())
                    Console.WriteLine(s);
            }

            public void Dispose()
            {
                q.CompleteAdding();
                try { t.Join(); } catch { }
                q.Dispose();
            }
        }
    }
}