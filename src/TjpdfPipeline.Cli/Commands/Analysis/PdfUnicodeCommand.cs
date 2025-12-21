using System;
using System.Collections.Generic;
using System.Linq;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Unified command for ToUnicode inspection.
    /// Usage:
    ///   tjpdf-cli pdf-unicode list --input file.pdf
    ///   tjpdf-cli pdf-unicode dump --input file.pdf
    /// </summary>
    public class PdfUnicodeCommand : Command
    {
        public override string Name => "pdf-unicode";
        public override string Description => "Lists fonts with ToUnicode or dumps ToUnicode maps";

        public override void Execute(string[] args)
        {
            var (mode, rest) = ParseMode(args, new[] { "list", "dump", "maps", "find" });
            if (string.IsNullOrWhiteSpace(mode))
            {
                ShowHelp();
                return;
            }

            switch (Normalize(mode))
            {
                case "list":
                case "find":
                    new FindToUnicodeCommand().Execute(rest);
                    break;
                case "dump":
                case "maps":
                    new ToUnicodeExtractCommand().Execute(rest);
                    break;
                default:
                    Console.WriteLine($"Unknown mode: {mode}");
                    ShowHelp();
                    break;
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pdf-unicode list --input file.pdf");
            Console.WriteLine("tjpdf-cli pdf-unicode dump --input file.pdf");
        }

        private static (string mode, string[] rest) ParseMode(string[] args, string[] modes)
        {
            string mode = "";
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if ((arg == "--mode" || arg == "--action") && i + 1 < args.Length)
                {
                    mode = args[++i];
                    continue;
                }
                if (!arg.StartsWith("-") && string.IsNullOrWhiteSpace(mode))
                {
                    mode = arg;
                    continue;
                }
                if (arg.StartsWith("--") && string.IsNullOrWhiteSpace(mode))
                {
                    var flag = arg.TrimStart('-');
                    if (modes.Contains(flag, StringComparer.OrdinalIgnoreCase))
                    {
                        mode = flag;
                        continue;
                    }
                }
                rest.Add(arg);
            }
            return (mode, rest.ToArray());
        }

        private static string Normalize(string mode)
        {
            return (mode ?? "").Trim().ToLowerInvariant();
        }
    }
}
