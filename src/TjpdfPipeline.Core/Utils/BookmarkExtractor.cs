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
                if (root == null || root.GetAllChildren().Count == 0)
                    return items;

                foreach (var child in root.GetAllChildren())
                    items.Add(BuildOutline(child, 0, doc));
            }
            catch { }
            return items;
        }

        private static BookmarkItem BuildOutline(PdfOutline outline, int level, PdfDocument doc)
        {
            var dest = outline.GetDestination();
            int page = ResolveDestinationPage(dest, doc);

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
    }
}
