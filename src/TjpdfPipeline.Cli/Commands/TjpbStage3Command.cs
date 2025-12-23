using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using FilterPDF;

namespace FilterPDF.Commands
{
    /// <summary>
    /// S3: executa PDFAnalyzer e grava JSON por arquivo.
    /// </summary>
    public class TjpbStage3Command : Command
    {
        public override string Name => "tjpb-s3";
        public override string Description => "S3: PDFAnalyzer -> JSON por arquivo";

        public override void Execute(string[] args)
        {
            string inputDir = ".";
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "stage3_out");
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

            var sources = ResolveSources(inputDir);
            Directory.CreateDirectory(outDir);

            var outputs = new List<Dictionary<string, string>>();
            int processed = 0;

            foreach (var src in sources.Items)
            {
                if (maxFiles.HasValue && processed >= maxFiles.Value) break;
                if (sources.Mode == "pdf")
                {
                    var analysis = new PDFAnalyzer(src.Path).AnalyzeFull();
                    var outPath = Path.Combine(outDir, $"{src.Process}.json");
                    File.WriteAllText(outPath, JsonConvert.SerializeObject(analysis, Formatting.Indented));
                    outputs.Add(new Dictionary<string, string>
                    {
                        ["process"] = src.Process,
                        ["source"] = src.Path,
                        ["output"] = outPath
                    });
                    processed++;
                }
                else
                {
                    var outPath = Path.Combine(outDir, Path.GetFileName(src.Path));
                    File.Copy(src.Path, outPath, true);
                    outputs.Add(new Dictionary<string, string>
                    {
                        ["process"] = src.Process,
                        ["source"] = src.Path,
                        ["output"] = outPath
                    });
                    processed++;
                }
            }

            var manifest = new Dictionary<string, object>
            {
                ["stage"] = "S3",
                ["mode"] = sources.Mode,
                ["output_dir"] = Path.GetFullPath(outDir),
                ["outputs"] = outputs
            };

            var manifestPath = Path.Combine(outDir, "stage3_outputs.json");
            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));

            Console.WriteLine($"[tjpb-s3] Output: {Path.GetFullPath(outDir)}");
            Console.WriteLine($"[tjpb-s3] Manifest: {manifestPath}");
            if (printSummary)
                Console.WriteLine(JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli tjpb-s3 [--input-dir <dir>] [--out-dir <dir>] [--max N] [--print-summary]");
            Console.WriteLine("Executa o PDFAnalyzer e grava JSON por arquivo (ou copia JSON de cache).");
        }

        private SourceManifest ResolveSources(string inputDir)
        {
            inputDir = Path.GetFullPath(inputDir);
            var pdfs = Directory.GetFiles(inputDir, "*.pdf", SearchOption.AllDirectories).OrderBy(p => p).ToList();
            var jsons = Directory.GetFiles(inputDir, "*.json", SearchOption.AllDirectories).OrderBy(p => p).ToList();
            if (pdfs.Count > 0)
            {
                return new SourceManifest
                {
                    Mode = "pdf",
                    Items = pdfs.Select(p => new SourceItem { Path = p, Process = DeriveProcessName(p) }).ToList()
                };
            }
            return new SourceManifest
            {
                Mode = "json_cache",
                Items = jsons.Select(p => new SourceItem { Path = p, Process = DeriveProcessName(p) }).ToList()
            };
        }

        private string DeriveProcessName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var m = System.Text.RegularExpressions.Regex.Match(name ?? "", @"\d+");
            if (m.Success) return m.Value;
            return name ?? "";
        }

        private sealed class SourceManifest
        {
            public string Mode { get; set; } = "pdf";
            public List<SourceItem> Items { get; set; } = new List<SourceItem>();
        }

        private sealed class SourceItem
        {
            public string Path { get; set; } = "";
            public string Process { get; set; } = "";
        }
    }
}
