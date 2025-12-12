using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using IpfbTool.Core;
using System.Xml; 

namespace IpfbTool.Archive
{
    internal static class IpfbPack
    {
        public static void Pack(string inputDir, string pakPath)
        {
            PackContext.RootDir = inputDir;
            Transformers.PreloadForPack(inputDir);
            LSTA.PreloadXml(inputDir);
            RATC.PreloadXml(inputDir);

            var files = CollectFiles(inputDir);
            var processed = new ConcurrentDictionary<uint, byte[]>();

            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, rel =>
            {
                string fullPath = Path.Combine(inputDir, rel);
                var (packedName, packedData) = Transformers.ProcessPack(rel, fullPath);
                uint hash = FileId.FromPath(packedName);
                byte[] chunk = ShouldCompress(packedName) ? CompressCustomBytes(packedData) : packedData;
                processed[hash] = chunk;
            });

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

static List<string> CollectFiles(string root)
{
    var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var xml in Directory.EnumerateFiles(root, "*.xml", SearchOption.AllDirectories))
    {
        try
        {
            var doc = new XmlDocument();
            doc.Load(xml);
            string dir = Path.GetDirectoryName(xml) ?? "";

            if (doc.DocumentElement?.Name == "RATC")
            {
                foreach (XmlElement file in doc.DocumentElement.SelectNodes("file")!)
                foreach (XmlElement item in file.SelectNodes("item")!)
                {
                    string name = item.GetAttribute("name");
                    skip.Add(Path.Combine(dir, name + ".png"));
                }
            }
            else if (doc.DocumentElement?.Name == "LSTA")
            {
                foreach (XmlElement file in doc.DocumentElement.SelectNodes("file")!)
                {
                    string fileName = file.GetAttribute("name");
                    foreach (XmlElement item in file.SelectNodes("item")!)
                    {
                        int index = int.TryParse(item.GetAttribute("index"), out int i) ? i : -1;
                        if (index > 0)
                            skip.Add(Path.Combine(dir, $"{fileName}.{index}.png"));
                    }
                }
            }
        }
        catch { }
    }

    var list = new List<string>();
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        string rel = Path.GetRelativePath(root, file);
        string name = Path.GetFileName(rel);
        if (name.Equals("list.xml", StringComparison.OrdinalIgnoreCase)) continue;
        if (name.Equals("RATC.xml", StringComparison.OrdinalIgnoreCase)) continue;
        if (name.Equals("LSTA.xml", StringComparison.OrdinalIgnoreCase)) continue;
        if (name.Equals("Non-compression-list.txt", StringComparison.OrdinalIgnoreCase)) continue;
        if (skip.Contains(file)) continue;
        list.Add(rel);
    }
    return list;
}

        static bool ShouldCompress(string relPath)
        {
            string ext = Path.GetExtension(relPath);
            if (ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase)) return false;
            if (ext.Equals(".ttc", StringComparison.OrdinalIgnoreCase)) return false;
            if (Path.GetFileName(relPath).StartsWith("$DA45E966", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
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

        sealed class Entry { public uint Hash; public int Offset, Size; }
    }
}