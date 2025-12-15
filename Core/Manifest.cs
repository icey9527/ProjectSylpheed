using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace IpfbTool.Core
{
    internal sealed class Manifest
    {
        public List<Dictionary<string, string>> T32 { get; } = new();
        public List<Dictionary<string, string>> FNT { get; } = new();
        public List<Dictionary<string, string>> PRT { get; } = new();

        public void AddT32(Dictionary<string, string> entry) => T32.Add(entry);
        public void AddFNT(Dictionary<string, string> entry) => FNT.Add(entry);
        public void AddPRT(Dictionary<string, string> entry) => PRT.Add(entry);

        public void Save(string path)
        {
            using var fs = new FileStream(path, FileMode.Create);
            using var xw = XmlWriter.Create(fs, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                NewLineChars = "\n"
            });

            xw.WriteStartDocument();
            xw.WriteStartElement("pak");

            WriteSection(xw, "T32", T32);
            WriteSection(xw, "FNT", FNT);
            WriteSection(xw, "PRT", PRT);

            xw.WriteEndElement();
            xw.WriteEndDocument();
        }

        static void WriteSection(XmlWriter xw, string name, List<Dictionary<string, string>> items)
        {
            if (items.Count == 0) return;

            xw.WriteStartElement(name);
            foreach (var dict in items)
            {
                xw.WriteStartElement("file");
                foreach (var kv in dict)
                    xw.WriteAttributeString(kv.Key, kv.Value);
                xw.WriteEndElement();
            }
            xw.WriteEndElement();
        }

        public static Manifest Load(string path)
        {
            var m = new Manifest();
            if (!File.Exists(path)) return m;

            var doc = new XmlDocument();
            doc.Load(path);

            LoadSection(doc, "T32", m.T32);
            LoadSection(doc, "FNT", m.FNT);
            LoadSection(doc, "PRT", m.PRT);

            return m;
        }

        static void LoadSection(XmlDocument doc, string name, List<Dictionary<string, string>> list)
        {
            var nodes = doc.SelectNodes($"/pak/{name}/file");
            if (nodes == null) return;

            foreach (XmlElement el in nodes)
            {
                var dict = new Dictionary<string, string>();
                foreach (XmlAttribute attr in el.Attributes)
                    dict[attr.Name] = attr.Value;
                list.Add(dict);
            }
        }
    }
}