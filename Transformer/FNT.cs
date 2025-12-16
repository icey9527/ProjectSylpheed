using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace IpfbTool.Core
{
    internal sealed class FNT : ITransformer
    {
        static ReadOnlySpan<byte> Magic => "LSTA"u8;
        const int EntryHeaderSize = 15;

        public bool CanPack => false;

        public bool CanTransformOnExtract(string name)
        {
            string ext = Path.GetExtension(name);
            return ext.Equals(".FNT", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".LSTA", StringComparison.OrdinalIgnoreCase);
        }

        public (string name, byte[] data) OnExtract(byte[] srcData, string srcName, Manifest manifest)
        {
            if (srcData.Length < 8 || !srcData.AsSpan(0, 4).SequenceEqual(Magic))
                throw new InvalidDataException("Not a FNT/LSTA file");

            int count = BeBinary.ReadInt32(srcData, 4);

            manifest.AddFNT(new Dictionary<string, string>(3, StringComparer.OrdinalIgnoreCase)
            {
                ["kind"] = "file",
                ["container"] = srcName,
                ["count"] = count.ToString()
            });

            string dirRel = Path.GetDirectoryName(srcName) ?? "";
            string folderRel = Path.Combine(dirRel, Path.GetFileNameWithoutExtension(srcName));

            int pos = 8;
            for (int i = 0; i < count; i++)
            {
                if (pos + EntryHeaderSize > srcData.Length) break;

                ushort charCode = (ushort)((srcData[pos] << 8) | srcData[pos + 1]);
                byte isExternal = srcData[pos + 2];
                int screenPosX = BeBinary.ReadInt32(srcData, pos + 3);
                int screenPosY = BeBinary.ReadInt32(srcData, pos + 7);
                int dataSize = unchecked((int)TextureUtil.ReadU32BE(srcData, pos + 11));
                pos += EntryHeaderSize;

                string imgName = charCode.ToString("X4");
                uint imgId = TexId.Embedded("FNT", srcName, i.ToString());
                string pngRel = Path.Combine(folderRel, imgName + ".png");

                manifest.AddFNT(new Dictionary<string, string>(10, StringComparer.OrdinalIgnoreCase)
                {
                    ["kind"] = "entry",
                    ["container"] = srcName,
                    ["index"] = i.ToString(),

                    ["char_code"] = charCode.ToString(),
                    ["is_external"] = isExternal.ToString(),
                    ["pos_x"] = screenPosX.ToString(),
                    ["pos_y"] = screenPosY.ToString(),

                    ["img_id"] = TexId.ToX8(imgId),
                    ["img_name"] = imgName
                });

                if (dataSize <= 0) continue;
                if ((uint)dataSize > (uint)(srcData.Length - pos)) break;

                byte[] payload = srcData[pos..(pos + dataSize)];
                pos += dataSize;

                if (TryExtractTexture(payload, imgId, imgName, "1", pngRel, manifest, out var png))
                    WriteOut(pngRel, png);
            }

            return (srcName, null!);
        }

        internal static Dictionary<string, byte[]> BuildAll(Manifest manifest, string rootDir, IReadOnlyDictionary<uint, Dictionary<string, string>> texById)
        {
            var byContainer = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in manifest.FNT)
            {
                if (!d.TryGetValue("kind", out var k) || !k.Equals("entry", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!d.TryGetValue("container", out var c) || string.IsNullOrWhiteSpace(c))
                    continue;

                var e = ParseEntry(d);
                if (e.Index < 0) continue;

                if (!byContainer.TryGetValue(c, out var list))
                {
                    list = new List<Entry>();
                    byContainer[c] = list;
                }

                list.Add(e);
            }

            var res = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            var texCache = new Dictionary<uint, byte[]>();

            foreach (var kv in byContainer)
            {
                string container = kv.Key;
                var entries = kv.Value;
                if (entries.Count == 0) continue;

                entries.Sort(static (a, b) => a.Index.CompareTo(b.Index));

                using var ms = new MemoryStream(checked(8 + entries.Count * 32));
                ms.Write(Magic);
                BeBinary.WriteInt32(ms, entries.Count);

                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];

                    byte[] imgData = Array.Empty<byte>();
                    if (e.ImgId != 0 && texById.TryGetValue(e.ImgId, out var texEntry))
                    {
                        if (!texCache.TryGetValue(e.ImgId, out imgData!))
                        {
                            imgData = BuildTexture(texEntry, rootDir);
                            texCache[e.ImgId] = imgData;
                        }
                    }

                    ms.WriteByte((byte)(e.CharCode >> 8));
                    ms.WriteByte((byte)e.CharCode);
                    ms.WriteByte(e.IsExternal);

                    WriteU32(ms, unchecked((uint)e.PosX));
                    WriteU32(ms, unchecked((uint)e.PosY));
                    WriteU32(ms, (uint)imgData.Length);
                    ms.Write(imgData);
                }

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

        readonly struct Entry
        {
            public readonly int Index;
            public readonly ushort CharCode;
            public readonly byte IsExternal;
            public readonly int PosX;
            public readonly int PosY;
            public readonly uint ImgId;

            public Entry(int index, ushort charCode, byte isExternal, int posX, int posY, uint imgId)
            {
                Index = index;
                CharCode = charCode;
                IsExternal = isExternal;
                PosX = posX;
                PosY = posY;
                ImgId = imgId;
            }
        }

        static Entry ParseEntry(Dictionary<string, string> d)
        {
            int index = int.TryParse(Get(d, "index"), out var i) ? i : -1;
            ushort charCode = (ushort)(uint.TryParse(Get(d, "char_code"), out var cc) ? cc : 0);
            byte isExternal = (byte)(uint.TryParse(Get(d, "is_external"), out var ie) ? ie : 0);
            int posX = int.TryParse(Get(d, "pos_x"), out var px) ? px : 0;
            int posY = int.TryParse(Get(d, "pos_y"), out var py) ? py : 0;
            uint imgId = TexId.Parse(Get(d, "img_id"));
            return new Entry(index, charCode, isExternal, posX, posY, imgId);
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

        static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        static string Get(Dictionary<string, string> d, string k) =>
            d.TryGetValue(k, out var v) ? (v ?? "") : "";
    }
}