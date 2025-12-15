using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace IpfbTool.Core
{
    internal sealed class FNT : ITransformer
    {
        static readonly byte[] Magic = "LSTA"u8.ToArray();
        const int EntryHeaderSize = 15;

        public bool CanPack => false;

        public bool CanTransformOnExtract(string name) =>
            Path.GetExtension(name).Equals(".FNT", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".LSTA", StringComparison.OrdinalIgnoreCase);

        public (string name, byte[] data) OnExtract(byte[] srcData, string srcName, Manifest manifest)
        {
            if (srcData.Length < 8 || !Match(srcData, 0, Magic))
                throw new InvalidDataException("Not a FNT/LSTA file");

            int count = BeBinary.ReadInt32(srcData, 4);

            manifest.AddFNT(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

                if (dataSize < 0 || pos + dataSize > srcData.Length) break;

                string imgName = charCode.ToString("X4");
                uint imgId = TexId.Embedded("FNT", srcName, i.ToString());
                string pngRel = Path.Combine(folderRel, imgName + ".png");

                manifest.AddFNT(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

                if (dataSize == 0) { continue; }

                byte[] payload = srcData[pos..(pos + dataSize)];
                pos += dataSize;

                if (TryExtractTexture(payload, imgId, imgName, "1", pngRel, manifest, out var png))
                    WriteOut(pngRel, png);  
            }

            return (srcName, null!);
        }

        internal static Dictionary<string, byte[]> BuildAll(Manifest manifest, string rootDir, IReadOnlyDictionary<uint, Dictionary<string, string>> texById)
        {
            var byContainer = manifest.FNT
                .Where(d => d.TryGetValue("kind", out var k) && k.Equals("entry", StringComparison.OrdinalIgnoreCase))
                .GroupBy(d => Get(d, "container"), StringComparer.OrdinalIgnoreCase);

            var res = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

            foreach (var g in byContainer)
            {
                string container = g.Key;
                var entries = g.Select(ParseEntry).Where(e => e.Index >= 0).OrderBy(e => e.Index).ToList();
                if (entries.Count == 0) continue;

                using var ms = new MemoryStream();
                ms.Write(Magic);
                BeBinary.WriteInt32(ms, entries.Count);

                foreach (var e in entries)
                {
                    byte[] imgData = Array.Empty<byte>();

                    if (e.ImgId != 0 && texById.TryGetValue(e.ImgId, out var texEntry))
                        imgData = BuildTexture(texEntry, rootDir);

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
            d.TryGetValue(k, out var v) ? v : "";

        static bool Match(byte[] data, int pos, byte[] seq)
        {
            if (pos + seq.Length > data.Length) return false;
            for (int i = 0; i < seq.Length; i++)
                if (data[pos + i] != seq[i]) return false;
            return true;
        }
    }
}