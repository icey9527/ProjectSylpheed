using System;
using System.IO;
using IpfbTool.Archive;

namespace IpfbTool
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            if (args.Length != 2)
            {
                Console.WriteLine("用法:");
                Console.WriteLine("  IpfbTool <xxx.pak> <输出目录>  (解包)");
                Console.WriteLine("  IpfbTool <输入目录> <xxx.pak>  (打包)");
                return 1;
            }

            string a0 = args[0];
            string a1 = args[1];

            bool a0IsPak = string.Equals(Path.GetExtension(a0), ".pak", StringComparison.OrdinalIgnoreCase);
            bool a1IsPak = string.Equals(Path.GetExtension(a1), ".pak", StringComparison.OrdinalIgnoreCase);

            if (a0IsPak && !a1IsPak)
            {
                string pakPath = Path.GetFullPath(a0);
                string outDir = Path.GetFullPath(a1);
                Directory.CreateDirectory(outDir);
                IpfbUnpack.Unpack(pakPath, outDir);
                return 0;
            }

            if (!a0IsPak && a1IsPak)
            {
                string inDir = Path.GetFullPath(a0);
                string pakPath = Path.GetFullPath(a1);
                if (!Directory.Exists(inDir))
                {
                    Console.WriteLine("输入目录不存在: " + inDir);
                    return 1;
                }

                IpfbPack.Pack(inDir, pakPath);
                return 0;
            }

            Console.WriteLine("参数不明确:");
            Console.WriteLine("  如果第一个参数是 .pak，第二个是目录 = 解包");
            Console.WriteLine("  如果第一个是目录，第二个是 .pak = 打包");
            return 1;
        }
    }
}