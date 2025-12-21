using System;
using System.Collections.Generic;
using System.Linq;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Single command to inspect page boxes or bookmark tree.
    /// Usage:
    ///   tjpdf-cli pdf-info page-boxes --input file.pdf
    ///   tjpdf-cli pdf-info bookmark-tree --input file.pdf
    /// </summary>
    public class PdfInfoCommand : Command
    {
        public override string Name => "pdf-info";
        public override string Description => "Shows page boxes/rotation or bookmark outline tree";

        public override void Execute(string[] args)
        {
            string inputFile = "";
            string mode = "";

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (string.Equals(arg, "page-boxes", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--page-boxes", StringComparison.OrdinalIgnoreCase))
                {
                    mode = "page-boxes";
                    continue;
                }
                if (string.Equals(arg, "bookmark-tree", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--bookmark-tree", StringComparison.OrdinalIgnoreCase))
                {
                    mode = "bookmark-tree";
                    continue;
                }
                if (string.Equals(arg, "--mode", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    mode = args[++i];
                    continue;
                }
                if (string.Equals(arg, "--input", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    inputFile = args[++i];
                    continue;
                }
                if (!arg.StartsWith("-") && string.IsNullOrWhiteSpace(inputFile))
                {
                    inputFile = arg;
                }
            }

            if (string.IsNullOrWhiteSpace(inputFile) || string.IsNullOrWhiteSpace(mode))
            {
                Console.WriteLine("Informe o modo e o arquivo de entrada.");
                ShowHelp();
                return;
            }

            if (string.Equals(mode, "page-boxes", StringComparison.OrdinalIgnoreCase))
            {
                AnalyzePages(inputFile);
                return;
            }
            if (string.Equals(mode, "bookmark-tree", StringComparison.OrdinalIgnoreCase))
            {
                VisualizeBookmarks(inputFile);
                return;
            }

            Console.WriteLine($"Unknown mode: {mode}");
            ShowHelp();
        }

        private void AnalyzePages(string inputFile)
        {
            using var doc = new PdfDocument(new PdfReader(inputFile));
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var media = page.GetMediaBox();
                var crop = page.GetCropBox();
                Console.WriteLine($"Página {p}: rot={page.GetRotation()} media=({media.GetLeft()}, {media.GetBottom()}, {media.GetRight()}, {media.GetTop()}) crop=({crop.GetLeft()}, {crop.GetBottom()}, {crop.GetRight()}, {crop.GetTop()})");
            }
        }

        private void VisualizeBookmarks(string inputFile)
        {
            using var doc = new PdfDocument(new PdfReader(inputFile));
            var outlines = doc.GetOutlines(false);
            if (outlines == null)
            {
                Console.WriteLine("Sem bookmarks (outlines).");
                return;
            }
            PrintOutline(outlines, 0, doc);
        }

        private void PrintOutline(PdfOutline outline, int level, PdfDocument doc)
        {
            foreach (var child in outline.GetAllChildren())
            {
                var dest = child.GetDestination();
                int page = 0;
                var destObj = dest?.GetPdfObject();
                if (destObj is PdfArray arr)
                {
                    for (int p = 1; p <= doc.GetNumberOfPages(); p++)
                    {
                        if (arr.Get(0).Equals(doc.GetPage(p).GetPdfObject()))
                        {
                            page = p;
                            break;
                        }
                    }
                }
                Console.WriteLine(new string(' ', level * 2) + "- " + child.GetTitle() + (page > 0 ? $" (p{page})" : ""));
                PrintOutline(child, level + 1, doc);
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pdf-info page-boxes --input file.pdf");
            Console.WriteLine("tjpdf-cli pdf-info bookmark-tree --input file.pdf");
            Console.WriteLine("Modes:");
            Console.WriteLine("  page-boxes    -> mediaBox/cropBox/rotation per page");
            Console.WriteLine("  bookmark-tree -> bookmarks (outlines) tree");
        }
    }
}
