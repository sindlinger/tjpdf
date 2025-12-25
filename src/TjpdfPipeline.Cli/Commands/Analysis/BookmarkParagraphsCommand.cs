using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FilterPDF;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Mostra slices do primeiro e ultimo paragrafo por bookmark.
    /// Uso:
    ///   tjpdf-cli bookmark-paragraphs --input-file <pdf> [--bookmark <texto>] [--all] [--first] [--last] [--json]
    /// </summary>
    public class BookmarkParagraphsCommand : Command
    {
        public override string Name => "bookmark-paragraphs";
        public override string Description => "Slices do primeiro/ultimo paragrafo por bookmark";

        public override void Execute(string[] args)
        {
            string? inputFile = null;
            bool asJson = false;
            bool all = false;
            bool wantFirst = false;
            bool wantLast = false;
            var filters = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--input-file":
                        if (i + 1 < args.Length) inputFile = args[++i];
                        break;
                    case "--bookmark":
                        if (i + 1 < args.Length)
                        {
                            var raw = args[++i];
                            if (!string.IsNullOrWhiteSpace(raw))
                                filters.AddRange(raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                    .Select(s => s.Trim())
                                                    .Where(s => s.Length > 0));
                        }
                        break;
                    case "--all":
                        all = true;
                        break;
                    case "--first":
                        wantFirst = true;
                        break;
                    case "--last":
                        wantLast = true;
                        break;
                    case "--json":
                        asJson = true;
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

            if (!wantFirst && !wantLast)
            {
                wantFirst = true;
                wantLast = true;
            }

            if (string.IsNullOrWhiteSpace(inputFile) || !File.Exists(inputFile))
            {
                ShowHelp();
                return;
            }

            var analysis = new PDFAnalyzer(inputFile).AnalyzeFull();
            if (analysis?.Pages == null || analysis.Pages.Count == 0)
            {
                Console.WriteLine("PDF sem paginas analisadas.");
                return;
            }
            var docs = BuildBookmarkDocs(analysis);

            if (docs.Count == 0)
            {
                Console.WriteLine("Sem bookmarks.");
                return;
            }

            if (!all && filters.Count == 0)
                all = true;

            var filtered = docs;
            if (!all && filters.Count > 0)
            {
                filtered = docs.Where(d => MatchesFilter(d, filters)).ToList();
            }

            if (asJson)
            {
                var payload = new
                {
                    file = Path.GetFileName(inputFile),
                    bookmarksFound = docs.Count,
                    documents = filtered.Select(d => BuildSlicePayload(d, analysis, wantFirst, wantLast)).ToList()
                };
                Console.WriteLine(JsonConvert.SerializeObject(payload, Formatting.Indented));
                return;
            }

            Console.WriteLine($"Arquivo: {Path.GetFileName(inputFile)}");
            foreach (var d in filtered)
            {
                Console.WriteLine($"- {d.TitleSanitized} (p{d.StartPage}-{d.EndPage})");
                var slice = BuildSlicePayload(d, analysis, wantFirst, wantLast);
                if (wantFirst)
                    Console.WriteLine($"  first: {slice.first_paragraph}");
                if (wantLast)
                    Console.WriteLine($"  last: {slice.last_paragraph}");
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli bookmark-paragraphs --input-file <pdf> [--bookmark <texto>] [--all] [--first] [--last] [--json]");
            Console.WriteLine("Mostra o primeiro e/ou ultimo paragrafo de cada bookmark.");
        }

        private bool MatchesFilter(BookmarkDoc d, List<string> filters)
        {
            if (filters == null || filters.Count == 0) return true;
            var raw = RemoveDiacritics(d.TitleRaw).ToLowerInvariant();
            var sanitized = RemoveDiacritics(d.TitleSanitized).ToLowerInvariant();
            foreach (var f in filters)
            {
                var ff = RemoveDiacritics(f).ToLowerInvariant();
                if (raw.Contains(ff) || sanitized.Contains(ff))
                    return true;
            }
            return false;
        }

        private SlicePayload BuildSlicePayload(BookmarkDoc d, PDFAnalysisResult analysis, bool wantFirst, bool wantLast)
        {
            var words = new List<Dictionary<string, object>>();
            int total = analysis.Pages?.Count ?? 0;
            int startPage = Math.Max(1, Math.Min(d.StartPage, total));
            int endPage = Math.Max(startPage, Math.Min(d.EndPage, total));

            for (int p = startPage; p <= endPage; p++)
            {
                var page = analysis.Pages != null && p - 1 < analysis.Pages.Count ? analysis.Pages[p - 1] : null;
                if (page?.TextInfo?.Words == null) continue;
                foreach (var w in page.TextInfo.Words)
                {
                    words.Add(new Dictionary<string, object>
                    {
                        ["text"] = w.Text,
                        ["page"] = p,
                        ["x0"] = w.X0,
                        ["y0"] = w.Y0,
                        ["x1"] = w.X1,
                        ["y1"] = w.Y1,
                        ["nx0"] = w.NormX0,
                        ["ny0"] = w.NormY0,
                        ["nx1"] = w.NormX1,
                        ["ny1"] = w.NormY1
                    });
                }
            }

            var paragraphs = BuildParagraphsFromWords(words)
                .OrderBy(p => p.Page)
                .ThenByDescending(p => p.Ny0)
                .ToList();

            var first = paragraphs.FirstOrDefault();
            var last = paragraphs.LastOrDefault();

            string firstText = "";
            string lastText = "";

            if (wantFirst && first != null)
            {
                var header = analysis.Pages != null && startPage - 1 < analysis.Pages.Count
                    ? GetHeaderText(analysis.Pages[startPage - 1])
                    : "";
                firstText = string.IsNullOrWhiteSpace(header) ? first.Text : $"{header}\n{first.Text}";
            }

            if (wantLast && last != null)
            {
                var footer = analysis.Pages != null && endPage - 1 < analysis.Pages.Count
                    ? GetFooterText(analysis.Pages[endPage - 1])
                    : "";
                lastText = string.IsNullOrWhiteSpace(footer) ? last.Text : $"{last.Text}\n{footer}";
            }

            return new SlicePayload
            {
                title_raw = d.TitleRaw,
                title_sanitized = d.TitleSanitized,
                start_page = startPage,
                end_page = endPage,
                first_paragraph = firstText,
                last_paragraph = lastText
            };
        }

        private string GetHeaderText(PageAnalysis page)
        {
            if (page?.TextInfo?.Headers == null || page.TextInfo.Headers.Count == 0) return "";
            return string.Join("\n", page.TextInfo.Headers.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct());
        }

        private string GetFooterText(PageAnalysis page)
        {
            if (page?.TextInfo?.Footers == null || page.TextInfo.Footers.Count == 0) return "";
            return string.Join("\n", page.TextInfo.Footers.Where(f => !string.IsNullOrWhiteSpace(f)).Distinct());
        }

        private List<ParagraphObj> BuildParagraphsFromWords(List<Dictionary<string, object>> words)
        {
            var stopWords = new HashSet<string>(new[] { "", "-", "/", "pg", "se", "em", "de", "da", "do", "das", "dos", "a", "o", "e", "que", "para", "com", "no", "na", "as", "os", "ao", "à", "até", "por", "uma", "um", "§", "art", "artigo" });
            var pages = new Dictionary<int, List<Dictionary<string, object>>>();
            foreach (var w in words)
            {
                int p = Convert.ToInt32(w["page"]);
                if (!pages.ContainsKey(p)) pages[p] = new List<Dictionary<string, object>>();
                pages[p].Add(w);
            }

            var paras = new List<ParagraphObj>();
            foreach (var kv in pages.OrderBy(k => k.Key))
            {
                var clusters = new List<List<Dictionary<string, object>>>();
                foreach (var w in kv.Value.OrderByDescending(w => Convert.ToDouble(w["y0"])))
                {
                    double y = Convert.ToDouble(w["y0"]);
                    bool placed = false;
                    foreach (var c in clusters)
                    {
                        double gy = c.Average(x => Convert.ToDouble(x["y0"]));
                        if (Math.Abs(gy - y) <= 1.5)
                        {
                            c.Add(w); placed = true; break;
                        }
                    }
                    if (!placed) clusters.Add(new List<Dictionary<string, object>> { w });
                }

                foreach (var cl in clusters)
                {
                    var line = cl.OrderBy(w => Convert.ToDouble(w["x0"])).ToList();
                    string text = RebuildLine(line);
                    text = DespaceIfNeeded(text);
                    var tokens = text.Split(' ')
                                     .Select(t => Regex.Replace(t, @"[^\w\d]+", "", RegexOptions.None).ToLowerInvariant())
                                     .Where(t => t.Length > 0 && !stopWords.Contains(t))
                                     .ToList();
                    paras.Add(new ParagraphObj
                    {
                        Page = kv.Key,
                        Ny0 = cl.Min(w => Convert.ToDouble(w["ny0"])),
                        Ny1 = cl.Max(w => Convert.ToDouble(w["ny1"])),
                        NX0 = cl.Min(w => Convert.ToDouble(w["nx0"])),
                        NX1 = cl.Max(w => Convert.ToDouble(w["nx1"])),
                        Text = text,
                        Tokens = tokens
                    });
                }
            }

            return paras;
        }

        private string RebuildLine(List<Dictionary<string, object>> ws, double spaceFactor = 0.6)
        {
            if (ws == null || ws.Count == 0) return "";
            var sorted = ws.OrderBy(w => Convert.ToDouble(w["x0"])).ToList();
            string result = sorted[0].TryGetValue("text", out var firstVal) ? (firstVal?.ToString() ?? "") : "";
            double avgW = sorted.Average(w => Convert.ToDouble(w["x1"]) - Convert.ToDouble(w["x0"]));
            for (int i = 1; i < sorted.Count; i++)
            {
                double gap = Convert.ToDouble(sorted[i]["x0"]) - Convert.ToDouble(sorted[i - 1]["x1"]);
                int spaces = (gap > avgW * 0.2) ? Math.Max(1, (int)(gap / (avgW * spaceFactor))) : 0;
                var token = sorted[i].TryGetValue("text", out var tv) ? (tv?.ToString() ?? "") : "";
                result += new string(' ', spaces) + token;
            }
            return result;
        }

        private string DespaceIfNeeded(string text)
        {
            var tokens = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return text;
            int single = tokens.Count(t => t.Length == 1);
            if ((double)single / tokens.Length > 0.5)
                return Regex.Replace(text, @"\s+", "");
            return text;
        }

        private List<BookmarkDoc> BuildBookmarkDocs(PDFAnalysisResult analysis)
        {
            var docs = new List<BookmarkDoc>();
            if (analysis?.BookmarksFlat == null || analysis.BookmarksFlat.Count == 0)
                return docs;

            var items = analysis.BookmarksFlat
                .Where(b => b.PageNumber > 0)
                .OrderBy(b => b.PageNumber)
                .ThenBy(b => b.Level)
                .ToList();

            if (items.Count == 0) return docs;

            for (int i = 0; i < items.Count; i++)
            {
                int start = items[i].PageNumber;
                int end;
                if (i + 1 < items.Count)
                {
                    int nextStart = items[i + 1].PageNumber;
                    end = nextStart > start ? nextStart - 1 : start;
                }
                else
                {
                    end = analysis.DocumentInfo.TotalPages;
                }

                var raw = items[i].Title ?? "";
                docs.Add(new BookmarkDoc
                {
                    TitleRaw = raw,
                    TitleSanitized = SanitizeBookmarkTitle(raw),
                    StartPage = start,
                    EndPage = end
                });
            }

            return docs;
        }

        private string SanitizeBookmarkTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            var t = raw.Replace('_', ' ');
            t = Regex.Replace(t, @"\s+", " ").Trim();
            t = Regex.Replace(t, @"\([\dA-Za-z]{4,}\)$", "").Trim();
            t = Regex.Replace(t, @"^\d+\s*-\s*", "");
            t = Regex.Replace(t, @"\s*-\s*SEI.*$", "", RegexOptions.IgnoreCase);
            var withoutTail = Regex.Replace(t, @"\s*[-–]?\s*(n[º°o]?|no)?\s*\d{1,8}(?:[./-]\d+)?\s*$", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(withoutTail) && withoutTail.Length >= 3)
                t = withoutTail;
            return t.Trim();
        }

        private string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var normalized = text.Normalize(NormalizationForm.FormD);
            var chars = normalized.Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark);
            return new string(chars.ToArray()).Normalize(NormalizationForm.FormC);
        }

        private class ParagraphObj
        {
            public int Page { get; set; }
            public double Ny0 { get; set; }
            public double Ny1 { get; set; }
            public double NX0 { get; set; }
            public double NX1 { get; set; }
            public string Text { get; set; } = "";
            public List<string> Tokens { get; set; } = new List<string>();
        }

        private class BookmarkDoc
        {
            public string TitleRaw { get; set; } = "";
            public string TitleSanitized { get; set; } = "";
            public int StartPage { get; set; }
            public int EndPage { get; set; }
        }

        private class SlicePayload
        {
            public string title_raw { get; set; } = "";
            public string title_sanitized { get; set; } = "";
            public int start_page { get; set; }
            public int end_page { get; set; }
            public string first_paragraph { get; set; } = "";
            public string last_paragraph { get; set; } = "";
        }
    }
}
