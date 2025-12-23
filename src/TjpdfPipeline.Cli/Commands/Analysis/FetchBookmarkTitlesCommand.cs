using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FilterPDF.Models;
using FilterPDF.Utils;
using Newtonsoft.Json;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Versão TJPDF do exemplo FetchBookmarkTitles (iText 5), com recursão nos nós.
    /// Uso: tjpdf-cli fetch-bookmark-titles --input-file <pdf> [--json] [--tree]
    /// </summary>
    public class FetchBookmarkTitlesCommand : Command
    {
        public override string Name => "fetch-bookmark-titles";
        public override string Description => "Lista títulos de bookmarks (recursivo)";

        public override void Execute(string[] args)
        {
            string? inputFile = null;
            string? inputDir = null;
            bool asJson = false;
            bool asTree = false;

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--input-file":
                        if (i + 1 < args.Length) inputFile = args[++i];
                        break;
                    case "--input-dir":
                        if (i + 1 < args.Length) inputDir = args[++i];
                        break;
                    case "--json":
                        asJson = true;
                        break;
                    case "--tree":
                        asTree = true;
                        break;
                    case "-h":
                    case "--help":
                        ShowHelp();
                        return;
                    default:
                        if (!args[i].StartsWith("-") && inputFile == null)
                            inputFile = args[i];
                        break;
                }
            }

            var files = new List<string>();
            if (!string.IsNullOrWhiteSpace(inputDir))
            {
                if (!Directory.Exists(inputDir))
                {
                    Console.Error.WriteLine($"Diretório não encontrado: {inputDir}");
                    return;
                }
                files.AddRange(Directory.GetFiles(inputDir, "*.pdf", SearchOption.TopDirectoryOnly));
            }
            else if (!string.IsNullOrWhiteSpace(inputFile))
            {
                if (!File.Exists(inputFile))
                {
                    Console.Error.WriteLine($"Arquivo não encontrado: {inputFile}");
                    return;
                }
                files.Add(inputFile);
            }
            else
            {
                ShowHelp();
                return;
            }

            foreach (var file in files.OrderBy(f => f))
            {
                var nodes = ExtractBookmarkNodes(file);
                if (asJson)
                {
                    var payload = new
                    {
                        file = Path.GetFileName(file),
                        bookmarksFound = nodes.Count,
                        bookmarks = nodes.Select(n => new
                        {
                            title = n.Title,
                            level = n.Level,
                            pageNumber = n.PageNumber,
                            childrenCount = n.ChildrenCount
                        }).ToList()
                    };
                    Console.WriteLine(JsonConvert.SerializeObject(payload, Formatting.Indented));
                    continue;
                }

                Console.WriteLine($"Arquivo: {Path.GetFileName(file)}");
                if (nodes.Count == 0)
                {
                    Console.WriteLine("  (sem bookmarks)");
                    continue;
                }
                foreach (var n in nodes)
                {
                    var indent = asTree ? new string(' ', n.Level * 2) : "";
                    var page = n.PageNumber > 0 ? $" (p{n.PageNumber})" : "";
                    Console.WriteLine($"{indent}{n.Title}{page}");
                }
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli fetch-bookmark-titles --input-file <pdf> [--json] [--tree]");
            Console.WriteLine("tjpdf-cli fetch-bookmark-titles --input-dir <dir> [--json] [--tree]");
            Console.WriteLine("Lista títulos de bookmarks com recursão nos nós (Kids) e pagina inicial.");
        }

        private class BookmarkNode
        {
            public string Title { get; set; } = "";
            public int Level { get; set; }
            public int PageNumber { get; set; }
            public int ChildrenCount { get; set; }
        }

        private List<BookmarkNode> ExtractBookmarkNodes(string filePath)
        {
            var nodes = new List<BookmarkNode>();
            try
            {
                using var reader = new PdfReader(filePath);
                reader.SetUnethicalReading(true);
                using var doc = new PdfDocument(reader);
                var items = BookmarkExtractor.Extract(doc);
                foreach (var item in items)
                    FlattenBookmark(item, nodes);
            }
            catch
            {
                // ignore
            }
            return nodes;
        }

        private void FlattenBookmark(BookmarkItem item, List<BookmarkNode> nodes)
        {
            if (item == null) return;
            nodes.Add(new BookmarkNode
            {
                Title = item.Title ?? "",
                Level = item.Level,
                PageNumber = item.Destination?.PageNumber ?? 0,
                ChildrenCount = item.Children?.Count ?? 0
            });
            if (item.Children == null || item.Children.Count == 0) return;
            foreach (var child in item.Children)
                FlattenBookmark(child, nodes);
        }
    }
}
