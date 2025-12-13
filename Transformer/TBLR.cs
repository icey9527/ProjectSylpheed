using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IpfbTool.Core
{
    internal sealed class TBLR : ITransformer
    {
        static TBLR()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        static readonly Encoding CP932 = Encoding.GetEncoding(932);
        static readonly Encoding UTF16BE = Encoding.BigEndianUnicode;

        static bool IsTarget(string name)
        {
            var ext = Path.GetExtension(name);
            return ext.Equals(".tbl", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".IDXD", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".IXUD", StringComparison.OrdinalIgnoreCase);
        }

        public bool CanTransformOnExtract(string name) => IsTarget(name);

        public (string name, byte[] data) OnExtract(byte[] srcData, string srcName)
        {
            if (srcData.Length < 8) return (srcName, srcData);

            var header = srcData.AsSpan(0, 4);
            bool isIdxd = header.SequenceEqual("IDXD"u8);
            bool isIxud = header.SequenceEqual("IXUD"u8);
            if (!isIdxd && !isIxud) return (srcName, srcData);

            bool isUtf16 = isIxud;
            var enc = isUtf16 ? UTF16BE : CP932;
            int ptrMul = isUtf16 ? 2 : 1;

            using var ms = new MemoryStream(srcData, false);
            using var br = new BinaryReader(ms);

            ms.Position = 4;

            int sectionCount = BeBinary.ReadInt32(br);
            var sections = new (int namePtr, int startIdx, int endIdx)[sectionCount];
            for (int i = 0; i < sectionCount; i++)
            {
                BeBinary.ReadInt32(br);
                sections[i] = (BeBinary.ReadInt32(br), BeBinary.ReadInt32(br), BeBinary.ReadInt32(br));
            }

            int kvCount = BeBinary.ReadInt32(br);
            var kvs = new (int key, int keyPtr, int valuePtr)[kvCount];
            for (int i = 0; i < kvCount; i++)
                kvs[i] = (BeBinary.ReadInt32(br), BeBinary.ReadInt32(br), BeBinary.ReadInt32(br));

            BeBinary.ReadInt32(br);
            long strBase = ms.Position;

            string GetStr(int ptr) => ptr < 0 ? "" : ReadString(ms, strBase + (long)ptr * ptrMul, enc, isUtf16);

            var sb = new StringBuilder();
            foreach (var (namePtr, startIdx, endIdx) in sections)
            {
                sb.Append('[').Append(GetStr(namePtr)).Append("]\r\n");

                int end = endIdx < kvCount ? endIdx : kvCount;
                for (int i = startIdx; i < end; i++)
                {
                    var (key, keyPtr, valuePtr) = kvs[i];
                    var value = GetStr(valuePtr);

                    if (keyPtr == -1)
                        sb.Append('#').AppendFormat("{0:X8}", key).Append('=').Append(value).Append("\r\n");
                    else
                        sb.Append(GetStr(keyPtr)).Append('=').Append(value).Append("\r\n");
                }

                sb.Append("\r\n");
            }

            var text = sb.ToString();
            if (!isUtf16) return (srcName, enc.GetBytes(text));

            var body = enc.GetBytes(text);
            var result = new byte[2 + body.Length];
            result[0] = 0xFE;
            result[1] = 0xFF;
            Buffer.BlockCopy(body, 0, result, 2, body.Length);
            return (srcName, result);
        }

        public bool CanTransformOnPack(string name) => IsTarget(name);

        public (string name, byte[] data) OnPack(string srcPath, string srcName)
        {
            var raw = File.ReadAllBytes(srcPath);

            if (raw.Length >= 4)
            {
                var h = raw.AsSpan(0, 4);
                if (h.SequenceEqual("IDXD"u8) || h.SequenceEqual("IXUD"u8))
                    return (srcName, raw);
            }

            bool isUtf16 = raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF;
            var enc = isUtf16 ? UTF16BE : CP932;
            int ptrMul = isUtf16 ? 2 : 1;

            int HashOf(string s) => isUtf16 ? Hash.UTF16BEhash(s) : Hash.JIShash(s);

            var sections = new List<(string name, int start, int end)>();
            var kvs = new List<(int? numKey, string strKey, string value)>();

            using (var ms = new MemoryStream(raw))
            using (var sr = new StreamReader(ms, enc, true))
            {
                string section = null;
                int start = 0;

                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;

                    int cut = line.IndexOf("//", StringComparison.Ordinal);
                    if (cut >= 0) line = line[..cut];
                    cut = line.IndexOf(';');
                    if (cut >= 0) line = line[..cut];

                    if (line.Length == 0) continue;

                    if (line[0] == '[' && line[^1] == ']')
                    {
                        if (section != null) sections.Add((section, start, kvs.Count));
                        section = line[1..^1];
                        start = kvs.Count;
                        continue;
                    }

                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;

                    var keyPart = line[..eq];
                    var valPart = line[(eq + 1)..];

                    if (keyPart.Length > 0 && keyPart[0] == '#' && int.TryParse(keyPart[1..], System.Globalization.NumberStyles.HexNumber, null, out int numKey))
                        kvs.Add((numKey, null, valPart));
                    else
                        kvs.Add((null, keyPart, valPart));
                }

                if (section != null) sections.Add((section, start, kvs.Count));
            }

            using var strPool = new MemoryStream();
            var strPtrs = new Dictionary<string, int>(StringComparer.Ordinal);
            int emptyPtr = -1;

            int AddString(string s)
            {
                s ??= "";

                if (s.Length == 0)
                {
                    if (emptyPtr >= 0) return emptyPtr;
                    emptyPtr = checked((int)(strPool.Position / ptrMul));
                    if (isUtf16) { strPool.WriteByte(0); strPool.WriteByte(0); }
                    else strPool.WriteByte(0);
                    strPtrs[""] = emptyPtr;
                    return emptyPtr;
                }

                if (strPtrs.TryGetValue(s, out var p)) return p;

                p = checked((int)(strPool.Position / ptrMul));
                var bytes = enc.GetBytes(s);
                strPool.Write(bytes, 0, bytes.Length);
                if (isUtf16) { strPool.WriteByte(0); strPool.WriteByte(0); }
                else strPool.WriteByte(0);
                strPtrs[s] = p;
                return p;
            }

            var sectionsBin = new (int hash, int namePtr, int startIdx, int endIdx)[sections.Count];
            for (int i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                sectionsBin[i] = (HashOf(s.name), AddString(s.name), s.start, s.end);
            }

            var kvsBin = new (int key, int keyPtr, int valuePtr)[kvs.Count];
            for (int i = 0; i < kvs.Count; i++)
            {
                var kv = kvs[i];
                kvsBin[i] = kv.numKey.HasValue
                    ? (kv.numKey.Value, -1, AddString(kv.value))
                    : (HashOf(kv.strKey), AddString(kv.strKey), AddString(kv.value));
            }

            using var ms2 = new MemoryStream();
            using var bw = new BinaryWriter(ms2);

            bw.Write(isUtf16 ? "IXUD"u8 : "IDXD"u8);

            BeBinary.WriteInt32(bw, sectionsBin.Length);
            foreach (var (hash, namePtr, startIdx, endIdx) in sectionsBin)
            {
                BeBinary.WriteInt32(bw, hash);
                BeBinary.WriteInt32(bw, namePtr);
                BeBinary.WriteInt32(bw, startIdx);
                BeBinary.WriteInt32(bw, endIdx);
            }

            BeBinary.WriteInt32(bw, kvsBin.Length);
            foreach (var (key, keyPtr, valuePtr) in kvsBin)
            {
                BeBinary.WriteInt32(bw, key);
                BeBinary.WriteInt32(bw, keyPtr);
                BeBinary.WriteInt32(bw, valuePtr);
            }

            BeBinary.WriteInt32(bw, checked((int)(strPool.Length / ptrMul)));
            if (strPool.TryGetBuffer(out var seg))
                bw.Write(seg.Array, seg.Offset, seg.Count);
            else
                bw.Write(strPool.ToArray());

            return (srcName, ms2.ToArray());
        }

        static string ReadString(Stream s, long off, Encoding enc, bool isUtf16)
        {
            long p = s.Position;
            s.Position = off;

            var buf = new List<byte>(64);
            if (isUtf16)
            {
                int a, b;
                while ((a = s.ReadByte()) >= 0 && (b = s.ReadByte()) >= 0 && (a | b) != 0)
                {
                    buf.Add((byte)a);
                    buf.Add((byte)b);
                }
            }
            else
            {
                int b;
                while ((b = s.ReadByte()) > 0) buf.Add((byte)b);
            }

            s.Position = p;
            return buf.Count == 0 ? "" : enc.GetString(buf.ToArray());
        }
    }
}