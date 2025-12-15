using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using IpfbTool.Core;

namespace IpfbTool.Archive
{
    internal static class IpfbUnpack
    {
        static Manifest _manifest = null!;
        public static void Unpack(string pakPath, string outDir)
        {
            _manifest = new Manifest();
            using var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.RandomAccess);
            using var br = new BinaryReader(fs, Encoding.ASCII, true);

            fs.Position = 4;
            uint idxCount = BeBinary.ReadUInt32(br);

            fs.Position = 0x10;
            var entries = new List<Entry>((int)idxCount);

            for (int i = 0; i < idxCount; i++)
            {
                int hash = BeBinary.ReadInt32(br);
                int offset = BeBinary.ReadInt32(br);
                int size = BeBinary.ReadInt32(br);

                if (hash == 0)
                    break;

                entries.Add(new Entry
                {
                    Hash = unchecked((uint)hash),
                    Offset = offset,
                    Size = size
                });
            }

            string pakDir = Path.GetDirectoryName(pakPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(pakPath);
            PackContext.CurrentOutputDir = outDir;
            Parallel.ForEach(entries, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, e =>
            {
                ExtractEntry(pakDir, baseName, e, outDir);
            });
            _manifest.Save(Path.Combine(outDir, "list.xml"));

        }

        public static Manifest CurrentManifest => _manifest;

        static void ExtractEntry(string pakDir, string baseName, Entry e, string outRoot)
        {
            uint encoded = unchecked((uint)e.Offset);
            int partIndex = (int)(encoded >> 28);
            long innerOffset = encoded & 0x0FFFFFFF;
            long size = (long)(uint)e.Size;

            string partPath = Path.Combine(pakDir, $"{baseName}.p{partIndex:00}");

            using var fs = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.RandomAccess);

            if (innerOffset + size > fs.Length)
                throw new InvalidDataException("条目越界");

            fs.Position = innerOffset;

            byte[] raw = new byte[size];
            int read = 0;
            while (read < raw.Length)
            {
                int r = fs.Read(raw, read, raw.Length - read);
                if (r <= 0) throw new EndOfStreamException();
                read += r;
            }

            byte[] payload;
            if (raw.Length > 16 && raw[0] == (byte)'Z' && raw[1] == (byte)'1')
            {
                using var ms = new MemoryStream(raw, 12, raw.Length - 16, false);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                ds.CopyTo(outMs);
                payload = outMs.ToArray();
            }
            else
            {
                payload = raw;
            }

            string name;
            if (!NameTable.TryGet(e.Hash, out name) || string.IsNullOrWhiteSpace(name))
            {
                name = "$" + e.Hash.ToString("X8");
                name = TryAppendPrefixExtension(name, payload);
            }

            string outPath = Path.Combine(outRoot, name);
            string? outDirPath = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrEmpty(outDirPath))
                Directory.CreateDirectory(outDirPath);

            Transformers.ProcessExtract(name, outPath, payload, _manifest);
        }

        static string TryAppendPrefixExtension(string name, byte[] payload)
        {
            if (payload == null || payload.Length < 4)
                return name;

            string s = Encoding.UTF8.GetString(payload, 0, Math.Min(4, payload.Length)).Replace("\0", "");
            if (s.Length < 3)
                return name;

            foreach (char c in s)
            {
                if (!char.IsLetterOrDigit(c))
                    return name;
            }

            return name + "." + s;
        }

        sealed class Entry
        {
            public uint Hash;
            public int Offset;
            public int Size;
        }
    }
}