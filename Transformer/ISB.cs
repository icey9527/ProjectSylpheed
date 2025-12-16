using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace IpfbTool.Core
{
    internal sealed class ISB : ITransformer
    {
        const uint P_INT = 0x40403;
        const uint P_FLOAT = 0x40402;
        const uint P_WSTR = 0x40400;
        const uint P_STR = 0x80002;
        const uint P_VAR = 0x80001;
        const uint P_END = 0x0401;

        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        static readonly Encoding Sjis = InitSjis();
        static readonly byte[] EllipsisSjis2 = InitEllipsis2();

        static readonly string[] CmdNames =
        {
            "AppendText","ClearText","CloseRat","HideRat","HideText","KeyWait","LoadRat","PlayBgm","PlaySe",
            "PlayVoice","ShowRat","ShowText","StopBgm","text","TextColor","TextSpeed","Wait"
        };

        static readonly string[] ScenarioNames =
        {
            "Briefing01","Briefing02","Briefing03","Briefing04","Briefing05","Briefing06","Briefing07","Briefing08",
            "Briefing09","Briefing10","Briefing11","Briefing12","Briefing13","Briefing14","Briefing15","Briefing16",
            "Challenge01","Challenge02","Challenge03","Challenge04","Challenge05","Challenge06",
            "CloseCtrl","CloseReadyRoom","HideCtrl","LoadCtrl","MapWait","NewGame","OpenReadyRoom","OpenReadyRoomFirst",
            "ShowCtrl","TextWait",
            "Tutorial_0101","Tutorial_0102","Tutorial_0103","Tutorial_0201","Tutorial_0301","Tutorial_0302","Tutorial_0401",
            "Tutorial_0501","Tutorial_0601",
            "WindowClose","WindowOpen"
        };

        static readonly Dictionary<uint, string> CmdByFileHash = BuildCmdByFileHash();
        static readonly Dictionary<uint, string> CallByMakeStrId = BuildCallByMakeStrId();

        static readonly Dictionary<ushort, (string Name, string? Fmt)> Opcodes = new()
        {
            { 0x0000, ("CMD",  null) },
            { 0x0001, ("MOV",  "vv") },
            { 0x0002, ("MOV",  "vi") },
            { 0x0003, ("PUSH", "v")  },
            { 0x0004, ("PUSH", "p")  },
            { 0x0005, ("POP",  "v")  },

            { 0x0100, ("INC",  "v")  },
            { 0x0101, ("DEC",  "v")  },
            { 0x0102, ("ADD",  "vv") },
            { 0x0103, ("ADD",  "vi") },
            { 0x0104, ("SUB",  "vv") },
            { 0x0105, ("SUB",  "vi") },
            { 0x0106, ("MUL",  "vv") },
            { 0x0107, ("MUL",  "vi") },
            { 0x0108, ("DIV",  "vv") },
            { 0x0109, ("DIV",  "vi") },

            { 0x0200, ("NOT",  "v")  },
            { 0x0201, ("AND",  "vv") },
            { 0x0202, ("AND",  "vi") },
            { 0x0203, ("OR",   "vv") },
            { 0x0204, ("OR",   "vi") },

            { 0x0300, ("SETE",  "vv") },
            { 0x0301, ("SETE",  "vi") },
            { 0x0302, ("SETNE", "vv") },
            { 0x0303, ("SETNE", "vi") },
            { 0x0304, ("SETG",  "vv") },
            { 0x0305, ("SETG",  "vi") },
            { 0x0306, ("SETGE", "vv") },
            { 0x0307, ("SETGE", "vi") },
            { 0x0308, ("SETL",  "vv") },
            { 0x0309, ("SETL",  "vi") },
            { 0x030A, ("SETLE", "vv") },
            { 0x030B, ("SETLE", "vi") },

            { 0x0400, ("CALL", "s") },
            { 0x0401, ("RET",  "")  },
            { 0x0402, ("JMP",  "s") },
            { 0x0403, ("JMP",  "l") },

            { 0x0500, ("JZ",  "vl")  },
            { 0x0501, ("JNZ", "vl")  },
            { 0x0502, ("JE",  "vvl") },
            { 0x0503, ("JE",  "vil") },
            { 0x0504, ("JNE", "vvl") },
            { 0x0505, ("JNE", "vil") },
            { 0x0506, ("JG",  "vvl") },
            { 0x0507, ("JG",  "vil") },
            { 0x0508, ("JGE", "vvl") },
            { 0x0509, ("JGE", "vil") },
            { 0x050A, ("JL",  "vvl") },
            { 0x050B, ("JL",  "vil") },
            { 0x050C, ("JLE", "vvl") },
            { 0x050D, ("JLE", "vil") },
        };

        static readonly Dictionary<string, ushort> NameToOpcodeBase = BuildNameToOpcodeBase();

        public bool CanExtract => true;
        public bool CanPack => true;

        public bool CanTransformOnExtract(string n) =>
            Path.GetExtension(n).Equals(".isb", StringComparison.OrdinalIgnoreCase);

        public bool CanTransformOnPack(string n) =>
            Path.GetExtension(n).Equals(".scn", StringComparison.OrdinalIgnoreCase);

        public (string, byte[]) OnExtract(byte[] src, string name, Manifest manifest)
        {
            if (src.Length < 8)
                return (Path.ChangeExtension(name, ".scn"), Utf8NoBom.GetBytes(""));

            uint total = ReadU32LE(src, src.Length - 4);
            int blocks = (int)(total & 0x3FFFFFFFu);
            int tblStart = src.Length - 4 - blocks * 4;
            if (blocks <= 0 || tblStart < 0)
                return (Path.ChangeExtension(name, ".scn"), Utf8NoBom.GetBytes(""));

            var addrs = new uint[blocks];
            for (int i = 0; i < blocks; i++)
                addrs[i] = ReadU32LE(src, tblStart + i * 4);

            var sb = new StringBuilder(4096);

            int b = 0;
            while (b < blocks)
            {
                ReadHeader(src, addrs, tblStart, blocks, b, out uint key, out uint msgs);

                sb.Append(key.ToString("X8"))
                  .Append(':')
                  .Append(msgs.ToString(CultureInfo.InvariantCulture))
                  .AppendLine(" {");

                b++;

                while (b < blocks)
                {
                    uint start = addrs[b];
                    uint end = (b + 1 < blocks) ? addrs[b + 1] : (uint)tblStart;
                    if (end > (uint)tblStart) end = (uint)tblStart;

                    var (line, isRet) = ParseInstructionBlock(src, start, end, key);
                    if (line != null) sb.AppendLine(line);

                    b++;
                    if (isRet) break;
                }

                sb.AppendLine("}");
                sb.AppendLine();
            }

            return (Path.ChangeExtension(name, ".scn"), Utf8NoBom.GetBytes(sb.ToString()));
        }

        public (string, byte[]) OnPack(string p, string n)
        {
            var file = new List<byte>(64 * 1024);
            var offsets = new List<uint>(4096);

            uint curKey = 0;
            bool inScenario = false;

            using var sr = new StreamReader(p, Utf8NoBom, detectEncodingFromByteOrderMarks: true);

            var block = new List<byte>(256);
            var argBuf = new List<string>(16);

            string? raw;
            int li = 0;

            while ((raw = sr.ReadLine()) != null)
            {
                li++;
                if (raw.Length == 0) continue;

                string trimmed = raw.Trim();
                if (trimmed.Length == 0) continue;

                if (trimmed.EndsWith("{", StringComparison.Ordinal))
                {
                    string head = trimmed.Substring(0, trimmed.Length - 1).Trim();
                    int colon = head.IndexOf(':');
                    if (colon <= 0) throw new FormatException($"Bad header at line {li}: {raw}");

                    string keyStr = head.Substring(0, colon).Trim();
                    string msgStr = head.Substring(colon + 1).Trim();

                    curKey = ParseHexU32(keyStr);
                    uint curMsgs = ParseU32(msgStr);

                    offsets.Add((uint)file.Count);
                    WriteU32(file, curKey);
                    WriteU32(file, curMsgs);

                    inScenario = true;
                    continue;
                }

                if (trimmed == "}")
                {
                    inScenario = false;
                    continue;
                }

                if (!inScenario) continue;

                if (TryParseBlockHeader(trimmed, out string blockCmd, out string headerArgs))
                {
                    if (!TryReadBlock(sr, ref li, blockCmd, out var lines))
                        throw new FormatException($"Unclosed {blockCmd} block near line {li}");

                    block.Clear();

                    int opcodePos = block.Count;
                    WriteU32(block, 0x0000u);
                    WriteU32(block, Hash.Filehash(blockCmd));

                    SplitArgsTo(headerArgs, argBuf);
                    for (int i = 0; i < argBuf.Count; i++)
                        WriteCmdParam(block, argBuf[i], curKey, blockCmd);

                    for (int i = 0; i < lines.Count; i++)
                    {
                        WriteU32(block, P_WSTR);
                        WriteEncBytes(block, Encoding.Unicode.GetBytes(lines[i]), curKey, blockCmd);
                    }

                    WriteU32(block, P_END);

                    int paramLen = block.Count - opcodePos - 8;
                    if ((uint)paramLen > 0xFFFFu)
                        throw new InvalidOperationException($"paramLen overflow at line {li}: {paramLen}");
                    block[opcodePos + 2] = (byte)paramLen;
                    block[opcodePos + 3] = (byte)(paramLen >> 8);

                    if ((block.Count & 3) != 0)
                        throw new InvalidOperationException($"Block not aligned at line {li}");

                    offsets.Add((uint)file.Count);
                    file.AddRange(block);
                    continue;
                }

                block.Clear();

                string body = raw;

                if (TryParseCmdCallHeader(body, out string cmdName, out string argsText))
                {
                    int opcodePos = block.Count;
                    WriteU32(block, 0x0000u);
                    WriteU32(block, Hash.Filehash(cmdName));

                    SplitArgsTo(argsText, argBuf);
                    for (int i = 0; i < argBuf.Count; i++)
                        WriteCmdParam(block, argBuf[i], curKey, cmdName);

                    WriteU32(block, P_END);

                    int paramLen = block.Count - opcodePos - 8;
                    if ((uint)paramLen > 0xFFFFu)
                        throw new InvalidOperationException($"paramLen overflow at line {li}: {paramLen}");

                    block[opcodePos + 2] = (byte)paramLen;
                    block[opcodePos + 3] = (byte)(paramLen >> 8);
                }
                else
                {
                    string ins = body.Trim();

                    if (ins.Equals("RET", StringComparison.OrdinalIgnoreCase) ||
                        ins.Equals("return", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteU32(block, 0x0401u);
                    }
                    else
                    {
                        string mnem;
                        string opsPart;
                        int sp = ins.IndexOf(' ');
                        if (sp < 0) { mnem = ins; opsPart = ""; }
                        else { mnem = ins.Substring(0, sp).Trim(); opsPart = ins.Substring(sp + 1).Trim(); }

                        string[] ops = opsPart.Length == 0 ? Array.Empty<string>() : SplitOps(opsPart);
                        ushort opcode = ResolveOpcode(mnem, ops);

                        int opcodePos = block.Count;
                        WriteU32(block, opcode);
                        WriteOperands(block, opcode, ops);

                        int paramLen = block.Count - opcodePos - 4;
                        if ((uint)paramLen > 0xFFFFu)
                            throw new InvalidOperationException($"paramLen overflow at line {li}: {paramLen}");

                        block[opcodePos + 2] = (byte)paramLen;
                        block[opcodePos + 3] = (byte)(paramLen >> 8);
                    }
                }

                if ((block.Count & 3) != 0)
                    throw new InvalidOperationException($"Block not aligned at line {li}");

                offsets.Add((uint)file.Count);
                file.AddRange(block);
            }

            int blocks = offsets.Count;

            for (int i = 0; i < blocks; i++)
                WriteU32(file, offsets[i]);

            WriteU32(file, (uint)blocks);

            return (Path.ChangeExtension(n, ".isb"), file.ToArray());
        }

        static bool TryParseBlockHeader(string trimmedLine, out string cmdName, out string headerArgs)
        {
            cmdName = "";
            headerArgs = "";

            int p = trimmedLine.IndexOf('(');
            if (p <= 0) return false;

            cmdName = trimmedLine.Substring(0, p).Trim();
            if (cmdName.Length == 0) return false;

            headerArgs = trimmedLine.Substring(p + 1);

            if (headerArgs.EndsWith(")", StringComparison.Ordinal))
                return false;

            return true;
        }

        static bool TryReadBlock(StreamReader sr, ref int li, string cmdName, out List<string> lines)
        {
            lines = new List<string>();
            string end = cmdName + ")";

            while (true)
            {
                string? raw = sr.ReadLine();
                if (raw == null) return false;
                li++;

                if (raw.Trim().Equals(end, StringComparison.OrdinalIgnoreCase))
                    return true;

                lines.Add(raw);
            }
        }

        static bool TryParseCmdCallHeader(string line, out string name, out string args)
        {
            name = "";
            args = "";

            int p = line.IndexOf('(');
            if (p <= 0) return false;

            string s = line.TrimEnd();
            if (!s.EndsWith(")", StringComparison.Ordinal)) return false;

            name = line.Substring(0, p).Trim();

            int last = s.LastIndexOf(')');
            if (last <= p) return false;

            args = s.Substring(p + 1, last - p - 1);
            return name.Length != 0;
        }

        static void ReadHeader(byte[] src, uint[] addrs, int tblStart, int blocks, int b, out uint key, out uint msgs)
        {
            key = 0; msgs = 0;
            uint start = addrs[b];
            uint end = (b + 1 < blocks) ? addrs[b + 1] : (uint)tblStart;
            if (end > (uint)tblStart) end = (uint)tblStart;
            if (start + 8 <= end && end <= src.Length)
            {
                key = ReadU32LE(src, (int)start);
                msgs = ReadU32LE(src, (int)start + 4);
            }
        }

        static (string? Line, bool IsRet) ParseInstructionBlock(byte[] data, uint start, uint end, uint key)
        {
            if (end <= start || end > data.Length) return (null, false);
            int pos = (int)start;

            if (!TryReadU32LE(data, ref pos, (int)end, out uint first))
                return (null, false);

            ushort op = (ushort)(first & 0xFFFF);
            if (!Opcodes.TryGetValue(op, out var def))
                return ($"unk_{op:X4}", false);

            if (def.Fmt is null)
            {
                if (!TryReadU32LE(data, ref pos, (int)end, out uint h))
                    return ("CMD(?)", false);

                string cmdName = CmdByFileHash.TryGetValue(h, out var n) ? n : $"0x{h:X8}";

                var ps = new List<string>(8);
                var wstrs = new List<string>(8);

                while (true)
                {
                    if (!TryReadU32LE(data, ref pos, (int)end, out uint ptype)) break;
                    if (ptype == P_END) break;

                    if (ptype == P_INT)
                    {
                        if (!TryReadI32LE(data, ref pos, (int)end, out int iv)) { ps.Add("?"); break; }
                        ps.Add(iv.ToString(CultureInfo.InvariantCulture));
                    }
                    else if (ptype == P_FLOAT)
                    {
                        if (!TryReadF32LE(data, ref pos, (int)end, out float fv)) { ps.Add("?"); break; }
                        ps.Add(fv.ToString("G6", CultureInfo.InvariantCulture) + "f");
                    }
                    else if (ptype == P_WSTR)
                    {
                        wstrs.Add(ReadEncWString(data, ref pos, (int)end, key));
                    }
                    else if (ptype == P_STR)
                    {
                        ps.Add($"\"{Escape(ReadEncAsciiString(data, ref pos, (int)end, key))}\"");
                    }
                    else if (ptype == P_VAR)
                    {
                        if (!TryReadU32LE(data, ref pos, (int)end, out uint vv)) { ps.Add("?"); break; }
                        ps.Add($"@{vv:X8}");
                    }
                    else
                    {
                        ps.Add($"?{ptype:X8}");
                        break;
                    }
                }

                if (wstrs.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.Append(cmdName).Append('(');
                    if (ps.Count != 0) sb.Append(string.Join(", ", ps));
                    sb.Append('\n');

                    for (int i = 0; i < wstrs.Count; i++)
                        sb.Append(wstrs[i]).Append('\n');

                    sb.Append(cmdName).Append(')');
                    return (sb.ToString(), false);
                }

                return ($"{cmdName}({string.Join(", ", ps)})", false);
            }

            if (def.Fmt.Length == 0)
                return (def.Name, op == 0x0401);

            var args = new List<string>(def.Fmt.Length);
            for (int i = 0; i < def.Fmt.Length; i++)
            {
                char c = def.Fmt[i];
                switch (c)
                {
                    case 'v':
                        if (!TryReadU32LE(data, ref pos, (int)end, out uint v)) return ($"{def.Name} ?", false);
                        args.Add($"@{v:X8}");
                        break;
                    case 'p':
                        if (!TryReadU32LE(data, ref pos, (int)end, out uint p)) return ($"{def.Name} ?", false);
                        args.Add($"*{p:X8}");
                        break;
                    case 'i':
                        if (!TryReadI32LE(data, ref pos, (int)end, out int iv)) return ($"{def.Name} ?", false);
                        args.Add(iv.ToString(CultureInfo.InvariantCulture));
                        break;
                    case 'l':
                        if (!TryReadU32LE(data, ref pos, (int)end, out uint l)) return ($"{def.Name} ?", false);
                        args.Add($"L{l}");
                        break;
                    case 's':
                        if (!TryReadU32LE(data, ref pos, (int)end, out uint sid)) return ($"{def.Name} ?", false);
                        args.Add(CallByMakeStrId.TryGetValue(sid, out var sn) ? sn : $"#{sid:X8}");
                        break;
                }
            }

            return ($"{def.Name} {string.Join(", ", args)}", op == 0x0401);
        }

        static void WriteOperands(List<byte> block, ushort opcode, string[] ops)
        {
            var fmt = Opcodes[opcode].Fmt ?? "";
            if (fmt.Length == 0) return;

            if (fmt.Length != ops.Length)
                throw new FormatException($"Operand count mismatch for {Opcodes[opcode].Name}: need {fmt.Length}, got {ops.Length}");

            for (int i = 0; i < fmt.Length; i++)
            {
                char c = fmt[i];
                string t = ops[i].Trim();

                switch (c)
                {
                    case 'v': WriteU32(block, ParseVarU32(t)); break;
                    case 'p': WriteU32(block, ParsePtrU32(t)); break;
                    case 'i': WriteI32(block, ParseI32(t)); break;
                    case 'l': WriteU32(block, ParseLabelU32(t)); break;
                    case 's': WriteU32(block, ParseSymbolU32(t)); break;
                    default: throw new FormatException($"Unknown operand fmt '{c}'");
                }
            }
        }

        static void WriteCmdParam(List<byte> block, string token, uint key, string cmdName)
        {
            token = token.Trim();
            if (token.Length == 0) return;

            if (token.StartsWith("@", StringComparison.Ordinal))
            {
                WriteU32(block, P_VAR);
                WriteU32(block, ParseVarU32(token));
                return;
            }

            if (token.EndsWith("f", StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(token.AsSpan(0, token.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float fv))
            {
                WriteU32(block, P_FLOAT);
                WriteU32(block, BitConverter.SingleToUInt32Bits(fv));
                return;
            }

            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iv2))
            {
                WriteU32(block, P_INT);
                WriteI32(block, iv2);
                return;
            }

            if (token.StartsWith("L\"", StringComparison.Ordinal) && token.EndsWith("\"", StringComparison.Ordinal))
            {
                WriteU32(block, P_WSTR);
                string s = Unescape(token.Substring(2, token.Length - 3));
                WriteEncBytes(block, Encoding.Unicode.GetBytes(s), key, cmdName);
                return;
            }

            if (token.StartsWith("\"", StringComparison.Ordinal) && token.EndsWith("\"", StringComparison.Ordinal))
            {
                WriteU32(block, P_STR);
                string s = Unescape(token.Substring(1, token.Length - 2));
                WriteEncBytes(block, Encoding.ASCII.GetBytes(s), key, cmdName);
                return;
            }

            WriteU32(block, P_WSTR);
            WriteEncBytes(block, Encoding.Unicode.GetBytes(token), key, cmdName);
        }

        static void WriteEncBytes(List<byte> dst, byte[] plainBytes, uint key, string cmdName)
        {
            uint len = (uint)plainBytes.Length;
            WriteU32(dst, len);

            int aligned = (plainBytes.Length + 3) & ~3;
            Span<byte> tmp = aligned <= 2048 ? stackalloc byte[aligned] : new byte[aligned];
            tmp.Clear();
            plainBytes.CopyTo(tmp);

            if ((plainBytes.Length & 3) == 2 && cmdName.Equals("text", StringComparison.OrdinalIgnoreCase))
            {
                if (EllipsisSjis2.Length == 2)
                    EllipsisSjis2.AsSpan(0, 2).CopyTo(tmp.Slice(plainBytes.Length, 2));
            }

            for (int off = 0; off < aligned; off += 4)
            {
                uint w = BinaryPrimitives.ReadUInt32LittleEndian(tmp.Slice(off, 4));
                uint enc = Rol3(w ^ key);
                WriteU32(dst, enc);
            }
        }

        static void SplitArgsTo(string s, List<string> dst)
        {
            dst.Clear();
            int start = 0;
            bool inQ = false;

            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];

                if (ch == '"' && (i == 0 || s[i - 1] != '\\'))
                    inQ = !inQ;

                if (!inQ && ch == ',')
                {
                    AddToken(dst, s, start, i - start);
                    start = i + 1;
                }
            }

            AddToken(dst, s, start, s.Length - start);

            static void AddToken(List<string> dst, string s, int start, int len)
            {
                if (len <= 0) return;
                var t = s.AsSpan(start, len).Trim();
                if (!t.IsEmpty) dst.Add(t.ToString());
            }
        }

        static string[] SplitOps(string s)
        {
            var list = new List<string>(4);
            int start = 0;

            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != ',') continue;
                Add(list, s, start, i - start);
                start = i + 1;
            }

            Add(list, s, start, s.Length - start);

            if (list.Count == 0) return Array.Empty<string>();
            return list.ToArray();

            static void Add(List<string> list, string s, int start, int len)
            {
                if (len <= 0) return;
                var t = s.AsSpan(start, len).Trim();
                if (!t.IsEmpty) list.Add(t.ToString());
            }
        }

        static ushort ResolveOpcode(string mnem, string[] ops)
        {
            string u = mnem.Trim().ToUpperInvariant();

            if (u == "CALL") return 0x0400;
            if (u == "RET") return 0x0401;
            if (u == "JZ") return 0x0500;
            if (u == "JNZ") return 0x0501;

            static bool IsVar(string t) => t.AsSpan().TrimStart().StartsWith("@", StringComparison.Ordinal);
            static bool IsLabel(string t) => t.AsSpan().TrimStart().StartsWith("L", StringComparison.OrdinalIgnoreCase);

            if (u == "JMP")
            {
                if (ops.Length != 1) throw new FormatException("JMP needs 1 operand");
                return IsLabel(ops[0]) ? (ushort)0x0403 : (ushort)0x0402;
            }

            if (u == "PUSH")
            {
                if (ops.Length != 1) throw new FormatException("PUSH needs 1 operand");
                string t = ops[0].Trim();
                if (t.StartsWith("*", StringComparison.Ordinal)) return 0x0004;
                if (t.StartsWith("@", StringComparison.Ordinal)) return 0x0003;
                throw new FormatException("PUSH operand must be *XXXXXXXX or @XXXXXXXX");
            }

            if (u == "POP") return 0x0005;
            if (u == "INC") return 0x0100;
            if (u == "DEC") return 0x0101;
            if (u == "NOT") return 0x0200;

            if (u == "MOV") return (ops.Length == 2 && IsVar(ops[1])) ? (ushort)0x0001 : (ushort)0x0002;

            if (u == "ADD") return (ops.Length == 2 && IsVar(ops[1])) ? (ushort)0x0102 : (ushort)0x0103;
            if (u == "SUB") return (ops.Length == 2 && IsVar(ops[1])) ? (ushort)0x0104 : (ushort)0x0105;
            if (u == "MUL") return (ops.Length == 2 && IsVar(ops[1])) ? (ushort)0x0106 : (ushort)0x0107;
            if (u == "DIV") return (ops.Length == 2 && IsVar(ops[1])) ? (ushort)0x0108 : (ushort)0x0109;

            if (u == "AND") return (ops.Length == 2 && IsVar(ops[1])) ? (ushort)0x0201 : (ushort)0x0202;
            if (u == "OR") return (ops.Length == 2 && IsVar(ops[1])) ? (ushort)0x0203 : (ushort)0x0204;

            ushort PickCmp(ushort vv, ushort vi)
            {
                if (ops.Length != 2) throw new FormatException($"{mnem} needs 2 operands");
                return IsVar(ops[1]) ? vv : vi;
            }

            if (u == "SETE") return PickCmp(0x0300, 0x0301);
            if (u == "SETNE") return PickCmp(0x0302, 0x0303);
            if (u == "SETG") return PickCmp(0x0304, 0x0305);
            if (u == "SETGE") return PickCmp(0x0306, 0x0307);
            if (u == "SETL") return PickCmp(0x0308, 0x0309);
            if (u == "SETLE") return PickCmp(0x030A, 0x030B);

            ushort PickJcc(ushort vvl, ushort vil)
            {
                if (ops.Length != 3) throw new FormatException($"{mnem} needs 3 operands");
                return IsVar(ops[1]) ? vvl : vil;
            }

            if (u == "JE") return PickJcc(0x0502, 0x0503);
            if (u == "JNE") return PickJcc(0x0504, 0x0505);
            if (u == "JG") return PickJcc(0x0506, 0x0507);
            if (u == "JGE") return PickJcc(0x0508, 0x0509);
            if (u == "JL") return PickJcc(0x050A, 0x050B);
            if (u == "JLE") return PickJcc(0x050C, 0x050D);

            if (NameToOpcodeBase.TryGetValue(u, out ushort direct))
                return direct;

            throw new FormatException($"Unknown mnemonic: {mnem}");
        }

        static uint ParseVarU32(string t)
        {
            t = t.Trim();
            if (!t.StartsWith("@", StringComparison.Ordinal))
                throw new FormatException($"Bad var: {t}");
            return ParseHexU32(t.Substring(1));
        }

        static uint ParsePtrU32(string t)
        {
            t = t.Trim();
            if (!t.StartsWith("*", StringComparison.Ordinal))
                throw new FormatException($"Bad ptr: {t}");
            return ParseHexU32(t.Substring(1));
        }

        static uint ParseLabelU32(string t)
        {
            t = t.Trim();
            if (!t.StartsWith("L", StringComparison.OrdinalIgnoreCase))
                throw new FormatException($"Bad label: {t}");
            return ParseU32(t.Substring(1));
        }

        static uint ParseSymbolU32(string t)
        {
            t = t.Trim();
            if (t.StartsWith("#", StringComparison.Ordinal))
                return ParseHexU32(t.Substring(1));
            return Hash.MakeStrID(t);
        }

        static uint ParseHexU32(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
            return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        static uint ParseU32(string s) =>
            uint.Parse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

        static int ParseI32(string s) =>
            int.Parse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);

        static string ReadEncAsciiString(byte[] data, ref int pos, int end, uint key)
        {
            if (!TryReadU32LE(data, ref pos, end, out uint lenU)) return "";
            int len = unchecked((int)lenU);
            if (len < 0) return "";

            int aligned = (len + 3) & ~3;
            if (pos + aligned > end) { pos = Math.Min(end, pos + aligned); return ""; }

            Span<byte> dec = aligned <= 1024 ? stackalloc byte[aligned] : new byte[aligned];
            for (int off = 0; off < aligned; off += 4)
            {
                uint w = ReadU32LE(data, pos + off);
                uint d = Ror3(w) ^ key;
                BinaryPrimitives.WriteUInt32LittleEndian(dec.Slice(off, 4), d);
            }

            pos += aligned;
            int take = Math.Min(len, aligned);
            return Encoding.ASCII.GetString(dec.Slice(0, take));
        }

        static string ReadEncWString(byte[] data, ref int pos, int end, uint key)
        {
            if (!TryReadU32LE(data, ref pos, end, out uint lenU)) return "";
            int len = unchecked((int)lenU);
            if (len < 0) return "";

            int aligned = (len + 3) & ~3;
            if (pos + aligned > end) { pos = Math.Min(end, pos + aligned); return ""; }

            Span<byte> dec = aligned <= 2048 ? stackalloc byte[aligned] : new byte[aligned];
            for (int off = 0; off < aligned; off += 4)
            {
                uint w = ReadU32LE(data, pos + off);
                uint d = Ror3(w) ^ key;
                BinaryPrimitives.WriteUInt32LittleEndian(dec.Slice(off, 4), d);
            }

            pos += aligned;

            int take = Math.Min(len, aligned);
            int even = take & ~1;
            if (even <= 0) return "";

            return Encoding.Unicode.GetString(dec.Slice(0, even));
        }

        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n")
                    .Replace("\0", "\\0");
        }

        static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }

                char n = s[++i];
                sb.Append(n switch
                {
                    '\\' => '\\',
                    '"' => '"',
                    'n' => '\n',
                    'r' => '\r',
                    '0' => '\0',
                    _ => n
                });
            }
            return sb.ToString();
        }

        static uint ReadU32LE(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

        static bool TryReadU32LE(byte[] data, ref int pos, int end, out uint v)
        {
            if (pos + 4 > end) { v = 0; return false; }
            v = ReadU32LE(data, pos);
            pos += 4;
            return true;
        }

        static bool TryReadI32LE(byte[] data, ref int pos, int end, out int v)
        {
            if (pos + 4 > end) { v = 0; return false; }
            v = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos, 4));
            pos += 4;
            return true;
        }

        static bool TryReadF32LE(byte[] data, ref int pos, int end, out float v)
        {
            if (pos + 4 > end) { v = 0; return false; }
            uint u = ReadU32LE(data, pos);
            v = BitConverter.UInt32BitsToSingle(u);
            pos += 4;
            return true;
        }

        static uint Ror3(uint x) => (x >> 3) | (x << 29);
        static uint Rol3(uint x) => (x << 3) | (x >> 29);

        static void WriteU32(List<byte> dst, uint v)
        {
            int p = dst.Count;
            dst.Add(0); dst.Add(0); dst.Add(0); dst.Add(0);
            BinaryPrimitives.WriteUInt32LittleEndian(CollectionsMarshal.AsSpan(dst).Slice(p, 4), v);
        }

        static void WriteI32(List<byte> dst, int v) => WriteU32(dst, unchecked((uint)v));

        static Dictionary<uint, string> BuildCmdByFileHash()
        {
            var d = new Dictionary<uint, string>(CmdNames.Length);
            for (int i = 0; i < CmdNames.Length; i++)
            {
                var n = CmdNames[i];
                uint h = Hash.Filehash(n);
                if (!d.ContainsKey(h)) d[h] = n;
            }
            return d;
        }

        static Dictionary<uint, string> BuildCallByMakeStrId()
        {
            var d = new Dictionary<uint, string>(ScenarioNames.Length);
            for (int i = 0; i < ScenarioNames.Length; i++)
            {
                var n = ScenarioNames[i];
                uint h = Hash.MakeStrID(n);
                if (!d.ContainsKey(h)) d[h] = n;
            }
            return d;
        }

        static Dictionary<string, ushort> BuildNameToOpcodeBase()
        {
            var d = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in Opcodes)
            {
                var fmt = kv.Value.Fmt;
                if (fmt is null) continue;
                if (fmt.Length != 0) continue;
                d[kv.Value.Name] = kv.Key;
            }
            return d;
        }

        static Encoding InitSjis()
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

        static byte[] InitEllipsis2()
        {
            try
            {
                var b = Sjis.GetBytes("…");
                if (b.Length >= 2) return new[] { b[0], b[1] };
            }
            catch { }
            return Array.Empty<byte>();
        }
    }
}