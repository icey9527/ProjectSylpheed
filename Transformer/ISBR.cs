using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;

namespace IpfbTool.Core
{
    internal sealed class ISB : ITransformer
    {
        const uint M_INT = 0x40403, M_STR = 0x40400, M_FLOAT = 0x40402;
        const uint M_VAR = 0x80001, M_WSTR = 0x80002, M_END = 0x401;
        const uint KEY_TH = 0x3000001;

        public bool CanTransformOnExtract(string n) => Path.GetExtension(n).Equals(".isb", StringComparison.OrdinalIgnoreCase);
        public bool CanTransformOnPack(string n) => Path.GetExtension(n).Equals(".isl", StringComparison.OrdinalIgnoreCase);

        public (string, byte[]) OnExtract(byte[] src, string name)
        {
            var buf = ToU32(src);
            int blocks = (int)(buf[^1] & 0x3FFFFFFF);
            int tblStart = buf.Length - 1 - blocks;
            var tbl = new uint[blocks + 1];
            for (int i = 0; i < blocks; i++) tbl[i] = buf[tblStart + i];
            tbl[blocks] = (uint)tblStart * 4;

            var sb = new StringBuilder();
            uint key = 0;

            for (int b = 0; b < blocks; b++)
            {
                int s = (int)(tbl[b] / 4), e = (int)(tbl[b + 1] / 4);
                if (s >= e) continue;

                sb.AppendLine($"ISL{tbl[b]:X}:{{");
                int i = s;

                while (i < e)
                {
                    uint v = buf[i++];
                    if (v >= KEY_TH && i == s + 1) { key = v; sb.AppendLine($".key {key:X8}"); continue; }
                    if (v == 0 || v == M_END) continue;
                    if (v == M_INT && i < e) { sb.AppendLine($".int {(int)buf[i++]}"); continue; }
                    if (v == M_FLOAT && i < e) { sb.AppendLine($".float {BitConverter.ToSingle(BitConverter.GetBytes(buf[i++]), 0)}"); continue; }
                    if (v == M_VAR && i < e) { sb.AppendLine($".var {Fv(buf[i++])}"); continue; }
                    if (v == M_STR && i < e) { i = WrStr(buf, i, e, key, false, sb); continue; }
                    if (v == M_WSTR && i < e) { i = WrStr(buf, i, e, key, true, sb); continue; }
                    i = FmtOp((ushort)(v & 0xFFFF), buf, i, e, key, sb);
                }
                sb.AppendLine("}");
                sb.AppendLine();
            }
            return (Path.ChangeExtension(name, ".isl"), Encoding.UTF8.GetBytes(sb.ToString()));
        }

        int FmtOp(ushort op, uint[] buf, int i, int e, uint key, StringBuilder sb)
        {
            switch (op)
            {
                case 0x0000:
                    uint fid = buf[i++];
                    var args = new List<(bool isStr, string val)>();
                    i = CollectArgs(buf, i, e, key, args);
                    bool hasStr = args.Exists(a => a.isStr);
                    if (hasStr)
                    {
                        sb.AppendLine($"\\{fid:X8}(");
                        var nonStr = args.Where(a => !a.isStr).Select(a => a.val);
                        sb.AppendLine(string.Join(", ", nonStr));
                        foreach (var (isStr, val) in args)
                            if (isStr) sb.AppendLine(val);
                        sb.AppendLine(")");
                    }
                    else
                    {
                        sb.AppendLine($"\\{fid:X8}({string.Join(", ", args.Select(a => a.val))})");
                    }
                    break;
                case 0x0001: sb.AppendLine($"{Fv(buf[i])} = {Fv(buf[i + 1])}"); i += 2; break;
                case 0x0002: sb.AppendLine($"{Fv(buf[i])} = {(int)buf[i + 1]}"); i += 2; break;
                case 0x0003: sb.AppendLine($"push {Fv(buf[i++])}"); break;
                case 0x0004: sb.AppendLine($"push {(int)buf[i++]}"); break;
                case 0x0005: sb.AppendLine($"pop {Fv(buf[i++])}"); break;
                case 0x0100: sb.AppendLine($"{Fv(buf[i++])}++"); break;
                case 0x0101: sb.AppendLine($"{Fv(buf[i++])}--"); break;
                case 0x0102: sb.AppendLine($"{Fv(buf[i])} += {Fv(buf[i + 1])}"); i += 2; break;
                case 0x0103: sb.AppendLine($"{Fv(buf[i])} += {(int)buf[i + 1]}"); i += 2; break;
                case 0x0104: sb.AppendLine($"{Fv(buf[i])} -= {Fv(buf[i + 1])}"); i += 2; break;
                case 0x0105: sb.AppendLine($"{Fv(buf[i])} -= {(int)buf[i + 1]}"); i += 2; break;
                case 0x0106: sb.AppendLine($"{Fv(buf[i])} *= {Fv(buf[i + 1])}"); i += 2; break;
                case 0x0107: sb.AppendLine($"{Fv(buf[i])} *= {(int)buf[i + 1]}"); i += 2; break;
                case 0x0108: sb.AppendLine($"{Fv(buf[i])} /= {Fv(buf[i + 1])}"); i += 2; break;
                case 0x0109: sb.AppendLine($"{Fv(buf[i])} /= {(int)buf[i + 1]}"); i += 2; break;
                case 0x0200: sb.AppendLine($"{Fv(buf[i])} = !{Fv(buf[i])}"); i++; break;
                case 0x0201: sb.AppendLine($"{Fv(buf[i])} &= {Fv(buf[i + 1])}"); i += 2; break;
                case 0x0202: sb.AppendLine($"{Fv(buf[i])} &= {(int)buf[i + 1]}"); i += 2; break;
                case 0x0203: sb.AppendLine($"{Fv(buf[i])} |= {Fv(buf[i + 1])}"); i += 2; break;
                case 0x0204: sb.AppendLine($"{Fv(buf[i])} |= {(int)buf[i + 1]}"); i += 2; break;
                case 0x0300: sb.AppendLine($"{Fv(buf[i])} = ({Fv(buf[i])} == {Fv(buf[i + 1])})"); i += 2; break;
                case 0x0301: sb.AppendLine($"{Fv(buf[i])} = ({Fv(buf[i])} == {(int)buf[i + 1]})"); i += 2; break;
                case 0x0302: sb.AppendLine($"{Fv(buf[i])} = ({Fv(buf[i])} != {Fv(buf[i + 1])})"); i += 2; break;
                case 0x0303: sb.AppendLine($"{Fv(buf[i])} = ({Fv(buf[i])} != {(int)buf[i + 1]})"); i += 2; break;
                case 0x0304: sb.AppendLine($"{Fv(buf[i])} = ({Fv(buf[i])} > {Fv(buf[i + 1])})"); i += 2; break;
                case 0x0305: sb.AppendLine($"{Fv(buf[i])} = ({Fv(buf[i])} > {(int)buf[i + 1]})"); i += 2; break;
                case 0x0306: sb.AppendLine($"{Fv(buf[i])} = ({Fv(buf[i])} >= {Fv(buf[i + 1])})"); i += 2; break;
                case 0x0307: sb.AppendLine($"{Fv(buf[i])} = ({Fv(buf[i])} >= {(int)buf[i + 1]})"); i += 2; break;
                case 0x0308: sb.AppendLine($"{Fv(buf[i])} = ({Fv(buf[i])} < {Fv(buf[i + 1])})"); i += 2; break;
                case 0x0309: sb.AppendLine($"{Fv(buf[i])} = ({Fv(buf[i])} < {(int)buf[i + 1]})"); i += 2; break;
                case 0x030A: sb.AppendLine($"{Fv(buf[i])} = ({Fv(buf[i])} <= {Fv(buf[i + 1])})"); i += 2; break;
                case 0x030B: sb.AppendLine($"{Fv(buf[i])} = ({Fv(buf[i])} <= {(int)buf[i + 1]})"); i += 2; break;
                case 0x0400: sb.AppendLine($"gosub ISL{buf[i++]:X8}"); break;
                case 0x0401: sb.AppendLine("return"); break;
                case 0x0402: sb.AppendLine($"goto ISL{buf[i++]:X8}"); break;
                case 0x0403: sb.AppendLine($"goto ISL{buf[i++]:X}"); break;
                case 0x0500: sb.AppendLine($"if {Fv(buf[i])} == 0 goto ISL{buf[i + 1]:X}"); i += 2; break;
                case 0x0501: sb.AppendLine($"if {Fv(buf[i])} != 0 goto ISL{buf[i + 1]:X}"); i += 2; break;
                case 0x0502: sb.AppendLine($"if {Fv(buf[i])} == {Fv(buf[i + 1])} goto ISL{buf[i + 2]:X}"); i += 3; break;
                case 0x0503: sb.AppendLine($"if {Fv(buf[i])} == {(int)buf[i + 1]} goto ISL{buf[i + 2]:X}"); i += 3; break;
                case 0x0504: sb.AppendLine($"if {Fv(buf[i])} != {Fv(buf[i + 1])} goto ISL{buf[i + 2]:X}"); i += 3; break;
                case 0x0505: sb.AppendLine($"if {Fv(buf[i])} != {(int)buf[i + 1]} goto ISL{buf[i + 2]:X}"); i += 3; break;
                case 0x0506: sb.AppendLine($"if {Fv(buf[i])} > {Fv(buf[i + 1])} goto ISL{buf[i + 2]:X}"); i += 3; break;
                case 0x0507: sb.AppendLine($"if {Fv(buf[i])} > {(int)buf[i + 1]} goto ISL{buf[i + 2]:X}"); i += 3; break;
                case 0x0508: sb.AppendLine($"if {Fv(buf[i])} >= {Fv(buf[i + 1])} goto ISL{buf[i + 2]:X}"); i += 3; break;
                case 0x0509: sb.AppendLine($"if {Fv(buf[i])} >= {(int)buf[i + 1]} goto ISL{buf[i + 2]:X}"); i += 3; break;
                case 0x050A: sb.AppendLine($"if {Fv(buf[i])} < {Fv(buf[i + 1])} goto ISL{buf[i + 2]:X}"); i += 3; break;
                case 0x050B: sb.AppendLine($"if {Fv(buf[i])} < {(int)buf[i + 1]} goto ISL{buf[i + 2]:X}"); i += 3; break;
                case 0x050C: sb.AppendLine($"if {Fv(buf[i])} <= {Fv(buf[i + 1])} goto ISL{buf[i + 2]:X}"); i += 3; break;
                case 0x050D: sb.AppendLine($"if {Fv(buf[i])} <= {(int)buf[i + 1]} goto ISL{buf[i + 2]:X}"); i += 3; break;
                default: sb.AppendLine($".raw {op:X4}"); break;
            }
            return i;
        }

        int CollectArgs(uint[] buf, int i, int e, uint key, List<(bool, string)> args)
        {
            while (i < e)
            {
                uint m = buf[i];
                if (m == M_END) { i++; break; }
                if (m == M_INT) { i++; args.Add((false, ((int)buf[i++]).ToString())); }
                else if (m == M_FLOAT) { i++; args.Add((false, $"{BitConverter.ToSingle(BitConverter.GetBytes(buf[i++]), 0)}f")); }
                else if (m == M_VAR) { i++; args.Add((false, Fv(buf[i++]))); }
                else if (m == M_STR) { i++; var (t, ni) = DecStr(buf, i, e, key, false); args.Add((true, t)); i = ni; }
                else if (m == M_WSTR) { i++; var (t, ni) = DecStr(buf, i, e, key, true); args.Add((true, t)); i = ni; }
                else break;
            }
            return i;
        }

        int WrStr(uint[] buf, int i, int e, uint key, bool ascii, StringBuilder sb)
        {
            var (t, ni) = DecStr(buf, i, e, key, ascii);
            sb.AppendLine(t);
            return ni;
        }

        (string, int) DecStr(uint[] buf, int i, int e, uint key, bool ascii)
        {
            uint len = buf[i++];
            int wc = (int)((len + 3) / 4);
            if (i + wc > e) return ("", i);
            byte[] b = new byte[wc * 4];
            for (int j = 0; j < wc; j++)
            {
                uint d = Ror3(buf[i + j]) ^ key;
                b[j * 4] = (byte)d; b[j * 4 + 1] = (byte)(d >> 8);
                b[j * 4 + 2] = (byte)(d >> 16); b[j * 4 + 3] = (byte)(d >> 24);
            }
            return ((ascii ? Encoding.ASCII.GetString(b, 0, (int)len) : Encoding.Unicode.GetString(b, 0, (int)len))
                .Replace("\r", "").Replace("\0", ""), i + wc);
        }

        string Fv(uint v) => (v & 0x30000000) switch
        {
            0x00000000 => $"r{v & 0x0FFFFFFF}",
            0x10000000 => $"t{v & 0x0FFFFFFF}",
            0x20000000 => $"s{v & 0x0FFFFFFF}",
            0x30000000 => $"g{v & 0x0FFFFFFF}",
            _ => $"${v:X8}"
        };

        uint[] ToU32(byte[] src)
        {
            int pad = (4 - src.Length % 4) % 4;
            byte[] buf = new byte[src.Length + pad];
            Buffer.BlockCopy(src, 0, buf, 0, src.Length);
            uint[] r = new uint[buf.Length / 4];
            for (int j = 0; j < r.Length; j++) r[j] = BitConverter.ToUInt32(buf, j * 4);
            return r;
        }

        static uint Ror3(uint x) => (x >> 3) | (x << 29);
        public (string, byte[]) OnPack(string p, string n) => throw new NotImplementedException();
    }
}