using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Xml;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace IpfbTool.Core
{
    internal sealed class RATC : ITransformer
    {
        static readonly byte[] RatcMagic = "RATC"u8.ToArray();
        static readonly byte[] OptMagic = "opt "u8.ToArray();
        static readonly byte[] EndMagic = "end "u8.ToArray();

        static readonly ConcurrentDictionary<string, ConcurrentBag<ItemInfo>> ExtractedItems = new();

        struct ItemInfo
        {
            public string Name;
            public uint Magic, F04, F08, F0C, F10, F20, LogicalW, LogicalH;
            public int Width, Height;
        }

        public bool CanTransformOnExtract(string name) =>
            Path.GetExtension(name).Equals(".prt", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".RATC", StringComparison.OrdinalIgnoreCase);

        public bool CanTransformOnPack(string name) =>
            Path.GetExtension(name).Equals(".prt", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(name).Equals(".RATC", StringComparison.OrdinalIgnoreCase);

        public static void Clear() => ExtractedItems.Clear();

        public static void SaveXml(string outputDir)
        {
            if (ExtractedItems.IsEmpty) return;

            string path = Path.Combine(outputDir, "RATC.xml");
            using var fs = new FileStream(path, FileMode.Create);
            using var xw = XmlWriter.Create(fs, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                NewLineChars = "\n"
            });

            xw.WriteStartDocument();
            xw.WriteStartElement("RATC");

            foreach (var kv in ExtractedItems)
            {
                xw.WriteStartElement("file");
                xw.WriteAttributeString("name", kv.Key);

                foreach (var item in kv.Value)
                {
                    xw.WriteStartElement("item");
                    xw.WriteAttributeString("name", item.Name);
                    xw.WriteAttributeString("magic", item.Magic.ToString());
                    xw.WriteAttributeString("f04", item.F04.ToString());
                    xw.WriteAttributeString("f08", item.F08.ToString());
                    xw.WriteAttributeString("f0C", item.F0C.ToString());
                    xw.WriteAttributeString("f10", item.F10.ToString());
                    xw.WriteAttributeString("width", item.Width.ToString());
                    xw.WriteAttributeString("height", item.Height.ToString());
                    xw.WriteAttributeString("f20", item.F20.ToString());
                    xw.WriteAttributeString("logicalW", item.LogicalW.ToString());
                    xw.WriteAttributeString("logicalH", item.LogicalH.ToString());
                    xw.WriteEndElement();
                }

                xw.WriteEndElement();
            }

            xw.WriteEndElement();
            xw.WriteEndDocument();

            ExtractedItems.Clear();
        }

        public static void PreloadXml(string rootDir)
        {
            Db.Load(Path.Combine(rootDir, "RATC.xml"));
        }

        public (string name, byte[] data) OnExtract(byte[] srcData, string srcName)
        {
            if (srcData.Length < 4 || !Match(srcData, 0, RatcMagic))
                throw new InvalidDataException("Not a RATC file");

            int optPos = FindSeq(srcData, OptMagic);
            if (optPos < 0) return (srcName, srcData);

            string dir = Path.Combine(PackContext.CurrentOutputDir, Path.GetDirectoryName(srcName) ?? "");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(PackContext.CurrentOutputDir, srcName), srcData[..optPos]);

            var items = ExtractedItems.GetOrAdd(srcName, _ => new ConcurrentBag<ItemInfo>());

            int pos = optPos;
            while (pos + 4 <= srcData.Length && !Match(srcData, pos, EndMagic))
            {
                if (!Match(srcData, pos, OptMagic)) { pos++; continue; }
                pos += 4;

                int nameLen = BeBinary.ReadInt32(srcData, pos); pos += 4;
                string name = Encoding.ASCII.GetString(srcData, pos, nameLen).TrimEnd('\0'); pos += nameLen;
                int size = BeBinary.ReadInt32(srcData, pos); pos += 4;
                byte[] imgData = srcData[pos..(pos + size)]; pos += size;

                if (imgData.Length >= 0x2C)
                {
                    items.Add(new ItemInfo
                    {
                        Name = name,
                        Magic = ReadU32(imgData, 0x00),
                        F04 = ReadU32(imgData, 0x04),
                        F08 = ReadU32(imgData, 0x08),
                        F0C = ReadU32(imgData, 0x0C),
                        F10 = ReadU32(imgData, 0x10),
                        Width = (int)ReadU32(imgData, 0x14),
                        Height = (int)ReadU32(imgData, 0x18),
                        F20 = ReadU32(imgData, 0x20),
                        LogicalW = ReadU32(imgData, 0x24),
                        LogicalH = ReadU32(imgData, 0x28)
                    });
                }

                try
                {
                    using var img = T8aImageCodec.Decode(imgData);
                    using var ms = new MemoryStream();
                    img.SaveAsPng(ms);
                    File.WriteAllBytes(Path.Combine(dir, name + ".png"), ms.ToArray());
                }
                catch { }
            }

            return (srcName, srcData[..optPos]);
        }

        public (string name, byte[] data) OnPack(string srcPath, string srcName)
        {
            if (!Db.TryGetFile(srcName, out var items))
                return (srcName, File.ReadAllBytes(srcPath));

            string dir = Path.GetDirectoryName(srcPath) ?? ".";

            using var ms = new MemoryStream();
            ms.Write(File.ReadAllBytes(srcPath));

            foreach (var item in items)
            {
                string pngPath = Path.Combine(dir, item.Name + ".png");
                if (!File.Exists(pngPath)) continue;

                using var img = Image.Load<Rgba32>(pngPath);

                byte[] header = new byte[0x2C];
                WriteU32(header, 0x00, item.Magic);
                WriteU32(header, 0x04, item.F04);
                WriteU32(header, 0x08, item.F08);
                WriteU32(header, 0x0C, item.F0C);
                WriteU32(header, 0x10, item.F10);
                WriteU32(header, 0x14, (uint)item.Width);
                WriteU32(header, 0x18, (uint)item.Height);
                WriteU32(header, 0x20, item.F20);
                WriteU32(header, 0x24, item.LogicalW);
                WriteU32(header, 0x28, item.LogicalH);

                byte[] imgData = T8aImageCodec.Encode(img, header);

                ms.Write(OptMagic);
                byte[] nameBytes = Encoding.ASCII.GetBytes(item.Name);
                BeBinary.WriteInt32(ms, nameBytes.Length);
                ms.Write(nameBytes);
                BeBinary.WriteInt32(ms, imgData.Length);
                ms.Write(imgData);
            }

            ms.Write(EndMagic);
            return (srcName, ms.ToArray());
        }

        static uint ReadU32(byte[] d, int o) =>
            (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

        static void WriteU32(byte[] d, int o, uint v)
        {
            d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16);
            d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
        }

        static int FindSeq(byte[] data, byte[] seq)
        {
            for (int i = 0; i <= data.Length - seq.Length; i++)
                if (Match(data, i, seq)) return i;
            return -1;
        }

        static bool Match(byte[] data, int pos, byte[] seq)
        {
            if (pos + seq.Length > data.Length) return false;
            for (int i = 0; i < seq.Length; i++)
                if (data[pos + i] != seq[i]) return false;
            return true;
        }

        static class Db
        {
            static Dictionary<string, List<ItemInfo>> dict = new(StringComparer.OrdinalIgnoreCase);

            public static void Load(string path)
            {
                dict.Clear();
                if (!File.Exists(path)) return;

                var doc = new XmlDocument();
                doc.Load(path);

                foreach (XmlElement fileEl in doc.SelectNodes("/ratc/file")!)
                {
                    string fileName = fileEl.GetAttribute("name");
                    var items = new List<ItemInfo>();

                    foreach (XmlElement itemEl in fileEl.SelectNodes("item")!)
                    {
                        items.Add(new ItemInfo
                        {
                            Name = itemEl.GetAttribute("name"),
                            Magic = P(itemEl, "magic"),
                            F04 = P(itemEl, "f04"),
                            F08 = P(itemEl, "f08"),
                            F0C = P(itemEl, "f0C"),
                            F10 = P(itemEl, "f10"),
                            Width = (int)P(itemEl, "width"),
                            Height = (int)P(itemEl, "height"),
                            F20 = P(itemEl, "f20"),
                            LogicalW = P(itemEl, "logicalW"),
                            LogicalH = P(itemEl, "logicalH")
                        });
                    }

                    dict[fileName] = items;
                }
            }

            public static bool TryGetFile(string name, out List<ItemInfo> items) =>
                dict.TryGetValue(name, out items!);

            static uint P(XmlElement e, string a) => uint.TryParse(e.GetAttribute(a), out uint v) ? v : 0;
        }
    }
}