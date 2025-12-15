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
            List<Dictionary<string, string>> t32, fnt, prt;
            lock (_lock)
            {
                t32 = new List<Dictionary<string, string>>(T32);
                fnt = new List<Dictionary<string, string>>(FNT);
                prt = new List<Dictionary<string, string>>(PRT);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            using var xw = XmlWriter.Create(fs, new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                NewLineChars = "\n"
            });

            xw.WriteStartDocument();
            xw.WriteStartElement("pak");

            WriteSection(xw, "T32", t32);
            WriteSection(xw, "FNT", fnt);
            WriteSection(xw, "PRT", prt);

            xw.WriteEndElement();
            xw.WriteEndDocument();
        }

        static void WriteSection(XmlWriter xw, string name, List<Dictionary<string, string>> items)
        {
            if (items == null || items.Count == 0) return;

            xw.WriteStartElement(name);

            foreach (var dict in items)
            {
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
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (XmlAttribute attr in el.Attributes)
                    dict[attr.Name] = attr.Value;
                list.Add(dict);
            }
        }
    }
}