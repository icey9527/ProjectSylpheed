using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace IpfbTool.Core
{
    internal sealed class LSTA : ITransformer
    {
        static readonly byte[] LstaMagic = "LSTA"u8.ToArray();
        const int EntryHeaderSize = 15;

        static readonly ConcurrentDictionary<string, ConcurrentBag<EntryInfo>> ExtractedEntries = new();

        struct EntryInfo
        {
            public int Index;
            public ushort TypeId;
            public byte Flag;
            public uint Unknown1, Unknown2;
            public uint Magic, F04, F08, F0C, F10, F20, LogicalW, LogicalH;
            public int Width, Height;
        }

        public bool CanTransformOnExtract(string name) =>
            Path.GetExtension(name).Equals(".LSTA", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(name).Equals(".FNT", StringComparison.OrdinalIgnoreCase);

        public bool CanTransformOnPack(string name) =>
            name.EndsWith(".LSTA.0.png", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".FNT.0.png", StringComparison.OrdinalIgnoreCase);

        public static void Clear() => ExtractedEntries.Clear();

        public static void SaveXml(string outputDir)
        {
            if (ExtractedEntries.IsEmpty) return;

            string path = Path.Combine(outputDir, "LSTA.xml");
            using var fs = new FileStream(path, FileMode.Create);
            using var xw = XmlWriter.Create(fs, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                NewLineChars = "\n"
            });

            xw.WriteStartDocument();
            xw.WriteStartElement("LSTA");

            foreach (var kv in ExtractedEntries)
            {
                xw.WriteStartElement("file");
                xw.WriteAttributeString("name", kv.Key);

                var sorted = new List<EntryInfo>(kv.Value);
                sorted.Sort((a, b) => a.Index.CompareTo(b.Index));

                foreach (var e in sorted)
                {
                    xw.WriteStartElement("item");
                    xw.WriteAttributeString("index", e.Index.ToString());
                    xw.WriteAttributeString("typeId", e.TypeId.ToString());
                    xw.WriteAttributeString("flag", e.Flag.ToString());
                    xw.WriteAttributeString("unknown1", e.Unknown1.ToString());
                    xw.WriteAttributeString("unknown2", e.Unknown2.ToString());
                    xw.WriteAttributeString("magic", e.Magic.ToString());
                    xw.WriteAttributeString("f04", e.F04.ToString());
                    xw.WriteAttributeString("f08", e.F08.ToString());
                    xw.WriteAttributeString("f0C", e.F0C.ToString());
                    xw.WriteAttributeString("f10", e.F10.ToString());
                    xw.WriteAttributeString("width", e.Width.ToString());
                    xw.WriteAttributeString("height", e.Height.ToString());
                    xw.WriteAttributeString("f20", e.F20.ToString());
                    xw.WriteAttributeString("logicalW", e.LogicalW.ToString());
                    xw.WriteAttributeString("logicalH", e.LogicalH.ToString());
                    xw.WriteEndElement();
                }

                xw.WriteEndElement();
            }

            xw.WriteEndElement();
            xw.WriteEndDocument();

            ExtractedEntries.Clear();
        }

        public static void PreloadXml(string rootDir) => Db.Load(Path.Combine(rootDir, "LSTA.xml"));

        public (string name, byte[] data) OnExtract(byte[] srcData, string srcName)
        {
            if (srcData.Length < 8 || !Match(srcData, 0, LstaMagic))
                throw new InvalidDataException("Not a LSTA file");

            int count = BeBinary.ReadInt32(srcData, 4);
            string dir = PackContext.CurrentOutputDir;
            var entries = ExtractedEntries.GetOrAdd(srcName, _ => new ConcurrentBag<EntryInfo>());

            int pos = 8;
            byte[] firstPng = null!;
            string firstName = null!;

            for (int i = 0; i < count; i++)
            {
                if (pos + EntryHeaderSize > srcData.Length) break;

                ushort typeId = (ushort)((srcData[pos] << 8) | srcData[pos + 1]);
                byte flag = srcData[pos + 2];
                uint unknown1 = ReadU32(srcData, pos + 3);
                uint unknown2 = ReadU32(srcData, pos + 7);
                int dataSize = (int)ReadU32(srcData, pos + 11);
                pos += EntryHeaderSize;

                if (pos + dataSize > srcData.Length) break;

                byte[] imgData = srcData[pos..(pos + dataSize)];
                pos += dataSize;

                var info = new EntryInfo
                {
                    Index = i,
                    TypeId = typeId,
                    Flag = flag,
                    Unknown1 = unknown1,
                    Unknown2 = unknown2
                };

                if (imgData.Length >= 0x2C)
                {
                    info.Magic = ReadU32(imgData, 0x00);
                    info.F04 = ReadU32(imgData, 0x04);
                    info.F08 = ReadU32(imgData, 0x08);
                    info.F0C = ReadU32(imgData, 0x0C);
                    info.F10 = ReadU32(imgData, 0x10);
                    info.Width = (int)ReadU32(imgData, 0x14);
                    info.Height = (int)ReadU32(imgData, 0x18);
                    info.F20 = ReadU32(imgData, 0x20);
                    info.LogicalW = ReadU32(imgData, 0x24);
                    info.LogicalH = ReadU32(imgData, 0x28);
                }

                entries.Add(info);

                try
                {
                    using var img = T8aImageCodec.Decode(imgData);
                    using var ms = new MemoryStream();
                    img.SaveAsPng(ms);
                    byte[] pngData = ms.ToArray();

                    string pngName = $"{srcName}.{i}.png";
                    if (i == 0)
                    {
                        firstPng = pngData;
                        firstName = pngName;
                    }
                    else
                    {
                        File.WriteAllBytes(Path.Combine(dir, pngName), pngData);
                    }
                }
                catch { }
            }

            return (firstName ?? srcName, firstPng ?? srcData);
        }

        public (string name, byte[] data) OnPack(string srcPath, string srcName)
        {
            string lstaName = ExtractLstaName(srcName);
            if (!Db.TryGetFile(lstaName, out var entries))
                return (srcName, File.ReadAllBytes(srcPath));

            string dir = Path.GetDirectoryName(srcPath) ?? ".";

            using var ms = new MemoryStream();
            ms.Write(LstaMagic);
            BeBinary.WriteInt32(ms, entries.Count);

            foreach (var e in entries)
            {
                string pngPath = Path.Combine(dir, $"{lstaName}.{e.Index}.png");
                byte[] imgData = Array.Empty<byte>();

                if (File.Exists(pngPath))
                {
                    using var img = Image.Load<Rgba32>(pngPath);
                    byte[] header = BuildHeader(e);
                    imgData = T8aImageCodec.Encode(img, header);
                }

                ms.WriteByte((byte)(e.TypeId >> 8));
                ms.WriteByte((byte)e.TypeId);
                ms.WriteByte(e.Flag);
                WriteU32(ms, e.Unknown1);
                WriteU32(ms, e.Unknown2);
                WriteU32(ms, (uint)imgData.Length);
                ms.Write(imgData);
            }

            return (lstaName, ms.ToArray());
        }

        static string ExtractLstaName(string name)
        {
            int idx = name.IndexOf(".LSTA.", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return name[..(idx + 5)];
            
            idx = name.IndexOf(".FNT.", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0) return name[..(idx + 4)];
            
            return name;
        }

        static byte[] BuildHeader(EntryInfo e)
        {
            byte[] header = new byte[0x2C];
            WriteU32(header, 0x00, e.Magic);
            WriteU32(header, 0x04, e.F04);
            WriteU32(header, 0x08, e.F08);
            WriteU32(header, 0x0C, e.F0C);
            WriteU32(header, 0x10, e.F10);
            WriteU32(header, 0x14, (uint)e.Width);
            WriteU32(header, 0x18, (uint)e.Height);
            WriteU32(header, 0x20, e.F20);
            WriteU32(header, 0x24, e.LogicalW);
            WriteU32(header, 0x28, e.LogicalH);
            return header;
        }

        static uint ReadU32(byte[] d, int o) =>
            (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

        static void WriteU32(byte[] d, int o, uint v)
        {
            d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16);
            d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
        }

        static void WriteU32(Stream s, uint v)
        {
            s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
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
            static Dictionary<string, List<EntryInfo>> dict = new(StringComparer.OrdinalIgnoreCase);

            public static void Load(string path)
            {
                dict.Clear();
                if (!File.Exists(path)) return;

                var doc = new XmlDocument();
                doc.Load(path);

                foreach (XmlElement fileEl in doc.SelectNodes("/lsta/file")!)
                {
                    string fileName = fileEl.GetAttribute("name");
                    var entries = new List<EntryInfo>();

                    foreach (XmlElement itemEl in fileEl.SelectNodes("item")!)
                    {
                        entries.Add(new EntryInfo
                        {
                            Index = (int)P(itemEl, "index"),
                            TypeId = (ushort)P(itemEl, "typeId"),
                            Flag = (byte)P(itemEl, "flag"),
                            Unknown1 = P(itemEl, "unknown1"),
                            Unknown2 = P(itemEl, "unknown2"),
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

                    entries.Sort((a, b) => a.Index.CompareTo(b.Index));
                    dict[fileName] = entries;
                }
            }

            public static bool TryGetFile(string name, out List<EntryInfo> entries) =>
                dict.TryGetValue(name, out entries!);

            static uint P(XmlElement e, string a) => uint.TryParse(e.GetAttribute(a), out uint v) ? v : 0;
        }
    }
}