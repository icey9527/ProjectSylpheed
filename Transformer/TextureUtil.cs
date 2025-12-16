using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace IpfbTool.Core
{
    internal static class TextureUtil
    {
        public static string ToPngPath(string srcName)
        {
            string dir = Path.GetDirectoryName(srcName) ?? "";
            string file = Path.GetFileName(srcName);

            string ext = Path.GetExtension(file);
            if (IsTextureExt(ext))
                file = Path.GetFileNameWithoutExtension(file);

            return string.IsNullOrEmpty(dir) ? (file + ".png") : Path.Combine(dir, file + ".png");
        }

        static bool IsTextureExt(string ext) => ext switch
        {
            ".T32" or ".T8aD" or ".T8aB" or ".T8aC" or ".T4aD" or ".T4aB" or ".T4aC" or
            ".T1aD" or ".T1aB" or ".T1aC" or ".4444" or ".1555" or
            ".TBM" or ".TBMD" or ".TBMC" or ".TBMB" => true,
            _ => false
        };

        public static uint ReadU32BE(byte[] d, int o) => unchecked((uint)BeBinary.ReadInt32(d, o));
        public static int ReadI32BE(byte[] d, int o) => BeBinary.ReadInt32(d, o);
        public static void WriteU32BE(byte[] d, int o, uint v) => BeBinary.WriteInt32(d, o, unchecked((int)v));
        public static void WriteI32BE(byte[] d, int o, int v) => BeBinary.WriteInt32(d, o, v);

        public static uint FourCC(string s)
        {
            ReadOnlySpan<char> span = (s ?? "").AsSpan();
            char c0 = span.Length > 0 ? span[0] : ' ';
            char c1 = span.Length > 1 ? span[1] : ' ';
            char c2 = span.Length > 2 ? span[2] : ' ';
            char c3 = span.Length > 3 ? span[3] : ' ';
            return ((uint)(byte)c0 << 24) | ((uint)(byte)c1 << 16) | ((uint)(byte)c2 << 8) | (byte)c3;
        }

        public static string Get(Dictionary<string, string> d, string key, string def = "")
            => d != null && d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;

        public static uint GetU32(Dictionary<string, string> d, string key, uint def = 0)
        {
            if (d == null || !d.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s)) return def;
            ReadOnlySpan<char> span = s.AsSpan().Trim();

            if (span.Length >= 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X') &&
                uint.TryParse(span[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx))
                return hx;

            return uint.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
        }

        public static int GetI32(Dictionary<string, string> d, string key, int def = 0)
        {
            if (d == null || !d.TryGetValue(key, out var s) || string.IsNullOrWhiteSpace(s)) return def;
            ReadOnlySpan<char> span = s.AsSpan().Trim();

            if (span.Length >= 2 && span[0] == '0' && (span[1] == 'x' || span[1] == 'X') &&
                int.TryParse(span[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx))
                return hx;

            return int.TryParse(span, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
        }
    }
}