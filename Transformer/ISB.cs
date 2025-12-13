using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IpfbTool.Core
{
    internal sealed class ISBU : ITransformer
    {
        const uint MARKER_NUMBER = 0x40403;
        const uint MARKER_TEXT = 0x40400;
        const uint KEY_THRESHOLD = 0x3000001;
        const int MAX_TEXT_LEN = 0x3f * 4;

        public bool CanTransformOnExtract(string name) =>
            Path.GetExtension(name).Equals(".isb", StringComparison.OrdinalIgnoreCase);

        public bool CanTransformOnPack(string name) =>
            Path.GetExtension(name).Equals(".txt", StringComparison.OrdinalIgnoreCase);

        public (string name, byte[] data) OnExtract(byte[] srcData, string srcName)
        {
            int pad = (4 - srcData.Length % 4) % 4;
            byte[] padded = new byte[srcData.Length + pad];
            Buffer.BlockCopy(srcData, 0, padded, 0, srcData.Length);

            uint[] buf = new uint[padded.Length / 4];
            for (int i = 0; i < buf.Length; i++)
                buf[i] = BitConverter.ToUInt32(padded, i * 4);

            if (buf.Length < 2)
                throw new InvalidDataException("File too small");

            uint blocksU = buf[^1];
            if (blocksU == 0 || blocksU > buf.Length)
                throw new InvalidDataException("Invalid block count");

            int blocks = (int)blocksU;
            int tableStart = buf.Length - 1 - blocks;

            var table = new List<long>(blocks + 1);
            for (int i = 0; i < blocks; i++)
                table.Add(buf[tableStart + i]);
            table.Add((long)tableStart * 4);

            var sb = new StringBuilder();
            uint key = 0;

            for (int b = 0; b < blocks; b++)
            {
                sb.AppendFormat("@{0:x}\n", table[b]);

                int start = (int)(table[b] / 4);
                int end = (int)(table[b + 1] / 4);
                if (start < 0 || start >= buf.Length || end > buf.Length) continue;

                int idx = start;
                int local50 = -1;

                if (idx < end)
                {
                    uint first = buf[idx];
                    if (first < KEY_THRESHOLD)
                        local50 = idx + (int)(first >> 18) + 1;
                    else
                    {
                        key = first;
                        sb.AppendFormat("${0,8:x}\n", first);
                        idx++;
                    }
                }

                while (idx < end)
                {
                    uint val = buf[idx];

                    if (val == MARKER_NUMBER)
                    {
                        idx++;
                        if (idx < end) sb.AppendFormat("+{0,8:x}\n", buf[idx++]);
                    }
                    else if (val == MARKER_TEXT)
                    {
                        if (idx + 1 >= end) { idx++; continue; }
                        uint lenU = buf[idx + 1];

                        if (lenU <= MAX_TEXT_LEN)
                        {
                            int len = (int)lenU;
                            idx += 2;
                            int wc = (len + 3) / 4;
                            if (idx + wc <= end)
                            {
                                byte[] bytes = new byte[wc * 4];
                                for (int i = 0; i < wc; i++)
                                {
                                    uint dec = Ror3(buf[idx + i]) ^ key;
                                    bytes[i * 4] = (byte)dec;
                                    bytes[i * 4 + 1] = (byte)(dec >> 8);
                                    bytes[i * 4 + 2] = (byte)(dec >> 16);
                                    bytes[i * 4 + 3] = (byte)(dec >> 24);
                                }
                                sb.AppendLine(Encoding.Unicode.GetString(bytes, 0, len));
                                idx += wc;
                            }
                            else idx++;
                        }
                        else
                        {
                            sb.AppendFormat("#{0,8:x}\n", buf[idx++]);
                            if (idx < end) sb.AppendFormat("#{0,8:x}\n", buf[idx++]);
                        }
                    }
                    else
                    {
                        sb.AppendFormat("{0}{1,8:x}\n",
                            local50 >= 0 && idx < local50 ? '#' : '$', val);
                        idx++;
                    }
                }
            }

            return (Path.ChangeExtension(srcName, ".txt"), Encoding.UTF8.GetBytes(sb.ToString()));
        }

        public (string name, byte[] data) OnPack(string srcPath, string srcName)
        {
            var blocks = new List<List<object>>();
            List<object> cur = null;
            uint key = 0;

            foreach (var line in File.ReadLines(srcPath, Encoding.UTF8))
            {
                string l = line.TrimEnd('\r', '\n');
                if (l.StartsWith("@"))
                {
                    if (cur != null) blocks.Add(cur);
                    cur = new List<object>();
                }
                else if (l.StartsWith("+"))
                    cur?.Add(("num", Convert.ToUInt32(l[1..].Trim(), 16)));
                else if (l.StartsWith("#"))
                {
                    uint v = Convert.ToUInt32(l[1..].Trim(), 16);
                    cur?.Add(("hex#", v));
                    if (blocks.Count == 0 && cur?.Count == 1 && v >= KEY_THRESHOLD) key = v;
                }
                else if (l.StartsWith("$"))
                {
                    uint v = Convert.ToUInt32(l[1..].Trim(), 16);
                    cur?.Add(("hex$", v));
                    if (blocks.Count == 0 && cur?.Count == 1 && v >= KEY_THRESHOLD) key = v;
                }
                else
                    cur?.Add(("text", l));
            }
            if (cur != null) blocks.Add(cur);

            var buffer = new List<uint>();
            var offsets = new List<uint>();
            uint curKey = key;

            for (int bi = 0; bi < blocks.Count; bi++)
            {
                offsets.Add((uint)(buffer.Count * 4));
                var entries = blocks[bi];
                int start = 0;

                if (bi == 0 && curKey >= KEY_THRESHOLD && entries.Count > 0 &&
                    entries[0] is (string t, uint v) && (t == "hex#" || t == "hex$") && v == curKey)
                    start = 1;

                if (bi == 0 && curKey >= KEY_THRESHOLD)
                    buffer.Add(curKey);

                for (int ei = start; ei < entries.Count; ei++)
                {
                    var entry = entries[ei];
                    if (entry is (string type, uint val))
                    {
                        if (type == "num")
                        {
                            buffer.Add(MARKER_NUMBER);
                            buffer.Add(val);
                        }
                        else
                        {
                            buffer.Add(val);
                            if (type == "hex$" && val >= KEY_THRESHOLD) curKey = val;
                        }
                    }
                    else if (entry is (string _, string text))
                    {
                        byte[] utf16 = Encoding.Unicode.GetBytes(text);
                        if (utf16.Length <= MAX_TEXT_LEN)
                        {
                            buffer.Add(MARKER_TEXT);
                            buffer.Add((uint)utf16.Length);

                            int padLen = (4 - utf16.Length % 4) % 4;
                            byte[] padded = new byte[utf16.Length + padLen];
                            Buffer.BlockCopy(utf16, 0, padded, 0, utf16.Length);

                            for (int i = 0; i < padded.Length / 4; i++)
                            {
                                uint w = BitConverter.ToUInt32(padded, i * 4);
                                buffer.Add(Rol3(w ^ curKey));
                            }
                        }
                    }
                }
            }

            foreach (uint off in offsets) buffer.Add(off);
            buffer.Add((uint)blocks.Count);

            byte[] outData = new byte[buffer.Count * 4];
            for (int i = 0; i < buffer.Count; i++)
            {
                uint v = buffer[i];
                outData[i * 4] = (byte)v;
                outData[i * 4 + 1] = (byte)(v >> 8);
                outData[i * 4 + 2] = (byte)(v >> 16);
                outData[i * 4 + 3] = (byte)(v >> 24);
            }

            return (Path.ChangeExtension(srcName, ".isb"), outData);
        }

        static uint Ror3(uint x) => (x >> 3) | (x << 29);
        static uint Rol3(uint x) => (x << 3) | (x >> 29);
    }
}