using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FilterPDF.Commands
{
    /// <summary>
    /// S7: segmentação + BuildDocObject (DTO por documento) -> JSON por processo.
    /// </summary>
    public class TjpbStage7Command : Command
    {
        public override string Name => "tjpb-s7";
        public override string Description => "S7: segmentação + BuildDocObject -> JSON por processo";

        public override void Execute(string[] args)
        {
            string inputDir = ".";
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "stage7_out");
            int? maxFiles = null;
            bool printSummary = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--help" || args[i] == "-h") { ShowHelp(); return; }
                if (args[i] == "--input-dir" && i + 1 < args.Length) inputDir = args[i + 1];
                if (args[i] == "--out-dir" && i + 1 < args.Length) outDir = args[i + 1];
                if (args[i] == "--max" && i + 1 < args.Length && int.TryParse(args[i + 1], out var m)) maxFiles = m;
                if (args[i] == "--print-summary") printSummary = true;
            }

            var passArgs = new List<string> { "--input-dir", inputDir, "--stage", "s7", "--out-dir", outDir };
            if (maxFiles.HasValue) passArgs.AddRange(new[] { "--limit", maxFiles.Value.ToString() });
            if (printSummary) passArgs.Add("--print-summary");

            var pipeline = new PipelineTjpbCommand();
            pipeline.Execute(passArgs.ToArray());
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli tjpb-s7 --input-dir <dir> [--out-dir <dir>] [--max N] [--print-summary]");
            Console.WriteLine("Executa a etapa S7 (segmentação + BuildDocObject) e grava JSON por processo.");
        }
    }
}
