using System.Collections.Generic;
using System.Linq;
using FilterPDF.Models;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Navigation;

namespace FilterPDF.Utils
{
    /// <summary>
    /// Extrai bookmarks usando a mesma lógica do comando fetch-bookmark-titles:
    /// - Outlines API
    /// - fallback /Outlines raw
    /// - resolve páginas via /Dest, /A e NameTree (/Dests, /Names)
    /// </summary>
    public static class BookmarkExtractor
    {
        public static List<BookmarkItem> Extract(PdfDocument doc)
        {
            var items = new List<BookmarkItem>();
            if (doc == null) return items;

            try
            {
                var root = doc.GetOutlines(false);
                if (root != null && root.GetAllChildren().Count > 0)
                {
                    foreach (var child in root.GetAllChildren())
                        items.Add(BuildOutline(child, 0, doc));
                    return items;
                }
            }
            catch { }

            try
            {
                var catalog = doc.GetCatalog().GetPdfObject();
                var outlines = catalog?.GetAsDictionary(PdfName.Outlines);
                var first = outlines?.GetAsDictionary(PdfName.First);
                if (first != null)
                    items = WalkOutlineDict(first, 0, doc);
            }
            catch { }

            return items;
        }

        private static BookmarkItem BuildOutline(PdfOutline outline, int level, PdfDocument doc)
        {
            var dest = outline.GetDestination();
            int page = ResolveDestinationPage(dest, doc);
            if (page == 0)
            {
                var destObj = dest?.GetPdfObject();
                page = ResolveDestToPage(destObj, doc);
            }

            var item = new BookmarkItem
            {
                Title = outline.GetTitle() ?? "",
                Level = level,
                IsOpen = outline.IsOpen(),
                Destination = new BookmarkDestination { PageNumber = page, Type = "Destination" },
                Children = new List<BookmarkItem>()
            };

            foreach (var child in outline.GetAllChildren())
                item.Children.Add(BuildOutline(child, level + 1, doc));

            return item;
        }

        private static List<BookmarkItem> WalkOutlineDict(PdfDictionary item, int level, PdfDocument doc)
        {
            var items = new List<BookmarkItem>();
            var current = item;
            while (current != null)
            {
                var title = current.GetAsString(PdfName.Title)?.ToUnicodeString() ?? "";
                PdfObject destObj = current.Get(PdfName.Dest);
                if (destObj == null)
                {
                    var action = current.GetAsDictionary(PdfName.A);
                    var actionType = action?.GetAsName(PdfName.S);
                    if (action != null && (actionType == null || PdfName.GoTo.Equals(actionType)))
                        destObj = action.Get(PdfName.D);
                }

                int page = ResolveDestToPage(destObj, doc);

                var entry = new BookmarkItem
                {
                    Title = title,
                    Level = level,
                    IsOpen = false,
                    Destination = new BookmarkDestination { PageNumber = page, Type = "Destination" },
                    Children = new List<BookmarkItem>()
                };

                var first = current.GetAsDictionary(PdfName.First);
                if (first != null)
                    entry.Children = WalkOutlineDict(first, level + 1, doc);

                items.Add(entry);
                current = current.GetAsDictionary(PdfName.Next);
            }

            return items;
        }

        private static int ResolveDestinationPage(PdfDestination destination, PdfDocument doc)
        {
            if (destination == null || doc == null) return 0;
            try
            {
                var nameTree = doc.GetCatalog().GetNameTree(PdfName.Dests);
                var names = nameTree?.GetNames();
                var destPage = destination.GetDestinationPage(names);
                if (destPage is PdfDictionary dict)
                    return doc.GetPageNumber(dict);
            }
            catch { }
            return 0;
        }

        private static int ResolveDestToPage(PdfObject destObj, PdfDocument doc)
        {
            if (destObj == null || doc == null) return 0;

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

        private static int ResolveArrayDest(PdfArray arr, PdfDocument doc)
        {
            if (arr == null || arr.Size() == 0 || doc == null) return 0;
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

        private static PdfObject ResolveNamedDestination(PdfObject nameObj, PdfDocument doc)
        {
            if (doc == null) return null;
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

        private static PdfObject ResolveInDestsDict(PdfDictionary dests, PdfObject nameObj)
        {
            if (dests == null || nameObj == null) return null;
            if (nameObj is PdfName nm)
                return dests.Get(nm);
            if (nameObj is PdfString str)
                return dests.Get(new PdfName(str.ToUnicodeString()));
            return null;
        }

        private static PdfObject ResolveInNameTree(PdfDictionary node, PdfObject nameObj)
        {
            if (node.ContainsKey(PdfName.Names))
            {
                var names = node.GetAsArray(PdfName.Names);
                if (names == null) return null;
                for (int i = 0; i < names.Size(); i += 2)
                {
                    var key = names.Get(i);
                    var val = names.Get(i + 1);
                    if (key == null) continue;
                    if (nameObj is PdfName nm && key is PdfName nk && nk.Equals(nm)) return val;
                    if (nameObj is PdfString ns && key is PdfString sk && sk.ToUnicodeString() == ns.ToUnicodeString())
                        return val;
                }
            }

            var kids = node.GetAsArray(PdfName.Kids);
            if (kids == null) return null;
            foreach (var kid in kids)
            {
                var dict = kid as PdfDictionary;
                if (dict == null) continue;
                var res = ResolveInNameTree(dict, nameObj);
                if (res != null) return res;
            }
            return null;
        }
    }
}
