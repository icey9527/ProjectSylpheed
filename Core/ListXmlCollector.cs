using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Xml;

namespace IpfbTool.Core
{
    internal static class ListXmlCollector
    {
        sealed class HeaderInfo
        {
            public uint Id;
            public uint Magic;
            public uint F04;
            public uint F08;
            public uint F0C;
            public uint F10;
            public int Width;
            public int Height;
            public uint F20;
            public uint LogicalW;
            public uint LogicalH;
            public string Ext = "";
            public string Format = "";
            public string Comment = "";
        }

        static readonly ConcurrentDictionary<uint, HeaderInfo> map = new();

        public static void Clear() => map.Clear();

        static uint ReadU32BE(byte[] d, int o) =>
            (uint)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]);

        static bool IsT8Like(uint magic) => magic switch
        {
            0x54386144 or 0x54386142 or 0x54386143 or 0x54333220 or
            0x54346144 or 0x54346142 or 0x54346143 or
            0x54316144 or 0x54316142 or 0x54316143 or
            0x34343434 or 0x31353535 => true,
            _ => false
        };

        static string GuessFormat(uint magic) => magic switch
        {
            0x54386144 or 0x54386142 or 0x54386143 or 0x54333220 => "RGBA32",
            0x54346144 or 0x54346142 or 0x54346143 or 0x34343434 => "ARGB4444",
            0x54316144 or 0x54316142 or 0x54316143 or 0x31353535 => "ARGB1555",
            _ => ""
        };

        static string GuessExt(uint magic) => magic switch
        {
            0x54386144 or 0x54386142 or 0x54386143 => "T8aD",
            0x54346144 or 0x54346142 or 0x54346143 or 0x34343434 => "T4aD",
            0x54316144 or 0x54316142 or 0x54316143 or 0x31353535 => "T1aD",
            0x54333220 => "T32",
            _ => "BIN"
        };

        public static void TryAddFromPayload(uint id, string name, byte[] payload)
        {
            if (payload == null || payload.Length < 0x2C)
                return;

            uint magic = ReadU32BE(payload, 0);
            if (!IsT8Like(magic))
                return;

            var info = new HeaderInfo
            {
                Id = id,
                Magic = magic,
                F04 = ReadU32BE(payload, 0x04),
                F08 = ReadU32BE(payload, 0x08),
                F0C = ReadU32BE(payload, 0x0C),
                F10 = ReadU32BE(payload, 0x10),
                Width = (int)ReadU32BE(payload, 0x14),
                Height = (int)ReadU32BE(payload, 0x18),
                F20 = ReadU32BE(payload, 0x20),
                LogicalW = ReadU32BE(payload, 0x24),
                LogicalH = ReadU32BE(payload, 0x28),
                Ext = GuessExt(magic),
                Format = GuessFormat(magic),
                Comment = name
            };

            map[id] = info;
        }

        public static void Save(string path)
        {
            if (map.IsEmpty)
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var xw = XmlWriter.Create(fs, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                NewLineChars = "\n"
            });

            xw.WriteStartDocument();
            xw.WriteDocType("PAKXML", null, null, null);
            xw.WriteStartElement("pak");

            foreach (var kv in map)
            {
                HeaderInfo h = kv.Value;

                xw.WriteStartElement("item");
                xw.WriteAttributeString("id", h.Id.ToString());

                if (!string.IsNullOrEmpty(h.Comment))
                    xw.WriteComment(h.Comment);

                xw.WriteStartElement("header");
                xw.WriteAttributeString("magic", h.Magic.ToString());
                xw.WriteAttributeString("f04", h.F04.ToString());
                xw.WriteAttributeString("f08", h.F08.ToString());
                xw.WriteAttributeString("f0C", h.F0C.ToString());
                xw.WriteAttributeString("f10", h.F10.ToString());
                xw.WriteAttributeString("width", h.Width.ToString());
                xw.WriteAttributeString("height", h.Height.ToString());
                xw.WriteAttributeString("f20", h.F20.ToString());
                xw.WriteAttributeString("logicalW", h.LogicalW.ToString());
                xw.WriteAttributeString("logicalH", h.LogicalH.ToString());
                xw.WriteAttributeString("ext", h.Ext);
                xw.WriteAttributeString("format", h.Format);
                xw.WriteEndElement();

                xw.WriteEndElement();
            }

            xw.WriteEndElement();
            xw.WriteEndDocument();
            
            map.Clear();
        }
    }
}