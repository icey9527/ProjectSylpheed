using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IpfbTool.Core
{
    internal sealed class TBL : ITransformer
    {
        public bool CanTransformOnExtract(string name)
        {
            string ext = Path.GetExtension(name);
            return ext.Equals(".tbl", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".IDXD", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".IXUD", StringComparison.OrdinalIgnoreCase);
        }

        public (string name, byte[] data) OnExtract(byte[] srcData, string srcName)
        {
            using var ms = new MemoryStream(srcData, false);
            using var br = new BinaryReader(ms);

            byte[] header = br.ReadBytes(4);
            bool isUtf16 = header.AsSpan().SequenceEqual("IXUD"u8);
            int ptrMul = isUtf16 ? 2 : 1;

            int idx1Count = ReadBE(br);
            var idx1 = new List<(int hash, int ptr, int p1, int p2)>(idx1Count);
            for (int i = 0; i < idx1Count; i++)
                idx1.Add((ReadBE(br), ReadBE(br), ReadBE(br), ReadBE(br)));

            int idx2Count = ReadBE(br);
            var idx2 = new List<(int hash, int ptr1, int ptr2)>(idx2Count);
            for (int i = 0; i < idx2Count; i++)
                idx2.Add((ReadBE(br), ReadBE(br), ReadBE(br)));

            ReadBE(br);
            long strStart = ms.Position;

            var sb = new StringBuilder();

            foreach (var (hash, ptr, p1, p2) in idx1)
            {
                string s = ptr != -1 ? ReadString(ms, strStart + (long)ptr * ptrMul, isUtf16) : "";
                sb.AppendFormat("##{0:X8} {1:X8} {2:X8}\n", hash, p1, p2);
                sb.AppendLine(s);
            }

            foreach (var (hash, ptr1, ptr2) in idx2)
            {
                string s1 = ptr1 != -1 ? ReadString(ms, strStart + (long)ptr1 * ptrMul, isUtf16) : "";
                string s2 = ptr2 != -1 ? ReadString(ms, strStart + (long)ptr2 * ptrMul, isUtf16) : "";
                sb.AppendFormat("#{0:X8}\n", hash);
                sb.AppendLine(s1);
                sb.AppendLine(s2);
            }

            string ext = isUtf16 ? ".xdib" : ".xdi";
            return (Path.ChangeExtension(srcName, ext), Encoding.UTF8.GetBytes(sb.ToString()));
        }

        public bool CanTransformOnPack(string name)
        {
            string ext = Path.GetExtension(name);
            return ext.Equals(".xdi", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".xdib", StringComparison.OrdinalIgnoreCase);
        }

        public (string name, byte[] data) OnPack(string srcPath, string srcName)
        {
            bool isUtf16 = srcPath.EndsWith(".xdib", StringComparison.OrdinalIgnoreCase);

            var idx1 = new List<(int hash, int p1, int p2, string s)>();
            var idx2 = new List<(int hash, string s1, string s2)>();

            using (var sr = new StreamReader(srcPath, Encoding.UTF8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0) continue;

                    if (line.StartsWith("##"))
                    {
                        var p = line[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        idx1.Add((
                            Convert.ToInt32(p[0], 16),
                            Convert.ToInt32(p[1], 16),
                            Convert.ToInt32(p[2], 16),
                            sr.ReadLine() ?? ""));
                    }
                    else if (line.StartsWith("#"))
                    {
                        int hash = Convert.ToInt32(line[1..].Trim(), 16);
                        idx2.Add((hash, sr.ReadLine() ?? "", sr.ReadLine() ?? ""));
                    }
                }
            }

            using var strData = new MemoryStream();
            var strOffsets = new Dictionary<string, int>();

            int AddStr(string s)
            {
                if (s.Length == 0) return -1;
                if (strOffsets.TryGetValue(s, out int off)) return isUtf16 ? off / 2 : off;
                off = (int)strData.Position;
                byte[] enc = isUtf16
                    ? Encoding.BigEndianUnicode.GetBytes(s + "\0")
                    : Encoding.UTF8.GetBytes(s + "\0");
                strData.Write(enc);
                strOffsets[s] = off;
                return isUtf16 ? off / 2 : off;
            }

            var idx1Bin = idx1.ConvertAll(x => (x.hash, AddStr(x.s), x.p1, x.p2));
            var idx2Bin = idx2.ConvertAll(x => (x.hash, AddStr(x.s1), AddStr(x.s2)));

            int strSize = isUtf16 ? (int)strData.Length / 2 : (int)strData.Length;

            using var msOut = new MemoryStream();
            using var bw = new BinaryWriter(msOut, Encoding.ASCII, true);

            bw.Write(isUtf16 ? "IXUD"u8 : "IDXD"u8);
            WriteBE(bw, idx1Bin.Count);
            foreach (var (hash, ptr, p1, p2) in idx1Bin)
            {
                WriteBE(bw, hash);
                WriteBE(bw, ptr);
                WriteBE(bw, p1);
                WriteBE(bw, p2);
            }

            WriteBE(bw, idx2Bin.Count);
            foreach (var (hash, ptr1, ptr2) in idx2Bin)
            {
                WriteBE(bw, hash);
                WriteBE(bw, ptr1);
                WriteBE(bw, ptr2);
            }

            WriteBE(bw, strSize);
            bw.Write(strData.ToArray());

            string baseName = Path.GetFileNameWithoutExtension(srcName);
            string dir = Path.GetDirectoryName(srcName) ?? "";
            string suffix = baseName.StartsWith("$") ? ".IXUD" : ".tbl";

            return (Path.Combine(dir, baseName + suffix), msOut.ToArray());
        }

        static int ReadBE(BinaryReader br)
        {
            byte[] b = br.ReadBytes(4);
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        static void WriteBE(BinaryWriter bw, int v)
        {
            bw.Write((byte)(v >> 24));
            bw.Write((byte)(v >> 16));
            bw.Write((byte)(v >> 8));
            bw.Write((byte)v);
        }

        static string ReadString(Stream fs, long offset, bool utf16)
        {
            long pos = fs.Position;
            fs.Position = offset;

            var buf = new List<byte>();
            if (utf16)
            {
                while (true)
                {
                    int b1 = fs.ReadByte(), b2 = fs.ReadByte();
                    if (b1 < 0 || b2 < 0 || (b1 == 0 && b2 == 0)) break;
                    buf.Add((byte)b1);
                    buf.Add((byte)b2);
                }
            }
            else
            {
                int b;
                while ((b = fs.ReadByte()) > 0) buf.Add((byte)b);
            }

            fs.Position = pos;
            return utf16
                ? Encoding.BigEndianUnicode.GetString(buf.ToArray())
                : Encoding.UTF8.GetString(buf.ToArray());
        }
    }
}