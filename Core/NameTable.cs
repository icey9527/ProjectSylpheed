using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IpfbTool.Core
{
    internal static class NameTable
    {
        static readonly Lazy<Dictionary<uint, string>> LazyTable = new(Load);
        public static bool TryGet(uint hash, out string name) => LazyTable.Value.TryGetValue(hash, out name);

        static Dictionary<uint, string> Load()
        {
            string baseDir = AppContext.BaseDirectory;
            string hashPath = Path.Combine(baseDir, "list.hash");
            string txtPath = Path.Combine(baseDir, "list.txt");

            if (File.Exists(hashPath))
                return LoadFromHash(hashPath);

            if (File.Exists(txtPath))
                return BuildFromText(txtPath, hashPath);

            return new Dictionary<uint, string>();
        }

        static Dictionary<uint, string> LoadFromHash(string path)
        {
            byte[] data = File.ReadAllBytes(path);
            if (data.Length < 4)
                return new Dictionary<uint, string>();

            int count = BitConverter.ToInt32(data, 0);
            if (count <= 0)
                return new Dictionary<uint, string>();

            int entriesOffset = 4;
            int namesOffset = entriesOffset + count * 8;
            if (namesOffset > data.Length)
                return new Dictionary<uint, string>();

            var dict = new Dictionary<uint, string>(count);

            for (int i = 0; i < count; i++)
            {
                int entryOff = entriesOffset + i * 8;
                if (entryOff + 8 > data.Length)
                    break;

                uint h = BitConverter.ToUInt32(data, entryOff);
                uint rel = BitConverter.ToUInt32(data, entryOff + 4);
                int pos = namesOffset + (int)rel;
                if (pos < namesOffset || pos >= data.Length)
                    continue;

                int end = pos;
                while (end < data.Length && data[end] != 0)
                    end++;

                if (end <= pos)
                    continue;

                string name = Encoding.UTF8.GetString(data, pos, end - pos);
                if (!dict.ContainsKey(h))
                    dict[h] = name;
            }

            return dict;
        }

        static Dictionary<uint, string> BuildFromText(string txtPath, string hashPath)
        {
            var dict = new Dictionary<uint, string>();
            foreach (var lineRaw in File.ReadAllLines(txtPath, Encoding.UTF8))
            {
                string line = lineRaw.Trim();
                if (line.Length == 0)
                    continue;
                if (line[0] == '#')
                    continue;

                string norm = line.Trim();
                uint h = FileId.FromPath(norm);
                if (!dict.ContainsKey(h))
                    dict[h] = norm;
            }

            var items = new List<KeyValuePair<uint, string>>(dict);
            items.Sort((a, b) => a.Key.CompareTo(b.Key));

            using var namesMs = new MemoryStream();
            using var entriesMs = new MemoryStream();
            using var namesBw = new BinaryWriter(namesMs, Encoding.UTF8, true);
            using var entriesBw = new BinaryWriter(entriesMs, Encoding.UTF8, true);

            foreach (var kv in items)
            {
                uint h = kv.Key;
                string name = kv.Value;
                int off = (int)namesMs.Position;
                byte[] nb = Encoding.UTF8.GetBytes(name);
                namesBw.Write(nb);
                namesBw.Write((byte)0);

                entriesBw.Write(h);
                entriesBw.Write((uint)off);
            }

            byte[] countBytes = BitConverter.GetBytes((uint)items.Count);
            byte[] entriesBytes = entriesMs.ToArray();
            byte[] namesBytes = namesMs.ToArray();

            byte[] all = new byte[4 + entriesBytes.Length + namesBytes.Length];
            Buffer.BlockCopy(countBytes, 0, all, 0, 4);
            Buffer.BlockCopy(entriesBytes, 0, all, 4, entriesBytes.Length);
            Buffer.BlockCopy(namesBytes, 0, all, 4 + entriesBytes.Length, namesBytes.Length);

            File.WriteAllBytes(hashPath, all);
            return dict;
        }
    }
}