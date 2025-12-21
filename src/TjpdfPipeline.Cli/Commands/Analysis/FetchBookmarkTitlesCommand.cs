using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Navigation;

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
                    Console.WriteLine($"{indent}{n.Title}");
                }
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli fetch-bookmark-titles --input-file <pdf> [--json] [--tree]");
            Console.WriteLine("tjpdf-cli fetch-bookmark-titles --input-dir <dir> [--json] [--tree]");
            Console.WriteLine("Lista títulos de bookmarks com recursão nos nós (Kids).");
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
                var root = doc.GetOutlines(false);
                if (root != null && root.GetAllChildren().Count > 0)
                {
                    foreach (var child in root.GetAllChildren())
                        WalkOutline(child, nodes, 0, doc);
                    return nodes;
                }

                // fallback raw /Outlines
                var catalog = doc.GetCatalog().GetPdfObject();
                var outlines = catalog?.GetAsDictionary(PdfName.Outlines);
                var first = outlines?.GetAsDictionary(PdfName.First);
                if (first != null)
                    WalkOutlineDict(first, nodes, 0, doc);
            }
            catch
            {
                // ignore
            }
            return nodes;
        }

        private void WalkOutline(PdfOutline outline, List<BookmarkNode> nodes, int level, PdfDocument doc)
        {
            if (outline == null) return;
            var dest = outline.GetDestination();
            int page = ResolveDestinationPage(dest, doc);
            if (page == 0)
            {
                var destObj = dest?.GetPdfObject();
                page = ResolveDestToPage(destObj, doc);
            }
            nodes.Add(new BookmarkNode
            {
                Title = outline.GetTitle() ?? "",
                Level = level,
                PageNumber = page,
                ChildrenCount = outline.GetAllChildren().Count
            });
            foreach (var child in outline.GetAllChildren())
                WalkOutline(child, nodes, level + 1, doc);
        }

        private void WalkOutlineDict(PdfDictionary item, List<BookmarkNode> nodes, int level, PdfDocument doc)
        {
            if (item == null) return;
            var title = item.GetAsString(PdfName.Title)?.ToUnicodeString() ?? "";
            var page = ResolveDestToPageFromOutlineDict(item, doc);
            var first = item.GetAsDictionary(PdfName.First);
            var next = item.GetAsDictionary(PdfName.Next);
            var hasChildren = first != null;
            var childrenCount = hasChildren ? CountSiblings(first) : 0;

            nodes.Add(new BookmarkNode
            {
                Title = title,
                Level = level,
                PageNumber = page,
                ChildrenCount = childrenCount
            });

            if (first != null)
                WalkOutlineDict(first, nodes, level + 1, doc);
            if (next != null)
                WalkOutlineDict(next, nodes, level, doc);
        }

        private int CountSiblings(PdfDictionary first)
        {
            int count = 0;
            var cur = first;
            while (cur != null)
            {
                count++;
                cur = cur.GetAsDictionary(PdfName.Next);
            }
            return count;
        }

        private int ResolveDestToPageFromOutlineDict(PdfDictionary outline, PdfDocument doc)
        {
            if (outline == null) return 0;
            PdfObject? destObj = outline.Get(PdfName.Dest);
            if (destObj == null)
            {
                var action = outline.GetAsDictionary(PdfName.A);
                var actionType = action?.GetAsName(PdfName.S);
                if (action != null && (actionType == null || PdfName.GoTo.Equals(actionType)))
                    destObj = action.Get(PdfName.D);
            }
            return ResolveDestToPage(destObj, doc);
        }

        private int ResolveDestToPage(PdfObject? destObj, PdfDocument doc)
        {
            if (destObj == null) return 0;

            if (destObj is PdfDictionary dict)
            {
                var actionType = dict.GetAsName(PdfName.S);
                if (PdfName.GoTo.Equals(actionType))
                    destObj = dict.Get(PdfName.D);
            }

            if (destObj is PdfArray arr)
                return ResolveArrayDest(arr, doc);

            if (destObj is PdfName || destObj is PdfString)
            {
                var resolved = ResolveNamedDestination(destObj, doc);
                if (resolved is PdfArray namedArr)
                    return ResolveArrayDest(namedArr, doc);
            }
            return 0;
        }

        private int ResolveDestinationPage(PdfDestination? destination, PdfDocument doc)
        {
            if (destination == null) return 0;
            try
            {
                var nameTree = doc.GetCatalog().GetNameTree(PdfName.Dests);
                var names = nameTree?.GetNames();
                var destPage = destination.GetDestinationPage(names);
                if (destPage is PdfDictionary dict)
                    return doc.GetPageNumber(dict);
            }
            catch
            {
                // ignore and fallback
            }
            return 0;
        }

        private int ResolveArrayDest(PdfArray arr, PdfDocument doc)
        {
            if (arr == null || arr.Size() == 0) return 0;
            var first = arr.Get(0);
            if (first is PdfNumber num)
                return (int)num.GetValue() + 1;

            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var pagePdfObj = doc.GetPage(p).GetPdfObject();
                if (first.Equals(pagePdfObj)) return p;
                var ref1 = first.GetIndirectReference();
                var ref2 = pagePdfObj.GetIndirectReference();
                if (ref1 != null && ref2 != null && ref1.GetObjNumber() == ref2.GetObjNumber())
                    return p;
            }
            return 0;
        }

        private PdfObject? ResolveNamedDestination(PdfObject nameObj, PdfDocument doc)
        {
            var catalog = doc.GetCatalog().GetPdfObject();
            if (catalog == null) return null;

            var dests = catalog.GetAsDictionary(PdfName.Dests);
            if (dests != null)
            {
                var hit = ResolveInDestsDict(dests, nameObj);
                if (hit != null) return hit;
            }

            var names = catalog.GetAsDictionary(PdfName.Names);
            var nameTree = names?.GetAsDictionary(PdfName.Dests);
            if (nameTree != null)
                return ResolveInNameTree(nameTree, nameObj);

            return null;
        }

        private PdfObject? ResolveInDestsDict(PdfDictionary dests, PdfObject nameObj)
        {
            if (dests == null || nameObj == null) return null;
            if (nameObj is PdfName nm)
                return dests.Get(nm);
            if (nameObj is PdfString str)
                return dests.Get(new PdfName(str.ToUnicodeString()));
            return null;
        }

        private PdfObject? ResolveInNameTree(PdfDictionary node, PdfObject nameObj)
        {
            if (node == null || nameObj == null) return null;
            string key = NameKey(nameObj);
            if (node.ContainsKey(PdfName.Names))
            {
                var names = node.GetAsArray(PdfName.Names);
                if (names != null)
                {
                    for (int i = 0; i + 1 < names.Size(); i += 2)
                    {
                        var nm = names.Get(i);
                        var val = names.Get(i + 1);
                        if (NameKey(nm).Equals(key, StringComparison.Ordinal))
                            return val;
                    }
                }
            }
            if (node.ContainsKey(PdfName.Kids))
            {
                var kids = node.GetAsArray(PdfName.Kids);
                if (kids != null)
                {
                    for (int i = 0; i < kids.Size(); i++)
                    {
                        var kid = kids.GetAsDictionary(i);
                        var hit = ResolveInNameTree(kid, nameObj);
                        if (hit != null) return hit;
                    }
                }
            }
            return null;
        }

        private string NameKey(PdfObject obj)
        {
            if (obj is PdfName nm) return nm.GetValue();
            if (obj is PdfString str) return str.ToUnicodeString();
            return obj?.ToString() ?? "";
        }
    }
}
