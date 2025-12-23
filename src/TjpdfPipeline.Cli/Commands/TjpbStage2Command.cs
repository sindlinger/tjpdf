using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace FilterPDF.Commands
{
    /// <summary>
    /// S2: resolve fontes (PDFs ou JSON cache) e gera manifest.
    /// </summary>
    public class TjpbStage2Command : Command
    {
        public override string Name => "tjpb-s2";
        public override string Description => "S2: resolve fontes (PDF vs cache) e gera manifest de inputs";

        public override void Execute(string[] args)
        {
            string inputDir = ".";
            string outManifest = "";
            bool printJson = false;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--help" || args[i] == "-h") { ShowHelp(); return; }
                if (args[i] == "--input-dir" && i + 1 < args.Length) inputDir = args[i + 1];
                if (args[i] == "--out" && i + 1 < args.Length) outManifest = args[i + 1];
                if (args[i] == "--print-json") printJson = true;
            }

            inputDir = Path.GetFullPath(inputDir);
            if (string.IsNullOrWhiteSpace(outManifest))
                outManifest = Path.Combine(inputDir, "stage2_sources.json");

            var manifest = BuildSourcesManifest(inputDir);
            File.WriteAllText(outManifest, JsonConvert.SerializeObject(manifest, Formatting.Indented));

            Console.WriteLine($"[tjpb-s2] Input: {inputDir}");
            Console.WriteLine($"[tjpb-s2] Manifest: {outManifest}");
            if (printJson)
                Console.WriteLine(JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli tjpb-s2 --input-dir <dir> [--out <file>] [--print-json]");
            Console.WriteLine("Gera manifest com a lista de fontes (PDFs ou JSON cache).");
        }

        private object BuildSourcesManifest(string inputDir)
        {
            var pdfs = Directory.GetFiles(inputDir, "*.pdf", SearchOption.AllDirectories)
                .OrderBy(p => p)
                .ToList();
            var jsons = Directory.GetFiles(inputDir, "*.json", SearchOption.AllDirectories)
                .OrderBy(p => p)
                .ToList();

            string mode;
            List<string> sources;
            if (pdfs.Count > 0)
            {
                mode = "pdf";
                sources = pdfs;
            }
            else
            {
                mode = "json_cache";
                sources = jsons;
            }

            var items = sources.Select(path => new Dictionary<string, string>
            {
                ["path"] = path,
                ["process"] = DeriveProcessName(path)
            }).ToList();

            return new Dictionary<string, object>
            {
                ["stage"] = "S2",
                ["input_dir"] = inputDir,
                ["mode"] = mode,
                ["sources"] = items
            };
        }

        private string DeriveProcessName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var m = System.Text.RegularExpressions.Regex.Match(name ?? "", @"\d+");
            if (m.Success) return m.Value;
            return name ?? "";
        }
    }
}
