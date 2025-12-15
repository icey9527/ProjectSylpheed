using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace IpfbTool.Core
{
    internal sealed class PRT : ITransformer
    {
        static readonly uint RATC = TextureUtil.FourCC("RATC");
        static readonly uint RATB = TextureUtil.FourCC("RATB");
        static readonly uint RATA = TextureUtil.FourCC("RAT@");

        static readonly byte[] OptMagic = "opt "u8.ToArray();
        static readonly byte[] EndMagic = "end "u8.ToArray();

        static readonly Encoding Sjis = InitEnc();

        public bool CanPack => false;

        public bool CanTransformOnExtract(string name) =>
            Path.GetExtension(name).Equals(".prt", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".RATC", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".RATB", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".RAT@", StringComparison.OrdinalIgnoreCase);

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

            manifest.AddPRT(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["kind"] = "header",
                ["container"] = srcName,
                ["class_tag"] = classTag.ToString(),
                ["rate"] = rate.ToString(),
                ["state"] = state16.ToString(),
                ["frame"] = frame.ToString(),
                ["wait_pos"] = waitPos.ToString(),
                ["end_pos"] = endPos.ToString(),
                ["priority"] = priority.ToString(),
                ["num_objects"] = numObjects.ToString(),
                ["screen_w"] = screenW.ToString(),
                ["screen_h"] = screenH.ToString()
            });

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

                manifest.AddPRT(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kind"] = "obj",
                    ["container"] = srcName,
                    ["index"] = i.ToString(),
                    ["szFileName"] = szFileName,
                    ["lParent"] = lParent.ToString(),
                    ["lMaster"] = lMaster.ToString(),
                    ["state"] = objState.ToString(),
                    ["buttonID"] = buttonID.ToString(),
                    ["centerX"] = centerX.ToString(),
                    ["centerY"] = centerY.ToString(),
                    ["centerZ"] = centerZ.ToString()
                });
            }

            for (int set = 0; set < numObjects; set++)
            {
                uint ownerID = r.U32();
                uint numKeys = r.U32();

                manifest.AddPRT(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["kind"] = "keyset",
                    ["container"] = srcName,
                    ["set"] = set.ToString(),
                    ["ownerID"] = ownerID.ToString(),
                    ["numKeys"] = numKeys.ToString()
                });

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

                    manifest.AddPRT(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["kind"] = "key",
                        ["container"] = srcName,
                        ["set"] = set.ToString(),
                        ["index"] = k.ToString(),
                        ["ownerID"] = ownerID.ToString(),
                        ["pos"] = pos.ToString(),
                        ["diffuse"] = diffuse.ToString(),
                        ["yaw"] = yaw.ToString(),
                        ["pitch"] = pitch.ToString(),
                        ["roll"] = roll.ToString(),
                        ["stretchX"] = stretchX.ToString(),
                        ["stretchY"] = stretchY.ToString(),
                        ["soundID"] = soundID.ToString(),
                        ["pt_x"] = ptX.ToString(),
                        ["pt_y"] = ptY.ToString()
                    });
                }
            }

            string dirRel = Path.GetDirectoryName(srcName) ?? "";
            string folderRel = Path.Combine(dirRel, Path.GetFileNameWithoutExtension(srcName));

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

                    manifest.AddPRT(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["kind"] = "item",
                        ["container"] = srcName,
                        ["index"] = itemIndex.ToString(),
                        ["img_id"] = TexId.ToX8(imgId),
                        ["img_name"] = itemName
                    });

                    if (TryExtractTexture(payload, imgId, itemName, "2", pngRel, manifest, out var png))
                        WriteOut(pngRel, png);

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
            var byContainer = manifest.PRT
                .Where(d => d.TryGetValue("container", out var c) && !string.IsNullOrWhiteSpace(c))
                .GroupBy(d => d["container"], StringComparer.OrdinalIgnoreCase);

            var res = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in byContainer)
            {
                string container = g.Key;
                var items = g.ToList();

                var header = items.FirstOrDefault(d => Get(d, "kind").Equals("header", StringComparison.OrdinalIgnoreCase));
                if (header == null) continue;

                uint classTag = ParseU32(header, "class_tag");
                ushort rate = (ushort)ParseU32(header, "rate");
                ushort state16 = (ushort)ParseU32(header, "state");
                uint frame = ParseU32(header, "frame");
                ushort waitPos = (ushort)ParseU32(header, "wait_pos");
                ushort endPos = (ushort)ParseU32(header, "end_pos");
                uint priority = ParseU32(header, "priority");
                uint numObjects = ParseU32(header, "num_objects");
                int screenW = (int)ParseU32(header, "screen_w");
                int screenH = (int)ParseU32(header, "screen_h");

                bool hasScreen = classTag == RATC;
                int objSize = classTag == RATB ? 60 : (classTag == RATA ? 56 : 60);

                var objs = items.Where(d => Get(d, "kind").Equals("obj", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => (int)ParseU32(d, "index"))
                    .ToList();

                var keysets = items.Where(d => Get(d, "kind").Equals("keyset", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => (int)ParseU32(d, "set"))
                    .ToList();

                var keys = items.Where(d => Get(d, "kind").Equals("key", StringComparison.OrdinalIgnoreCase))
                    .GroupBy(d => (int)ParseU32(d, "set"))
                    .ToDictionary(gk => gk.Key, gk => gk.OrderBy(x => (int)ParseU32(x, "index")).ToList());

                var sub = items.Where(d => Get(d, "kind").Equals("item", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(d => (int)ParseU32(d, "index"))
                    .ToList();

                using var ms = new MemoryStream();

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
                    var o = i < objs.Count ? objs[i] : new Dictionary<string, string>();
                    WriteFixed32(ms, Get(o, "szFileName"));
                    WriteI32(ms, (int)ParseU32(o, "lParent"));
                    WriteI32(ms, (int)ParseU32(o, "lMaster"));
                    WriteU32(ms, ParseU32(o, "state"));
                    WriteI32(ms, (int)ParseU32(o, "buttonID"));
                    WriteI32(ms, (int)ParseU32(o, "centerX"));
                    WriteI32(ms, (int)ParseU32(o, "centerY"));
                    if (objSize == 60) WriteI32(ms, (int)ParseU32(o, "centerZ"));
                }

                for (int set = 0; set < numObjects; set++)
                {
                    var ks = set < keysets.Count ? keysets[set] : new Dictionary<string, string>();
                    uint ownerID = ParseU32(ks, "ownerID");
                    uint numKeys = ParseU32(ks, "numKeys");

                    WriteU32(ms, ownerID);
                    WriteU32(ms, numKeys);

                    if (!keys.TryGetValue(set, out var list)) list = new List<Dictionary<string, string>>();

                    for (int k = 0; k < numKeys; k++)
                    {
                        var key = k < list.Count ? list[k] : new Dictionary<string, string>();

                        WriteI32(ms, (int)ParseU32(key, "pos"));
                        WriteU32(ms, ParseU32(key, "diffuse"));
                        WriteI32(ms, (int)ParseU32(key, "yaw"));
                        WriteI32(ms, (int)ParseU32(key, "pitch"));
                        WriteI32(ms, (int)ParseU32(key, "roll"));
                        WriteI32(ms, (int)ParseU32(key, "stretchX"));
                        WriteI32(ms, (int)ParseU32(key, "stretchY"));
                        WriteI32(ms, (int)ParseU32(key, "soundID"));
                        WriteI32(ms, (int)ParseU32(key, "pt_x"));
                        WriteI32(ms, (int)ParseU32(key, "pt_y"));
                    }
                }

                foreach (var it in sub)
                {
                    uint imgId = TexId.Parse(Get(it, "img_id"));
                    string imgName = Get(it, "img_name");

                    if (imgId == 0 || !texById.TryGetValue(imgId, out var texEntry)) continue;

                    byte[] payload = BuildTexture(texEntry, rootDir);

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

        static string Get(Dictionary<string, string> d, string k) =>
            d != null && d.TryGetValue(k, out var v) ? v : "";

        static uint ParseU32(Dictionary<string, string> d, string k)
        {
            string s = Get(d, k);
            if (uint.TryParse(s, out var v)) return v;
            return TexId.Parse(s);
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

            var b = Sjis.GetBytes(text ?? "");
            int n = Math.Min(32, b.Length);
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

            public byte[] Bytes(int n)
            {
                if (n < 0 || p + n > d.Length) throw new EndOfStreamException();
                var b = d[p..(p + n)];
                p += n;
                return b;
            }

            public string StrFixed(int n)
            {
                var b = Bytes(n);
                int z = Array.IndexOf(b, (byte)0);
                if (z >= 0) b = b[..z];
                return Sjis.GetString(b).Trim();
            }

            public string StrVar(int n)
            {
                var b = Bytes(n);
                int z = Array.IndexOf(b, (byte)0);
                if (z >= 0) b = b[..z];
                return Sjis.GetString(b).Trim();
            }

            public uint? PeekU32()
            {
                if (p + 4 > d.Length) return null;
                return TextureUtil.ReadU32BE(d, p);
            }

            public bool Peek4(byte[] seq)
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