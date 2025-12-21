using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using FilterPDF.Utils;
using System.Text;
using System.Security.Cryptography;
using System.IO.Compression;
using FilterPDF.TjpbDespachoExtractor.Config;
using FilterPDF.TjpbDespachoExtractor.Extraction;
using FilterPDF.TjpbDespachoExtractor.Utils;
using FilterPDF.TjpbDespachoExtractor.Models;
using FilterPDF.TjpbDespachoExtractor.Reference;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Navigation;
using iText.Kernel.Utils;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Executa a etapa FPDF do pipeline-tjpb: processa todos os PDFs de um diretório
    /// e gera um JSON consolidado compatível com tmp/pipeline/step2/fpdf.json.
    ///
    /// Uso:
    ///   fpdf pipeline-tjpb --input-dir tmp/preprocessor_stage --output tmp/pipeline/step2/fpdf.json
    ///
    /// Campos gerados por documento:
    /// - process, pdf_path
    /// - doc_label, start_page, end_page, doc_pages, total_pages
    /// - text (concatenação das páginas do documento)
    /// - fonts (nomes), images (quantidade), page_size, has_signature_image (heurística)
    /// </summary>
    public class PipelineTjpbCommand : Command
    {
        public override string Name => "pipeline-tjpb";
        public override string Description => "Etapa FPDF do pipeline-tjpb: consolida documentos em JSON";
        private TjpbDespachoConfig? _tjpbCfg;
        private PeritoCatalog? _peritoCatalog;
        private HonorariosTable? _honorariosTable;
        private LaudoHashDb? _laudoHashDb;
        private string _laudoHashDbPath = "";

        public override void Execute(string[] args)
        {
            string inputDir = ".";
            bool splitAnexos = false; // backward compat (anexos now covered by bookmark docs)
            int maxBookmarkPages = 30; // agora interno, sem flag na CLI
            bool onlyDespachos = false;
            string? signerContains = null;
            bool debugDocSummary = false;
            bool fieldsOnly = false;
            string pgUri = FilterPDF.Utils.PgDocStore.DefaultPgUri;
            string configPath = Path.Combine("configs", "config.yaml");
            var analysesByProcess = new Dictionary<string, PDFAnalysisResult>();
            var pdfMetaByProcess = new Dictionary<string, Dictionary<string, object>>();
            string cacheDir = Path.Combine(Directory.GetCurrentDirectory(), "tmp", "cache");
            // Localiza configs/fields e docid/layout_hashes.csv respeitando cwd ou pasta do binário.
            string cwd = Directory.GetCurrentDirectory();
            string exeBase = AppContext.BaseDirectory;
            string[] fieldsCandidates =
            {
                Path.Combine(cwd, "configs/fields"),
                Path.Combine(cwd, "../configs/fields"),
                Path.Combine(cwd, "../../configs/fields"),
                Path.GetFullPath(Path.Combine(exeBase, "../../../../configs/fields"))
            };
            string[] hashCandidates =
            {
                Path.Combine(cwd, "docid/layout_hashes.csv"),
                Path.Combine(cwd, "../docid/layout_hashes.csv"),
                Path.Combine(cwd, "../../docid/layout_hashes.csv"),
                Path.GetFullPath(Path.Combine(exeBase, "../../../../docid/layout_hashes.csv"))
            };

            string fieldScriptsPath = fieldsCandidates.FirstOrDefault(Directory.Exists)
                                      ?? throw new DirectoryNotFoundException("configs/fields não encontrado");
            string layoutHashesPath = hashCandidates.FirstOrDefault(File.Exists) ?? "";

            var fieldScripts = FieldScripts.LoadScripts(fieldScriptsPath);
            if (!string.IsNullOrEmpty(layoutHashesPath))
                DocIdClassifier.LoadHashes(layoutHashesPath);

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--input-dir" && i + 1 < args.Length) inputDir = args[i + 1];
                if (args[i] == "--split-anexos") splitAnexos = true;
                if (args[i] == "--only-despachos") onlyDespachos = true;
                if (args[i] == "--signer-contains" && i + 1 < args.Length) signerContains = args[i + 1];
                if (args[i] == "--config" && i + 1 < args.Length) configPath = args[i + 1];
                if (args[i] == "--debug-docsummary") debugDocSummary = true;
                if (args[i] == "--fields-only") fieldsOnly = true;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(configPath) && File.Exists(configPath))
                    _tjpbCfg = TjpbDespachoConfig.Load(configPath);
                else
                    _tjpbCfg = new TjpbDespachoConfig();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[pipeline-tjpb] WARN config: {ex.Message}");
                _tjpbCfg = new TjpbDespachoConfig();
            }
            EnsureReferenceCaches();

            var dir = new DirectoryInfo(inputDir);
            if (!dir.Exists)
            {
                Console.WriteLine($"Diretório não encontrado: {inputDir}");
                return;
            }

            string? preprocessDir = null;
            bool cleanupPreprocessDir = false;
            try
            {
                if (ContainsZipFiles(inputDir))
                {
                    preprocessDir = Path.Combine(Path.GetTempPath(), "tjpdf-preprocess-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(preprocessDir);
                    cleanupPreprocessDir = true;

                    var ok = PreprocessZipInbox(inputDir, preprocessDir);
                    if (!ok)
                    {
                        Console.Error.WriteLine("[pipeline-tjpb] Nenhum PDF útil encontrado após preprocessamento de ZIP.");
                        return;
                    }
                    inputDir = preprocessDir;
                    dir = new DirectoryInfo(inputDir);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[pipeline-tjpb] WARN preprocess: {ex.Message}");
                if (cleanupPreprocessDir && preprocessDir != null)
                {
                    try { Directory.Delete(preprocessDir, true); } catch { }
                }
                return;
            }

            try
            {
                // Detecta se estamos lendo de cache (json) ou de PDFs
                var jsonCaches = dir.GetFiles("*.json").OrderBy(f => f.Name).ToList();
                var pdfs = dir.GetFiles("*.pdf").OrderBy(f => f.Name).ToList();
                bool useCache = jsonCaches.Count > 0 && pdfs.Count == 0;

                var allDocs = new List<Dictionary<string, object>>();
                var allDocsWords = new List<List<Dictionary<string, object>>>();

                var sources = useCache ? jsonCaches.Cast<FileInfo>() : pdfs.Cast<FileInfo>();

                foreach (var file in sources)
                {
                    try
                    {
                    PDFAnalysisResult analysis;
                    string pdfPath;

                    if (useCache)
                    {
                        var text = File.ReadAllText(file.FullName);
                        analysis = JsonConvert.DeserializeObject<PDFAnalysisResult>(text)
                                   ?? throw new Exception("Cache inválido");
                        pdfPath = file.FullName;
                    }
                    else
                    {
                        analysis = new PDFAnalyzer(file.FullName).AnalyzeFull();
                        pdfPath = file.FullName;
                    }

                    var procName = DeriveProcessName(pdfPath);
                    analysesByProcess[procName] = analysis;
                    if (!useCache)
                    {
                        try
                        {
                            var hallJson = JsonConvert.SerializeObject(analysis);
                            PgDocStore.UpsertRawProcess(pgUri, procName, pdfPath, hallJson);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[pipeline-tjpb] WARN raw hall {procName}: {ex.Message}");
                        }
                        try
                        {
                            var bytes = File.ReadAllBytes(pdfPath);
                            var sha = ComputeSha256Hex(bytes);
                            var size = bytes.LongLength;
                            PgDocStore.UpsertRawFile(pgUri, procName, pdfPath, bytes);
                            pdfMetaByProcess[procName] = new Dictionary<string, object>
                            {
                                ["fileName"] = Path.GetFileName(pdfPath),
                                ["filePath"] = pdfPath,
                                ["pages"] = analysis.DocumentInfo.TotalPages,
                                ["sha256"] = sha,
                                ["fileSize"] = size
                            };
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[pipeline-tjpb] WARN raw file {procName}: {ex.Message}");
                        }
                    }
                    ExtractionResult? despachoResult = null;
                    try
                    {
                        var cfg = _tjpbCfg ?? new TjpbDespachoConfig();
                        var extractor = new DespachoExtractor(cfg);
                        var options = new ExtractionOptions { ProcessNumber = procName };
                        despachoResult = extractor.Extract(analysis, pdfPath, options);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[pipeline-tjpb] WARN despacho-extractor {procName}: {ex.Message}");
                    }
                    var bookmarkDocs = BuildBookmarkBoundaries(analysis, maxBookmarkPages);
                    var segmenter = new DocumentSegmenter(new DocumentSegmentationConfig());
                    var docs = bookmarkDocs.Count > 0 ? bookmarkDocs : segmenter.FindDocuments(analysis);

                    foreach (var d in docs)
                    {
                        var obj = BuildDocObject(d, analysis, pdfPath, fieldScripts, despachoResult, debugDocSummary);
                        allDocs.Add(obj);
                        if (obj.ContainsKey("words"))
                        {
                            var w = obj["words"] as List<Dictionary<string, object>>;
                            if (w != null) allDocsWords.Add(w);
                        }
                        if (debugDocSummary)
                        {
                            var preview = new Dictionary<string, object>
                            {
                                ["process"] = obj.GetValueOrDefault("process") ?? "",
                                ["doc_label"] = obj.GetValueOrDefault("doc_label") ?? "",
                                ["doc_type"] = obj.GetValueOrDefault("doc_type") ?? "",
                                ["start_page"] = obj.GetValueOrDefault("start_page") ?? 0,
                                ["end_page"] = obj.GetValueOrDefault("end_page") ?? 0,
                                ["header"] = obj.GetValueOrDefault("header") ?? "",
                                ["footer"] = obj.GetValueOrDefault("footer") ?? "",
                                ["footer_signature_raw"] = obj.GetValueOrDefault("footer_signature_raw") ?? "",
                                ["origin_main"] = obj.GetValueOrDefault("origin_main") ?? "",
                                ["origin_sub"] = obj.GetValueOrDefault("origin_sub") ?? "",
                                ["origin_extra"] = obj.GetValueOrDefault("origin_extra") ?? "",
                                ["signer"] = obj.GetValueOrDefault("signer") ?? "",
                                ["signed_at"] = obj.GetValueOrDefault("signed_at") ?? "",
                                ["date_footer"] = obj.GetValueOrDefault("date_footer") ?? "",
                                ["process_line"] = obj.GetValueOrDefault("process_line") ?? "",
                                ["interested_name"] = obj.GetValueOrDefault("interested_name") ?? "",
                                ["interested_profession"] = obj.GetValueOrDefault("interested_profession") ?? "",
                                ["juizo_vara"] = obj.GetValueOrDefault("juizo_vara") ?? "",
                                ["comarca"] = obj.GetValueOrDefault("comarca") ?? "",
                                ["_debug"] = obj.GetValueOrDefault("_debug") ?? new Dictionary<string, object>()
                            };
                            Console.WriteLine(JsonConvert.SerializeObject(preview, Formatting.Indented));
                        }

                        // Legacy split-anexos now redundant; kept for compatibility
                        if (splitAnexos && d.DetectedType == "anexo")
                        {
                            var anexosChildren = SplitAnexos(d, analysis, pdfPath, fieldScripts);
                            allDocs.AddRange(anexosChildren);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[pipeline-tjpb] WARN {file.Name}: {ex.Message}");
                }
            }

                if (debugDocSummary)
                    return;

                // Filtros opcionais (CLI) aplicados em C# para evitar pós-processamento externo
                var filteredDocs = new List<Dictionary<string, object>>();
                var filteredWords = new List<List<Dictionary<string, object>>>();
                foreach (var doc in allDocs)
                {
                    string label = doc.TryGetValue("doc_label", out var dl) ? dl?.ToString() ?? "" : "";
                    string docType = doc.TryGetValue("doc_type", out var dt) ? dt?.ToString() ?? "" : "";
                    string signer = doc.TryGetValue("signer", out var s) ? s?.ToString() ?? "" : "";

                    bool pass = true;
                    if (onlyDespachos)
                        pass &= label.Contains("despacho", StringComparison.OrdinalIgnoreCase) ||
                                docType.Contains("despacho", StringComparison.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(signerContains))
                        pass &= signer.Contains(signerContains, StringComparison.OrdinalIgnoreCase);
                    if (!pass) continue;

                    filteredDocs.Add(doc);
                    if (doc.TryGetValue("words", out var wobj) && wobj is List<Dictionary<string, object>> wlist)
                        filteredWords.Add(wlist);
                }

                var docsForStats = filteredWords.Count > 0 ? filteredWords : allDocsWords;
                var paragraphStats = BuildParagraphStats(docsForStats);
                var grouped = (filteredDocs.Count > 0 ? filteredDocs : allDocs)
                              .GroupBy(d => d.TryGetValue("process", out var p) ? p?.ToString() ?? "sem_processo" : "sem_processo")
                              .Select(g => new { process = g.Key, documents = g.ToList() })
                              .ToList();

                if (fieldsOnly)
                {
                    var targetDocs = filteredDocs.Count > 0 ? filteredDocs : allDocs;
                    var payload = new
                    {
                        documents = targetDocs.Select(BuildFieldsOnlyDoc).ToList()
                    };
                    Console.WriteLine(JsonConvert.SerializeObject(payload, Formatting.Indented));
                    return;
                }

                // Persistir por processo no Postgres (tabelas processes + documents)
                foreach (var grp in grouped)
                {
                    var procName = grp.process;
                    var firstDoc = grp.documents.FirstOrDefault();
                    var sourcePath = firstDoc != null && firstDoc.TryGetValue("pdf_path", out var pp) ? pp?.ToString() ?? procName : procName;
                    var analysis = analysesByProcess.TryGetValue(procName, out var an) ? an : new PDFAnalysisResult();
                    var pdfMeta = pdfMetaByProcess.TryGetValue(procName, out var meta) ? meta : new Dictionary<string, object>
                    {
                        ["fileName"] = Path.GetFileName(sourcePath ?? ""),
                        ["filePath"] = sourcePath ?? "",
                        ["pages"] = analysis.DocumentInfo.TotalPages,
                        ["sha256"] = "",
                        ["fileSize"] = 0L
                    };
                    var payload = new { process = procName, pdf = pdfMeta, documents = grp.documents, paragraph_stats = paragraphStats };
                    var tokenProc = JToken.FromObject(payload);
                    foreach (var v in tokenProc.SelectTokens("$..*").OfType<JValue>())
                    {
                        if (v.Type == JTokenType.String && v.Value != null)
                        {
                            var s = v.Value.ToString();
                            s = Regex.Replace(s, @"\p{C}+", " ");
                            v.Value = s;
                        }
                    }
                    var jsonProc = tokenProc.ToString(Formatting.None);
                    try
                    {
                        PgDocStore.UpsertProcess(pgUri, sourcePath, analysis, new BookmarkClassifier(),
                                                 storeJson: true, storeDocuments: false, jsonPayload: jsonProc);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[pipeline-tjpb] WARN PG save {procName}: {ex.Message}");
                    }
                }
            }
            finally
            {
                if (cleanupPreprocessDir && preprocessDir != null)
                {
                    try { Directory.Delete(preprocessDir, true); } catch { }
                }
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("fpdf pipeline-tjpb --input-dir <dir> [--split-anexos] [--only-despachos] [--signer-contains <texto>]");
            Console.WriteLine("--config: caminho para config.yaml (opcional; usado para hints de despacho).");
            Console.WriteLine("Lê caches (tmp/cache/*.json) ou PDFs; grava no Postgres (processes + documents). Não gera arquivo.");
            Console.WriteLine("Se houver .zip no input-dir, mergeia PDFs internos e cria bookmarks por nome de arquivo.");
            Console.WriteLine("--split-anexos: cria subdocumentos a partir de bookmarks 'Anexo/Anexos' dentro de cada documento.");
            Console.WriteLine("--only-despachos: filtra apenas documentos cujo doc_label/doc_type contenha 'Despacho'.");
            Console.WriteLine("--signer-contains: filtra documentos cujo signer contenha o texto informado (case-insensitive).");
            Console.WriteLine("--debug-docsummary: imprime header/footer + campos de cabeçalho/assinatura por documento e não grava no Postgres.");
            Console.WriteLine("--fields-only: imprime apenas os fields principais (JSON) e não grava no Postgres.");
        }

        private void EnsureReferenceCaches()
        {
            var cfg = _tjpbCfg ?? new TjpbDespachoConfig();
            var baseDir = string.IsNullOrWhiteSpace(cfg.BaseDir) ? Directory.GetCurrentDirectory() : cfg.BaseDir;

            if (_peritoCatalog == null)
            {
                try
                {
                    _peritoCatalog = PeritoCatalog.Load(baseDir, cfg.Reference.PeritosCatalogPaths);
                }
                catch
                {
                    _peritoCatalog = new PeritoCatalog();
                }
            }

            if (_honorariosTable == null)
            {
                try
                {
                    _honorariosTable = new HonorariosTable(cfg.Reference.Honorarios, baseDir);
                }
                catch
                {
                    _honorariosTable = null;
                }
            }

            if (_laudoHashDb == null)
            {
                try
                {
                    var candidates = new[]
                    {
                        Path.Combine(baseDir, "src/PipelineTjpb/reference/laudos_hashes/laudos_hashes.csv"),
                        Path.Combine(baseDir, "src/PipelineTjpb/reference/laudos_hashes/laudos_hashes_unique.csv"),
                        Path.Combine(baseDir, "PipelineTjpb/reference/laudos_hashes/laudos_hashes.csv"),
                        Path.Combine(baseDir, "PipelineTjpb/reference/laudos_hashes/laudos_hashes_unique.csv")
                    };
                    var path = candidates.FirstOrDefault(File.Exists) ?? "";
                    _laudoHashDbPath = path;
                    _laudoHashDb = string.IsNullOrWhiteSpace(path) ? null : LaudoHashDb.LoadCsv(path);
                }
                catch
                {
                    _laudoHashDb = null;
                    _laudoHashDbPath = "";
                }
            }
        }

        private bool ContainsZipFiles(string inputDir)
        {
            try
            {
                return Directory.GetFiles(inputDir, "*.zip", SearchOption.AllDirectories).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private bool PreprocessZipInbox(string inputDir, string outDir)
        {
            var inputs = Directory.GetFiles(inputDir, "*.*", SearchOption.AllDirectories)
                                  .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                                              f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(f => f)
                                  .ToList();
            if (inputs.Count == 0) return false;

            var created = 0;
            foreach (var src in inputs)
            {
                var proc = DeriveProcessName(src);
                if (string.IsNullOrWhiteSpace(proc)) continue;

                var outPdf = Path.Combine(outDir, proc + ".pdf");
                if (File.Exists(outPdf))
                {
                    Console.Error.WriteLine($"[pipeline-tjpb] Aviso: processo duplicado {proc}; ignorando {Path.GetFileName(src)}.");
                    continue;
                }

                if (src.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var ok = MergeZipToPdf(src, outPdf);
                    if (!ok)
                    {
                        Console.Error.WriteLine($"[pipeline-tjpb] ZIP sem PDFs úteis: {Path.GetFileName(src)}");
                        continue;
                    }
                }
                else
                {
                    File.Copy(src, outPdf, overwrite: true);
                }
                created++;
            }
            return created > 0;
        }

        private bool MergeZipToPdf(string zipPath, string outPdf)
        {
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
                    var dest = PdfExplicitDestination.CreateFit(destDoc.GetPage(pageOffset + 1));
                    root.AddOutline(Path.GetFileNameWithoutExtension(entry.FullName)).AddDestination(dest);
                    pageOffset += pages;
                }
                return pageOffset > 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[pipeline-tjpb] Falha ao mergear ZIP {Path.GetFileName(zipPath)}: {ex.Message}");
                return false;
            }
        }

        private string DeriveProcessName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var m = Regex.Match(name, @"\d+");
            if (m.Success) return m.Value;
            return name;
        }

        private Dictionary<string, object> BuildDocObject(DocumentBoundary d, PDFAnalysisResult analysis, string pdfPath, List<FieldScript> scripts, ExtractionResult? despachoResult, bool debug = false)
        {
            var docText = string.Join("\n", Enumerable.Range(d.StartPage, d.PageCount)
                                                      .Select(p => analysis.Pages[p - 1].TextInfo.PageText ?? ""));
            var lastPageText = analysis.Pages[Math.Max(0, Math.Min(analysis.Pages.Count - 1, d.EndPage - 1))].TextInfo.PageText ?? "";
            var lastTwoText = lastPageText;
            if (d.PageCount >= 2)
            {
                var prev = analysis.Pages[Math.Max(0, d.EndPage - 2)].TextInfo.PageText ?? "";
                lastTwoText = $"{prev}\n{lastPageText}";
            }
            var footerSignatureRaw = ExtractFooterSignatureRaw(lastPageText);

            var fonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int p = d.StartPage; p <= d.EndPage; p++)
                foreach (var f in analysis.Pages[p - 1].TextInfo.Fonts)
                    fonts.Add(f.Name);

            // Capturar rodapé predominante do intervalo
            var footerLines = new List<string>();
            int images = 0;
            bool hasSignature = false;
            int wordCount = 0;
            int charCount = 0;
            double wordsArea = 0;
            double pageAreaAcc = 0;
            var wordsWithCoords = new List<Dictionary<string, object>>();
            var docBookmarks = ExtractBookmarksForRange(analysis, d.StartPage, d.EndPage);
            var unpackedAttachments = new List<Dictionary<string, object>>();
            string header = string.Empty;
            string footer = string.Empty;

            for (int p = d.StartPage; p <= d.EndPage; p++)
            {
                images += analysis.Pages[p - 1].Resources.Images?.Count ?? 0;
                // Heurística simples: imagem com largura/altura > 100 pode ser assinatura
                hasSignature |= (analysis.Pages[p - 1].Resources.Images?.Any(img => img.Width > 100 && img.Height > 30) ?? false);

                var page = analysis.Pages[p - 1];
                wordCount += page.TextInfo.WordCount;
                charCount += page.TextInfo.CharacterCount;

                var size = page.Size;
                var pageArea = Math.Max(1, size.Width * size.Height);
                pageAreaAcc += pageArea;

                foreach (var w in page.TextInfo.Words)
                {
                    double wArea = Math.Max(0, (w.X1 - w.X0) * (w.Y1 - w.Y0));
                    wordsArea += wArea;
                    wordsWithCoords.Add(new Dictionary<string, object>
                    {
                        ["text"] = w.Text,
                        ["page"] = p,
                        ["font"] = w.Font,
                        ["size"] = w.Size,
                        ["bold"] = w.Bold,
                        ["italic"] = w.Italic,
                        ["underline"] = w.Underline,
                        ["render_mode"] = w.RenderMode,
                        ["char_spacing"] = w.CharSpacing,
                        ["word_spacing"] = w.WordSpacing,
                        ["horiz_scaling"] = w.HorizontalScaling,
                        ["rise"] = w.Rise,
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

                if (string.IsNullOrEmpty(header) && page.TextInfo.Headers.Any()) header = page.TextInfo.Headers.First();
                if (string.IsNullOrEmpty(footer) && page.TextInfo.Footers.Any()) footer = page.TextInfo.Footers.First();
                if (page.TextInfo.Footers != null && page.TextInfo.Footers.Count > 0)
                    footerLines.AddRange(page.TextInfo.Footers);
            }

            string footerLabel = footerLines
                .GroupBy(x => x)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault() ?? "";

            var pageSize = analysis.Pages.First().Size.GetPaperSize();

            // Heurística: bookmark intitulado "Anexo" ou "Anexos" pode conter vários documentos fundidos.
            // Vamos registrar todos os bookmarks de nível 1/2 com esse título dentro do range,
            // para permitir pós-processamento (split fino) sem quebrar a segmentação principal.
            var anexos = docBookmarks
                .Where(b => Regex.IsMatch(b["title"].ToString() ?? "", "^anexos?$", RegexOptions.IgnoreCase))
                .ToList();
            double textDensity = pageAreaAcc > 0 ? wordsArea / pageAreaAcc : 0;
            double blankRatio = 1 - textDensity;

            var originalLabel = !string.IsNullOrWhiteSpace(d.RawTitle)
                ? d.RawTitle
                : (!string.IsNullOrWhiteSpace(d.Title) ? d.Title : ExtractDocumentName(d));
            var docLabel = NormalizeDocLabel(originalLabel);
            var docType = docLabel; // não classificar; manter o nome do bookmark

            // Rodapé preferencial: compacta e usa só a parte antes de "/ pg."
            if (!string.IsNullOrWhiteSpace(footerLabel) && footerLabel.Contains("SEI"))
            {
                var compact = CompactFooter(footerLabel);
                var cut = compact.Split("/ pg", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(cut))
                {
                    docLabel = cut.Trim();
                    docType = docLabel;
                }
            }

            var docSummary = BuildDocSummary(d, pdfPath, docText, lastPageText, lastTwoText, header, footer, footerSignatureRaw, docBookmarks, analysis.Signatures, docLabel, wordsWithCoords);
            var laudoHash = ComputeLaudoHashSha1(docText);
            LaudoHashDbEntry? hashHit = null;
            if (_laudoHashDb != null && !string.IsNullOrWhiteSpace(laudoHash))
            {
                _laudoHashDb.TryGet(laudoHash, out hashHit);
            }
            if (hashHit != null)
            {
            }
            var despachoMatch = FindBestDespachoMatch(d, despachoResult, out var overlapRatio, out var overlapPages, "despacho");
            var certidaoMatch = FindBestDespachoMatch(d, despachoResult, out var certOverlapRatio, out var certOverlapPages, "certidao");
            if (despachoMatch != null)
            {
                var despachoFields = ConvertDespachoFields(despachoMatch, "despacho_extractor");
                if (despachoFields.Count > 0)
                {
                    // add after script extraction
                }
            }
            if (certidaoMatch != null)
            {
                var certidaoFields = ConvertDespachoFields(certidaoMatch, "certidao_extractor");
                if (certidaoFields.Count > 0)
                {
                    // add after script extraction
                }
            }
            var labelIsDespacho = docLabel.Contains("despacho", StringComparison.OrdinalIgnoreCase) ||
                                  docType.Contains("despacho", StringComparison.OrdinalIgnoreCase);
            var labelIsCertidao = docLabel.Contains("certidao", StringComparison.OrdinalIgnoreCase) ||
                                  docLabel.Contains("certidão", StringComparison.OrdinalIgnoreCase) ||
                                  docType.Contains("certidao", StringComparison.OrdinalIgnoreCase) ||
                                  docType.Contains("certidão", StringComparison.OrdinalIgnoreCase);
            var attachDespacho = despachoMatch != null && (labelIsDespacho || overlapRatio >= 0.5);
            var attachCertidao = certidaoMatch != null && (labelIsCertidao || certOverlapRatio >= 0.5);
            var isDespacho = labelIsDespacho || attachDespacho;
            var isCertidao = labelIsCertidao || attachCertidao;
            var dateFooter = docSummary.TryGetValue("date_footer", out var df) ? df?.ToString() ?? "" : "";
            var signedAtSummary = docSummary.TryGetValue("signed_at", out var sa) ? sa?.ToString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(signedAtSummary))
                dateFooter = signedAtSummary;
            if (!string.IsNullOrWhiteSpace(dateFooter))
            {
                if (isCertidao)
                    docSummary["certidao_date"] = dateFooter;
                if (isDespacho)
                    docSummary["despacho_date"] = dateFooter;
            }
            var forcedBucket = isDespacho ? "principal" : null;
            var extractedFields = ExtractFields(docText, wordsWithCoords, d, pdfPath, scripts, forcedBucket);
            var bandFields = new List<Dictionary<string, object>>();
            var directedFields = ExtractDirectedValues(analysis, d, docType, docText);
            if (attachDespacho)
            {
                var despachoFields = ConvertDespachoFields(despachoMatch, "despacho_extractor");
                if (despachoFields.Count > 0)
                    extractedFields.AddRange(despachoFields);
            }
            if (attachCertidao)
            {
                var certidaoFields = ConvertDespachoFields(certidaoMatch, "certidao_extractor");
                if (certidaoFields.Count > 0)
                    extractedFields.AddRange(certidaoFields);
            }
            if (isDespacho)
            {
                bandFields = ExtractBandFields(docText, wordsWithCoords, d.StartPage);
                extractedFields = MergeFields(extractedFields, bandFields);
            }
            if (directedFields.Count > 0)
                extractedFields = MergeFields(extractedFields, directedFields);
            AddDocSummaryFallbacks(extractedFields, docSummary, d.StartPage);
            var normalizedFields = NormalizeAndValidateFields(extractedFields);
            var forensics = BuildForensics(d, analysis, docText, wordsWithCoords);
            var despachoInfo = DetectDespachoTipo(docText, lastTwoText);
            var isDespachoValid = isDespacho && d.PageCount >= 2 && MatchesOrigin(docSummary, "despacho") && MatchesSigner(docSummary);
            var isDespachoShort = isDespacho && d.PageCount < 2;
            var isCertidaoValid = isCertidao && MatchesOrigin(docSummary, "certidao") && MatchesSigner(docSummary);
            var isRequerimento = docType.Contains("requerimento de pagamento de honorarios", StringComparison.OrdinalIgnoreCase);

            var obj = new Dictionary<string, object>
            {
                ["process"] = DeriveProcessName(pdfPath),
                ["pdf_path"] = pdfPath,
                ["doc_label"] = docLabel,
                ["doc_label_original"] = originalLabel,
                ["doc_type"] = docType,
                ["laudo_hash"] = laudoHash,
                ["hash_db_match"] = hashHit != null,
                ["hash_db_path"] = _laudoHashDbPath,
                ["hash_db_especie"] = hashHit?.Especie ?? "",
                ["hash_db_natureza"] = hashHit?.Natureza ?? "",
                ["hash_db_autor"] = hashHit?.Autor ?? "",
                ["hash_db_arquivo"] = hashHit?.Arquivo ?? "",
                ["hash_db_quesitos"] = hashHit?.Quesitos ?? "",
                ["is_despacho"] = isDespacho,
                ["is_certidao"] = isCertidao,
                ["is_despacho_valid"] = isDespachoValid,
                ["is_despacho_short"] = isDespachoShort,
                ["is_certidao_valid"] = isCertidaoValid,
                ["is_requerimento_pagamento_honorarios"] = isRequerimento,
                ["despacho_overlap_ratio"] = despachoMatch != null ? overlapRatio : 0.0,
                ["despacho_overlap_pages"] = despachoMatch != null ? overlapPages : 0,
                ["despacho_match_score"] = despachoMatch != null ? despachoMatch.MatchScore : 0.0,
                ["certidao_overlap_ratio"] = certidaoMatch != null ? certOverlapRatio : 0.0,
                ["certidao_overlap_pages"] = certidaoMatch != null ? certOverlapPages : 0,
                ["certidao_match_score"] = certidaoMatch != null ? certidaoMatch.MatchScore : 0.0,
                ["despacho_tipo"] = isDespacho ? despachoInfo.Tipo : "-",
                ["despacho_categoria"] = isDespacho ? despachoInfo.Categoria : "-",
                ["despacho_destino"] = isDespacho ? despachoInfo.Destino : "-",
                ["is_despacho_autorizacao"] = isDespacho && despachoInfo.Categoria == "autorizacao",
                ["is_despacho_encaminhamento"] = isDespacho && despachoInfo.Categoria == "encaminhamento",
                ["is_despacho_georc"] = isDespacho && despachoInfo.Destino == "georc",
                ["is_despacho_conselho"] = isDespacho && despachoInfo.Destino == "conselho",
                ["modification_dates"] = analysis.ModificationDates,
                ["start_page"] = d.StartPage,
                ["end_page"] = d.EndPage,
                ["doc_pages"] = d.PageCount,
                ["total_pages"] = analysis.DocumentInfo.TotalPages,
                ["text"] = docText,
                ["fonts"] = fonts.ToArray(),
                ["images"] = images,
                ["page_size"] = pageSize,
                ["has_signature_image"] = hasSignature,
                ["is_attachment"] = false,
                ["word_count"] = wordCount,
                ["char_count"] = charCount,
                ["text_density"] = textDensity,
                ["blank_ratio"] = blankRatio,
                ["words"] = wordsWithCoords,
                ["header"] = header,
                ["footer"] = footer,
                ["footer_signature_raw"] = footerSignatureRaw,
                ["bookmarks"] = docBookmarks,
                ["anexos_bookmarks"] = anexos,
                ["fields"] = normalizedFields,
                ["band_fields"] = bandFields,
                ["despacho_extraction"] = attachDespacho ? despachoMatch : null,
                ["certidao_extraction"] = attachCertidao ? certidaoMatch : null,
                ["forensics"] = forensics
            };
            foreach (var kv in docSummary)
            {
                if (!obj.ContainsKey(kv.Key))
                    obj[kv.Key] = kv.Value;
            }
            if (debug)
            {
                obj["_debug"] = new Dictionary<string, object>
                {
                    ["first_page_text"] = Truncate(analysis.Pages[d.StartPage - 1].TextInfo.PageText ?? "", 2000),
                    ["last_page_text"] = Truncate(lastPageText, 2000),
                    ["last_two_text"] = Truncate(lastTwoText, 4000),
                    ["footer_signature_raw"] = Truncate(footerSignatureRaw, 2000)
                };
            }
            return obj;
        }

        private Dictionary<string, object> BuildFieldsOnlyDoc(Dictionary<string, object> doc)
        {
            var fields = doc.TryGetValue("fields", out var fobj) && fobj is List<Dictionary<string, object>> flist
                ? flist
                : new List<Dictionary<string, object>>();

            string Get(string name) => PickBestFieldValue(fields, name);

            var vJz = Get("VALOR_ARBITRADO_JZ");
            var vDe = Get("VALOR_ARBITRADO_DE");
            var vCm = Get("VALOR_ARBITRADO_CM");
            var vGeral = SumMoney(vJz, vDe, vCm);

            return new Dictionary<string, object>
            {
                ["process"] = doc.GetValueOrDefault("process") ?? "",
                ["doc_label"] = doc.GetValueOrDefault("doc_label") ?? "",
                ["doc_type"] = doc.GetValueOrDefault("doc_type") ?? "",
                ["start_page"] = doc.GetValueOrDefault("start_page") ?? 0,
                ["end_page"] = doc.GetValueOrDefault("end_page") ?? 0,
                ["processo_administrativo"] = Get("PROCESSO_ADMINISTRATIVO"),
                ["processo_judicial"] = Get("PROCESSO_JUDICIAL"),
                ["perito"] = Get("PERITO"),
                ["perito_cpf"] = Get("CPF_PERITO"),
                ["especialidade"] = Get("ESPECIALIDADE"),
                ["especie_pericia"] = Get("ESPECIE_DA_PERICIA"),
                ["valor_arbitrado_jz"] = vJz,
                ["valor_arbitrado_de"] = vDe,
                ["valor_arbitrado_cm"] = vCm,
                ["valor_arbitrado_geral"] = vGeral,
                ["comarca"] = Get("COMARCA"),
                ["vara"] = Get("VARA")
            };
        }

        private string PickBestFieldValue(List<Dictionary<string, object>> fields, string name)
        {
            if (fields == null || fields.Count == 0) return "";
            var hits = fields.Where(f =>
            {
                var n = f.GetValueOrDefault("name")?.ToString() ?? "";
                return string.Equals(n, name, StringComparison.OrdinalIgnoreCase);
            }).ToList();
            if (hits.Count == 0) return "";
            var best = hits
                .Select(h => new
                {
                    value = h.GetValueOrDefault("value")?.ToString() ?? "",
                    weight = TryToDouble(h.GetValueOrDefault("weight")),
                    page = h.GetValueOrDefault("page")?.ToString() ?? ""
                })
                .OrderByDescending(x => x.weight)
                .ThenByDescending(x => x.value.Length)
                .FirstOrDefault();
            return best?.value ?? "";
        }

        private double TryToDouble(object? v)
        {
            if (v == null) return 0;
            if (v is double d) return d;
            if (v is float f) return f;
            if (v is decimal m) return (double)m;
            if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r)) return r;
            return 0;
        }

        private string SumMoney(params string[] values)
        {
            double sum = 0;
            int count = 0;
            foreach (var v in values)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                {
                    sum += d;
                    count++;
                }
            }
            if (count == 0) return "";
            return sum.ToString("0.00", CultureInfo.InvariantCulture);
        }

        // Doc type classification removida: usamos apenas o nome do bookmark como rótulo.

        private Dictionary<string, object> BuildDocSummary(DocumentBoundary d, string pdfPath, string fullText, string lastPageText, string lastTwoText, string header, string footer, string footerSignatureRaw, List<Dictionary<string, object>> bookmarks, List<DigitalSignature> signatures, string docLabel, List<Dictionary<string, object>> words)
        {
            string originMain = ExtractOrigin(header, bookmarks, fullText, excludeGeneric: true);
            string originSub = ExtractSubOrigin(header, bookmarks, fullText, originMain, excludeGeneric: true);
            string originExtra = ExtractExtraOrigin(header, bookmarks, fullText, originMain, originSub);
            var sei = ExtractSeiMetadata(fullText, lastTwoText, footer, docLabel);
            string signer = sei.Signer ?? ExtractSigner(lastTwoText, footer, footerSignatureRaw, signatures);
            string signedAt = sei.SignedAt ?? ExtractSignedAt(lastTwoText, footer, footerSignatureRaw);
            string dateFooter = ExtractDateFromFooter(lastTwoText, footer, header, footerSignatureRaw);
            string headerHash = HashText(header);
            string template = docLabel; // não classificar; manter o nome do bookmark
            string title = ExtractTitle(header, bookmarks, fullText, originMain, originSub);
            var paras = BuildParagraphsFromWords(words);
            var party = ExtractPartyInfo(fullText);
            var partyBBoxes = ExtractPartyBBoxes(paras, d.StartPage, d.EndPage);
            var procInfo = ExtractProcessInfo(paras, sei.Process);

            return new Dictionary<string, object>
            {
                ["origin_main"] = originMain,
                ["origin_sub"] = originSub,
                ["origin_extra"] = originExtra,
                ["signer"] = signer,
                ["signed_at"] = signedAt,
                ["header_hash"] = headerHash,
                ["title"] = title,
                ["template"] = template,
                ["footer_signature_raw"] = footerSignatureRaw,
                ["sei_process"] = string.IsNullOrWhiteSpace(sei.Process) ? procInfo.ProcessNumber : sei.Process,
                ["sei_doc"] = sei.DocNumber,
                ["sei_crc"] = sei.CRC,
                ["sei_verifier"] = sei.Verifier,
                ["auth_url"] = sei.AuthUrl,
                ["date_footer"] = dateFooter,
                ["process_line"] = procInfo.ProcessLine,
                ["process_bbox"] = procInfo.ProcessBBox,
                ["interested_line"] = party.InterestedLine,
                ["interested_name"] = party.InterestedName,
                ["interested_profession"] = party.InterestedProfession,
                ["interested_email"] = party.InterestedEmail,
                ["juizo_line"] = party.JuizoLine,
                ["juizo_vara"] = party.JuizoVara,
                ["comarca"] = party.Comarca,
                ["interested_bbox"] = partyBBoxes.InterestedBBox,
                ["juizo_bbox"] = partyBBoxes.JuizoBBox
            };
        }

        private (string InterestedLine, string InterestedName, string InterestedProfession, string InterestedEmail, string JuizoLine, string JuizoVara, string Comarca) ExtractPartyInfo(string fullText)
        {
            string interestedLine = "";
            string interestedName = "";
            string interestedProf = "";
            string interestedEmail = "";
            string juizoLine = "";
            string juizoVara = "";
            string comarca = "";

            var lines = (fullText ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

            foreach (var line in lines)
            {
                var m = Regex.Match(line, @"^(interessad[oa])\s*:\s*(.+)$", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    interestedLine = line;
                    var rest = m.Groups[2].Value.Trim();
                    var em = Regex.Match(rest, @"([\w.+-]+@[\w.-]+)");
                    if (em.Success) interestedEmail = em.Groups[1].Value;
                    var parts = Regex.Split(rest, @"\s[-–]\s");
                    if (parts.Length > 0) interestedName = parts[0].Trim();
                    if (parts.Length > 1) interestedProf = parts[1].Trim();
                    break;
                }
            }

            foreach (var line in lines)
            {
                var m = Regex.Match(line, @"^(ju[ií]zo|vara).*", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    juizoLine = line;
                    var vm = Regex.Match(line, @"(ju[ií]zo\s+da\s+|ju[ií]zo\s+do\s+|vara\s+)([^,]+)", RegexOptions.IgnoreCase);
                    if (vm.Success) juizoVara = vm.Groups[2].Value.Trim();
                    var cm = Regex.Match(line, @"comarca\s+de\s+([^,\-]+)", RegexOptions.IgnoreCase);
                    if (cm.Success) comarca = cm.Groups[1].Value.Trim();
                    break;
                }
            }

            return (interestedLine, interestedName, interestedProf, interestedEmail, juizoLine, juizoVara, comarca);
        }

        private (Dictionary<string, double> InterestedBBox, Dictionary<string, double> JuizoBBox) ExtractPartyBBoxes(ParagraphObj[] paras, int startPage, int endPage)
        {
            Dictionary<string, double> interested = null;
            Dictionary<string, double> juizo = null;

            foreach (var p in paras)
            {
                if (p.Page < startPage || p.Page > endPage) continue;
                var text = p.Text ?? "";
                if (interested == null && Regex.IsMatch(text, @"^(interessad[oa])\s*:", RegexOptions.IgnoreCase))
                {
                    interested = new Dictionary<string, double> { ["nx0"] = p.NX0, ["ny0"] = p.Ny0, ["nx1"] = p.NX1, ["ny1"] = p.Ny1 };
                }
                if (juizo == null && Regex.IsMatch(text, @"^(ju[ií]zo|vara)", RegexOptions.IgnoreCase))
                {
                    juizo = new Dictionary<string, double> { ["nx0"] = p.NX0, ["ny0"] = p.Ny0, ["nx1"] = p.NX1, ["ny1"] = p.Ny1 };
                }
                if (interested != null && juizo != null) break;
            }

            return (interested, juizo);
        }

        private (string ProcessLine, string ProcessNumber, Dictionary<string, double> ProcessBBox) ExtractProcessInfo(ParagraphObj[] paras, string fallbackProcess)
        {
            foreach (var p in paras)
            {
                var text = p.Text ?? "";
                var m = Regex.Match(text, @"processo\s*n[º°]?\s*:?\s*([\d\.\-\/]+)", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var bbox = new Dictionary<string, double> { ["nx0"] = p.NX0, ["ny0"] = p.Ny0, ["nx1"] = p.NX1, ["ny1"] = p.Ny1 };
                    return (text, m.Groups[1].Value.Trim(), bbox);
                }
            }
            return ("", fallbackProcess ?? "", null);
        }

        private class SeiMeta
        {
            public string Process { get; set; }
            public string DocNumber { get; set; }
            public string CRC { get; set; }
            public string Verifier { get; set; }
            public string SignedAt { get; set; }
            public string Signer { get; set; }
            public string AuthUrl { get; set; }
        }

        private SeiMeta ExtractSeiMetadata(string fullText, string lastTwoText, string footer, string docLabel)
        {
            var meta = new SeiMeta();
            // Usar apenas o texto das duas últimas páginas + footer para evitar capturar datas irrelevantes (ex.: nascimento)
            string hay = $"{lastTwoText}\n{footer}";

            // Processo SEI (formato com hífens/pontos)
            var mProc = Regex.Match(hay, @"Processo\s+n[º°]?\s*([\d]{6,7}-\d{2}\.\d{4}\.\d\.\d{2}(?:\.\d{4})?)", RegexOptions.IgnoreCase);
            if (!mProc.Success)
                mProc = Regex.Match(hay, @"SEI\s+([\d]{6,7}-\d{2}\.\d{4}\.\d\.\d{2}(?:\.\d{4})?)", RegexOptions.IgnoreCase);
            if (mProc.Success) meta.Process = mProc.Groups[1].Value.Trim();

            // Número da peça SEI (doc)
            var mDoc = Regex.Match(hay, @"SEI\s*n[º°]?\s*([0-9]{4,})", RegexOptions.IgnoreCase);
            if (!mDoc.Success)
                mDoc = Regex.Match(docLabel ?? "", @"\((\d{4,})\)");
            if (mDoc.Success) meta.DocNumber = mDoc.Groups[1].Value.Trim();

            var mCRC = Regex.Match(hay, @"CRC\s+([A-Z0-9]+)", RegexOptions.IgnoreCase);
            if (mCRC.Success) meta.CRC = mCRC.Groups[1].Value.Trim();

            var mVer = Regex.Match(hay, @"verificador\s+([0-9]{4,})", RegexOptions.IgnoreCase);
            if (mVer.Success) meta.Verifier = mVer.Groups[1].Value.Trim();

            if (hay.Contains("assinado eletronicamente", StringComparison.OrdinalIgnoreCase))
            {
                var mSigner = Regex.Match(hay, @"Documento assinado eletronicamente por\s+(.+?),\s*(.+?),\s*em", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (mSigner.Success)
                    meta.Signer = $"{mSigner.Groups[1].Value.Trim()} - {mSigner.Groups[2].Value.Trim()}";
                else
                {
                    var line = lastTwoText.Split('\n').Select(l => l.Trim()).Reverse().FirstOrDefault(l => l.Contains("–"));
                    if (!string.IsNullOrWhiteSpace(line)) meta.Signer = line;
                }

                // Data/hora logo após a frase de assinatura
                var mDate = Regex.Match(hay, @"assinado eletronicamente.*?em\s*([0-9]{2}/[0-9]{2}/[0-9]{4}).*?([0-9]{2}:[0-9]{2})", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (mDate.Success)
                {
                    var s = $"{mDate.Groups[1].Value} {mDate.Groups[2].Value}";
                    if (DateTime.TryParse(s, new System.Globalization.CultureInfo("pt-BR"), DateTimeStyles.AssumeLocal, out var dt))
                        meta.SignedAt = dt.ToString("yyyy-MM-dd HH:mm");
                }
                else
                {
                    // Data por extenso (ex.: 22 de julho de 2024)
                    var ext = Regex.Match(hay, @"(\d{1,2})\s+de\s+(janeiro|fevereiro|març|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\s+de\s+(\d{4})", RegexOptions.IgnoreCase);
                    if (ext.Success)
                    {
                        var dia = ext.Groups[1].Value.PadLeft(2, '0');
                        var mes = ext.Groups[2].Value.ToLower()
                            .Replace("març", "marco");
                        var ano = ext.Groups[3].Value;
                        var meses = new Dictionary<string, string>
                        {
                            ["janeiro"]="01",["fevereiro"]="02",["marco"]="03",["abril"]="04",["maio"]="05",["junho"]="06",
                            ["julho"]="07",["agosto"]="08",["setembro"]="09",["outubro"]="10",["novembro"]="11",["dezembro"]="12"
                        };
                        if (meses.TryGetValue(mes, out var mnum))
                            meta.SignedAt = $"{ano}-{mnum}-{dia}";
                    }
                }
            }

            var mUrl = Regex.Match(hay, @"https?://\S+autentica\S*", RegexOptions.IgnoreCase);
            if (mUrl.Success) meta.AuthUrl = mUrl.Value.TrimEnd('.', ',');

            return meta;
        }

        // FORÊNSE
        private Dictionary<string, object> BuildForensics(DocumentBoundary d, PDFAnalysisResult analysis, string docText, List<Dictionary<string, object>> words)
        {
            var result = new Dictionary<string, object>();

            // clusterizar linhas (y) por página
            var lineObjs = BuildLines(words);
            // parágrafos (forensics)
            var paragraphs = BuildParagraphsFromWords(words);

            // fontes/tamanhos dominantes
            var fontNames = words.Select(w => w["font"]?.ToString() ?? "").Where(f => f != "").ToList();
            var fontSizes = words.Select(w => Convert.ToDouble(w["size"])).Where(s => s > 0).ToList();
            string dominantFont = Mode(fontNames);
            double dominantSize = Median(fontSizes);

            // outliers: linha cujo tamanho médio difere >20% ou fonte não dominante
            var outliers = lineObjs.Where(l =>
            {
                double sz = l.FontSizeAvg;
                bool sizeOut = dominantSize > 0 && Math.Abs(sz - dominantSize) / dominantSize > 0.2;
                bool fontOut = !string.IsNullOrEmpty(dominantFont) && !string.Equals(l.Font, dominantFont, StringComparison.OrdinalIgnoreCase);
                return sizeOut || fontOut;
            }).Take(10).ToList();

            // linhas repetidas (hash texto)
            var repeats = lineObjs.GroupBy(l => l.TextNorm)
                                  .Where(g => g.Count() > 1)
                                  .Select(g => new { text = g.Key, count = g.Count() })
                                  .OrderByDescending(x => x.count)
                                  .Take(10)
                                  .ToList();

            // anchors: assinatura, verificador, crc, rodapé SEI
            var anchors = DetectAnchors(lineObjs);

            // bandas header/subheader/body1/body2/footer
            var bands = SummarizeBands(lineObjs);

            // anotações agregadas
            var ann = SummarizeAnnotations(analysis);

            result["font_dominant"] = dominantFont;
            result["size_dominant"] = dominantSize;
            result["outlier_lines"] = outliers.Select(l => l.ToDict()).ToList();
            result["repeat_lines"] = repeats;
            result["anchors"] = anchors;
            result["bands"] = bands;
            result["paragraphs"] = paragraphs.Select(p => new
            {
                page = p.Page,
                nx0 = p.NX0,
                ny0 = p.Ny0,
                nx1 = p.NX1,
                ny1 = p.Ny1,
                text = p.Text,
                tokens = p.Tokens
            }).ToList();
            result["annotations"] = ann;

            return result;
        }

        private List<LineObj> BuildLines(List<Dictionary<string, object>> words)
        {
            var lines = new List<LineObj>();
            var byPage = words.GroupBy(w => Convert.ToInt32(w["page"]));
            foreach (var pg in byPage)
            {
                var clusters = ClusterLines(pg.ToList());
                foreach (var c in clusters)
                {
                    var ordered = c.OrderBy(w => Convert.ToDouble(w["x0"])).ToList();
                    string text = string.Join(" ", ordered.Select(w => w["text"].ToString()));
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    double nx0 = ordered.Min(w => Convert.ToDouble(w["nx0"]));
                    double ny0 = ordered.Min(w => Convert.ToDouble(w["ny0"]));
                    double nx1 = ordered.Max(w => Convert.ToDouble(w["nx1"]));
                    double ny1 = ordered.Max(w => Convert.ToDouble(w["ny1"]));
                    var fonts = ordered.Select(w => w["font"]?.ToString() ?? "").Where(f => f != "").ToList();
                    var sizes = ordered.Select(w => Convert.ToDouble(w["size"])).Where(s => s > 0).ToList();

                    lines.Add(new LineObj
                    {
                        Page = pg.Key,
                        Text = text.Trim(),
                        TextNorm = Regex.Replace(text.ToLowerInvariant(), @"\\s+", " ").Trim(),
                        NX0 = nx0, NX1 = nx1, NY0 = ny0, NY1 = ny1,
                        Font = Mode(fonts),
                        FontSizeAvg = sizes.Count > 0 ? sizes.Average() : 0
                    });
                }
            }
            return lines;
        }

        private List<List<Dictionary<string, object>>> ClusterLines(List<Dictionary<string, object>> words, double tol = 1.5)
        {
            var groups = new List<List<Dictionary<string, object>>>();
            foreach (var w in words.OrderByDescending(w => Convert.ToDouble(w["y0"])))
            {
                double y = Convert.ToDouble(w["y0"]);
                bool placed = false;
                foreach (var g in groups)
                {
                    double gy = g.Average(x => Convert.ToDouble(x["y0"]));
                    if (Math.Abs(gy - y) <= tol)
                    {
                        g.Add(w);
                        placed = true;
                        break;
                    }
                }
                if (!placed) groups.Add(new List<Dictionary<string, object>> { w });
            }
            return groups;
        }

        private Dictionary<string, object> DetectAnchors(List<LineObj> lines)
        {
            var anchors = new Dictionary<string, object>();
            anchors["signature"] = FindAnchor(lines, "documento assinado eletronicamente");
            anchors["verifier"] = FindAnchor(lines, "código verificador");
            anchors["crc"] = FindAnchor(lines, "crc");
            anchors["sei_footer"] = FindAnchor(lines, " / pg");
            return anchors;
        }

        // -------- Paragraph stats across all docs --------

        private List<object> BuildParagraphStats(List<List<Dictionary<string, object>>> allDocsWords)
        {
            if (allDocsWords == null || allDocsWords.Count == 0)
                return new List<object>();

            var paragraphsPerDoc = allDocsWords.Select(BuildParagraphsFromWords).ToList();
            if (paragraphsPerDoc.Count == 0)
                return new List<object>();

            int maxPars = paragraphsPerDoc.Max(p => p.Length);
            var stats = new List<object>();

            for (int idx = 0; idx < maxPars; idx++)
            {
                int docn = 0;
                var df2 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var tf2 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var df3 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var tf3 = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var pars in paragraphsPerDoc)
                {
                    if (idx >= pars.Length) continue;
                    docn++;
                    var tokens = pars[idx].Tokens;
                    var seen2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var seen3 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var bg in Ngrams(tokens, 2))
                    {
                        tf2[bg] = tf2.TryGetValue(bg, out var v) ? v + 1 : 1;
                        seen2.Add(bg);
                    }
                    foreach (var tr in Ngrams(tokens, 3))
                    {
                        tf3[tr] = tf3.TryGetValue(tr, out var v) ? v + 1 : 1;
                        seen3.Add(tr);
                    }
                    foreach (var bg in seen2)
                        df2[bg] = df2.TryGetValue(bg, out var v) ? v + 1 : 1;
                    foreach (var tr in seen3)
                        df3[tr] = df3.TryGetValue(tr, out var v) ? v + 1 : 1;
                }

                int thresholdStable = (int)Math.Ceiling(docn * 0.6);
                int thresholdVariable = (int)Math.Floor(docn * 0.2);

                var stable2 = df2.Where(kv => kv.Value >= thresholdStable)
                                 .OrderByDescending(kv => kv.Value)
                                 .ThenByDescending(kv => tf2[kv.Key])
                                 .ThenBy(kv => kv.Key)
                                 .Take(20)
                                 .Select(kv => new { ngram = kv.Key, docfreq = kv.Value, tf = tf2[kv.Key] });

                var stable3 = df3.Where(kv => kv.Value >= thresholdStable)
                                 .OrderByDescending(kv => kv.Value)
                                 .ThenByDescending(kv => tf3[kv.Key])
                                 .ThenBy(kv => kv.Key)
                                 .Take(20)
                                 .Select(kv => new { ngram = kv.Key, docfreq = kv.Value, tf = tf3[kv.Key] });

                var variable2 = df2.Where(kv => kv.Value <= thresholdVariable)
                                   .OrderBy(kv => kv.Value)
                                   .ThenByDescending(kv => tf2[kv.Key])
                                   .ThenBy(kv => kv.Key)
                                   .Take(20)
                                   .Select(kv => new { ngram = kv.Key, docfreq = kv.Value, tf = tf2[kv.Key] });

                var variable3 = df3.Where(kv => kv.Value <= thresholdVariable)
                                   .OrderBy(kv => kv.Value)
                                   .ThenByDescending(kv => tf3[kv.Key])
                                   .ThenBy(kv => kv.Key)
                                   .Take(20)
                                   .Select(kv => new { ngram = kv.Key, docfreq = kv.Value, tf = tf3[kv.Key] });

                stats.Add(new
                {
                    paragraph = idx + 1,
                    docs_with_par = docn,
                    stable_bigrams = stable2,
                    stable_trigrams = stable3,
                    variable_bigrams = variable2,
                    variable_trigrams = variable3
                });
            }

            return stats;
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

        private ParagraphObj[] BuildParagraphsFromWords(List<Dictionary<string, object>> words)
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

            return paras.OrderByDescending(p => p.Page)
                        .ThenByDescending(p => p.Ny0)
                        .ToArray();
        }

        private string RebuildLine(List<Dictionary<string, object>> ws, double spaceFactor = 0.6)
        {
            if (ws == null || ws.Count == 0) return "";
            var sorted = ws.OrderBy(w => Convert.ToDouble(w["x0"])).ToList();
            string result = sorted[0]["text"].ToString();
            double avgW = sorted.Average(w => Convert.ToDouble(w["x1"]) - Convert.ToDouble(w["x0"]));
            for (int i = 1; i < sorted.Count; i++)
            {
                double gap = Convert.ToDouble(sorted[i]["x0"]) - Convert.ToDouble(sorted[i - 1]["x1"]);
                int spaces = (gap > avgW * 0.2) ? Math.Max(1, (int)(gap / (avgW * spaceFactor))) : 0;
                result += new string(' ', spaces) + sorted[i]["text"];
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

        private IEnumerable<string> Ngrams(List<string> tokens, int n)
        {
            if (tokens == null || tokens.Count < n) yield break;
            for (int i = 0; i <= tokens.Count - n; i++)
                yield return string.Join(" ", tokens.GetRange(i, n));
        }

        private object SummarizeBands(List<LineObj> lines)
        {
            var bands = new Dictionary<string, object>();
            bands["header"] = SummarizeBand(lines.Where(l => l.NY0 >= 0.85).ToList());
            bands["subheader"] = SummarizeBand(lines.Where(l => l.NY0 >= 0.78 && l.NY0 < 0.85).ToList());
            bands["body1"] = SummarizeBand(lines.Where(l => l.NY0 >= 0.55 && l.NY0 < 0.78).ToList());
            bands["body2"] = SummarizeBand(lines.Where(l => l.NY0 >= 0.30 && l.NY0 < 0.55).ToList());
            bands["footer_band"] = SummarizeBand(lines.Where(l => l.NY0 < 0.30).ToList());
            return bands;
        }

        private object SummarizeBand(List<LineObj> lines)
        {
            if (lines == null || lines.Count == 0) return new { count = 0 };
            var nx0 = lines.Select(l => l.NX0).ToList();
            var ny0 = lines.Select(l => l.NY0).ToList();
            var nx1 = lines.Select(l => l.NX1).ToList();
            var ny1 = lines.Select(l => l.NY1).ToList();
            var fonts = lines.Select(l => l.Font ?? "").Where(f => f != "").ToList();
            var sizes = lines.Select(l => l.FontSizeAvg).Where(s => s > 0).ToList();

            return new
            {
                count = lines.Count,
                bbox = new
                {
                    nx0 = Median(nx0),
                    ny0 = Median(ny0),
                    nx1 = Median(nx1),
                    ny1 = Median(ny1),
                    p25 = new { nx0 = Quantile(nx0, 0.25), ny0 = Quantile(ny0, 0.25), nx1 = Quantile(nx1, 0.25), ny1 = Quantile(ny1, 0.25) },
                    p75 = new { nx0 = Quantile(nx0, 0.75), ny0 = Quantile(ny0, 0.75), nx1 = Quantile(nx1, 0.75), ny1 = Quantile(ny1, 0.75) }
                },
                font = Mode(fonts),
                size = Median(sizes),
                samples = lines.Take(5).Select(l => l.Text).ToList()
            };
        }

        private object FindAnchor(List<LineObj> lines, string needle)
        {
            var hits = lines.Where(l => l.TextNorm.Contains(needle.ToLowerInvariant())).ToList();
            if (!hits.Any()) return new { found = false };
            return new
            {
                found = true,
                count = hits.Count,
                bbox = new
                {
                    nx0 = Median(hits.Select(h => h.NX0).ToList()),
                    ny0 = Median(hits.Select(h => h.NY0).ToList()),
                    nx1 = Median(hits.Select(h => h.NX1).ToList()),
                    ny1 = Median(hits.Select(h => h.NY1).ToList())
                },
                samples = hits.Take(3).Select(h => h.Text).ToList()
            };
        }

        private object SummarizeAnnotations(PDFAnalysisResult analysis)
        {
            var anns = analysis.Pages.SelectMany(p => p.Annotations ?? new List<Annotation>()).ToList();
            var byType = anns.GroupBy(a => a.Type ?? "").ToDictionary(g => g.Key, g => g.Count());
            DateTime? min = anns.Where(a => a.ModificationDate.HasValue).Select(a => a.ModificationDate).DefaultIfEmpty(null).Min();
            DateTime? max = anns.Where(a => a.ModificationDate.HasValue).Select(a => a.ModificationDate).DefaultIfEmpty(null).Max();
            return new
            {
                count = anns.Count,
                by_type = byType,
                date_min = min,
                date_max = max
            };
        }

        private string Mode(List<string> items)
        {
            var clean = items?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
            if (clean.Count == 0) return "";
            return clean.GroupBy(x => x.ToLowerInvariant())
                        .OrderByDescending(g => g.Count())
                        .Select(g => g.Key)
                        .FirstOrDefault();
        }

        private double Median(List<double> items)
        {
            if (items == null || items.Count == 0) return 0;
            items = items.OrderBy(x => x).ToList();
            int n = items.Count;
            if (n % 2 == 1) return items[n / 2];
            return (items[n / 2 - 1] + items[n / 2]) / 2.0;
        }

        private double Quantile(List<double> items, double q)
        {
            if (items == null || items.Count == 0) return 0;
            items = items.OrderBy(x => x).ToList();
            double pos = (items.Count - 1) * q;
            int idx = (int)pos;
            double frac = pos - idx;
            if (idx + 1 < items.Count)
                return items[idx] * (1 - frac) + items[idx + 1] * frac;
            return items[idx];
        }

        private class LineObj
        {
            public int Page { get; set; }
            public string Text { get; set; }
            public string TextNorm { get; set; }
            public double NX0 { get; set; }
            public double NX1 { get; set; }
            public double NY0 { get; set; }
            public double NY1 { get; set; }
            public string Font { get; set; }
            public double FontSizeAvg { get; set; }

            public Dictionary<string, object> ToDict() => new Dictionary<string, object>
            {
                ["page"] = Page,
                ["text"] = Text,
                ["nx0"] = NX0,
                ["ny0"] = NY0,
                ["nx1"] = NX1,
                ["ny1"] = NY1,
                ["font"] = Font,
                ["size_avg"] = FontSizeAvg
            };
        }

        private string CompactFooter(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            // remove espaçamento intercalado (ex.: "D e s p a c h o" -> "Despacho")
            var noSpaces = Regex.Replace(text, @"\s{1}", " ");
            var collapsed = Regex.Replace(noSpaces, @"\s+", " ").Trim();
            var chars = collapsed.ToCharArray();
            var sb = new StringBuilder(chars.Length);
            int consecutiveSingles = 0;
            foreach (var c in chars)
            {
                if (c == ' ')
                {
                    consecutiveSingles++;
                    if (consecutiveSingles > 1) continue;
                }
                else
                {
                    consecutiveSingles = 0;
                }
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        private readonly string[] GenericOrigins = new[]
        {
            "PODER JUDICIÁRIO",
            "PODER JUDICIARIO",
            "TRIBUNAL DE JUSTIÇA",
            "TRIBUNAL DE JUSTICA",
            "MINISTÉRIO PÚBLICO",
            "MINISTERIO PUBLICO",
            "DEFENSORIA PÚBLICA",
            "DEFENSORIA PUBLICA",
            "PROCURADORIA",
            "ESTADO DA PARAÍBA",
            "ESTADO DA PARAIBA",
            "GOVERNO DO ESTADO"
        };

        private bool IsGeneric(string text)
        {
            var t = (text ?? "").Trim();
            return GenericOrigins.Any(g => string.Equals(g, t, StringComparison.OrdinalIgnoreCase));
        }

        private bool IsCandidateTitle(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var lower = line.ToLowerInvariant();
            string[] kws = { "despacho", "certidão", "certidao", "sentença", "sentenca", "decisão", "decisao", "ofício", "oficio", "laudo", "nota de empenho", "autorização", "autorizacao", "requisição", "requisicao" };
            return kws.Any(k => lower.Contains(k));
        }

        private string ExtractOrigin(string header, List<Dictionary<string, object>> bookmarks, string text, bool excludeGeneric)
        {
            // 1) header em maiúsculas é o melhor candidato
            if (!string.IsNullOrWhiteSpace(header))
            {
                var lines = header.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 3).ToList();
                var upper = lines.FirstOrDefault(l => l.All(c => !char.IsLetter(c) || char.IsUpper(c)));
                if (!string.IsNullOrWhiteSpace(upper) && (!excludeGeneric || !IsGeneric(upper))) return upper;
                var first = lines.FirstOrDefault(l => !excludeGeneric || !IsGeneric(l));
                if (!string.IsNullOrWhiteSpace(first)) return first;
            }

            // 2) bookmark de nível 1
            var bm = bookmarks.FirstOrDefault(b =>
            {
                if (b.TryGetValue("level", out var lvlObj) && int.TryParse(lvlObj?.ToString(), out var lvl))
                    return lvl <= 1;
                return false;
            });
            if (bm != null && bm.TryGetValue("title", out var t) && t != null)
            {
                var val = t.ToString() ?? "";
                if (!excludeGeneric || !IsGeneric(val)) return val;
            }

            // 3) primeira linha do texto
            var firstLine = (text ?? "").Split('\n').FirstOrDefault() ?? "";
            if (excludeGeneric && IsGeneric(firstLine)) return "";
            return firstLine;
        }

        private string ExtractSubOrigin(string header, List<Dictionary<string, object>> bookmarks, string text, string originMain, bool excludeGeneric)
        {
            if (!string.IsNullOrWhiteSpace(header))
            {
                var lines = header.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 3).ToList();
                if (lines.Count > 1)
                {
                    var second = lines.Skip(1).FirstOrDefault(l => !string.Equals(l, originMain, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(second) && (!excludeGeneric || !IsGeneric(second))) return second;
                }
            }

            var bmSub = bookmarks.Skip(1).FirstOrDefault();
            if (bmSub != null && bmSub.TryGetValue("title", out var t) && t != null)
            {
                var val = t.ToString() ?? "";
                if (!excludeGeneric || !IsGeneric(val)) return val;
            }

            // fallback: segunda linha do texto
            var secondLine = (text ?? "").Split('\n').Skip(1).FirstOrDefault() ?? "";
            if (excludeGeneric && IsGeneric(secondLine)) return "";
            return secondLine;
        }

        private string ExtractExtraOrigin(string header, List<Dictionary<string, object>> bookmarks, string text, string originMain, string originSub)
        {
            // Procura uma terceira linha de header que não seja genérica nem igual às anteriores
            if (!string.IsNullOrWhiteSpace(header))
            {
                var lines = header.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 3).ToList();
                var extra = lines.Skip(2).FirstOrDefault(l =>
                    !string.Equals(l, originMain, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(l, originSub, StringComparison.OrdinalIgnoreCase) &&
                    !IsGeneric(l));
                if (!string.IsNullOrWhiteSpace(extra)) return extra;
            }

            // Bookmark de nível 2/3 que não seja genérico
            var bm = bookmarks.FirstOrDefault(b =>
            {
                if (b.TryGetValue("level", out var lvlObj) && int.TryParse(lvlObj?.ToString(), out var lvl))
                    return lvl >= 2;
                return false;
            });
            if (bm != null && bm.TryGetValue("title", out var t) && t != null)
            {
                var val = t.ToString() ?? "";
                if (!IsGeneric(val)) return val;
            }

            // fallback: linha do texto com palavras-chave de setor/órgão (ex.: "Diretoria", "Secretaria", etc.)
            var firstNonGeneric = (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l =>
                !string.IsNullOrWhiteSpace(l) &&
                !IsGeneric(l) &&
                !string.Equals(l, originMain, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(l, originSub, StringComparison.OrdinalIgnoreCase));
            return firstNonGeneric ?? "";
        }

        private string ExtractTitle(string header, List<Dictionary<string, object>> bookmarks, string text, string originMain, string originSub)
        {
            // Se o header tiver 3+ linhas, terceira linha costuma ser o título
            if (!string.IsNullOrWhiteSpace(header))
            {
                var lines = header.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 3).ToList();
                var titleFromHeader = lines.Skip(2).FirstOrDefault(l =>
                    !string.Equals(l, originMain, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(l, originSub, StringComparison.OrdinalIgnoreCase) &&
                    !IsGeneric(l) &&
                    IsCandidateTitle(l));
                if (!string.IsNullOrWhiteSpace(titleFromHeader)) return titleFromHeader;
            }

            // Bookmark de nível 2 ou 3 pode conter o título específico
            var bm = bookmarks.FirstOrDefault(b =>
            {
                if (b.TryGetValue("level", out var lvlObj) && int.TryParse(lvlObj?.ToString(), out var lvl))
                    return lvl >= 2;
                return false;
            });
            if (bm != null && bm.TryGetValue("title", out var t) && t != null)
            {
                var val = t.ToString() ?? "";
                if (IsCandidateTitle(val) && !IsGeneric(val)) return val;
            }

            // fallback: primeira linha não vazia do texto (evita repetir origem)
            var firstLine = (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l =>
                !string.IsNullOrWhiteSpace(l) &&
                !string.Equals(l, originMain, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(l, originSub, StringComparison.OrdinalIgnoreCase) &&
                !IsGeneric(l) &&
                IsCandidateTitle(l));
            return firstLine ?? "";
        }

        private string ExtractFooterSignatureRaw(string lastPageText)
        {
            if (string.IsNullOrWhiteSpace(lastPageText)) return "";
            var lines = lastPageText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) return "";

            int start = Math.Max(0, lines.Count - 12);
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (Regex.IsMatch(line, @"assinad|assinatura|verificador|crc|sei|documento|c[oó]digo", RegexOptions.IgnoreCase))
                    start = Math.Min(start, i);
                if (lines.Count - i > 24) break;
            }
            return string.Join("\n", lines.Skip(start));
        }

        private string ExtractSignerFromSignatureLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "";
            var matches = Regex.Matches(line, @"[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+(?:\s+(?:de|da|do|dos|das|e|d')\s+)?[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+(?:\s+[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+){0,4}", RegexOptions.IgnoreCase);
            if (matches.Count == 0) return "";
            var candidate = matches[matches.Count - 1].Value.Trim();
            if (candidate.Length < 6) return "";
            if (IsGeneric(candidate)) return "";
            if (candidate.Any(char.IsDigit)) return "";
            return candidate;
        }

        private string ExtractSigner(string lastPageText, string footer, string footerSignatureRaw, List<DigitalSignature> signatures)
        {
            string signatureBlock = footerSignatureRaw ?? "";
            string[] sources =
            {
                $"{lastPageText}\n{footer}\n{signatureBlock}",
                ReverseText($"{lastPageText}\n{footer}\n{signatureBlock}")
            };

            foreach (var source in sources)
            {
                // "Documento 27 página 2 assinado eletronicamente por: NOME - CPF: ... em 12/06/2024"
                var docPageSigned = Regex.Match(source, @"documento\s+\d+[^\n]{0,120}?assinado[^\n]{0,60}?por\s*[:\-]?\s*([\p{L} .'’\-]+?)(?=\s*(?:,|\(|\bcpf\b|\bem\b|\n|$|-\s*\d))", RegexOptions.IgnoreCase);
                if (docPageSigned.Success) return docPageSigned.Groups[1].Value.Trim();

                // Formato mais completo do SEI: "Documento assinado eletronicamente por NOME, <cargo>, em 12/03/2024"
                var docSigned = Regex.Match(source, @"documento\s+assinado\s+eletronicamente\s+por\s*[:\-]?\s*([\p{L} .'’\-]+?)(?=\s*(?:,|\(|\bcpf\b|\bem\b|\n|$|-\s*\d))", RegexOptions.IgnoreCase);
                if (docSigned.Success) return docSigned.Groups[1].Value.Trim();

                var match = Regex.Match(source, @"assinado(?:\s+digitalmente|\s+eletronicamente)?\s+por\s*[:\-]?\s*([\p{L} .'’\-]+?)(?=\s*(?:,|\(|\bcpf\b|\bem\b|\n|$|-\s*\d))", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();

                // Digital signature block (X.509)
                var sigMatch = Regex.Match(source, @"Assinatura(?:\s+)?:(.+)", RegexOptions.IgnoreCase);
                if (sigMatch.Success) return sigMatch.Groups[1].Value.Trim();
            }

            if (!string.IsNullOrWhiteSpace(signatureBlock))
            {
                var sigLines = signatureBlock.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                for (int i = 0; i < sigLines.Count; i++)
                {
                    var line = sigLines[i];
                    if (!Regex.IsMatch(line, @"assinad|assinatura", RegexOptions.IgnoreCase)) continue;
                    var val = ExtractSignerFromSignatureLine(line);
                    if (!string.IsNullOrWhiteSpace(val)) return val;
                    if (i + 1 < sigLines.Count)
                    {
                        val = ExtractSignerFromSignatureLine(sigLines[i + 1]);
                        if (!string.IsNullOrWhiteSpace(val)) return val;
                    }
                }
            }

            // Info vinda do objeto de assinatura digital (se existir)
            if (signatures != null && signatures.Count > 0)
            {
                var sigName = signatures.Select(s => s.SignerName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                if (!string.IsNullOrWhiteSpace(sigName)) return sigName.Trim();
                var sigField = signatures.Select(s => s.Name).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                if (!string.IsNullOrWhiteSpace(sigField)) return sigField.Trim();
            }

            // Heurística: linha final com nome/cargo em maiúsculas
            var lines = lastPageText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var cargoKeywords = new[] { "diretor", "diretora", "presidente", "juiz", "juíza", "desembargador", "desembargadora", "secretário", "secretaria", "chefe", "coordenador", "coordenadora", "gerente", "perito", "analista", "assessor", "assessora", "procurador", "procuradora" };
            var namePattern = new Regex(@"^[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+(\s+[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+){1,4}(\s*[,–-]\s*.+)?$", RegexOptions.Compiled);
            foreach (var line in lines.AsEnumerable().Reverse())
            {
                if (line.Length < 8 || line.Length > 120) continue;
                if (Regex.IsMatch(line, @"\d{2}[\\/]\d{2}[\\/]\d{2,4}")) continue; // data
                if (line.IndexOf("SEI", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (line.IndexOf("pg.", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("página", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (IsGeneric(line)) continue;
                if (line.ToLowerInvariant() == line) continue; // toda minúscula
                // evita repetir origens ou título
                if (line.Equals(lastPageText, StringComparison.OrdinalIgnoreCase)) continue;
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;
                var lower = line.ToLowerInvariant();
                if (cargoKeywords.Any(k => lower.Contains(k)) || namePattern.IsMatch(line))
                    return line;
            }
            return "";
        }

        private string ReverseText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var arr = text.ToCharArray();
            Array.Reverse(arr);
            return new string(arr);
        }

        private string ExtractSignedAt(string lastPagesText, string footer, string footerSignatureRaw)
        {
            string signatureBlock = footerSignatureRaw ?? "";
            var source = $"{lastPagesText}\n{footer}\n{signatureBlock}";

            var preferredSources = new List<string>();
            if (!string.IsNullOrWhiteSpace(signatureBlock)) preferredSources.Add(signatureBlock);
            preferredSources.Add(source);

            // Prefer datas próximas a termos de assinatura
            foreach (var src in preferredSources)
            {
                if (string.IsNullOrWhiteSpace(src)) continue;
                var windowMatch = Regex.Match(src, @"assinado[\s\S]{0,200}?(\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4})", RegexOptions.IgnoreCase);
                if (windowMatch.Success)
                {
                    var val = NormalizeDate(windowMatch.Groups[1].Value);
                    if (!string.IsNullOrEmpty(val)) return val;
                }
                var emMatch = Regex.Match(src, @"\bem\s+(\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4})(?:\s+\d{1,2}:\d{2})?", RegexOptions.IgnoreCase);
                if (emMatch.Success)
                {
                    var val = NormalizeDate(emMatch.Groups[1].Value);
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }

            // Datas por extenso: 25 de agosto de 2024
            var extensoMatch = Regex.Match(source, @"\\b(\\d{1,2})\\s+de\\s+(janeiro|fevereiro|març|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\\s+de\\s+(\\d{4})\\b", RegexOptions.IgnoreCase);
            if (extensoMatch.Success)
            {
                var val = NormalizeDateExtenso(extensoMatch.Groups[1].Value, extensoMatch.Groups[2].Value, extensoMatch.Groups[3].Value);
                if (!string.IsNullOrEmpty(val)) return val;
            }

            // Fallback: primeira data plausível
            var match = Regex.Match(source, @"\\b(\\d{1,2}[\\/-]\\d{1,2}[\\/-]\\d{2,4})\\b");
            if (match.Success)
            {
                var val = NormalizeDate(match.Groups[1].Value);
                if (!string.IsNullOrEmpty(val)) return val;
            }

            return "";
        }

        private string ExtractDateFromFooter(string lastPagesText, string footer, string header = "", string footerSignatureRaw = "")
        {
            var source = $"{footerSignatureRaw}\n{footer}\n{lastPagesText}\n{header}";
            var dates = new List<DateTime>();

            var signedAt = ExtractSignedAt(lastPagesText, footer, footerSignatureRaw);
            if (!string.IsNullOrWhiteSpace(signedAt) && DateTime.TryParse(signedAt, out var dtSigned))
                dates.Add(dtSigned);

            var extensoMatches = Regex.Matches(source, @"\b(\d{1,2})\s+de\s+(janeiro|fevereiro|març|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\s+de\s+(\d{4})\b", RegexOptions.IgnoreCase);
            foreach (Match m in extensoMatches)
            {
                var val = NormalizeDateExtenso(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
                if (DateTime.TryParse(val, out var dt)) dates.Add(dt);
            }

            var numericMatches = Regex.Matches(source, @"\b(\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4})\b");
            foreach (Match m in numericMatches)
            {
                var val = NormalizeDate(m.Groups[1].Value);
                if (DateTime.TryParse(val, out var dt)) dates.Add(dt);
            }

            if (dates.Count == 0) return "";
            if (!string.IsNullOrWhiteSpace(signedAt) && DateTime.TryParse(signedAt, out var dtSignedFirst))
                return dtSignedFirst.ToString("yyyy-MM-dd");

            var latest = dates.OrderByDescending(d => d).First();
            return latest.ToString("yyyy-MM-dd");
        }

        private string NormalizeDate(string raw)
        {
            string[] formats = { "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "d-M-yyyy", "dd/MM/yy", "d/M/yy", "dd-MM-yy", "d-M-yy" };
            if (DateTime.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            {
                // Filtrar datas implausíveis (anos muito antigos)
                int year = dt.Year;
                int currentYear = DateTime.UtcNow.Year;
                if (year < 1990 || year > currentYear + 1)
                    return "";
                return dt.ToString("yyyy-MM-dd");
            }
            return "";
        }

        private string NormalizeDateExtenso(string dayStr, string monthStr, string yearStr)
        {
            int day, year;
            if (!int.TryParse(dayStr, out day)) return "";
            if (!int.TryParse(yearStr, out year)) return "";
            var month = MonthFromPortuguese(monthStr);
            if (month == 0) return "";
            try
            {
                var dt = new DateTime(year, month, day);
                int currentYear = DateTime.UtcNow.Year;
                if (year < 1990 || year > currentYear + 1) return "";
                return dt.ToString("yyyy-MM-dd");
            }
            catch { return ""; }
        }

        private int MonthFromPortuguese(string month)
        {
            var m = month.ToLowerInvariant();
            switch (m)
            {
                case "janeiro": return 1;
                case "fevereiro": return 2;
                case "março":
                case "marco": return 3;
                case "abril": return 4;
                case "maio": return 5;
                case "junho": return 6;
                case "julho": return 7;
                case "agosto": return 8;
                case "setembro": return 9;
                case "outubro": return 10;
                case "novembro": return 11;
                case "dezembro": return 12;
                default: return 0;
            }
        }

        // ------------------ Simple field extraction (first module) ------------------

        private static readonly Regex CnjRegex = new Regex(@"\b\d{7}-\d{2}\.\d{4}\.\d\.\d{2}\.\d{4}\b", RegexOptions.Compiled);
        private static readonly Regex SeiLikeRegex = new Regex(@"\b\d{6,}\b", RegexOptions.Compiled);
        private static readonly Regex CpfRegex = new Regex(@"\b\d{3}\.\d{3}\.\d{3}-\d{2}\b", RegexOptions.Compiled);
        private static readonly Regex MoneyRegex = new Regex(@"R\$ ?\d{1,3}(\.\d{3})*,\d{2}", RegexOptions.Compiled);
        private static readonly Regex DateNumRegex = new Regex(@"\b[0-3]?\d/[01]?\d/\d{2,4}\b", RegexOptions.Compiled);

        private List<Dictionary<string, object>> ExtractFields(string fullText, List<Dictionary<string, object>> words, DocumentBoundary d, string pdfPath, List<FieldScript> scripts, string? forcedBucket = null)
        {
            var name = ExtractDocumentName(d);
            var bucket = forcedBucket ?? ClassifyBucket(name, fullText);
            var namePdf = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name : name + ".pdf";
            return FieldScripts.RunScripts(scripts, namePdf, fullText ?? string.Empty, words, d.StartPage, bucket);
        }

        private void AddDocSummaryFallbacks(List<Dictionary<string, object>> fields, Dictionary<string, object> docSummary, int page)
        {
            if (fields == null) return;
            string GetStr(string key)
            {
                return docSummary.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
            }

            AddFieldIfMissing(fields, "PROCESSO_ADMINISTRATIVO", GetStr("sei_process"), "doc_meta", 0.55, page);
            AddFieldIfMissing(fields, "VARA", GetStr("juizo_vara"), "doc_meta", 0.55, page);
            AddFieldIfMissing(fields, "COMARCA", GetStr("comarca"), "doc_meta", 0.55, page);
            AddFieldIfMissing(fields, "PERITO", GetStr("interested_name"), "doc_meta", 0.50, page);
            AddFieldIfMissing(fields, "ESPECIALIDADE", GetStr("interested_profession"), "doc_meta", 0.45, page);
            AddFieldIfMissing(fields, "DATA", GetStr("signed_at"), "doc_meta", 0.45, page);
            AddFieldIfMissing(fields, "ASSINANTE", GetStr("signer"), "doc_meta", 0.50, page);
        }

        private void AddFieldIfMissing(List<Dictionary<string, object>> fields, string name, string value, string method, double weight, int page)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) return;
            var existing = fields.FirstOrDefault(f => string.Equals(f.TryGetValue("name", out var n) ? n?.ToString() : "", name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                var existingValue = existing.TryGetValue("value", out var v) ? v?.ToString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(existingValue) && existingValue.Trim() != "-")
                    return;

                existing["value"] = value.Trim();
                existing["method"] = method;
                existing["weight"] = weight;
                existing["page"] = page;
                return;
            }

            fields.Add(new Dictionary<string, object>
            {
                ["name"] = name,
                ["value"] = value.Trim(),
                ["method"] = method,
                ["weight"] = weight,
                ["page"] = page
            });
        }


        private List<Dictionary<string, object>> MergeFields(List<Dictionary<string, object>> primary, List<Dictionary<string, object>> secondary)
        {
            var result = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddRange(List<Dictionary<string, object>> src)
            {
                foreach (var item in src)
                {
                    var key = $"{item.GetValueOrDefault("name")}|{item.GetValueOrDefault("value")}";
                    if (seen.Contains(key)) continue;
                    seen.Add(key);
                    result.Add(item);
                }
            }

            AddRange(primary ?? new List<Dictionary<string, object>>());
            AddRange(secondary ?? new List<Dictionary<string, object>>());
            return result;
        }

        /// <summary>
        /// Extrai valores direcionados por função: JZ (relato inicial), DE (autorização GEORC), CM (certidão CM).
        /// Não depende dos YAML; usa contexto de página/faixa para reduzir ruído.
        /// </summary>
        private List<Dictionary<string, object>> ExtractDirectedValues(PDFAnalysisResult analysis, DocumentBoundary d, string docType, string fullText)
        {
            var hits = new List<Dictionary<string, object>>();
            if (analysis?.Pages == null || analysis.Pages.Count == 0) return hits;

            int firstPage = d.StartPage;
            int lastPage = d.EndPage;
            string page1Text = SafePageText(analysis, firstPage);
            string lastPageText = SafePageText(analysis, lastPage);

            var mJz = Regex.Match(page1Text, @"honor[aá]rios[^\n]{0,120}?R\$\s*([0-9\.]{1,3}(?:\.[0-9]{3})*,\d{2})", RegexOptions.IgnoreCase);
            if (mJz.Success)
                AddValueHit(hits, "VALOR_ARBITRADO_JZ", mJz.Groups[1].Value, firstPage, "direct_jz_page1");

            var mDe = Regex.Match(lastPageText, @"(?:(?:autorizo a despesa)|(?:reserva or[cç]ament[áa]ria)|(?:encaminh?em-se[^\n]{0,80}?GEORC)|(?:proceder à reserva or[cç]ament[áa]ria))[^\n]{0,200}?R\$\s*([0-9\.]{1,3}(?:\.[0-9]{3})*,\d{2})", RegexOptions.IgnoreCase);
            if (mDe.Success)
                AddValueHit(hits, "VALOR_ARBITRADO_DE", mDe.Groups[1].Value, lastPage, "direct_de_lastpage");

            if (!string.IsNullOrWhiteSpace(docType) &&
                (docType.Contains("certidao_cm", StringComparison.OrdinalIgnoreCase) ||
                 docType.Contains("certidao", StringComparison.OrdinalIgnoreCase) ||
                 docType.Contains("certidão", StringComparison.OrdinalIgnoreCase)))
            {
                var mCm = Regex.Match(fullText ?? "", @"(?:autoriza(?:d[oa])?.{0,80}pagamento|despesa)[^\n]{0,160}?R\$\s*([0-9\.]{1,3}(?:\.[0-9]{3})*,\d{2})", RegexOptions.IgnoreCase);
                if (mCm.Success)
                    AddValueHit(hits, "VALOR_ARBITRADO_CM", mCm.Groups[1].Value, lastPage, "direct_cm");
            }

            return hits;
        }

        private string SafePageText(PDFAnalysisResult analysis, int page)
        {
            if (analysis == null || analysis.Pages == null) return "";
            if (page < 1 || page > analysis.Pages.Count) return "";
            return analysis.Pages[page - 1].TextInfo.PageText ?? "";
        }

        private void AddValueHit(List<Dictionary<string, object>> hits, string field, string raw, int page, string pattern)
        {
            raw = raw?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(raw)) return;
            var cleaned = CleanStandard(field, raw);
            if (!ValidateStandard(field, cleaned)) return;
            hits.Add(new Dictionary<string, object>
            {
                ["name"] = field,
                ["value"] = cleaned,
                ["page"] = page,
                ["pattern"] = pattern,
                ["weight"] = 1.0
            });
        }

        private List<Dictionary<string, object>> NormalizeAndValidateFields(List<Dictionary<string, object>> fields)
        {
            if (fields == null) return new List<Dictionary<string, object>>();
            var cleaned = new List<Dictionary<string, object>>();
            double? valorHonor = null;
            string profissaoVal = "";
            string specialtyVal = "";
            string profPeritoLinked = "";
            int peritoPage = 0;

            foreach (var f in fields)
            {
                var rawName = f.GetValueOrDefault("name")?.ToString() ?? "";
                var val = f.GetValueOrDefault("value")?.ToString() ?? "";
                var method = f.GetValueOrDefault("method")?.ToString() ?? "";
                var band = f.GetValueOrDefault("band")?.ToString();

                if (string.IsNullOrWhiteSpace(rawName) || string.IsNullOrWhiteSpace(val)) continue;
                if (val.Trim() == "-") continue;
                if (string.Equals(method, "not_found", StringComparison.OrdinalIgnoreCase)) continue;

                var name = NormalizeFieldNameForPipeline(rawName);
                if (string.IsNullOrWhiteSpace(name)) continue;

                val = TrimAffixes(name, val);

                if (string.Equals(name, "ESPECIE_DA_PERICIA", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (rawName.IndexOf("PROFISS", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    profissaoVal = val;
                    if (int.TryParse(f.GetValueOrDefault("page")?.ToString(), out var pg))
                    {
                        if (peritoPage > 0 && pg == peritoPage)
                            profPeritoLinked = val;
                        else if (peritoPage == 0 && pg == 0)
                            profPeritoLinked = val;
                    }
                }

                if (string.Equals(name, "ESPECIALIDADE", StringComparison.OrdinalIgnoreCase))
                    specialtyVal = string.IsNullOrWhiteSpace(val) ? specialtyVal : val;

                if (string.Equals(name, "PERITO", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(f.GetValueOrDefault("page")?.ToString(), out var pg))
                        peritoPage = pg;
                }

                if (name.StartsWith("VALOR", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "ADIANTAMENTO", StringComparison.OrdinalIgnoreCase))
                {
                    if (double.TryParse(val, NumberStyles.Any, CultureInfo.InvariantCulture, out var vMoney))
                        valorHonor ??= vMoney;
                }

                val = CleanStandard(name, val);
                if (!ValidateStandard(name, val)) continue;
                f["name"] = name;
                f["value"] = val;
                if (!string.IsNullOrWhiteSpace(band)) f["band"] = band;
                cleaned.Add(f);
            }

            cleaned = cleaned.Where(f =>
            {
                var n = f.GetValueOrDefault("name")?.ToString();
                if (!string.Equals(n, "ESPECIALIDADE", StringComparison.OrdinalIgnoreCase)) return true;
                var v = f.GetValueOrDefault("value")?.ToString() ?? "";
                var words = v.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                return v.Length <= 40 && words <= 6;
            }).ToList();

            double? PickMoney(string fieldName)
            {
                var item = cleaned.FirstOrDefault(f => string.Equals(f.GetValueOrDefault("name")?.ToString(), fieldName, StringComparison.OrdinalIgnoreCase));
                if (item == null) return null;
                var v = item.GetValueOrDefault("value")?.ToString() ?? "";
                if (double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
                return null;
            }

            valorHonor = PickMoney("VALOR_ARBITRADO_JZ")
                         ?? PickMoney("VALOR_ARBITRADO_DE")
                         ?? PickMoney("VALOR_ARBITRADO_CM")
                         ?? PickMoney("VALOR_HONORARIOS")
                         ?? valorHonor;

            string specialtyCandidate = "";
            if (!string.IsNullOrWhiteSpace(profPeritoLinked))
                specialtyCandidate = MapProfissaoToHonorariosArea(profPeritoLinked);
            if (string.IsNullOrWhiteSpace(specialtyCandidate) && !string.IsNullOrWhiteSpace(profissaoVal))
                specialtyCandidate = MapProfissaoToHonorariosArea(profissaoVal);
            if (string.IsNullOrWhiteSpace(specialtyCandidate) && !string.IsNullOrWhiteSpace(specialtyVal))
                specialtyCandidate = MapProfissaoToHonorariosArea(specialtyVal);

            if (!string.IsNullOrWhiteSpace(specialtyCandidate))
            {
                cleaned = cleaned.Where(x => !string.Equals(x.GetValueOrDefault("name")?.ToString(), "ESPECIALIDADE", StringComparison.OrdinalIgnoreCase)).ToList();
                cleaned.Add(new Dictionary<string, object>
                {
                    ["name"] = "ESPECIALIDADE",
                    ["value"] = specialtyCandidate,
                    ["page"] = 0,
                    ["pattern"] = "profissao_map",
                    ["weight"] = 0.7
                });
            }

            if (!string.IsNullOrWhiteSpace(specialtyCandidate) && valorHonor.HasValue && _honorariosTable != null)
            {
                if (_honorariosTable.TryMatch(specialtyCandidate, (decimal)valorHonor.Value, out var entry, out var conf))
                {
                    cleaned.Add(new Dictionary<string, object>
                    {
                        ["name"] = "ESPECIE_DA_PERICIA",
                        ["value"] = entry.Descricao,
                        ["page"] = 0,
                        ["pattern"] = "honorarios_catalog",
                        ["weight"] = Math.Max(0.6, conf)
                    });
                    cleaned.Add(new Dictionary<string, object>
                    {
                        ["name"] = "HONORARIOS_TABELA_ID",
                        ["value"] = entry.Id,
                        ["page"] = 0,
                        ["pattern"] = "honorarios_catalog",
                        ["weight"] = Math.Max(0.6, conf)
                    });
                    cleaned.Add(new Dictionary<string, object>
                    {
                        ["name"] = "HONORARIOS_TABELA_DESC",
                        ["value"] = entry.Descricao,
                        ["page"] = 0,
                        ["pattern"] = "honorarios_catalog",
                        ["weight"] = Math.Max(0.6, conf)
                    });
                    cleaned.Add(new Dictionary<string, object>
                    {
                        ["name"] = "HONORARIOS_TABELA_VALOR",
                        ["value"] = entry.Valor.ToString("0.00", CultureInfo.InvariantCulture),
                        ["page"] = 0,
                        ["pattern"] = "honorarios_catalog",
                        ["weight"] = Math.Max(0.6, conf)
                    });
                }
            }

            string perito = cleaned.FirstOrDefault(f => string.Equals(f.GetValueOrDefault("name")?.ToString(), "PERITO", StringComparison.OrdinalIgnoreCase))?.GetValueOrDefault("value")?.ToString();
            string cpf = cleaned.FirstOrDefault(f => string.Equals(f.GetValueOrDefault("name")?.ToString(), "CPF_PERITO", StringComparison.OrdinalIgnoreCase))?.GetValueOrDefault("value")?.ToString();
            string especialidade = cleaned.FirstOrDefault(f => string.Equals(f.GetValueOrDefault("name")?.ToString(), "ESPECIALIDADE", StringComparison.OrdinalIgnoreCase))?.GetValueOrDefault("value")?.ToString();
            string profissao = profissaoVal;

            if (_peritoCatalog != null && _peritoCatalog.TryResolve(perito, cpf, out var info, out var confPerito))
            {
                cleaned = cleaned.Where(f =>
                {
                    var n = f.GetValueOrDefault("name")?.ToString();
                    return !(string.Equals(n, "PERITO", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(n, "CPF_PERITO", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(n, "ESPECIALIDADE", StringComparison.OrdinalIgnoreCase));
                }).ToList();

                cleaned.Add(new Dictionary<string, object> { ["name"] = "PERITO", ["value"] = NormalizeName(info.Name), ["page"] = 0, ["pattern"] = "perito_catalog", ["weight"] = 0.95 });
                if (!string.IsNullOrWhiteSpace(info.Cpf))
                    cleaned.Add(new Dictionary<string, object> { ["name"] = "CPF_PERITO", ["value"] = FormatCpf(info.Cpf), ["page"] = 0, ["pattern"] = "perito_catalog", ["weight"] = 0.95 });

                var espFinal = !string.IsNullOrWhiteSpace(info.Especialidade) ? info.Especialidade : especialidade;
                espFinal = NormalizeShortSpecialty(espFinal ?? "");
                if (!string.IsNullOrWhiteSpace(espFinal))
                    cleaned.Add(new Dictionary<string, object> { ["name"] = "ESPECIALIDADE", ["value"] = espFinal, ["page"] = 0, ["pattern"] = "perito_catalog", ["weight"] = 0.90 });
            }
            else
            {
                string promA = cleaned.FirstOrDefault(f => string.Equals(f.GetValueOrDefault("name")?.ToString(), "PROMOVENTE", StringComparison.OrdinalIgnoreCase))?.GetValueOrDefault("value")?.ToString();
                string promB = cleaned.FirstOrDefault(f => string.Equals(f.GetValueOrDefault("name")?.ToString(), "PROMOVIDO", StringComparison.OrdinalIgnoreCase))?.GetValueOrDefault("value")?.ToString();
                string normPerito = NormalizeName(perito ?? "");

                bool peritoCoincideParte = (!string.IsNullOrWhiteSpace(normPerito) &&
                                            (string.Equals(normPerito, NormalizeName(promA ?? ""), StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(normPerito, NormalizeName(promB ?? ""), StringComparison.OrdinalIgnoreCase)));

                bool hasCpfPerito = !string.IsNullOrWhiteSpace(cpf);

                if (peritoCoincideParte && !hasCpfPerito)
                {
                    cleaned = cleaned.Where(f =>
                    {
                        var n = f.GetValueOrDefault("name")?.ToString();
                        return !(string.Equals(n, "PERITO", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(n, "PROFISSAO", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(n, "CPF_PERITO", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(n, "ESPECIALIDADE", StringComparison.OrdinalIgnoreCase));
                    }).ToList();
                }
                else
                {
                    string espFromProf = !string.IsNullOrWhiteSpace(profissao)
                        ? MapProfissaoToHonorariosArea(profissao)
                        : string.Empty;
                    if (string.IsNullOrWhiteSpace(espFromProf) && !string.IsNullOrWhiteSpace(especialidade))
                        espFromProf = MapProfissaoToHonorariosArea(especialidade);

                    if (!string.IsNullOrWhiteSpace(espFromProf))
                    {
                        cleaned = cleaned.Where(f => !string.Equals(f.GetValueOrDefault("name")?.ToString(), "ESPECIALIDADE", StringComparison.OrdinalIgnoreCase)).ToList();
                        cleaned.Add(new Dictionary<string, object>
                        {
                            ["name"] = "ESPECIALIDADE",
                            ["value"] = espFromProf,
                            ["page"] = 0,
                            ["pattern"] = "profissao_map",
                            ["weight"] = 0.55
                        });
                    }
                }
            }

            var dedup = new List<Dictionary<string, object>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in cleaned)
            {
                var key = $"{f.GetValueOrDefault("name")}|{f.GetValueOrDefault("value")}";
                if (seen.Contains(key)) continue;
                seen.Add(key);
                dedup.Add(f);
            }

            return dedup;
        }

        private string NormalizeFieldNameForPipeline(string field)
        {
            if (string.IsNullOrWhiteSpace(field)) return field ?? "";
            var f = field.Trim();
            var upper = f.ToUpperInvariant();
            return upper switch
            {
                "PROCESSO JUDICIAL" => "PROCESSO_JUDICIAL",
                "PROCESSO ADMINISTRATIVO" => "PROCESSO_ADMINISTRATIVO",
                "CPF/CNPJ" => "CPF_PERITO",
                "CPF PERITO" => "CPF_PERITO",
                "CPF DO PERITO" => "CPF_PERITO",
                "PROFISSÃO" => "PROFISSAO",
                "PROFISSAO" => "PROFISSAO",
                "ESPÉCIE DE PERÍCIA" => "ESPECIE_DA_PERICIA",
                "ESPECIE DE PERICIA" => "ESPECIE_DA_PERICIA",
                "JUÍZO" => "VARA",
                "JUIZO" => "VARA",
                "VALOR ARBITRADO - JZ" => "VALOR_ARBITRADO_JZ",
                "VALOR ARBITRADO - DE" => "VALOR_ARBITRADO_DE",
                "VALOR ARBITRADO - CM" => "VALOR_ARBITRADO_CM",
                "VALOR HONORARIOS" => "VALOR_HONORARIOS",
                "VALOR HONORÁRIOS" => "VALOR_HONORARIOS",
                "VALOR TABELADO ANEXO I - TABELA I" => "VALOR_TABELADO_ANEXO_I",
                "DATA DA AUTORIZACAO DA DESPESA" => "DATA",
                "DATA DA AUTORIZAÇÃO DA DESPESA" => "DATA",
                "DATA DO DESPACHO" => "DATA",
                "DATA DA CERTIDAO" => "DATA",
                _ => f.Replace(" ", "_")
            };
        }

        private string CleanStandard(string field, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value ?? "";
            var f = (field ?? "").Trim().ToUpperInvariant();
            switch (f)
            {
                case "VALOR_HONORARIOS":
                case "VALOR_ARBITRADO_JZ":
                case "VALOR_ARBITRADO_DE":
                case "VALOR_ARBITRADO_CM":
                case "ADIANTAMENTO":
                case "VALOR_TABELADO_ANEXO_I":
                    var money = CleanMoney(value);
                    if (double.TryParse(money, NumberStyles.Any, CultureInfo.InvariantCulture, out var vm))
                    {
                        if (vm > 1000) vm = vm / 100.0;
                        money = vm.ToString("0.00", CultureInfo.InvariantCulture);
                    }
                    return money;
                case "CPF_PERITO":
                    return FormatCpf(value);
                case "PERITO":
                    value = StripSpecialtyFromPerito(value);
                    value = TrimAffixes("PERITO", value);
                    return NormalizeName(value);
                case "PROMOVENTE":
                case "PROMOVIDO":
                case "ASSINANTE":
                    return NormalizeName(value);
                case "PROFISSAO":
                case "ESPECIALIDADE":
                case "ESPECIE_DA_PERICIA":
                    value = TrimAffixes(f, value);
                    value = NormalizeShortSpecialty(value);
                    return value;
                case "COMARCA":
                    return NormalizeComarca(value);
                case "DATA":
                    var dt = NormalizeDateFlexible(value);
                    return string.IsNullOrWhiteSpace(dt) ? Regex.Replace(value, @"\s+", " ").Trim() : dt;
                default:
                    return Regex.Replace(value, @"\s+", " ").Trim();
            }
        }

        private bool ValidateStandard(string field, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var f = (field ?? "").Trim().ToUpperInvariant();
            return f switch
            {
                "VALOR_HONORARIOS" or "VALOR_ARBITRADO_JZ" or "VALOR_ARBITRADO_DE" or "VALOR_ARBITRADO_CM" or "ADIANTAMENTO" or "VALOR_TABELADO_ANEXO_I"
                    => Regex.IsMatch(value, @"^\d+(?:\.\d{2})?$"),
                "CPF_PERITO" => value.Length >= 11,
                "PERITO" => value.Length >= 5,
                "PROFISSAO" or "ESPECIALIDADE" or "ESPECIE_DA_PERICIA" or "PROMOVENTE" or "PROMOVIDO" or "COMARCA" or "VARA"
                    => value.Length >= 3,
                "PROCESSO_JUDICIAL"
                    => Regex.IsMatch(value, @"^\d{7}-\d{2}\.\d{4}\.\d\.\d{2}\.\d{4}$"),
                "DATA"
                    => Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$"),
                _ => true
            };
        }

        private string CleanMoney(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw ?? "";
            var trimmed = raw.Trim();
            var match = Regex.Match(trimmed, @"\d[\d\.]*,\d{2}");
            if (match.Success && double.TryParse(match.Value, NumberStyles.Any, new CultureInfo("pt-BR"), out var vbr))
                return vbr.ToString("0.00", CultureInfo.InvariantCulture);

            var digits = trimmed.Replace("R$", "", StringComparison.OrdinalIgnoreCase)
                                .Replace(" ", "");
            digits = digits.Replace(".", "");
            digits = digits.Replace(",", ".");
            if (double.TryParse(digits, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            {
                if (!trimmed.Contains(",") && !trimmed.Contains(".") && v > 1000) v = v / 100.0;
                if (v > 1000 && Regex.IsMatch(trimmed, @"^\d{4,6}\.00$")) v = v / 100.0;
                return v.ToString("0.00", CultureInfo.InvariantCulture);
            }
            var onlyDigits = Regex.Replace(digits, @"[^\d]", "");
            if (onlyDigits.Length > 2 && double.TryParse(onlyDigits, out var v2))
                return (v2 / 100.0).ToString("0.00", CultureInfo.InvariantCulture);
            return trimmed;
        }

        private string FormatCpf(string raw)
        {
            var digits = Regex.Replace(raw ?? "", @"\D", "");
            if (digits.Length == 11)
                return $"{digits.Substring(0, 3)}.{digits.Substring(3, 3)}.{digits.Substring(6, 3)}-{digits.Substring(9, 2)}";
            return digits;
        }

        private string NormalizeName(string val)
        {
            val = Regex.Replace(val ?? "", @"\s+", " ").Trim();
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(val.ToLowerInvariant());
        }

        private string NormalizeTitle(string val)
        {
            val = Regex.Replace(val ?? "", @"\s+", " ").Trim();
            return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(val.ToLowerInvariant());
        }

        private string NormalizeComarca(string val)
        {
            val = (val ?? "").Replace("Comarca de", "", StringComparison.OrdinalIgnoreCase);
            return NormalizeTitle(val);
        }

        private string StripSpecialtyFromPerito(string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return val ?? "";
            var parts = Regex.Split(val, @"\s*[–-]\s*");
            if (parts.Length >= 2)
            {
                var left = parts[0].Trim();
                var right = string.Join(" - ", parts.Skip(1)).Trim();
                bool leftSpec = LooksLikeSpecialty(left);
                bool rightSpec = LooksLikeSpecialty(right);
                if (leftSpec && !rightSpec) return right;
                if (rightSpec && !leftSpec) return left;
            }
            return val;
        }

        private string TrimAffixes(string field, string val)
        {
            if (string.IsNullOrWhiteSpace(val)) return val ?? "";
            var f = field.ToUpperInvariant();

            if (f == "PERITO" || f == "ESPECIALIDADE" || f == "PROFISSAO")
            {
                val = Regex.Split(val, @"(?i)(CPF|PIS|PASEP|INSS|CBO|NASCID|EMAIL|E-MAIL|FONE|TEL)").FirstOrDefault() ?? val;
                val = Regex.Replace(val, @"^(?i)(perit[oa]|dr\.?|dra\.?|doutor(?:a)?|sr\.?|sra\.?)\s+", "");
                val = Regex.Replace(val, @"(?i)\s*[-–]\s*perit[oa].*$", "");
                val = Regex.Replace(val, @"\s+", " ").Trim();
            }

            if (f == "PROMOVENTE" || f == "PROMOVIDO" || f == "VARA")
            {
                val = Regex.Replace(val, @"(?i)perante o ju[ií]zo\s+de\s+", "");
                val = Regex.Replace(val, @"(?i)nos autos\s+do\s+processo.*", "");
                val = Regex.Replace(val, @"\s+", " ").Trim();
            }

            return val;
        }

        private bool LooksLikeSpecialty(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = text.ToLowerInvariant();
            var keywords = new[] { "psiq", "psi", "medic", "engenheir", "odont", "grafot", "assistente social", "social", "contab", "perito", "perita" };
            return keywords.Any(k => t.Contains(k));
        }

        private string MapProfissaoToHonorariosArea(string prof)
        {
            if (string.IsNullOrWhiteSpace(prof)) return "";
            var norm = RemoveDiacritics(prof).ToLowerInvariant();
            if (norm.Contains("psiqu")) return "MEDICINA PSIQUIATRIA";
            if (norm.Contains("neuro")) return "MEDICINA NEUROLOGIA";
            if (norm.Contains("trabalho") && norm.Contains("medic")) return "MEDICINA DO TRABALHO";
            if (norm.Contains("odont")) return "MEDICINA / ODONTOLOGIA";
            if (norm.Contains("medic")) return "MEDICINA";
            if (norm.Contains("psicolog")) return "PSICOLOGIA";
            if (norm.Contains("engenhe") || norm.Contains("arquitet")) return "ENGENHARIA E ARQUITETURA";
            if (norm.Contains("grafot")) return "GRAFOTECNIA";
            if (norm.Contains("assistente social") || norm.Contains("servico social") || norm.Contains("serviço social"))
                return "SERVIÇO SOCIAL";
            if (norm.Contains("contab") || norm.Contains("contador")) return "CIÊNCIAS CONTÁBEIS";
            return NormalizeShortSpecialty(prof);
        }

        private string NormalizeDateFlexible(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            var iso = Regex.Match(raw, @"(\d{4}-\d{2}-\d{2})");
            if (iso.Success)
            {
                var valIso = NormalizeDate(iso.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(valIso)) return valIso;
            }

            var ext = Regex.Match(raw, @"(\d{1,2})\s+de\s+(janeiro|fevereiro|març|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro)\s+de\s+(\d{4})", RegexOptions.IgnoreCase);
            if (ext.Success)
            {
                var val = NormalizeDateExtenso(ext.Groups[1].Value, ext.Groups[2].Value, ext.Groups[3].Value);
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }

            var num = Regex.Match(raw, @"(\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4})");
            if (num.Success)
            {
                var val = NormalizeDate(num.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }

            return NormalizeDate(raw);
        }

        private string NormalizeShortSpecialty(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            var v = Regex.Replace(value, @"\s+", " ").Trim();
            if (v.Length > 80) v = v.Substring(0, 80).Trim();
            return v;
        }

        private string RemoveDiacritics(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            var normalized = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder();
            foreach (var c in normalized)
            {
                var uc = CharUnicodeInfo.GetUnicodeCategory(c);
                if (uc != UnicodeCategory.NonSpacingMark) sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private string ComputeLaudoHashSha1(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var norm = RemoveDiacritics(text).ToLowerInvariant();
            norm = Regex.Replace(norm, @"\s+", " ").Trim();
            using var sha1 = SHA1.Create();
            var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(norm));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private class BandPattern
        {
            public BandPattern(string field, string pattern, RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.Singleline)
            {
                Field = field;
                Pattern = pattern;
                Options = options;
            }
            public string Field { get; }
            public string Pattern { get; }
            public RegexOptions Options { get; }
        }

        private List<Dictionary<string, object>> ExtractBandFields(string fullText, List<Dictionary<string, object>> words, int fallbackPage)
        {
            var hits = new List<Dictionary<string, object>>();
            if (words == null || words.Count == 0) return hits;

            var paragraphs = BuildParagraphsFromWords(words);
            var bandTexts = new Dictionary<string, string>
            {
                ["header"] = "",
                ["subheader"] = "",
                ["body1"] = "",
                ["body2"] = "",
                ["body3"] = "",
                ["body4"] = "",
                ["footer"] = ""
            };

            foreach (var p in paragraphs)
            {
                string band = p.Ny0 >= 0.88 ? "header"
                              : p.Ny0 >= 0.80 ? "subheader"
                              : p.Ny0 >= 0.65 ? "body1"
                              : p.Ny0 >= 0.50 ? "body2"
                              : p.Ny0 >= 0.35 ? "body3"
                              : p.Ny0 >= 0.20 ? "body4"
                              : "footer";
                bandTexts[band] += p.Text + "\n";
            }

            var patterns = new List<BandPattern>
            {
                new BandPattern("PROCESSO_ADMINISTRATIVO", @"Processo\s+n[ºo]\s+([\d\.\-\/]+)"),
                new BandPattern("PROMOVENTE", @"Requerente:\s*([^\n]{1,120}?)\s+Interessado:", RegexOptions.IgnoreCase | RegexOptions.Singleline),
                new BandPattern("PERITO", @"Interessado:\s*([^–-]{3,80})[–-]\s*([^\n]{2,80})"),
                new BandPattern("ESPECIALIDADE", @"Interessado:\s*[^–-]{3,80}[–-]\s*([^\n]{2,80})"),
                new BandPattern("CPF_PERITO", @"CPF\s+([\d\.\-]{11,18})"),
                new BandPattern("PROCESSO_JUDICIAL", @"autos do processo nº\s+([\d\.\-\/]+)"),
                new BandPattern("PROMOVENTE", @"movid[oa]\s+por\s+(.+?)(?=,\s*(?:CPF|CNPJ))"),
                new BandPattern("PROMOVIDO", @"em face de\s+(.+?)(?=,\s*(?:CPF|CNPJ))"),
                new BandPattern("VALOR_HONORARIOS", @"valor de R\$\s*([\d\.,]+)"),
                new BandPattern("ASSINANTE", @"Em razão do exposto, autorizo a despesa,[\s\S]{0,400}?([^\n]{1,80})$")
            };

            var filled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var band in new[] { "header", "subheader", "body1", "body2", "body3", "body4", "footer" })
            {
                var text = bandTexts[band];
                foreach (var pat in patterns)
                {
                    if (filled.Contains(pat.Field)) continue;
                    var rg = new Regex(pat.Pattern, pat.Options);
                    var m = rg.Match(text);
                    if (!m.Success) continue;

                    string value;
                    if (string.Equals(pat.Field, "ESPECIALIDADE", StringComparison.OrdinalIgnoreCase) && m.Groups.Count > 1)
                        value = m.Groups[1].Value;
                    else if (m.Groups.Count > 1)
                        value = m.Groups[1].Value;
                    else
                        value = m.Value;

                    var hit = MakeBandHit(pat.Field, value, pat.Pattern, words, fallbackPage, band);
                    if (hit.Count > 0)
                    {
                        hits.Add(hit);
                        filled.Add(pat.Field);
                    }
                }
            }

            return hits;
        }

        private Dictionary<string, object> MakeBandHit(string field, string value, string pattern, List<Dictionary<string, object>> words, int fallbackPage, string band)
        {
            value = value?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(value)) return new Dictionary<string, object>();
            var bbox = FindBBoxForBand(words, value);
            int page = bbox?.page ?? fallbackPage;
            value = CleanStandard(field, value);
            if (!ValidateStandard(field, value)) return new Dictionary<string, object>();

            var hit = new Dictionary<string, object>
            {
                ["name"] = field,
                ["value"] = value,
                ["page"] = page,
                ["pattern"] = pattern,
                ["weight"] = 0.6,
                ["band"] = band
            };
            if (bbox != null)
            {
                hit["bbox"] = new Dictionary<string, double>
                {
                    ["nx0"] = bbox.Value.nx0,
                    ["ny0"] = bbox.Value.ny0,
                    ["nx1"] = bbox.Value.nx1,
                    ["ny1"] = bbox.Value.ny1
                };
            }
            return hit;
        }

        private (int page, double nx0, double ny0, double nx1, double ny1)? FindBBoxForBand(List<Dictionary<string, object>> words, string raw)
        {
            if (words == null || words.Count == 0 || string.IsNullOrWhiteSpace(raw)) return null;
            var tokens = Regex.Split(raw, @"\s+")
                              .Select(t => Regex.Replace(t, @"[^\p{L}\p{N}]+", "").ToLowerInvariant())
                              .Where(t => t.Length > 0)
                              .ToList();
            if (tokens.Count == 0) return null;

            var wordTokens = words.Select(w => Regex.Replace(w.GetValueOrDefault("text")?.ToString() ?? "", @"[^\p{L}\p{N}]+", "").ToLowerInvariant()).ToList();
            for (int i = 0; i < wordTokens.Count; i++)
            {
                if (wordTokens[i] != tokens[0]) continue;
                int k = 0;
                int page = Convert.ToInt32(words[i]["page"]);
                while (i + k < wordTokens.Count && k < tokens.Count)
                {
                    int pcur = Convert.ToInt32(words[i + k]["page"]);
                    if (pcur != page) break;
                    if (wordTokens[i + k] != tokens[k]) break;
                    k++;
                }
                if (k == tokens.Count)
                {
                    var slice = words.Skip(i).Take(k);
                    double nx0 = slice.Min(w => Convert.ToDouble(w["nx0"]));
                    double ny0 = slice.Min(w => Convert.ToDouble(w["ny0"]));
                    double nx1 = slice.Max(w => Convert.ToDouble(w["nx1"]));
                    double ny1 = slice.Max(w => Convert.ToDouble(w["ny1"]));
                    return (page, nx0, ny0, nx1, ny1);
                }
            }
            return null;
        }

        private string ClassifyBucket(string name, string text)
        {
            var n = (name ?? string.Empty).ToLowerInvariant();
            var t = (text ?? string.Empty).ToLowerInvariant();
            var snippet = t.Length > 2000 ? t.Substring(0, 2000) : t;
            if (IsLaudo(n, snippet)) return "laudo";
            if (IsPrincipal(n, snippet)) return "principal";
            if (IsApoio(n, snippet)) return "apoio";
            return "outro";
        }

        private string ComputeSha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(data);
            var sb = new StringBuilder(hash.Length * 2);
            foreach (var b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private bool IsLaudo(string name, string text)
        {
            string[] kws = { "laudo", "quesito", "perícia", "pericial", "esclarecimento", "parecer" };
            return kws.Any(k => name.Contains(k) || text.Contains(k));
        }

        private bool IsPrincipal(string name, string text)
        {
            string[] kws = { "despacho", "decisão", "decisao", "senten", "certidao", "certidão", "oficio", "ofício", "nota de empenho", "autorizacao", "autorização", "requisição", "requisicao" };
            return kws.Any(k => name.Contains(k) || text.Contains(k));
        }

        private bool IsApoio(string name, string text)
        {
            string[] kws = { "anexo", "relatório", "relatorio", "planilha" };
            return kws.Any(k => name.Contains(k) || text.Contains(k));
        }
private int FindPageForText(List<Dictionary<string, object>> words, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(t => t.ToLowerInvariant()).ToList();
            if (tokens.Count == 0) return 0;

            for (int i = 0; i < words.Count; i++)
            {
                if (words[i]["text"].ToString()!.ToLowerInvariant() != tokens[0]) continue;
                bool ok = true;
                for (int k = 1; k < tokens.Count; k++)
                {
                    if (i + k >= words.Count || words[i + k]["text"].ToString()!.ToLowerInvariant() != tokens[k])
                    {
                        ok = false; break;
                    }
                }
                if (ok)
                {
                    return Convert.ToInt32(words[i]["page"]);
                }
            }
            // fallback: no page found
            return 0;
        }

        private string NormalizeDigits(string raw) => new string(raw.Where(char.IsDigit).ToArray());

        private List<Dictionary<string, object>> ExtractBookmarksForRange(PDFAnalysisResult analysis, int startPage, int endPage)
        {
            var list = new List<Dictionary<string, object>>();
            if (analysis.Bookmarks?.RootItems == null) return list;

            void Walk(IEnumerable<BookmarkItem> items)
            {
                foreach (var b in items)
                {
                    int page = b.Destination?.PageNumber ?? 0;
                    if (page >= startPage && page <= endPage && page > 0)
                    {
                        list.Add(new Dictionary<string, object>
                        {
                            ["title"] = b.Title,
                            ["page"] = page,
                            ["level"] = b.Level
                        });
                    }

                    if (b.Children != null && b.Children.Count > 0)
                        Walk(b.Children);
                }
            }

            Walk(analysis.Bookmarks.RootItems);
            return list.OrderBy(b => (int)b["page"]).ToList();
        }

        /// <summary>
        /// Converte bookmarks em limites de documentos. Bookmarks passam a ser "documentos".
        /// Se não houver bookmarks, retorna lista vazia (segmenter padrão será usado).
        /// </summary>
        private List<DocumentBoundary> BuildBookmarkBoundaries(PDFAnalysisResult analysis, int maxBookmarkPages)
        {
            var result = new List<DocumentBoundary>();
            var bms = ExtractBookmarksForRange(analysis, 1, analysis.DocumentInfo.TotalPages);
            if (bms.Count == 0) return result;

            for (int i = 0; i < bms.Count; i++)
            {
                int start = (int)bms[i]["page"];
                int end = (i + 1 < bms.Count) ? ((int)bms[i + 1]["page"]) - 1 : analysis.DocumentInfo.TotalPages;
                if (end < start) end = start;

                var rawTitle = bms[i]["title"]?.ToString() ?? "";
                var titleStr = SanitizeBookmarkTitle(rawTitle);
                bool isAnexo = Regex.IsMatch(titleStr, "^anexos?$", RegexOptions.IgnoreCase) ||
                               Regex.IsMatch(rawTitle, "^anexos?$", RegexOptions.IgnoreCase);
                // Mesmo que seja grande, respeitamos o bookmark: não resegmentamos aqui.
                var boundary = new DocumentBoundary
                {
                    StartPage = start,
                    EndPage = end,
                    Title = titleStr,
                    RawTitle = rawTitle,
                    DetectedType = isAnexo ? "anexo" : "bookmark",
                    FirstPageText = analysis.Pages[start - 1].TextInfo.PageText,
                    LastPageText = analysis.Pages[end - 1].TextInfo.PageText,
                    FullText = string.Join("\n", Enumerable.Range(start, end - start + 1).Select(p => analysis.Pages[p - 1].TextInfo.PageText)),
                    Fonts = new HashSet<string>(analysis.Pages.Skip(start - 1).Take(end - start + 1).SelectMany(p => p.TextInfo.Fonts.Select(f => f.Name)), StringComparer.OrdinalIgnoreCase),
                    PageSize = analysis.Pages.First().Size.GetPaperSize(),
                    HasSignatureImage = analysis.Pages.Skip(start - 1).Take(end - start + 1).Any(p => p.Resources.Images?.Any(img => img.Width > 100 && img.Height > 30) ?? false),
                    TotalWords = analysis.Pages.Skip(start - 1).Take(end - start + 1).Sum(p => p.TextInfo.WordCount)
                };
                result.Add(boundary);
            }

            return result;
        }

        private PDFAnalysisResult CloneRange(PDFAnalysisResult analysis, int startPage, int endPage)
        {
            var clone = new PDFAnalysisResult
            {
                FilePath = analysis.FilePath,
                FileSize = analysis.FileSize,
                AnalysisDate = analysis.AnalysisDate,
                Metadata = analysis.Metadata,
                XMPMetadata = analysis.XMPMetadata,
                DocumentInfo = new DocumentInfo { TotalPages = endPage - startPage + 1 },
                Pages = analysis.Pages.Skip(startPage - 1).Take(endPage - startPage + 1)
                    .Select(p => p) // shallow copy is enough for segmentation heuristics
                    .ToList(),
                Security = analysis.Security,
                Resources = analysis.Resources,
                Statistics = analysis.Statistics,
                Accessibility = analysis.Accessibility,
                Layers = analysis.Layers,
                Signatures = analysis.Signatures,
                ColorProfiles = analysis.ColorProfiles,
                Bookmarks = new BookmarkStructure { RootItems = new List<BookmarkItem>(), MaxDepth = 0, TotalCount = 0 },
                PDFACompliance = analysis.PDFACompliance,
                Multimedia = analysis.Multimedia,
                PDFAValidation = analysis.PDFAValidation,
                SecurityInfo = analysis.SecurityInfo,
                AccessibilityInfo = analysis.AccessibilityInfo
            };
            return clone;
        }

        private string ExtractDocumentName(DocumentBoundary d)
        {
            // mesmo critério do FpdfDocumentsCommand: primeira linha do FullText/FirstPageText
            var text = d.FullText ?? d.FirstPageText ?? "";
            var firstLine = text.Split('\n').FirstOrDefault() ?? "";
            return firstLine.Length > 80 ? firstLine.Substring(0, 80) + "..." : firstLine;
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

        private string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || max <= 0) return "";
            if (text.Length <= max) return text;
            return text.Substring(0, max);
        }

        private string NormalizeDocLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label)) return "";
            var norm = RemoveDiacritics(label).ToLowerInvariant();
            if (norm.Contains("requerimento") && norm.Contains("pagamento") && norm.Contains("honor"))
                return "Requerimento de Pagamento de Honorarios";
            if (norm.StartsWith("despacho"))
                return "Despacho";
            if (norm.StartsWith("certidao") || norm.StartsWith("certidão"))
                return "Certidao";
            return label.Trim();
        }

        private bool MatchesOrigin(Dictionary<string, object> docSummary, string docType)
        {
            if (docSummary == null) return false;
            var origin = $"{docSummary.GetValueOrDefault("origin_main")} {docSummary.GetValueOrDefault("origin_sub")} {docSummary.GetValueOrDefault("origin_extra")} {docSummary.GetValueOrDefault("title")}";
            origin = RemoveDiacritics(origin ?? "").ToLowerInvariant();
            var cfg = _tjpbCfg ?? new TjpbDespachoConfig();

            List<string> hints;
            if (docType.Equals("certidao", StringComparison.OrdinalIgnoreCase))
                hints = cfg.Certidao.HeaderHints.Concat(cfg.Certidao.TitleHints).ToList();
            else
                hints = cfg.Anchors.Header.Concat(cfg.Anchors.Subheader).ToList();

            if (hints == null || hints.Count == 0) return false;
            foreach (var h in hints)
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                var hh = RemoveDiacritics(h).ToLowerInvariant();
                if (origin.Contains(hh)) return true;
            }
            return false;
        }

        private bool MatchesSigner(Dictionary<string, object> docSummary)
        {
            if (docSummary == null) return false;
            var signer = docSummary.GetValueOrDefault("signer")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(signer)) return false;
            var norm = RemoveDiacritics(signer).ToLowerInvariant();
            var cfg = _tjpbCfg ?? new TjpbDespachoConfig();
            if (cfg.Anchors.SignerHints == null || cfg.Anchors.SignerHints.Count == 0) return false;
            foreach (var h in cfg.Anchors.SignerHints)
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                var hh = RemoveDiacritics(h).ToLowerInvariant();
                if (norm.Contains(hh)) return true;
            }
            return false;
        }

        private string HashText(string text)
        {
            text ??= "";
            var normalized = Regex.Replace(text, @"\s+", " ").Trim().ToLowerInvariant();
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(normalized);
            var hash = sha.ComputeHash(bytes);
            return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
        }

        private DespachoDocumentInfo? FindBestDespachoMatch(DocumentBoundary d, ExtractionResult? result, out double overlapRatio, out int overlapPages, string? docTypeContains = null)
        {
            overlapRatio = 0.0;
            overlapPages = 0;
            if (result?.Documents == null || result.Documents.Count == 0) return null;

            DespachoDocumentInfo? best = null;
            foreach (var doc in result.Documents)
            {
                if (!string.IsNullOrWhiteSpace(docTypeContains))
                {
                    var dtype = doc.DocType ?? "";
                    if (dtype.IndexOf(docTypeContains, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                }
                var interStart = Math.Max(d.StartPage, doc.StartPage1);
                var interEnd = Math.Min(d.EndPage, doc.EndPage1);
                var inter = interEnd - interStart + 1;
                if (inter <= 0) continue;
                var despachoPages = Math.Max(1, doc.EndPage1 - doc.StartPage1 + 1);
                var docPages = Math.Max(1, d.EndPage - d.StartPage + 1);
                var ratioDespacho = inter / (double)despachoPages;
                var ratioDoc = inter / (double)docPages;
                var score = Math.Max(ratioDespacho, ratioDoc);
                if (best == null || score > overlapRatio)
                {
                    best = doc;
                    overlapRatio = score;
                    overlapPages = inter;
                }
            }

            if (best == null) return null;

            // aceita match mesmo com docs grandes (overlap pequeno), mas exige pelo menos 1 página comum
            if (overlapPages >= 1 && (overlapRatio >= 0.2 || (d.EndPage - d.StartPage + 1) <= 3))
                return best;

            return null;
        }

        private List<Dictionary<string, object>> ConvertDespachoFields(DespachoDocumentInfo doc, string source)
        {
            var list = new List<Dictionary<string, object>>();
            if (doc?.Fields == null) return list;
            foreach (var kv in doc.Fields)
            {
                var info = kv.Value;
                if (info == null) continue;
                var ev = info.Evidence;
                list.Add(new Dictionary<string, object>
                {
                    ["name"] = kv.Key,
                    ["value"] = info.Value ?? "-",
                    ["confidence"] = info.Confidence,
                    ["method"] = info.Method ?? "not_found",
                    ["page"] = ev?.Page1 ?? 0,
                    ["snippet"] = ev?.Snippet ?? "",
                    ["bboxN"] = ev?.BBoxN,
                    ["source"] = source
                });
            }
            return list;
        }

        private class DespachoTipoInfo
        {
            public string Tipo { get; set; } = "indefinido";
            public string Categoria { get; set; } = "-";
            public string Destino { get; set; } = "-";
            public bool HasGeorc { get; set; }
            public bool HasConselho { get; set; }
            public bool HasAutorizacao { get; set; }
        }

        private DespachoTipoInfo DetectDespachoTipo(string fullText, string lastTwoText)
        {
            var cfg = _tjpbCfg ?? new TjpbDespachoConfig();
            var tail = fullText ?? "";
            if (tail.Length > 4000)
                tail = tail.Substring(tail.Length - 4000);
            var norm = TextUtils.NormalizeForMatch($"{lastTwoText}\n{tail}");
            bool hasGeorc = ContainsAny(norm, cfg.DespachoType.GeorcHints);
            bool hasConselho = ContainsAny(norm, cfg.DespachoType.ConselhoHints);
            bool hasAutorizacao = ContainsAny(norm, cfg.DespachoType.AutorizacaoHints);

            var info = new DespachoTipoInfo
            {
                HasGeorc = hasGeorc,
                HasConselho = hasConselho,
                HasAutorizacao = hasAutorizacao
            };

            if (hasConselho)
            {
                info.Categoria = "encaminhamento";
                info.Destino = "conselho";
                info.Tipo = "encaminhamento_conselho";
                return info;
            }

            if (hasGeorc)
            {
                info.Categoria = "encaminhamento";
                info.Destino = "georc";
                info.Tipo = "encaminhamento_georc";
                return info;
            }

            if (hasAutorizacao)
            {
                info.Categoria = "autorizacao";
                info.Destino = "-";
                info.Tipo = "autorizacao";
                return info;
            }

            return info;
        }

        private bool ContainsAny(string norm, List<string> hints)
        {
            if (string.IsNullOrWhiteSpace(norm) || hints == null || hints.Count == 0) return false;
            foreach (var h in hints)
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                if (norm.Contains(TextUtils.NormalizeForMatch(h))) return true;
            }
            return false;
        }

        private List<Dictionary<string, object>> SplitAnexos(DocumentBoundary parent, PDFAnalysisResult analysis, string pdfPath, List<FieldScript> scripts)
        {
            var list = new List<Dictionary<string, object>>();

            // Pega bookmarks "Anexo/Anexos" no range do documento
            var anexos = ExtractBookmarksForRange(analysis, parent.StartPage, parent.EndPage)
                .Where(b => Regex.IsMatch(b["title"].ToString() ?? "", "^anexos?$", RegexOptions.IgnoreCase))
                .OrderBy(b => (int)b["page"])
                .ToList();

            if (anexos.Count == 0) return list;

            // Cria subfaixas a partir dos bookmarks: cada anexo vai da página do bookmark até a página anterior ao próximo bookmark (ou fim do doc)
            for (int i = 0; i < anexos.Count; i++)
            {
                int start = (int)anexos[i]["page"];
                int end = (i + 1 < anexos.Count) ? ((int)anexos[i + 1]["page"]) - 1 : parent.EndPage;
                if (start < parent.StartPage || start > parent.EndPage) continue;
                if (end < start) end = start;

                var boundary = new DocumentBoundary
                {
                    StartPage = start,
                    EndPage = end,
                    DetectedType = "anexo",
                    FirstPageText = analysis.Pages[start - 1].TextInfo.PageText,
                    LastPageText = analysis.Pages[end - 1].TextInfo.PageText,
                    FullText = string.Join("\n", Enumerable.Range(start, end - start + 1).Select(p => analysis.Pages[p - 1].TextInfo.PageText)),
                    Fonts = parent.Fonts,
                    PageSize = parent.PageSize,
                    HasSignatureImage = parent.HasSignatureImage,
                    TotalWords = parent.TotalWords
                };

                var obj = BuildDocObject(boundary, analysis, pdfPath, scripts, null, false);
                obj["parent_doc_label"] = ExtractDocumentName(parent);
                obj["doc_label"] = anexos[i]["title"]?.ToString() ?? "Anexo";
                obj["doc_type"] = "anexo_split";
                list.Add(obj);
            }

            return list;
        }
    }
}
