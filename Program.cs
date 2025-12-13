using System;
using System.IO;
using System.Text;
using IpfbTool.Archive;
using IpfbTool.Core;

namespace IpfbTool
{
    internal static class Program
    {
        static int Main(string[] args)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            if (args.Length < 2 || HasHelp(args))
            {
                PrintHelp();
                return args.Length < 2 ? 1 : 0;
            }

            if (HasList(args))
            {
                PrintTransformers();
                return 0;
            }

            string a0 = args[0];
            string a1 = args[1];

            if (!TryGetXformSpec(args, out var spec, out var err))
            {
                Console.WriteLine(err);
                PrintHelp();
                return 1;
            }

            if (!Transformers.TryConfigure(spec, out var cfgErr))
            {
                Console.WriteLine(cfgErr);
                PrintHelp();
                return 1;
            }

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

                Transformers.PreloadForPack(inDir);
                IpfbPack.Pack(inDir, pakPath);
                return 0;
            }

            Console.WriteLine("参数不明确:");
            Console.WriteLine("  如果第一个参数是 .pak，第二个是目录 = 解包");
            Console.WriteLine("  如果第一个是目录，第二个是 .pak = 打包");
            PrintHelp();
            return 1;
        }

        static bool HasHelp(string[] args)
        {
            foreach (var a in args)
                if (a is "-h" or "--help" or "/?" or "help")
                    return true;
            return false;
        }

        static bool HasList(string[] args)
        {
            foreach (var a in args)
                if (a is "--list-xform" or "--list-transform" or "--list")
                    return true;
            return false;
        }

        static bool TryGetXformSpec(string[] args, out string spec, out string error)
        {
            spec = "all";
            error = "";

            for (int i = 2; i < args.Length; i++)
            {
                var a = args[i];

                if (a.StartsWith("--xform=", StringComparison.OrdinalIgnoreCase) ||
                    a.StartsWith("--transform=", StringComparison.OrdinalIgnoreCase))
                {
                    spec = a[(a.IndexOf('=') + 1)..];
                    continue;
                }

                if (a.Equals("--xform", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("--transform", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("-x", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 >= args.Length)
                    {
                        error = $"缺少参数值: {a}";
                        return false;
                    }
                    spec = args[++i];
                    continue;
                }

                if (a is "--list-xform" or "--list-transform" or "--list" or "-h" or "--help" or "/?" or "help")
                    continue;

                error = $"未知参数: {a}";
                return false;
            }

            return true;
        }

        static void PrintTransformers()
        {
            Console.WriteLine("可用转换器:");
            foreach (var n in Transformers.Available)
                Console.WriteLine("  " + n);
        }

        static void PrintHelp()
        {
            Console.WriteLine("用法:");
            Console.WriteLine("  IpfbTool <xxx.pak> <输出目录>  (解包)");
            Console.WriteLine("  IpfbTool <输入目录> <xxx.pak>  (打包)");
            Console.WriteLine();
            Console.WriteLine("可选参数:");
            Console.WriteLine("  --xform=all|none|名单");
            Console.WriteLine("  --xform all|none|名单");
            Console.WriteLine("  -x all|none|名单");
            Console.WriteLine("  --list-xform");
            Console.WriteLine();
            Console.WriteLine("名单格式:");
            Console.WriteLine("  TBLR");
            Console.WriteLine("  TBLR,ISB");
            Console.WriteLine("  all,-TBLR");
            Console.WriteLine();
            PrintTransformers();
        }
    }
}