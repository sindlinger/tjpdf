using System;
using System.Collections.Generic;
using System.Linq;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Unified command for PDF stream inspection.
    /// Usage:
    ///   tjpdf-cli pdf-streams list --input file.pdf [--limit N]
    ///   tjpdf-cli pdf-streams show --input file.pdf --id N
    /// </summary>
    public class PdfStreamsCommand : Command
    {
        public override string Name => "pdf-streams";
        public override string Description => "Lists PDF streams or shows a specific stream";

        public override void Execute(string[] args)
        {
            var (mode, rest) = ParseMode(args, new[] { "list", "show", "inspect" });
            if (string.IsNullOrWhiteSpace(mode))
            {
                ShowHelp();
                return;
            }

            switch (Normalize(mode))
            {
                case "list":
                    new VisualizeStreamsCommand().Execute(rest);
                    break;
                case "show":
                case "inspect":
                    new InspectStreamsCommand().Execute(rest);
                    break;
                default:
                    Console.WriteLine($"Unknown mode: {mode}");
                    ShowHelp();
                    break;
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pdf-streams list --input file.pdf [--limit N]");
            Console.WriteLine("tjpdf-cli pdf-streams show --input file.pdf --id N");
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
