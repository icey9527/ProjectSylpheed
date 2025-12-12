using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace IpfbTool.Core
{
    internal sealed class T8aD : ITransformer
    {
        static readonly HashSet<string> SupportedExts = new(StringComparer.OrdinalIgnoreCase)
            { ".T32", ".T8aD", ".T8aB", ".T8aC", ".T4aD", ".T1aD" };

        public bool CanTransformOnExtract(string name) =>
            SupportedExts.Contains(Path.GetExtension(name));

        public bool CanTransformOnPack(string name) =>
            name.EndsWith(".png", StringComparison.OrdinalIgnoreCase);

        public static void PreloadHeaderDb(string rootDir) => HeaderDb.Preload(rootDir);

        public (string name, byte[] data) OnExtract(byte[] srcData, string srcName)
        {
            using var img = T8aImageCodec.Decode(srcData);
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            return (srcName + ".png", ms.ToArray());
        }

        public (string name, byte[] data) OnPack(string srcPath, string srcName)
        {
            string logicalName = srcName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? srcName[..^4] : srcName;

            uint id = FileId.FromPath(logicalName);
            if (!HeaderDb.TryGet(id, out var h))
                return (logicalName, File.ReadAllBytes(srcPath));

            using var img = Image.Load<Rgba32>(srcPath);
            if (img.Width != h.Width || img.Height != h.Height)
                throw new InvalidDataException($"Size mismatch: {img.Width}x{img.Height} vs {h.Width}x{h.Height}");

            byte[] header = new byte[0x2C];
            BeBinary.WriteInt32(header, 0x00, (int)h.Magic);
            BeBinary.WriteInt32(header, 0x04, (int)h.F04);
            BeBinary.WriteInt32(header, 0x08, (int)h.F08);
            BeBinary.WriteInt32(header, 0x0C, (int)h.F0C);
            BeBinary.WriteInt32(header, 0x10, (int)h.F10);
            BeBinary.WriteInt32(header, 0x14, h.Width);
            BeBinary.WriteInt32(header, 0x18, h.Height);
            BeBinary.WriteInt32(header, 0x20, (int)h.F20);
            BeBinary.WriteInt32(header, 0x24, (int)h.LogicalW);
            BeBinary.WriteInt32(header, 0x28, (int)h.LogicalH);

            byte[] result = T8aImageCodec.Encode(img, header);
            return (logicalName, result);
        }

        struct HeaderInfo
        {
            public uint Magic, F04, F08, F0C, F10, F20, LogicalW, LogicalH;
            public int Width, Height;
        }

        static class HeaderDb
        {
            static Dictionary<uint, HeaderInfo> dict = new();

            public static void Preload(string root)
            {
                if (string.IsNullOrEmpty(root)) return;
                string path = Path.Combine(root, "list.xml");
                if (!File.Exists(path)) { dict = new(); return; }

                var res = new Dictionary<uint, HeaderInfo>();
                var doc = new XmlDocument();
                doc.Load(path);
                var nodes = doc.SelectNodes("/pak/item");
                if (nodes == null) { dict = res; return; }

                foreach (XmlNode item in nodes)
                {
                    if (!uint.TryParse(item.Attributes?["id"]?.Value, out uint id)) continue;
                    if (item.SelectSingleNode("header") is not XmlElement h) continue;

                    res[id] = new HeaderInfo
                    {
                        Magic = P(h, "magic"), F04 = P(h, "f04"), F08 = P(h, "f08"), F0C = P(h, "f0C"),
                        F10 = P(h, "f10"), Width = (int)P(h, "width"), Height = (int)P(h, "height"),
                        F20 = P(h, "f20"), LogicalW = P(h, "logicalW"), LogicalH = P(h, "logicalH")
                    };
                }
                dict = res;
            }

            public static bool TryGet(uint id, out HeaderInfo h) => dict.TryGetValue(id, out h);

            static uint P(XmlElement e, string a) => uint.TryParse(e.GetAttribute(a), out uint v) ? v : 0;
        }
    }
}