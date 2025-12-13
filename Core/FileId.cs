using System;
using System.IO;

namespace IpfbTool.Core
{
    internal static class FileId
    {
        public static uint FromPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return 0;

            string fileName = Path.GetFileName(path);

            if (fileName.Length > 0 && fileName[0] == '$')
            {
                string s = fileName.Substring(1);
                int i = 0;
                while (i < s.Length && i < 8 && Uri.IsHexDigit(s[i]))
                    i++;
                if (i == 0)
                    return 0;
                string hex = s.Substring(0, i);
                return Convert.ToUInt32(hex, 16);
            }

            return Hash.Filehash(path);
        }
    }
}