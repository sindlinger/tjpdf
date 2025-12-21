using System;
using System.Collections.Generic;
using System.Linq;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Unified command for PDF object inspection.
    /// Usage:
    ///   tjpdf-cli pdf-objects list --input file.pdf [--limit N]
    ///   tjpdf-cli pdf-objects analyze --input file.pdf [--limit N]
    ///   tjpdf-cli pdf-objects deep --input file.pdf [--limit N]
    /// </summary>
    public class PdfObjectsCommand : Command
    {
        public override string Name => "pdf-objects";
        public override string Description => "Lists/analyzes PDF objects (basic, summary, deep)";

        public override void Execute(string[] args)
        {
            var (mode, rest) = ParseMode(args, new[] { "list", "analyze", "deep", "inspect", "summary" });
            if (string.IsNullOrWhiteSpace(mode))
            {
                ShowHelp();
                return;
            }

            switch (Normalize(mode))
            {
                case "list":
                case "inspect":
                    new InspectPdfCommand().Execute(rest);
                    break;
                case "analyze":
                case "summary":
                    new AnalyzePdfObjectStructureCommand().Execute(rest);
                    break;
                case "deep":
                    new DeepPdfObjectAnalyzerCommand().Execute(rest);
                    break;
                default:
                    Console.WriteLine($"Unknown mode: {mode}");
                    ShowHelp();
                    break;
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pdf-objects list --input file.pdf [--limit N]");
            Console.WriteLine("tjpdf-cli pdf-objects analyze --input file.pdf [--limit N]");
            Console.WriteLine("tjpdf-cli pdf-objects deep --input file.pdf [--limit N]");
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
