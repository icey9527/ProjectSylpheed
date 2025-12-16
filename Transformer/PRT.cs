using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace IpfbTool.Core
{
    internal sealed class PRT : ITransformer
    {
        static readonly uint RATC = TextureUtil.FourCC("RATC");
        static readonly uint RATB = TextureUtil.FourCC("RATB");
        static readonly uint RATA = TextureUtil.FourCC("RAT@");

        static ReadOnlySpan<byte> OptMagic => "opt "u8;
        static ReadOnlySpan<byte> EndMagic => "end "u8;

        static readonly Encoding Sjis = InitEnc();

        public bool CanPack => false;

        public bool CanTransformOnExtract(string name)
        {
            string ext = Path.GetExtension(name);
            return ext.Equals(".prt", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".RATC", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".RATB", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".RAT@", StringComparison.OrdinalIgnoreCase);
        }

        public (string name, byte[] data) OnExtract(byte[] srcData, string srcName, Manifest manifest)
        {
            var r = new Reader(srcData);

            uint classTag = r.U32();
            if (classTag != RATC && classTag != RATB && classTag != RATA)
                throw new InvalidDataException("Not a RAT file");

            ushort rate = r.U16();
            ushort state16 = r.U16();
            uint frame = r.U32();
            ushort waitPos = r.U16();
            ushort endPos = r.U16();
            uint priority = r.U32();
            uint numObjects = r.U32();

            int screenW, screenH, objSize;
            bool hasScreen = classTag == RATC;
            if (hasScreen)
            {
                screenW = r.I32();
                screenH = r.I32();
                objSize = 60;
            }
            else
            {
                screenW = 640;
                screenH = 480;
                objSize = classTag == RATB ? 60 : 56;
            }

            {
                var e = new Dictionary<string, string>(13, StringComparer.OrdinalIgnoreCase)
                {
                    ["kind"] = "header",
                    ["container"] = srcName,
                    ["class_tag"] = classTag.ToString(CultureInfo.InvariantCulture),
                    ["rate"] = rate.ToString(CultureInfo.InvariantCulture),
                    ["state"] = state16.ToString(CultureInfo.InvariantCulture),
                    ["frame"] = frame.ToString(CultureInfo.InvariantCulture),
                    ["wait_pos"] = waitPos.ToString(CultureInfo.InvariantCulture),
                    ["end_pos"] = endPos.ToString(CultureInfo.InvariantCulture),
                    ["priority"] = priority.ToString(CultureInfo.InvariantCulture),
                    ["num_objects"] = numObjects.ToString(CultureInfo.InvariantCulture),
                    ["screen_w"] = screenW.ToString(CultureInfo.InvariantCulture),
                    ["screen_h"] = screenH.ToString(CultureInfo.InvariantCulture)
                };
                manifest.AddPRT(e);
            }

            for (int i = 0; i < numObjects; i++)
            {
                string szFileName = r.StrFixed(32);
                int lParent = r.I32();
                int lMaster = r.I32();
                uint objState = r.U32();
                int buttonID = r.I32();
                int centerX = r.I32();
                int centerY = r.I32();
                int centerZ = objSize == 60 ? r.I32() : 0;

                var e = new Dictionary<string, string>(11, StringComparer.OrdinalIgnoreCase)
                {
                    ["kind"] = "obj",
                    ["container"] = srcName,
                    ["index"] = i.ToString(CultureInfo.InvariantCulture),
                    ["szFileName"] = szFileName,
                    ["lParent"] = lParent.ToString(CultureInfo.InvariantCulture),
                    ["lMaster"] = lMaster.ToString(CultureInfo.InvariantCulture),
                    ["state"] = objState.ToString(CultureInfo.InvariantCulture),
                    ["buttonID"] = buttonID.ToString(CultureInfo.InvariantCulture),
                    ["centerX"] = centerX.ToString(CultureInfo.InvariantCulture),
                    ["centerY"] = centerY.ToString(CultureInfo.InvariantCulture),
                    ["centerZ"] = centerZ.ToString(CultureInfo.InvariantCulture)
                };
                manifest.AddPRT(e);
            }

            for (int set = 0; set < numObjects; set++)
            {
                uint ownerID = r.U32();
                uint numKeys = r.U32();

                {
                    var e = new Dictionary<string, string>(6, StringComparer.OrdinalIgnoreCase)
                    {
                        ["kind"] = "keyset",
                        ["container"] = srcName,
                        ["set"] = set.ToString(CultureInfo.InvariantCulture),
                        ["ownerID"] = ownerID.ToString(CultureInfo.InvariantCulture),
                        ["numKeys"] = numKeys.ToString(CultureInfo.InvariantCulture)
                    };
                    manifest.AddPRT(e);
                }

                for (int k = 0; k < numKeys; k++)
                {
                    int pos = r.I32();
                    uint diffuse = r.U32();
                    int yaw = r.I32();
                    int pitch = r.I32();
                    int roll = r.I32();
                    int stretchX = r.I32();
                    int stretchY = r.I32();
                    int soundID = r.I32();
                    int ptX = r.I32();
                    int ptY = r.I32();

                    var e = new Dictionary<string, string>(14, StringComparer.OrdinalIgnoreCase)
                    {
                        ["kind"] = "key",
                        ["container"] = srcName,
                        ["set"] = set.ToString(CultureInfo.InvariantCulture),
                        ["index"] = k.ToString(CultureInfo.InvariantCulture),
                        ["ownerID"] = ownerID.ToString(CultureInfo.InvariantCulture),
                        ["pos"] = pos.ToString(CultureInfo.InvariantCulture),
                        ["diffuse"] = diffuse.ToString(CultureInfo.InvariantCulture),
                        ["yaw"] = yaw.ToString(CultureInfo.InvariantCulture),
                        ["pitch"] = pitch.ToString(CultureInfo.InvariantCulture),
                        ["roll"] = roll.ToString(CultureInfo.InvariantCulture),
                        ["stretchX"] = stretchX.ToString(CultureInfo.InvariantCulture),
                        ["stretchY"] = stretchY.ToString(CultureInfo.InvariantCulture),
                        ["soundID"] = soundID.ToString(CultureInfo.InvariantCulture),
                        ["pt_x"] = ptX.ToString(CultureInfo.InvariantCulture),
                        ["pt_y"] = ptY.ToString(CultureInfo.InvariantCulture)
                    };
                    manifest.AddPRT(e);
                }
            }

            string dirRel = Path.GetDirectoryName(srcName) ?? "";
            string folderRel = Path.Combine(dirRel, Path.GetFileNameWithoutExtension(srcName));

            {
                var e = new Dictionary<string, string>(3, StringComparer.OrdinalIgnoreCase)
                {
                    ["kind"] = "unpack_dir",
                    ["container"] = srcName,
                    ["dir"] = folderRel
                };
                manifest.AddPRT(e);
            }

            int itemIndex = 0;
            while (!r.Eof)
            {
                if (r.PeekU32() == null) break;

                if (r.Peek4(OptMagic))
                {
                    r.Skip(4);

                    uint nameLen = r.U32();
                    if (nameLen == 0 || nameLen > 4096 || r.Remaining < nameLen) break;
                    string itemName = r.StrVar((int)nameLen);

                    uint size = r.U32();
                    if (size == 0 || r.Remaining < size) break;
                    byte[] payload = r.Bytes((int)size);

                    uint imgId = TexId.Embedded("PRT", srcName, itemName);

                    string filePart = Path.GetFileName(itemName);
                    if (string.IsNullOrWhiteSpace(filePart)) filePart = $"item_{itemIndex}";
                    string pngRel = Path.Combine(folderRel, filePart + ".png");

                    string magic4 = payload.Length >= 4 ? Encoding.ASCII.GetString(payload, 0, 4) : "";

                    var entry = new Dictionary<string, string>(10, StringComparer.OrdinalIgnoreCase)
                    {
                        ["kind"] = "item",
                        ["container"] = srcName,
                        ["index"] = itemIndex.ToString(CultureInfo.InvariantCulture),
                        ["img_id"] = TexId.ToX8(imgId),
                        ["img_name"] = itemName,
                        ["payload_size"] = payload.Length.ToString(CultureInfo.InvariantCulture),
                        ["payload_magic"] = magic4
                    };

                    if (TryExtractTexture(payload, imgId, itemName, "2", pngRel, manifest, out var png))
                    {
                        entry["payload_kind"] = "texture";
                        manifest.AddPRT(entry);
                        WriteOut(pngRel, png);
                    }
                    else
                    {
                        entry["payload_kind"] = "raw";
                        string rawRel = Path.Combine(folderRel, filePart);
                        entry["payload_file"] = rawRel;
                        manifest.AddPRT(entry);
                        WriteOut(rawRel, payload);
                    }

                    itemIndex++;
                    continue;
                }

                if (r.Peek4(EndMagic))
                {
                    r.Skip(4);
                    break;
                }

                break;
            }

            return (srcName, null!);
        }

        internal static Dictionary<string, byte[]> BuildAll(Manifest manifest, string rootDir, IReadOnlyDictionary<uint, Dictionary<string, string>> texById)
        {
            var byContainer = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in manifest.PRT)
            {
                if (!d.TryGetValue("container", out var c) || string.IsNullOrWhiteSpace(c)) continue;
                if (!byContainer.TryGetValue(c, out var list))
                {
                    list = new List<Dictionary<string, string>>();
                    byContainer[c] = list;
                }
                list.Add(d);
            }

            var res = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in byContainer)
            {
                string container = kv.Key;
                var items = kv.Value;

                Dictionary<string, string>? header = null;

                List<Dictionary<string, string>> objs = new();
                List<Dictionary<string, string>> keysets = new();
                Dictionary<int, List<Dictionary<string, string>>> keysBySet = new();
                List<Dictionary<string, string>> subs = new();

                for (int i = 0; i < items.Count; i++)
                {
                    var it = items[i];
                    if (!it.TryGetValue("kind", out var kind) || string.IsNullOrEmpty(kind)) continue;

                    if (kind.Equals("header", StringComparison.OrdinalIgnoreCase)) { header = it; continue; }
                    if (kind.Equals("obj", StringComparison.OrdinalIgnoreCase)) { objs.Add(it); continue; }
                    if (kind.Equals("keyset", StringComparison.OrdinalIgnoreCase)) { keysets.Add(it); continue; }
                    if (kind.Equals("key", StringComparison.OrdinalIgnoreCase))
                    {
                        int set = ParseI32(it, "set");
                        if (!keysBySet.TryGetValue(set, out var list)) { list = new List<Dictionary<string, string>>(); keysBySet[set] = list; }
                        list.Add(it);
                        continue;
                    }
                    if (kind.Equals("item", StringComparison.OrdinalIgnoreCase)) { subs.Add(it); continue; }
                }

                if (header == null) continue;

                objs.Sort((a, b) => ParseI32(a, "index").CompareTo(ParseI32(b, "index")));
                keysets.Sort((a, b) => ParseI32(a, "set").CompareTo(ParseI32(b, "set")));
                subs.Sort((a, b) => ParseI32(a, "index").CompareTo(ParseI32(b, "index")));
                foreach (var p in keysBySet)
                    p.Value.Sort((a, b) => ParseI32(a, "index").CompareTo(ParseI32(b, "index")));

                uint classTag = ParseU32(header, "class_tag");
                ushort rate = (ushort)ParseU32(header, "rate");
                ushort state16 = (ushort)ParseU32(header, "state");
                uint frame = ParseU32(header, "frame");
                ushort waitPos = (ushort)ParseU32(header, "wait_pos");
                ushort endPos = (ushort)ParseU32(header, "end_pos");
                uint priority = ParseU32(header, "priority");
                uint numObjects = ParseU32(header, "num_objects");
                int screenW = ParseI32(header, "screen_w");
                int screenH = ParseI32(header, "screen_h");

                bool hasScreen = classTag == RATC;
                int objSize = classTag == RATB ? 60 : (classTag == RATA ? 56 : 60);

                using var ms = new MemoryStream(64 * 1024);

                WriteU32(ms, classTag);
                WriteU16(ms, rate);
                WriteU16(ms, state16);
                WriteU32(ms, frame);
                WriteU16(ms, waitPos);
                WriteU16(ms, endPos);
                WriteU32(ms, priority);
                WriteU32(ms, numObjects);

                if (hasScreen)
                {
                    WriteI32(ms, screenW);
                    WriteI32(ms, screenH);
                }

                for (int i = 0; i < numObjects; i++)
                {
                    var o = i < objs.Count ? objs[i] : null;
                    WriteFixed32(ms, Get(o, "szFileName"));
                    WriteI32(ms, ParseI32(o, "lParent"));
                    WriteI32(ms, ParseI32(o, "lMaster"));
                    WriteU32(ms, ParseU32(o, "state"));
                    WriteI32(ms, ParseI32(o, "buttonID"));
                    WriteI32(ms, ParseI32(o, "centerX"));
                    WriteI32(ms, ParseI32(o, "centerY"));
                    if (objSize == 60) WriteI32(ms, ParseI32(o, "centerZ"));
                }

                for (int set = 0; set < numObjects; set++)
                {
                    var ks = set < keysets.Count ? keysets[set] : null;
                    uint ownerID = ParseU32(ks, "ownerID");
                    uint numKeys = ParseU32(ks, "numKeys");

                    WriteU32(ms, ownerID);
                    WriteU32(ms, numKeys);

                    keysBySet.TryGetValue(set, out var list);
                    list ??= s_emptyList;

                    for (int k = 0; k < numKeys; k++)
                    {
                        var key = k < list.Count ? list[k] : null;

                        WriteI32(ms, ParseI32(key, "pos"));
                        WriteU32(ms, ParseU32(key, "diffuse"));
                        WriteI32(ms, ParseI32(key, "yaw"));
                        WriteI32(ms, ParseI32(key, "pitch"));
                        WriteI32(ms, ParseI32(key, "roll"));
                        WriteI32(ms, ParseI32(key, "stretchX"));
                        WriteI32(ms, ParseI32(key, "stretchY"));
                        WriteI32(ms, ParseI32(key, "soundID"));
                        WriteI32(ms, ParseI32(key, "pt_x"));
                        WriteI32(ms, ParseI32(key, "pt_y"));
                    }
                }

                foreach (var it in subs)
                {
                    string imgName = Get(it, "img_name");
                    string fileRel = Get(it, "payload_file");

                    byte[] payload;
                    if (!string.IsNullOrEmpty(fileRel))
                    {
                        payload = File.ReadAllBytes(Path.Combine(rootDir, fileRel));
                    }
                    else
                    {
                        uint imgId = TexId.Parse(Get(it, "img_id"));
                        if (imgId == 0 || !texById.TryGetValue(imgId, out var texEntry)) continue;
                        payload = BuildTexture(texEntry, rootDir);
                    }

                    ms.Write(OptMagic);

                    byte[] nameBytes = Sjis.GetBytes(imgName);
                    WriteU32(ms, (uint)nameBytes.Length);
                    ms.Write(nameBytes);

                    WriteU32(ms, (uint)payload.Length);
                    ms.Write(payload);
                }

                ms.Write(EndMagic);

                res[container] = ms.ToArray();
            }

            return res;
        }

        static readonly List<Dictionary<string, string>> s_emptyList = new(0);

        static byte[] BuildTexture(Dictionary<string, string> texEntry, string rootDir)
        {
            string tex = Get(texEntry, "tex");
            return tex.Equals("TBM", StringComparison.OrdinalIgnoreCase)
                ? TBM.Build(texEntry, rootDir)
                : T32.Build(texEntry, rootDir);
        }

        static bool TryExtractTexture(byte[] payload, uint imgId, string logicalName, string type, string pngRel, Manifest manifest, out byte[] png)
        {
            png = Array.Empty<byte>();
            if (payload.Length < 4) return false;

            string m = Encoding.ASCII.GetString(payload, 0, 4);

            if (IsT32(m))
            {
                png = T32.ExtractAndRecord(payload, imgId, logicalName, type, pngRel, manifest);
                return true;
            }

            if (m is "TBM " or "TBMB" or "TBMC" or "TBMD")
            {
                png = TBM.ExtractAndRecord(payload, imgId, logicalName, type, pngRel, manifest);
                return true;
            }

            return false;
        }

        static bool IsT32(string m) => m is
            "T8aD" or "T8aB" or "T8aC" or "T32 " or
            "T4aD" or "T4aB" or "T4aC" or
            "T1aD" or "T1aB" or "T1aC" or
            "4444" or "1555";

        static void WriteOut(string rel, byte[] data)
        {
            string full = Path.Combine(PackContext.CurrentOutputDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? ".");
            File.WriteAllBytes(full, data);
        }

        static string Get(Dictionary<string, string>? d, string k) =>
            d != null && d.TryGetValue(k, out var v) ? (v ?? "") : "";

        static uint ParseU32(Dictionary<string, string>? d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var s) || string.IsNullOrWhiteSpace(s)) return 0;
            ReadOnlySpan<char> span = s.AsSpan().Trim();

            if (span.Length >= 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X') &&
                uint.TryParse(span[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx))
                return hx;

            if (uint.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u))
                return u;

            if (int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                return unchecked((uint)i);

            return TexId.Parse(span.ToString());
        }

        static int ParseI32(Dictionary<string, string>? d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var s) || string.IsNullOrWhiteSpace(s)) return 0;
            ReadOnlySpan<char> span = s.AsSpan().Trim();

            if (span.Length >= 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X') &&
                uint.TryParse(span[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx))
                return unchecked((int)hx);

            if (int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                return i;

            if (uint.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u))
                return unchecked((int)u);

            return unchecked((int)TexId.Parse(span.ToString()));
        }

        static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        static void WriteI32(Stream s, int v) => WriteU32(s, unchecked((uint)v));

        static void WriteU16(Stream s, ushort v)
        {
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        static void WriteFixed32(Stream s, string text)
        {
            Span<byte> buf = stackalloc byte[32];
            buf.Clear();

            byte[] b = Sjis.GetBytes(text ?? "");
            int n = b.Length > 32 ? 32 : b.Length;
            b.AsSpan(0, n).CopyTo(buf);

            s.Write(buf);
        }

        static Encoding InitEnc()
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

        sealed class Reader
        {
            readonly byte[] d;
            int p;

            public Reader(byte[] data) { d = data; p = 0; }

            public bool Eof => p >= d.Length;
            public int Remaining => d.Length - p;

            public uint U32()
            {
                uint v = TextureUtil.ReadU32BE(d, p);
                p += 4;
                return v;
            }

            public int I32()
            {
                int v = TextureUtil.ReadI32BE(d, p);
                p += 4;
                return v;
            }

            public ushort U16()
            {
                if (p + 2 > d.Length) throw new EndOfStreamException();
                ushort v = (ushort)((d[p] << 8) | d[p + 1]);
                p += 2;
                return v;
            }

            public ReadOnlySpan<byte> Span(int n)
            {
                if (n < 0 || p + n > d.Length) throw new EndOfStreamException();
                var s = d.AsSpan(p, n);
                p += n;
                return s;
            }

            public byte[] Bytes(int n)
            {
                if (n < 0 || p + n > d.Length) throw new EndOfStreamException();
                var b = d[p..(p + n)];
                p += n;
                return b;
            }

            public string StrFixed(int n)
            {
                var s = Span(n);
                int z = s.IndexOf((byte)0);
                if (z >= 0) s = s[..z];
                return Sjis.GetString(s).Trim();
            }

            public string StrVar(int n)
            {
                var s = Span(n);
                int z = s.IndexOf((byte)0);
                if (z >= 0) s = s[..z];
                return Sjis.GetString(s).Trim();
            }

            public uint? PeekU32()
            {
                if (p + 4 > d.Length) return null;
                return TextureUtil.ReadU32BE(d, p);
            }

            public bool Peek4(ReadOnlySpan<byte> seq)
            {
                if (p + 4 > d.Length) return false;
                return d[p] == seq[0] && d[p + 1] == seq[1] && d[p + 2] == seq[2] && d[p + 3] == seq[3];
            }

            public void Skip(int n)
            {
                p += n;
                if (p > d.Length) p = d.Length;
            }
        }
    }
}