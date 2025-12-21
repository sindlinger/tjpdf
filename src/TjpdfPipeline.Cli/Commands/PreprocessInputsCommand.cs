using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Navigation;
using iText.Kernel.Utils;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Pré-processamento de entradas:
    /// - ZIP: mergeia PDFs internos e cria bookmarks com o nome do arquivo.
    /// - PDF avulso: regrava (mesmo conteúdo) com título do processo.
    /// Saída padrão: pasta por processo com <processo>.pdf.
    /// </summary>
    public class PreprocessInputsCommand : Command
    {
        public override string Name => "preprocess-inputs";
        public override string Description => "Mergeia ZIPs e organiza PDFs para o pipeline-tjpb (com bookmarks).";

        public override void Execute(string[] args)
        {
            string inputDir = ".";
            string outDir = Path.Combine("tmp", "preprocessor_stage");
            int? maxFiles = null;
            bool perProcessDir = true;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--input-dir":
                        if (i + 1 < args.Length) inputDir = args[++i];
                        break;
                    case "--out-dir":
                        if (i + 1 < args.Length) outDir = args[++i];
                        break;
                    case "--max":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out var m)) maxFiles = m;
                        break;
                    case "--per-process-dir":
                        perProcessDir = true;
                        break;
                    case "--flat":
                        perProcessDir = false;
                        break;
                    case "-h":
                    case "--help":
                        ShowHelp();
                        return;
                }
            }

            if (!Directory.Exists(inputDir))
            {
                Console.Error.WriteLine($"Diretório não encontrado: {inputDir}");
                return;
            }

            Directory.CreateDirectory(outDir);

            var inputs = Directory.GetFiles(inputDir, "*.*", SearchOption.AllDirectories)
                                  .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                                              f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(f => f)
                                  .ToList();
            if (inputs.Count == 0)
            {
                Console.Error.WriteLine("[preprocess-inputs] Nenhum PDF/ZIP encontrado.");
                return;
            }

            int processed = 0;
            int merged = 0;
            int copied = 0;
            int skipped = 0;

            foreach (var src in inputs)
            {
                if (maxFiles.HasValue && processed >= maxFiles.Value) break;

                var proc = DeriveProcessName(src);
                if (string.IsNullOrWhiteSpace(proc))
                {
                    skipped++;
                    continue;
                }

                var targetDir = perProcessDir ? Path.Combine(outDir, proc) : outDir;
                Directory.CreateDirectory(targetDir);
                var outPdf = Path.Combine(targetDir, proc + ".pdf");

                if (File.Exists(outPdf))
                {
                    Console.Error.WriteLine($"[preprocess-inputs] Aviso: processo duplicado {proc}; ignorando {Path.GetFileName(src)}.");
                    skipped++;
                    continue;
                }

                bool ok;
                if (src.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ok = MergeZipToPdf(src, outPdf, proc, out var titles);
                    if (ok)
                    {
                        merged++;
                        WriteMergeList(outDir, proc, titles);
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else
                {
                    ok = CopyPdfWithTitle(src, outPdf, proc);
                    if (ok) copied++; else skipped++;
                }

                if (ok) processed++;
            }

            Console.WriteLine($"[preprocess-inputs] Processados: {processed} | merge ZIP: {merged} | copiados: {copied} | ignorados: {skipped}");
            Console.WriteLine($"[preprocess-inputs] Saída: {Path.GetFullPath(outDir)}");
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli preprocess-inputs --input-dir <dir> [--out-dir <dir>] [--max N] [--per-process-dir] [--flat]");
            Console.WriteLine("Mergeia ZIPs em PDFs com bookmarks por arquivo; PDFs avulsos são copiados.");
            Console.WriteLine("Padrão: saída por pasta de processo (use --flat para saída plana).");
            Console.WriteLine("Para ZIPs, gera um arquivo texto na raiz do out-dir com a lista ordenada de arquivos.");
        }

        private string DeriveProcessName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var m = Regex.Match(name ?? "", @"\\d+");
            if (m.Success) return m.Value;
            return name ?? "";
        }

        private bool MergeZipToPdf(string zipPath, string outPdf, string processNumber, out List<string> titles)
        {
            titles = new List<string>();
            try
            {
                using var archive = ZipFile.OpenRead(zipPath);
                var pdfEntries = archive.Entries
                    .Where(e => e.FullName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(e => e.FullName)
                    .ToList();
                if (pdfEntries.Count == 0) return false;

                using var writer = new PdfWriter(outPdf);
                using var destDoc = new PdfDocument(writer);
                if (!string.IsNullOrWhiteSpace(processNumber))
                    destDoc.GetDocumentInfo().SetTitle(processNumber);
                var merger = new PdfMerger(destDoc);
                PdfOutline root = destDoc.GetOutlines(false);
                int pageOffset = 0;

                foreach (var entry in pdfEntries)
                {
                    using var ms = new MemoryStream();
                    using (var s = entry.Open()) { s.CopyTo(ms); }
                    ms.Position = 0;
                    var reader = new PdfReader(ms);
                    reader.SetUnethicalReading(true);
                    using var srcDoc = new PdfDocument(reader);
                    int pages = srcDoc.GetNumberOfPages();
                    if (pages == 0) continue;

                    merger.Merge(srcDoc, 1, pages);
                    var title = Path.GetFileNameWithoutExtension(entry.FullName);
                    if (string.IsNullOrWhiteSpace(title)) title = entry.FullName;
                    var dest = PdfExplicitDestination.CreateFit(destDoc.GetPage(pageOffset + 1));
                    root.AddOutline(title).AddDestination(dest);
                    titles.Add(title);
                    pageOffset += pages;
                }
                return pageOffset > 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[preprocess-inputs] Falha ao mergear ZIP {Path.GetFileName(zipPath)}: {ex.Message}");
                return false;
            }
        }

        private void WriteMergeList(string outDir, string processNumber, List<string> titles)
        {
            if (string.IsNullOrWhiteSpace(processNumber)) return;
            if (titles == null || titles.Count == 0) return;
            try
            {
                var listPath = Path.Combine(outDir, $"{processNumber}_arquivos.txt");
                using var sw = new StreamWriter(listPath, false);
                for (int i = 0; i < titles.Count; i++)
                    sw.WriteLine($"{i + 1}. {titles[i]}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[preprocess-inputs] Falha ao gravar lista {processNumber}: {ex.Message}");
            }
        }

        private bool CopyPdfWithTitle(string srcPath, string outPdf, string processNumber)
        {
            try
            {
                using var reader = new PdfReader(srcPath);
                reader.SetUnethicalReading(true);
                using var writer = new PdfWriter(outPdf);
                using var destDoc = new PdfDocument(writer);
                if (!string.IsNullOrWhiteSpace(processNumber))
                    destDoc.GetDocumentInfo().SetTitle(processNumber);
                using var srcDoc = new PdfDocument(reader);
                int pages = srcDoc.GetNumberOfPages();
                if (pages > 0)
                    srcDoc.CopyPagesTo(1, pages, destDoc);
                return pages > 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[preprocess-inputs] Falha ao copiar {Path.GetFileName(srcPath)}: {ex.Message}");
                return false;
            }
        }
    }
}
