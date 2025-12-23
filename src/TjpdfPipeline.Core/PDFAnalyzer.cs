using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FilterPDF.Strategies;
using FilterPDF.Utils;
using iText.Forms;
using iText.Forms.Fields;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using iText.Kernel.Pdf.Navigation;
using iText.Signatures;

namespace FilterPDF
{
    /// <summary>
    /// Analisador completo 100% iText7 (sem iTextSharp).
    /// Foca em texto, fontes, recursos, anotações, formulários, outlines e metadados.
    /// </summary>
    public class PDFAnalyzer
    {
        private readonly string pdfPath;
        private readonly bool forceLegacyText;
        private PdfDocument? doc;
        private readonly bool ownsDoc;

        public PDFAnalyzer(string pdfPath)
        {
            this.pdfPath = pdfPath;
            this.forceLegacyText = Environment.GetEnvironmentVariable("FPDF_TEXT_LEGACY") == "1";
            Console.WriteLine($"    [PDFAnalyzer] Opening PDF: {System.IO.Path.GetFileName(pdfPath)}");

            this.doc = PdfAccessManager7.GetDocument(pdfPath);
            this.ownsDoc = false; // gerenciado pelo cache
            Console.WriteLine($"    [PDFAnalyzer] PDF opened successfully. Pages: {doc.GetNumberOfPages()}");
        }

        public PDFAnalyzer(string pdfPath, PdfDocument existingDoc)
        {
            this.pdfPath = pdfPath;
            this.forceLegacyText = Environment.GetEnvironmentVariable("FPDF_TEXT_LEGACY") == "1";
            this.doc = existingDoc;
            this.ownsDoc = false;
        }

        public PDFAnalysisResult AnalyzeFull()
        {
            var result = new PDFAnalysisResult
            {
                FilePath = pdfPath,
                FileSize = new FileInfo(pdfPath).Length,
                AnalysisDate = DateTime.Now
            };

            try
            {
                result.Metadata = ExtractMetadata();
                result.XMPMetadata = ExtractXMPMetadata();
                result.DocumentInfo = ExtractDocumentInfo();
                result.Pages = AnalyzePages();
                result.Security = ExtractSecurityInfo();
                result.Resources = ExtractResources(result.Pages);
                result.Statistics = CalculateStatistics(result);
                result.Accessibility = ExtractAccessibilityInfo();
                result.Layers = ExtractOptionalContentGroups();
                result.Signatures = ExtractDigitalSignatures();
                result.ColorProfiles = ExtractColorProfiles();
                result.ModificationDates = BuildModificationDates(result);
                result.Bookmarks = ExtractBookmarkStructure();
                result.BookmarksFlat = FlattenBookmarks(result.Bookmarks);
                // PDFA compliance básica: se tagged e sem encriptação simples heurística
                result.PDFACompliance = new PDFAInfo { IsPDFA = doc?.IsTagged() ?? false };
                result.Multimedia = new List<MultimediaInfo>(); // não implementado ainda
            }
            finally
            {
                if (ownsDoc && doc != null && !doc.IsClosed())
                    doc.Close();
            }

            return result;
        }

        private Metadata ExtractMetadata()
        {
            var meta = new Metadata();
            if (doc == null) return meta;

            var info = doc.GetDocumentInfo();
            meta.Title = info.GetTitle();
            meta.Author = info.GetAuthor();
            meta.Subject = info.GetSubject();
            meta.Keywords = info.GetKeywords();
            meta.Creator = info.GetCreator();
            meta.Producer = info.GetProducer();
            meta.CreationDate = ParsePDFDate(info.GetMoreInfo(PdfName.CreationDate.GetValue()));
            meta.ModificationDate = ParsePDFDate(info.GetMoreInfo(PdfName.ModDate.GetValue()));
            meta.PDFVersion = doc.GetPdfVersion().ToString();
            meta.IsTagged = doc.IsTagged();
            return meta;
        }

        private DocumentInfo ExtractDocumentInfo()
        {
            if (doc == null) return new DocumentInfo();
            var reader = doc.GetReader();
            return new DocumentInfo
            {
                TotalPages = doc.GetNumberOfPages(),
                IsEncrypted = reader.IsEncrypted(),
                IsLinearized = false, // iText7 não expõe diretamente
                HasAcroForm = PdfAcroForm.GetAcroForm(doc, false) != null,
                HasXFA = false,
                FileStructure = reader.HasRebuiltXref() ? "Rebuilt" : "Original"
            };
        }

        private List<PageAnalysis> AnalyzePages()
        {
            var pages = new List<PageAnalysis>();
            if (doc == null) return pages;

            int total = doc.GetNumberOfPages();
            for (int i = 1; i <= total; i++)
            {
                var page = new PageAnalysis
                {
                    PageNumber = i,
                    Size = GetPageSize(i),
                    Rotation = doc.GetPage(i).GetRotation(),
                    TextInfo = AnalyzePageText(i),
                    Resources = AnalyzePageResources(i),
                    Annotations = ExtractAnnotations(i)
                };

                page.FontInfo = page.TextInfo.Fonts;
                DetectHeadersFooters(page);
                DetectDocumentReferences(page);
                pages.Add(page);
            }
            return pages;
        }

        private PageSize GetPageSize(int pageNum)
        {
            var rect = doc!.GetPage(pageNum).GetPageSize();
            return new PageSize
            {
                Width = rect.GetWidth(),
                Height = rect.GetHeight(),
                WidthPoints = rect.GetWidth(),
                HeightPoints = rect.GetHeight(),
                WidthInches = rect.GetWidth() / 72f,
                HeightInches = rect.GetHeight() / 72f,
                WidthMM = rect.GetWidth() * 0.352778f,
                HeightMM = rect.GetHeight() * 0.352778f
            };
        }

        private TextInfo AnalyzePageText(int pageNum)
        {
            string text = string.Empty;
            if (doc != null && !forceLegacyText)
            {
                try { text = PdfTextExtractor.GetTextFromPage(doc.GetPage(pageNum)); }
                catch (Exception ex) { Console.Error.WriteLine($"[WARN] text extract failed p{pageNum}: {ex.Message}"); }
            }

            var textInfo = new TextInfo
            {
                CharacterCount = text.Length,
                WordCount = CountWords(text),
                LineCount = text.Split('\n').Length,
                Languages = DetectLanguages(text),
                HasTables = false,
                HasColumns = false,
                AverageLineLength = CalculateAverageLineLength(text),
                PageText = text,
                PageTextRaw = "",
                PageTextRawReady = false
            };

            textInfo.Fonts = ExtractAllPageFontsWithSizes(pageNum);

            float pageWidth = 0;
            float pageHeight = 0;
            var rect = doc?.GetPage(pageNum).GetPageSize();
            if (rect != null)
            {
                pageWidth = rect.GetWidth();
                pageHeight = rect.GetHeight();
            }

            try
            {
                if (doc != null)
                {
                    var collector = new IText7LineCollector();
                    var processor = new PdfCanvasProcessor(collector);
                    processor.ProcessPageContent(doc.GetPage(pageNum));
                    textInfo.Lines = collector.GetLines();
                    if (pageWidth > 0 && pageHeight > 0)
                    {
                        foreach (var l in textInfo.Lines)
                        {
                            l.NormX0 = l.X0 / pageWidth;
                            l.NormX1 = l.X1 / pageWidth;
                            l.NormY0 = l.Y0 / pageHeight;
                            l.NormY1 = l.Y1 / pageHeight;
                        }
                    }
                }
            }
            catch { }

            try
            {
                if (doc != null)
                {
                    var wordCollector = new IText7WordCollector();
                    var processor = new PdfCanvasProcessor(wordCollector);
                    processor.ProcessPageContent(doc.GetPage(pageNum));
                    textInfo.Words = wordCollector.GetWords();
                    if (textInfo.Words?.Count > 0)
                        textInfo.WordCount = textInfo.Words.Count;

                    if (pageWidth > 0 && pageHeight > 0)
                    {
                        foreach (var w in textInfo.Words)
                        {
                            w.NormX0 = w.X0 / pageWidth;
                            w.NormX1 = w.X1 / pageWidth;
                            w.NormY0 = w.Y0 / pageHeight;
                            w.NormY1 = w.Y1 / pageHeight;
                        }
                    }

                    var tabulated = BuildPageTextTabulated(textInfo.Words);
                    if (!string.IsNullOrWhiteSpace(tabulated))
                    {
                        textInfo.PageTextRaw = tabulated;
                        textInfo.PageTextRawReady = true;
                        textInfo.PageText = tabulated;
                        textInfo.CharacterCount = tabulated.Length;
                        textInfo.LineCount = tabulated.Split('\n').Length;
                        textInfo.AverageLineLength = CalculateAverageLineLength(tabulated);
                        textInfo.Languages = DetectLanguages(tabulated);
                    }
                }
            }
            catch { }

            if (!textInfo.PageTextRawReady)
            {
                // fallback: mantém o texto extraído, mesmo sem tabulação refinada
                textInfo.PageText = textInfo.PageText ?? "";
                textInfo.CharacterCount = textInfo.PageText.Length;
                textInfo.LineCount = textInfo.PageText.Split('\n').Length;
                textInfo.AverageLineLength = CalculateAverageLineLength(textInfo.PageText);
                textInfo.Languages = DetectLanguages(textInfo.PageText);
            }

            return textInfo;
        }

        private string BuildPageTextTabulated(List<WordInfo> words, float yTolerance = 1.5f)
        {
            if (words == null || words.Count == 0) return string.Empty;

            var lines = new List<List<WordInfo>>();
            foreach (var w in words.OrderByDescending(w => w.Y0))
            {
                bool placed = false;
                foreach (var line in lines)
                {
                    double ly = line.Average(x => x.Y0);
                    if (Math.Abs(ly - w.Y0) <= yTolerance)
                    {
                        line.Add(w);
                        placed = true;
                        break;
                    }
                }
                if (!placed)
                    lines.Add(new List<WordInfo> { w });
            }

            var sb = new System.Text.StringBuilder();
            foreach (var line in lines)
            {
                var ordered = line.OrderBy(w => w.X0).ToList();
                if (ordered.Count == 0) continue;

                var charWidths = ordered
                    .Select(w => w.Text?.Length > 0 ? (double)(w.X1 - w.X0) / w.Text.Length : 0.0)
                    .Where(v => v > 0)
                    .ToList();

                double avgChar = Median(charWidths);
                if (avgChar <= 0) avgChar = ordered.Average(w => w.X1 - w.X0);
                if (avgChar <= 0) avgChar = 1.0;

                int singleCount = ordered.Count(w => (w.Text ?? "").Trim().Length == 1);
                double singleRatio = ordered.Count > 0 ? (double)singleCount / ordered.Count : 0.0;
                double minGapForSpace = 0.0;
                if (singleRatio >= 0.6 && avgChar > 0 && ordered.Count > 2)
                {
                    var ratios = new List<double>();
                    for (int i = 1; i < ordered.Count; i++)
                    {
                        double gap = ordered[i].X0 - ordered[i - 1].X1;
                        if (gap > 0) ratios.Add(gap / avgChar);
                    }
                    if (ratios.Count > 0)
                    {
                        ratios.Sort();
                        double med = ratios[ratios.Count / 2];
                        double dynamic = med * 4.0;
                        if (dynamic < 0.02) dynamic = 0.02;
                        if (dynamic > 1.2) dynamic = 1.2;
                        minGapForSpace = dynamic * avgChar;
                    }
                }

                var lineText = new System.Text.StringBuilder();
                WordInfo prev = null;
                foreach (var w in ordered)
                {
                    if (prev != null)
                    {
                        double gap = w.X0 - prev.X1;
                        if (gap > 0)
                        {
                            if (minGapForSpace <= 0 || gap >= minGapForSpace)
                            {
                                int spaces = (int)Math.Round(gap / avgChar);
                                if (spaces < 1) spaces = 1;
                                if (spaces > 40) spaces = 40;
                                lineText.Append(' ', spaces);
                            }
                        }
                    }
                    lineText.Append(w.Text);
                    prev = w;
                }

                var rendered = lineText.ToString().TrimEnd();
                if (rendered.Length == 0) continue;
                if (singleRatio >= 0.6)
                    rendered = FilterPDF.TjpbDespachoExtractor.Utils.TextUtils.CollapseSpacedLettersText(rendered);
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(rendered);
            }

            return sb.ToString();
        }

        private double Median(List<double> values)
        {
            if (values == null || values.Count == 0) return 0;
            values.Sort();
            int mid = values.Count / 2;
            return values.Count % 2 == 0 ? (values[mid - 1] + values[mid]) / 2.0 : values[mid];
        }


        private List<FontInfo> ExtractAllPageFontsWithSizes(int pageNum)
        {
            var fonts = new List<FontInfo>();
            var fontSizeMap = new Dictionary<string, HashSet<float>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var page = doc!.GetPage(pageNum);
                var collector = new FontSizeCollector7(fontSizeMap);
                var processor = new PdfCanvasProcessor(collector);
                processor.ProcessPageContent(page);

                var resources = page.GetResources();
                var fontDict = resources?.GetResource(PdfName.Font) as PdfDictionary;
                if (fontDict != null)
                {
                    foreach (var fontKey in fontDict.KeySet())
                    {
                        var fontObj = fontDict.GetAsDictionary(fontKey);
                        if (fontObj == null) continue;

                        var baseFont = fontObj.GetAsName(PdfName.BaseFont) ?? fontObj.GetAsName(PdfName.FontName);
                        string fontName = baseFont?.ToString() ?? fontKey.ToString();
                        fontName = FontNameFixer.Fix(fontName);

                        var sizes = new HashSet<float>();
                        string fontKeyStr = fontKey.ToString();
                        if (fontSizeMap.ContainsKey(fontKeyStr))
                            sizes.UnionWith(fontSizeMap[fontKeyStr]);
                        if (sizes.Count == 0) sizes.Add(12f);

                        string fontType = "Type1";
                        var subtype = fontObj.GetAsName(PdfName.Subtype);
                        if (subtype != null)
                        {
                            var subtypeStr = subtype.ToString();
                            if (subtypeStr.Contains("TrueType")) fontType = "TrueType";
                            else if (subtypeStr.Contains("Type0")) fontType = "Type0";
                            else if (subtypeStr.Contains("CIDFont")) fontType = "CIDFont";
                            else if (subtypeStr.Contains("Type3")) fontType = "Type3";
                        }

                        bool isBold = fontName.ToLower().Contains("bold") || fontName.ToLower().Contains("black");
                        bool isItalic = fontName.ToLower().Contains("italic") || fontName.ToLower().Contains("oblique");
                        bool isMonospace = fontName.Contains("Courier") || fontName.Contains("Mono");
                        bool isSerif = fontName.Contains("Times") || fontName.Contains("Serif");
                        bool isSansSerif = fontName.Contains("Arial") || fontName.Contains("Helvetica") || fontName.Contains("Sans");

                        fonts.Add(new FontInfo
                        {
                            Name = fontName,
                            BaseFont = fontName,
                            FontType = fontType,
                            Size = sizes.First(),
                            Style = GetStyleFromName(fontName),
                            IsEmbedded = false,
                            FontSizes = sizes.ToList(),
                            IsBold = isBold,
                            IsItalic = isItalic,
                            IsMonospace = isMonospace,
                            IsSerif = isSerif,
                            IsSansSerif = isSansSerif
                        });
                    }
                }
            }
            catch { }
            return fonts;
        }

        private string GetStyleFromName(string fontName)
        {
            if (fontName.Contains("Bold") && fontName.Contains("Italic")) return "BoldItalic";
            if (fontName.Contains("Bold")) return "Bold";
            if (fontName.Contains("Italic") || fontName.Contains("Oblique")) return "Italic";
            return "Regular";
        }

        private PageResources AnalyzePageResources(int pageNum)
        {
            var result = new PageResources();
            if (doc == null) return result;

            var page = doc.GetPage(pageNum);
            var resources = page.GetResources();
            if (resources == null) return result;

            var xobjects = resources.GetResource(PdfName.XObject) as PdfDictionary;
            if (xobjects != null)
            {
                foreach (var key in xobjects.KeySet())
                {
                    var obj = xobjects.Get(key);
                    if (obj == null) continue;
                    var stream = obj is PdfIndirectReference ir ? ir.GetRefersTo() as PdfStream : obj as PdfStream;
                    if (stream == null) continue;
                    var subtype = stream.GetAsName(PdfName.Subtype);
                    if (PdfName.Image.Equals(subtype))
                    {
                        result.Images.Add(ExtractImageInfo(stream, key.ToString()));
                    }
                }
            }

            var fonts = resources.GetResource(PdfName.Font) as PdfDictionary;
            if (fonts != null) result.FontCount = fonts.KeySet().Count;

            result.FormFields = ExtractPageFormFields(pageNum);
            return result;
        }

        private List<Annotation> ExtractAnnotations(int pageNum)
        {
            var list = new List<Annotation>();
            if (doc == null) return list;
            var page = doc.GetPage(pageNum);
            foreach (var annot in page.GetAnnotations())
            {
                var rect = annot.GetRectangle() ?? new iText.Kernel.Pdf.PdfArray(new float[] { 0, 0, 0, 0 });
                var ann = new Annotation
                {
                    Type = annot.GetSubtype()?.ToString() ?? "",
                    Contents = annot.GetContents()?.ToString() ?? "",
                    Author = annot.GetTitle()?.ToString() ?? "",
                    Subject = annot.GetPdfObject()?.GetAsString(PdfName.Subj)?.ToString() ?? "",
                    ModificationDate = ParsePDFDate(annot.GetPdfObject()?.GetAsString(PdfName.M)?.ToString()),
                    X = rect.GetAsNumber(0)?.FloatValue() ?? 0,
                    Y = rect.GetAsNumber(1)?.FloatValue() ?? 0
                };

                // File attachment info
                if (PdfName.FileAttachment.Equals(annot.GetSubtype()))
                {
                    var fs = annot.GetPdfObject()?.GetAsDictionary(PdfName.FS);
                    if (fs != null)
                    {
                        ann.FileName = fs.GetAsString(PdfName.F)?.ToString() ?? "";
                        var ef = fs.GetAsDictionary(PdfName.EF);
                        var fstream = ef?.GetAsStream(PdfName.F);
                        if (fstream != null)
                        {
                            try { ann.FileSize = fstream.GetLength(); } catch { }
                        }
                    }
                }

                list.Add(ann);
            }
            return list;
        }

        private void DetectHeadersFooters(PageAnalysis page)
        {
            if (doc == null) return;
            float pageHeight = doc.GetPage(page.PageNumber).GetPageSize().GetHeight();

            try
            {
                var headerStrategy = new Strategies.AdvancedHeaderFooterStrategy7(true, pageHeight);
                var procHeader = new PdfCanvasProcessor(headerStrategy);
                procHeader.ProcessPageContent(doc.GetPage(page.PageNumber));
                page.Headers = ParseHeaderFooterText(headerStrategy.GetResultantText());

                var footerStrategy = new Strategies.AdvancedHeaderFooterStrategy7(false, pageHeight);
                var procFooter = new PdfCanvasProcessor(footerStrategy);
                procFooter.ProcessPageContent(doc.GetPage(page.PageNumber));
                page.Footers = ParseHeaderFooterText(footerStrategy.GetResultantText());

                // Fallback: se nada veio da estratégia, usar as últimas linhas do texto da página
                if (page.Footers.Count == 0 && !string.IsNullOrWhiteSpace(page.TextInfo.PageText))
                {
                    var lines = page.TextInfo.PageText.Split('\n')
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToList();
                    var tail = lines.TakeLast(3).ToList();
                    if (tail.Count > 0)
                        page.Footers.AddRange(tail);
                }

                // Espelhar em TextInfo para consumidores que leem por ali
                page.TextInfo.Headers = page.Headers.ToList();
                page.TextInfo.Footers = page.Footers.ToList();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] header/footer p{page.PageNumber}: {ex.Message}");
            }
        }

        private List<string> ParseHeaderFooterText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new List<string>();
            return text.Split('\n')
                       .Where(line => !string.IsNullOrWhiteSpace(line))
                       .Select(line => line.Trim())
                       .ToList();
        }

        private void DetectDocumentReferences(PageAnalysis page)
        {
            var references = new List<string>();
            string text = page.TextInfo.PageText ?? string.Empty;
            var patterns = new[]
            {
                @"SEI\s+\d{6}-\d{2}\.\d{4}\.\d\.\d{2}(?:\s*/\s*pg\.\s*\d+)?",
                @"Processo\s+n[º°]\s*[\d.-]+",
                @"Ofício\s+\d+\s+(?:\(\d+\))?",
                @"Anexo\s+\(\d+\)"
            };
            foreach (var pattern in patterns)
                foreach (Match match in Regex.Matches(text, pattern))
                    references.Add(match.Value);
            page.DocumentReferences = references.Distinct().ToList();
        }

        private SecurityInfo ExtractSecurityInfo()
        {
            var sec = new SecurityInfo();
            if (doc == null) return sec;
            var reader = doc.GetReader();
            sec.IsEncrypted = reader.IsEncrypted();
            sec.PermissionFlags = (int)reader.GetPermissions();
            sec.EncryptionType = reader.IsEncrypted() ? 1 : 0;
            sec.CanPrint = !reader.IsEncrypted() || reader.GetPermissions() == -1;
            sec.CanModify = sec.CanPrint;
            sec.CanCopy = sec.CanPrint;
            sec.CanAnnotate = sec.CanPrint;
            return sec;
        }

        private ResourcesSummary ExtractResources(List<PageAnalysis> pages)
        {
            var res = new ResourcesSummary();
            res.TotalImages = pages.Sum(p => p.Resources.Images.Count);
            res.TotalFonts = pages.Sum(p => p.Resources.FontCount);
            res.Forms = pages.Sum(p => p.Resources.FormFields.Count);

            try
            {
                var catalogDict = doc!.GetCatalog().GetPdfObject();
                var namesDict = catalogDict.GetAsDictionary(PdfName.Names);
                var embeddedDict = namesDict?.GetAsDictionary(PdfName.EmbeddedFiles);
                var nameArray = embeddedDict?.GetAsArray(PdfName.Names);
                if (nameArray != null)
                {
                    res.HasAttachments = nameArray.Size() > 0;
                    res.AttachmentCount = nameArray.Size() / 2;

                    for (int i = 0; i < nameArray.Size(); i += 2)
                    {
                        var keyStrObj = nameArray.GetAsString(i);
                        var fsDict = nameArray.GetAsDictionary(i + 1);
                        string fname = keyStrObj?.ToString() ?? $"Embedded_{i / 2}";
                        long? fsize = null;

                        if (fsDict != null)
                        {
                            var fileName = fsDict.GetAsString(PdfName.F);
                            if (fileName != null) fname = fileName.ToString();
                            var ef = fsDict.GetAsDictionary(PdfName.EF);
                            var fstream = ef?.GetAsStream(PdfName.F);
                            if (fstream != null)
                            {
                                try { fsize = fstream.GetLength(); } catch { }
                            }
                        }

                        res.EmbeddedFiles.Add(fname);
                        res.EmbeddedFileInfos.Add(new EmbeddedFileInfo
                        {
                            Name = fname,
                            Size = fsize,
                            Source = "EmbeddedFiles"
                        });
                    }
                }
            }
            catch { }
            return res;
        }

        private Statistics CalculateStatistics(PDFAnalysisResult result)
        {
            var stats = new Statistics();
            stats.TotalCharacters = result.Pages.Sum(p => p.TextInfo.CharacterCount);
            stats.TotalWords = result.Pages.Sum(p => p.TextInfo.WordCount);
            stats.TotalLines = result.Pages.Sum(p => p.TextInfo.LineCount);
            stats.AverageWordsPerPage = result.Pages.Count > 0 ? stats.TotalWords / result.Pages.Count : 0;
            stats.TotalImages = result.Pages.Sum(p => p.Resources.Images.Count);
            stats.TotalAnnotations = result.Pages.Sum(p => p.Annotations.Count);

            var allFonts = new HashSet<string>();
            foreach (var page in result.Pages)
                foreach (var font in page.TextInfo.Fonts)
                    allFonts.Add(font.Name);
            stats.UniqueFonts = allFonts.Count;
            stats.PagesWithImages = result.Pages.Count(p => p.Resources.Images.Count > 0);
            stats.PagesWithTables = result.Pages.Count(p => p.TextInfo.HasTables);
            stats.PagesWithColumns = result.Pages.Count(p => p.TextInfo.HasColumns);
            return stats;
        }

        private ImageInfo ExtractImageInfo(PdfStream stream, string name)
        {
            var info = new ImageInfo { Name = name };
            var width = stream.GetAsNumber(PdfName.Width);
            var height = stream.GetAsNumber(PdfName.Height);
            var bits = stream.GetAsNumber(PdfName.BitsPerComponent);
            var filter = stream.GetAsName(PdfName.Filter);
            if (width != null) info.Width = width.IntValue();
            if (height != null) info.Height = height.IntValue();
            if (bits != null) info.BitsPerComponent = bits.IntValue();
            if (filter != null) info.CompressionType = filter.ToString();
            var cs = stream.GetAsName(PdfName.ColorSpace);
            info.ColorSpace = cs?.ToString() ?? "Unknown";
            return info;
        }

        private DateTime? ParsePDFDate(string? pdfDate)
        {
            if (string.IsNullOrEmpty(pdfDate)) return null;
            try
            {
                var s = pdfDate.Replace("D:", "").Replace("'", "");
                if (s.Length >= 14)
                {
                    int year = int.Parse(s.Substring(0, 4));
                    int month = int.Parse(s.Substring(4, 2));
                    int day = int.Parse(s.Substring(6, 2));
                    int hour = int.Parse(s.Substring(8, 2));
                    int minute = int.Parse(s.Substring(10, 2));
                    int second = int.Parse(s.Substring(12, 2));
                    return new DateTime(year, month, day, hour, minute, second);
                }
            }
            catch { }
            return null;
        }

        private int CountWords(string text) => Regex.Matches(text, @"\b\w+\b").Count;

        private List<string> DetectLanguages(string text)
        {
            var langs = new List<string>();
            if (Regex.IsMatch(text, @"\b(the|and|of|to|in|is|are)\b", RegexOptions.IgnoreCase)) langs.Add("English");
            if (Regex.IsMatch(text, @"\b(de|da|do|que|para|com|em|por)\b", RegexOptions.IgnoreCase)) langs.Add("Português");
            if (Regex.IsMatch(text, @"\b(el|la|de|que|en|es|un|una)\b", RegexOptions.IgnoreCase)) langs.Add("Español");
            return langs.Distinct().ToList();
        }

        private double CalculateAverageLineLength(string text)
        {
            var lines = text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            return lines.Length > 0 ? lines.Average(l => l.Length) : 0;
        }

        private XMPMetadata ExtractXMPMetadata()
        {
            var xmp = new XMPMetadata();
            try
            {
                var metadataBytes = doc?.GetXmpMetadata();
                if (metadataBytes != null)
                {
                    var xmpString = System.Text.Encoding.UTF8.GetString(metadataBytes);
                    // preenchimentos simples
                    xmp.DocumentID = ExtractXmpSimple(xmpString, "xmpMM:DocumentID");
                    xmp.InstanceID = ExtractXmpSimple(xmpString, "xmpMM:InstanceID");
                    xmp.CreatorTool = ExtractXmpSimple(xmpString, "xmp:CreatorTool");
                    xmp.DublinCoreTitle = ExtractXmpSimple(xmpString, "dc:title");
                    xmp.DublinCoreSubject = ExtractXmpSimple(xmpString, "dc:subject");
                    xmp.CreateDate = ParseXmpDate(ExtractXmpSimple(xmpString, "xmp:CreateDate"));
                    xmp.ModifyDate = ParseXmpDate(ExtractXmpSimple(xmpString, "xmp:ModifyDate"));
                    xmp.MetadataDate = ParseXmpDate(ExtractXmpSimple(xmpString, "xmp:MetadataDate"));
                }
            }
            catch { }
            return xmp;
        }

        private string ExtractXmpSimple(string xml, string tag)
        {
            var m = Regex.Match(xml, $"<{tag}[^>]*>(.*?)</{tag}>", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : "";
        }

        private DateTime? ParseXmpDate(string? iso)
        {
            if (string.IsNullOrWhiteSpace(iso)) return null;
            try
            {
                if (DateTimeOffset.TryParse(iso, out var dto))
                    return dto.UtcDateTime;
            }
            catch { }
            return null;
        }

        private AccessibilityInfo ExtractAccessibilityInfo()
        {
            var acc = new AccessibilityInfo();
            try
            {
                acc.IsTaggedPDF = doc?.IsTagged() ?? false;
                var catalog = doc?.GetCatalog();
                var lang = catalog?.GetPdfObject()?.GetAsString(PdfName.Lang);
                if (lang != null) acc.Language = lang.ToString();
            }
            catch { }
            return acc;
        }

        private List<OptionalContentGroup> ExtractOptionalContentGroups()
        {
            var layers = new List<OptionalContentGroup>();
            try
            {
                var ocProps = doc?.GetCatalog()?.GetOCProperties(false);
                var ocgs = ocProps?.GetPdfObject()?.GetAsArray(PdfName.OCGs);
                if (ocgs != null)
                    for (int i = 0; i < ocgs.Size(); i++)
                    {
                        var dict = ocgs.GetAsDictionary(i);
                        if (dict == null) continue;
                        layers.Add(new OptionalContentGroup
                        {
                            Name = dict.GetAsString(PdfName.Name)?.ToString() ?? "Layer",
                            Intent = dict.GetAsName(PdfName.Intent)?.ToString() ?? "View",
                            IsVisible = true,
                            CanToggle = true
                        });
                    }
            }
            catch { }
            return layers;
        }

        private List<DigitalSignature> ExtractDigitalSignatures()
        {
            var sigs = new List<DigitalSignature>();
            try
            {
                var acro = PdfAcroForm.GetAcroForm(doc, false);
                var sigUtil = new SignatureUtil(doc);
                var names = sigUtil.GetSignatureNames();
                if (names == null || names.Count == 0) return sigs;

                foreach (var name in names)
                {
                    PdfPKCS7? pkcs7 = null;
                    try { pkcs7 = sigUtil.ReadSignatureData(name); } catch { }

                    var signerName = NormalizeSignerName(pkcs7?.GetSignName() ?? "");
                    if (string.IsNullOrWhiteSpace(signerName))
                    {
                        try
                        {
                            var cert = pkcs7?.GetSigningCertificate();
                            if (cert != null)
                                signerName = NormalizeSignerName(cert.SubjectDN?.ToString() ?? "");
                        }
                        catch { }
                    }

                    DateTime? signDate = null;
                    try { signDate = pkcs7?.GetSignDate(); } catch { }

                    var reason = "";
                    var location = "";
                    try { reason = pkcs7?.GetReason() ?? ""; } catch { }
                    try { location = pkcs7?.GetLocation() ?? ""; } catch { }

                    bool isValid = false;
                    try { isValid = pkcs7?.VerifySignatureIntegrityAndAuthenticity() ?? false; } catch { }

                    var field = acro?.GetField(name);
                    var widgets = field?.GetWidgets();
                    if (widgets == null || widgets.Count == 0)
                    {
                        sigs.Add(new DigitalSignature
                        {
                            Name = name,
                            FieldName = name,
                            SignatureType = "Digital Signature",
                            SignDate = signDate,
                            SigningTime = signDate,
                            SignerName = signerName,
                            Reason = reason ?? "",
                            Location = location ?? "",
                            IsValid = isValid,
                            Page1 = 0
                        });
                        continue;
                    }

                    foreach (var widget in widgets)
                    {
                        var page = widget.GetPage();
                        var page1 = page != null ? doc.GetPageNumber(page) : 0;
                        var rect = widget.GetRectangle()?.ToRectangle();
                        var bbox = NormalizeRect(rect, page?.GetPageSize());

                        sigs.Add(new DigitalSignature
                        {
                            Name = name,
                            FieldName = name,
                            SignatureType = "Digital Signature",
                            SignDate = signDate,
                            SigningTime = signDate,
                            SignerName = signerName,
                            Reason = reason ?? "",
                            Location = location ?? "",
                            IsValid = isValid,
                            Page1 = page1,
                            BBoxX0 = bbox?.x0,
                            BBoxY0 = bbox?.y0,
                            BBoxX1 = bbox?.x1,
                            BBoxY1 = bbox?.y1
                        });
                    }
                }
            }
            catch { }
            return sigs;
        }

        private static (float x0, float y0, float x1, float y1)? NormalizeRect(Rectangle? rect, Rectangle? pageRect)
        {
            if (rect == null || pageRect == null) return null;
            var width = pageRect.GetWidth();
            var height = pageRect.GetHeight();
            if (width <= 0 || height <= 0) return null;
            var x0 = rect.GetLeft() / width;
            var y0 = rect.GetBottom() / height;
            var x1 = rect.GetRight() / width;
            var y1 = rect.GetTop() / height;
            return (
                (float)Math.Max(0, Math.Min(1, x0)),
                (float)Math.Max(0, Math.Min(1, y0)),
                (float)Math.Max(0, Math.Min(1, x1)),
                (float)Math.Max(0, Math.Min(1, y1))
            );
        }

        private static string NormalizeSignerName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var v = value;
            var m = Regex.Match(v, @"(?i)\bCN\s*=\s*([^,;/]+)");
            if (m.Success)
                v = m.Groups[1].Value;
            v = Regex.Replace(v, @"[^A-Za-zÁÂÃÀÉÊÍÓÔÕÚÇ\s'-]+", " ");
            v = NormalizeWhitespace(v);
            return v;
        }

        private static string NormalizeWhitespace(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return Regex.Replace(text, "\\s+", " ").Trim();
        }

        private List<ColorProfile> ExtractColorProfiles()
        {
            var profiles = new List<ColorProfile>();
            try
            {
                for (int i = 1; i <= doc!.GetNumberOfPages(); i++)
                {
                    var page = doc.GetPage(i);
                    var resources = page.GetResources();
                    var colorSpaces = resources?.GetResource(PdfName.ColorSpace) as PdfDictionary;
                    if (colorSpaces != null)
                    {
                        foreach (var key in colorSpaces.KeySet())
                        {
                            profiles.Add(new ColorProfile
                            {
                                Name = key.ToString(),
                                ColorSpace = colorSpaces.Get(key).ToString()
                            });
                        }
                    }
                }
            }
            catch { }
            return profiles;
        }

        private ModificationDates BuildModificationDates(PDFAnalysisResult result)
        {
            var md = new ModificationDates
            {
                InfoCreationDate = result.Metadata.CreationDate,
                InfoModDate = result.Metadata.ModificationDate,
                XmpCreateDate = result.XMPMetadata.CreateDate,
                XmpModifyDate = result.XMPMetadata.ModifyDate ?? result.XMPMetadata.MetadataDate
            };

            if (result.Signatures != null)
            {
                foreach (var sig in result.Signatures)
                {
                    var when = sig.SignDate ?? sig.SigningTime ?? sig.TimestampDate;
                    md.SignatureDates.Add(when);
                }
            }

            if (result.Pages != null)
            {
                foreach (var p in result.Pages)
                {
                    if (p.Annotations == null) continue;
                    foreach (var a in p.Annotations)
                        md.AnnotationDates.Add(a.ModificationDate);
                }
            }
            return md;
        }

        private BookmarkStructure ExtractBookmarkStructure()
        {
            var b = new BookmarkStructure();
            try
            {
                var items = BookmarkExtractor.Extract(doc);
                if (items != null && items.Count > 0)
                {
                    b.RootItems = items;
                    b.TotalCount = CountBookmarks(b.RootItems);
                    b.MaxDepth = CalculateMaxDepth(b.RootItems);
                }
            }
            catch { }
            return b;
        }

        private List<BookmarkFlatItem> FlattenBookmarks(BookmarkStructure bookmarks)
        {
            var flat = new List<BookmarkFlatItem>();
            if (bookmarks?.RootItems == null || bookmarks.RootItems.Count == 0) return flat;
            foreach (var item in bookmarks.RootItems)
                FlattenBookmarkItem(item, flat);
            return flat;
        }

        private void FlattenBookmarkItem(BookmarkItem item, List<BookmarkFlatItem> flat)
        {
            if (item == null) return;
            flat.Add(new BookmarkFlatItem
            {
                Title = item.Title ?? "",
                PageNumber = item.Destination?.PageNumber ?? 0,
                Level = item.Level,
                HasChildren = item.Children != null && item.Children.Count > 0,
                ChildrenCount = item.Children?.Count ?? 0
            });
            if (item.Children == null || item.Children.Count == 0) return;
            foreach (var child in item.Children)
                FlattenBookmarkItem(child, flat);
        }

        private List<BookmarkItem> ConvertOutline(PdfOutline outline, int level = 0)
        {
            var items = new List<BookmarkItem>();
            foreach (var child in outline.GetAllChildren())
            {
                var item = new BookmarkItem
                {
                    Title = child.GetTitle(),
                    Level = level,
                    IsOpen = child.IsOpen()
                };
                item.Destination = new BookmarkDestination { PageNumber = 1, Type = "Destination" };
                var dest = child.GetDestination();
                var pageFromDest = ResolveDestinationPage(dest);
                if (pageFromDest == 0)
                {
                    var destObj = dest?.GetPdfObject();
                    pageFromDest = ResolveDestToPage(destObj);
                }
                if (pageFromDest > 0)
                    item.Destination.PageNumber = pageFromDest;
                item.Children = ConvertOutline(child, level + 1);
                items.Add(item);
            }
            return items;
        }

        private List<BookmarkItem> ExtractBookmarksFromOutlinesDict()
        {
            var items = new List<BookmarkItem>();
            if (doc == null) return items;
            try
            {
                var catalog = doc.GetCatalog().GetPdfObject();
                var outlines = catalog?.GetAsDictionary(PdfName.Outlines);
                var first = outlines?.GetAsDictionary(PdfName.First);
                if (first != null)
                    items = ConvertOutlineDict(first, 0);
            }
            catch { }
            return items;
        }

        private List<BookmarkItem> ConvertOutlineDict(PdfDictionary item, int level = 0)
        {
            var items = new List<BookmarkItem>();
            var current = item;
            while (current != null)
            {
                string title = "";
                var titleObj = current.Get(PdfName.Title);
                if (titleObj is PdfString ps) title = ps.ToUnicodeString();
                else if (titleObj != null) title = titleObj.ToString();

                var destObj = current.Get(PdfName.Dest);
                if (destObj == null)
                {
                    var action = current.GetAsDictionary(PdfName.A);
                    var actionType = action?.GetAsName(PdfName.S);
                    if (PdfName.GoTo.Equals(actionType))
                        destObj = action?.Get(PdfName.D);
                }

                var pageFromDest = ResolveDestToPage(destObj);
                if (pageFromDest <= 0) pageFromDest = 1;

                var entry = new BookmarkItem
                {
                    Title = title ?? "",
                    Level = level,
                    IsOpen = false,
                    Destination = new BookmarkDestination { PageNumber = pageFromDest, Type = "Destination" }
                };

                var first = current.GetAsDictionary(PdfName.First);
                if (first != null)
                    entry.Children = ConvertOutlineDict(first, level + 1);

                items.Add(entry);
                current = current.GetAsDictionary(PdfName.Next);
            }
            return items;
        }

        private int ResolveDestinationPage(PdfDestination? destination)
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
            catch
            {
                // ignore and fallback
            }
            return 0;
        }

        private int ResolveDestToPage(PdfObject? destObj)
        {
            if (destObj == null || doc == null) return 0;

            if (destObj is PdfDictionary dict)
            {
                var actionType = dict.GetAsName(PdfName.S);
                if (PdfName.GoTo.Equals(actionType))
                    destObj = dict.Get(PdfName.D);
            }

            if (destObj is PdfArray arr)
                return ResolveArrayDest(arr);

            if (destObj is PdfName || destObj is PdfString)
            {
                var resolved = ResolveNamedDestination(destObj);
                if (resolved is PdfArray namedArr)
                    return ResolveArrayDest(namedArr);
            }
            return 0;
        }

        private int ResolveArrayDest(PdfArray arr)
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

        private PdfObject? ResolveNamedDestination(PdfObject nameObj)
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

        private int CountBookmarks(List<BookmarkItem> items) => items.Count + items.Sum(i => CountBookmarks(i.Children));
        private int CalculateMaxDepth(List<BookmarkItem> items) => items.Count == 0 ? 0 : 1 + items.Max(i => CalculateMaxDepth(i.Children));

        private List<FormField> ExtractPageFormFields(int pageNum)
        {
            var list = new List<FormField>();
            try
            {
                var acro = PdfAcroForm.GetAcroForm(doc, false);
                if (acro == null) return list;
                foreach (var kv in acro.GetFormFields())
                {
                    var field = kv.Value;
                    var widgets = field.GetWidgets();
                    if (widgets == null) continue;
                    foreach (var widget in widgets)
                    {
                        var page = widget.GetPage();
                        if (page != null && doc!.GetPageNumber(page) == pageNum)
                        {
                            var rect = widget.GetRectangle();
                            var ff = new FormField
                            {
                                Name = kv.Key,
                                Type = field.GetFormType()?.ToString() ?? "Unknown",
                                Value = field.GetValueAsString(),
                                DefaultValue = field.GetDefaultValue()?.ToString() ?? string.Empty,
                                IsReadOnly = field.IsReadOnly(),
                                IsRequired = field.IsRequired(),
                                X = rect.GetAsNumber(0)?.FloatValue() ?? 0,
                                Y = rect.GetAsNumber(1)?.FloatValue() ?? 0,
                                Width = (rect.GetAsNumber(2)?.FloatValue() ?? 0) - (rect.GetAsNumber(0)?.FloatValue() ?? 0),
                                Height = (rect.GetAsNumber(3)?.FloatValue() ?? 0) - (rect.GetAsNumber(1)?.FloatValue() ?? 0)
                            };
                            var opt = field.GetPdfObject().GetAsArray(PdfName.Opt);
                            if (opt != null)
                            {
                                for (int j = 0; j < opt.Size(); j++)
                                {
                                    var option = opt.GetAsString(j);
                                    if (option != null) ff.Options.Add(option.ToString());
                                }
                            }
                            list.Add(ff);
                        }
                    }
                }
            }
            catch { }
            return list;
        }
    }
}
