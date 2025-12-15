using System;
using System.Globalization;

namespace IpfbTool.Core
{
    internal static class TexId
    {
        public static uint Standalone(string name) => FileId.FromPath(name);

        public static uint Embedded(string kind, string container, string key) =>
            FileId.FromPath($"{kind}|{container}|{key}");

        public static string ToX8(uint id) => id.ToString("X8");

        public static uint Parse(string s)
        {
            s = (s ?? "").Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            if (uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx)) return hx;
            return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }
    }
}