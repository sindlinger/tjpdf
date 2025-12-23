using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace FilterPDF.Commands
{
    /// <summary>
    /// S1: padroniza entrada (ZIP/PDF) -> 1 pasta por processo com 1 PDF.
    /// Gera manifest do que foi entregue.
    /// </summary>
    public class TjpbStage1Command : Command
    {
        public override string Name => "tjpb-s1";
        public override string Description => "S1: ingestão/organização (ZIP/PDF -> 1 pasta por processo + 1 PDF)";

        public override void Execute(string[] args)
        {
            string inputDir = ".";
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "stage1_out");
            int? maxFiles = null;
            bool printJson = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--help" || args[i] == "-h") { ShowHelp(); return; }
                if (args[i] == "--input-dir" && i + 1 < args.Length) inputDir = args[i + 1];
                if (args[i] == "--out-dir" && i + 1 < args.Length) outDir = args[i + 1];
                if (args[i] == "--max" && i + 1 < args.Length && int.TryParse(args[i + 1], out var m)) maxFiles = m;
                if (args[i] == "--print-json") printJson = true;
            }

            var cmd = new PreprocessInputsCommand();
            var passArgs = new List<string> { "--input-dir", inputDir, "--out-dir", outDir, "--per-process-dir" };
            if (maxFiles.HasValue) passArgs.AddRange(new[] { "--max", maxFiles.Value.ToString() });
            cmd.Execute(passArgs.ToArray());

            var manifest = BuildManifest(inputDir, outDir);
            var manifestPath = Path.Combine(outDir, "stage1_manifest.json");
            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));

            Console.WriteLine($"[tjpb-s1] Saída: {Path.GetFullPath(outDir)}");
            Console.WriteLine($"[tjpb-s1] Manifest: {manifestPath}");
            if (printJson)
                Console.WriteLine(JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli tjpb-s1 --input-dir <dir> [--out-dir <dir>] [--max N] [--print-json]");
            Console.WriteLine("Padroniza a entrada: cria 1 pasta por processo com 1 PDF (mergeado se ZIP).");
        }

        private object BuildManifest(string inputDir, string outDir)
        {
            var processDirs = Directory.Exists(outDir)
                ? Directory.GetDirectories(outDir).OrderBy(d => d).ToList()
                : new List<string>();

            var items = new List<Dictionary<string, object>>();
            foreach (var dir in processDirs)
            {
                var process = Path.GetFileName(dir);
                var pdf = Directory.GetFiles(dir, "*.pdf").OrderBy(f => f).FirstOrDefault();
                var listFile = Path.Combine(outDir, $"{process}_arquivos.txt");
                var fromZip = File.Exists(listFile);
                var titles = new List<string>();
                if (fromZip)
                {
                    try
                    {
                        titles = File.ReadAllLines(listFile)
                            .Select(l => l.Contains('.') ? l.Substring(l.IndexOf('.') + 1).Trim() : l.Trim())
                            .Where(l => !string.IsNullOrWhiteSpace(l))
                            .ToList();
                    }
                    catch { }
                }

                items.Add(new Dictionary<string, object>
                {
                    ["process"] = process ?? "",
                    ["dir"] = dir,
                    ["pdf"] = pdf ?? "",
                    ["from_zip"] = fromZip,
                    ["bookmarks_created"] = fromZip,
                    ["bookmarks_list_file"] = fromZip ? listFile : "",
                    ["bookmark_titles"] = titles
                });
            }

            return new Dictionary<string, object>
            {
                ["stage"] = "S1",
                ["input_dir"] = Path.GetFullPath(inputDir),
                ["output_dir"] = Path.GetFullPath(outDir),
                ["processes"] = items
            };
        }
    }
}
