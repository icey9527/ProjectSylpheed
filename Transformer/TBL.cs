using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace IpfbTool.Core
{
    internal sealed class TBL : ITransformer
    {
        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        static readonly Encoding CP932 = Init932();
        static readonly Encoding UTF16BE = Encoding.BigEndianUnicode;

        static Encoding Init932()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(932);
            }
            catch
            {
                return Encoding.ASCII;
            }
        }

        static bool IsTbl(string name) =>
            Path.GetExtension(name).Equals(".tbl", StringComparison.OrdinalIgnoreCase);

        static bool IsIdx(string name) =>
            Path.GetExtension(name).Equals(".idx", StringComparison.OrdinalIgnoreCase);

        static bool IsIxu(string name) =>
            Path.GetExtension(name).Equals(".ixu", StringComparison.OrdinalIgnoreCase);

        public bool CanExtract => true;
        public bool CanPack => true;

        public bool CanTransformOnExtract(string name) => IsTbl(name);

        public bool CanTransformOnPack(string name) => IsIdx(name) || IsIxu(name);

        public (string name, byte[] data) OnExtract(byte[] srcData, string srcName, Manifest manifest)
        {
            if (srcData.Length < 8) return (srcName, srcData);

            var header = srcData.AsSpan(0, 4);
            bool isIdxd = header.SequenceEqual("IDXD"u8);
            bool isIxud = header.SequenceEqual("IXUD"u8);
            if (!isIdxd && !isIxud) return (srcName, srcData);

            bool isUtf16 = isIxud;
            var enc = isUtf16 ? UTF16BE : CP932;
            int ptrMul = isUtf16 ? 2 : 1;

            int pos = 4;

            if (!TryReadI32BE(srcData, ref pos, out int sectionCount)) return (srcName, srcData);
            if (sectionCount < 0 || sectionCount > 1_000_000) return (srcName, srcData);

            var sections = new (int namePtr, int startIdx, int endIdx)[sectionCount];
            for (int i = 0; i < sectionCount; i++)
            {
                if (!TrySkip(srcData, ref pos, 4)) return (srcName, srcData);
                if (!TryReadI32BE(srcData, ref pos, out int namePtr)) return (srcName, srcData);
                if (!TryReadI32BE(srcData, ref pos, out int startIdx)) return (srcName, srcData);
                if (!TryReadI32BE(srcData, ref pos, out int endIdx)) return (srcName, srcData);
                sections[i] = (namePtr, startIdx, endIdx);
            }

            if (!TryReadI32BE(srcData, ref pos, out int kvCount)) return (srcName, srcData);
            if (kvCount < 0 || kvCount > 5_000_000) return (srcName, srcData);

            var kvs = new (int key, int keyPtr, int valuePtr)[kvCount];
            for (int i = 0; i < kvCount; i++)
            {
                if (!TryReadI32BE(srcData, ref pos, out int key)) return (srcName, srcData);
                if (!TryReadI32BE(srcData, ref pos, out int keyPtr)) return (srcName, srcData);
                if (!TryReadI32BE(srcData, ref pos, out int valuePtr)) return (srcName, srcData);
                kvs[i] = (key, keyPtr, valuePtr);
            }

            if (!TryReadI32BE(srcData, ref pos, out _)) return (srcName, srcData);
            int strBase = pos;

            string GetStr(int ptr)
            {
                if (ptr < 0) return "";
                long offL = (long)strBase + (long)ptr * ptrMul;
                if (offL < 0 || offL > int.MaxValue) return "";
                int off = (int)offL;
                if ((uint)off >= (uint)srcData.Length) return "";
                return ReadStringFromSpan(srcData, off, enc, isUtf16);
            }

            var sb = new StringBuilder(srcData.Length / 2);

            for (int si = 0; si < sections.Length; si++)
            {
                var (namePtr, startIdx, endIdx) = sections[si];
                sb.Append('[').Append(GetStr(namePtr)).Append("]\n");

                int start = startIdx < 0 ? 0 : startIdx;
                int end = endIdx < 0 ? 0 : endIdx;
                if (end > kvCount) end = kvCount;

                for (int i = start; i < end; i++)
                {
                    var (key, keyPtr, valuePtr) = kvs[i];
                    var value = GetStr(valuePtr);

                    if (keyPtr == -1)
                        sb.Append('#').Append(key.ToString("X8", CultureInfo.InvariantCulture)).Append('=').Append(value).Append('\n');
                    else
                        sb.Append(GetStr(keyPtr)).Append('=').Append(value).Append('\n');
                }

                sb.Append('\n');
            }

            return (Path.ChangeExtension(srcName, isUtf16 ? ".ixu" : ".idx"), Utf8NoBom.GetBytes(sb.ToString()));
        }

        public (string name, byte[] data) OnPack(string srcPath, string srcName)
        {
            byte[] raw = File.ReadAllBytes(srcPath);

            bool isUtf16 = IsIxu(srcName) || srcPath.EndsWith(".ixu", StringComparison.OrdinalIgnoreCase);
            var enc = isUtf16 ? UTF16BE : CP932;
            int ptrMul = isUtf16 ? 2 : 1;

            int HashOf(string s) => isUtf16 ? Hash.UTF16BEhash(s) : Hash.JIShash(s);

            var sections = new List<(string name, int start, int end)>(64);
            var kvs = new List<(int? numKey, string? strKey, string value)>(4096);

            using (var ms = new MemoryStream(raw))
            using (var sr = new StreamReader(ms, Utf8NoBom, detectEncodingFromByteOrderMarks: true))
            {
                string? section = null;
                int start = 0;

                string? line;
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

                    if (keyPart.Length > 0 && keyPart[0] == '#' &&
                        int.TryParse(keyPart.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int numKey))
                        kvs.Add((numKey, null, valPart));
                    else
                        kvs.Add((null, keyPart, valPart));
                }

                if (section != null) sections.Add((section, start, kvs.Count));
            }

            using var strPool = new MemoryStream(raw.Length / 4);
            var strPtrs = new Dictionary<string, int>(StringComparer.Ordinal);
            int emptyPtr = -1;

            int AddString(string? s)
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
                    : (HashOf(kv.strKey ?? ""), AddString(kv.strKey), AddString(kv.value));
            }

            using var ms2 = new MemoryStream(raw.Length);
            using var bw = new BinaryWriter(ms2);

            bw.Write(isUtf16 ? "IXUD"u8 : "IDXD"u8);

            BeBinary.WriteInt32(bw, sectionsBin.Length);
            for (int i = 0; i < sectionsBin.Length; i++)
            {
                var (hash, namePtr, startIdx, endIdx) = sectionsBin[i];
                BeBinary.WriteInt32(bw, hash);
                BeBinary.WriteInt32(bw, namePtr);
                BeBinary.WriteInt32(bw, startIdx);
                BeBinary.WriteInt32(bw, endIdx);
            }

            BeBinary.WriteInt32(bw, kvsBin.Length);
            for (int i = 0; i < kvsBin.Length; i++)
            {
                var (key, keyPtr, valuePtr) = kvsBin[i];
                BeBinary.WriteInt32(bw, key);
                BeBinary.WriteInt32(bw, keyPtr);
                BeBinary.WriteInt32(bw, valuePtr);
            }

            BeBinary.WriteInt32(bw, checked((int)(strPool.Length / ptrMul)));

            if (strPool.TryGetBuffer(out var seg) && seg.Array != null)
                bw.Write(seg.Array, seg.Offset, seg.Count);
            else
                bw.Write(strPool.ToArray());

            return (Path.ChangeExtension(srcName, ".tbl"), ms2.ToArray());
        }

        static bool TryReadI32BE(byte[] d, ref int pos, out int v)
        {
            if ((uint)(pos + 4) > (uint)d.Length) { v = 0; return false; }
            v = BeBinary.ReadInt32(d, pos);
            pos += 4;
            return true;
        }

        static bool TrySkip(byte[] d, ref int pos, int n)
        {
            if (n < 0) return false;
            if ((uint)(pos + n) > (uint)d.Length) return false;
            pos += n;
            return true;
        }

        static string ReadStringFromSpan(byte[] data, int off, Encoding enc, bool isUtf16)
        {
            if (!isUtf16)
            {
                int i = off;
                while ((uint)i < (uint)data.Length && data[i] != 0) i++;
                int len = i - off;
                return len <= 0 ? "" : enc.GetString(data, off, len);
            }
            else
            {
                int i = off;
                while ((uint)(i + 1) < (uint)data.Length)
                {
                    if ((data[i] | data[i + 1]) == 0) break;
                    i += 2;
                }
                int len = i - off;
                return len <= 0 ? "" : enc.GetString(data, off, len);
            }
        }
    }
}