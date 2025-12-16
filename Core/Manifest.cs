using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace IpfbTool.Core
{
    internal sealed class Manifest
    {
        readonly object _lock = new();

        public List<Dictionary<string, string>> T32 { get; } = new();
        public List<Dictionary<string, string>> FNT { get; } = new();
        public List<Dictionary<string, string>> PRT { get; } = new();

        public void AddT32(Dictionary<string, string> entry)
        {
            if (entry == null) return;
            lock (_lock) T32.Add(entry);
        }

        public void AddFNT(Dictionary<string, string> entry)
        {
            if (entry == null) return;
            lock (_lock) FNT.Add(entry);
        }

        public void AddPRT(Dictionary<string, string> entry)
        {
            if (entry == null) return;
            lock (_lock) PRT.Add(entry);
        }

        public void Save(string path)
        {
            Dictionary<string, string>[] t32;
            Dictionary<string, string>[] fnt;
            Dictionary<string, string>[] prt;

            lock (_lock)
            {
                t32 = T32.ToArray();
                fnt = FNT.ToArray();
                prt = PRT.ToArray();
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var xw = XmlWriter.Create(fs, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                NewLineChars = "\n",
                CloseOutput = false
            });

            xw.WriteStartDocument();
            xw.WriteStartElement("pak");

            WriteSection(xw, "T32", t32);
            WriteSection(xw, "FNT", fnt);
            WriteSection(xw, "PRT", prt);

            xw.WriteEndElement();
            xw.WriteEndDocument();
        }

        static void WriteSection(XmlWriter xw, string name, IReadOnlyList<Dictionary<string, string>> items)
        {
            if (items == null || items.Count == 0) return;

            xw.WriteStartElement(name);

            for (int i = 0; i < items.Count; i++)
            {
                var dict = items[i];
                if (dict == null) continue;

                xw.WriteStartElement("file");

                foreach (var kv in dict)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    xw.WriteAttributeString(kv.Key, kv.Value ?? "");
                }

                xw.WriteEndElement();
            }

            xw.WriteEndElement();
        }

        public static Manifest Load(string path)
        {
            var m = new Manifest();
            if (!File.Exists(path)) return m;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var xr = XmlReader.Create(fs, new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                DtdProcessing = DtdProcessing.Prohibit,
                CloseInput = false
            });

            List<Dictionary<string, string>>? current = null;

            while (xr.Read())
            {
                if (xr.NodeType != XmlNodeType.Element) continue;

                if (xr.Depth == 1)
                {
                    if (xr.Name.Equals("T32", StringComparison.OrdinalIgnoreCase)) { current = m.T32; continue; }
                    if (xr.Name.Equals("FNT", StringComparison.OrdinalIgnoreCase)) { current = m.FNT; continue; }
                    if (xr.Name.Equals("PRT", StringComparison.OrdinalIgnoreCase)) { current = m.PRT; continue; }
                }

                if (xr.Depth == 2 && xr.Name.Equals("file", StringComparison.OrdinalIgnoreCase) && current != null)
                {
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    if (xr.HasAttributes)
                    {
                        while (xr.MoveToNextAttribute())
                            dict[xr.Name] = xr.Value ?? "";
                        xr.MoveToElement();
                    }

                    current.Add(dict);
                }
            }

            return m;
        }
    }
}