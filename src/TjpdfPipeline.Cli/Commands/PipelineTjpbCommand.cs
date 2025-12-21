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
using FilterPDF.TjpbDespachoExtractor.Config;
using FilterPDF.TjpbDespachoExtractor.Extraction;
using FilterPDF.TjpbDespachoExtractor.Utils;
using FilterPDF.TjpbDespachoExtractor.Models;

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

        public override void Execute(string[] args)
        {
            string inputDir = ".";
            bool splitAnexos = false; // backward compat (anexos now covered by bookmark docs)
            int maxBookmarkPages = 30; // agora interno, sem flag na CLI
            bool onlyDespachos = false;
            string? signerContains = null;
            string pgUri = FilterPDF.Utils.PgDocStore.DefaultPgUri;
            string configPath = Path.Combine("configs", "config.yaml");
            var analysesByProcess = new Dictionary<string, PDFAnalysisResult>();
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

            var dir = new DirectoryInfo(inputDir);
            if (!dir.Exists)
            {
                Console.WriteLine($"Diretório não encontrado: {inputDir}");
                return;
            }

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
                        var obj = BuildDocObject(d, analysis, pdfPath, fieldScripts, despachoResult);
                        allDocs.Add(obj);
                        if (obj.ContainsKey("words"))
                        {
                            var w = obj["words"] as List<Dictionary<string, object>>;
                            if (w != null) allDocsWords.Add(w);
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

            // Filtros opcionais (CLI) aplicados em C# para evitar pós-processamento externo
            var filteredDocs = new List<Dictionary<string, object>>();
            var filteredWords = new List<List<Dictionary<string, object>>>();
            foreach (var doc in allDocs)
            {
                string label = doc.TryGetValue("doc_label", out var dl) ? dl?.ToString() ?? "" : "";
                string docType = doc.TryGetValue("doc_type", out var dt) ? dt?.ToString() ?? "" : "";
                string signer = "";
                if (doc.TryGetValue("doc_summary", out var dsObj) && dsObj is Dictionary<string, object> ds)
                    signer = ds.TryGetValue("signer", out var s) ? s?.ToString() ?? "" : "";

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

            // Persistir por processo no Postgres (tabelas processes + documents)
            foreach (var grp in grouped)
            {
                var procName = grp.process;
                var firstDoc = grp.documents.FirstOrDefault();
                var sourcePath = firstDoc != null && firstDoc.TryGetValue("pdf_path", out var pp) ? pp?.ToString() ?? procName : procName;
                var analysis = analysesByProcess.TryGetValue(procName, out var an) ? an : new PDFAnalysisResult();
                var payload = new { process = procName, documents = grp.documents, paragraph_stats = paragraphStats };
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

        public override void ShowHelp()
        {
            Console.WriteLine("fpdf pipeline-tjpb --input-dir <dir> [--split-anexos] [--only-despachos] [--signer-contains <texto>]");
            Console.WriteLine("--config: caminho para config.yaml (opcional; usado para hints de despacho).");
            Console.WriteLine("Lê caches (tmp/cache/*.json) ou PDFs; grava no Postgres (processes + documents). Não gera arquivo.");
            Console.WriteLine("--split-anexos: cria subdocumentos a partir de bookmarks 'Anexo/Anexos' dentro de cada documento.");
            Console.WriteLine("--only-despachos: filtra apenas documentos cujo doc_label/doc_type contenha 'Despacho'.");
            Console.WriteLine("--signer-contains: filtra documentos cujo signer contenha o texto informado (case-insensitive).");
        }

        private string DeriveProcessName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var m = Regex.Match(name, @"\d+");
            if (m.Success) return m.Value;
            return name;
        }

        private Dictionary<string, object> BuildDocObject(DocumentBoundary d, PDFAnalysisResult analysis, string pdfPath, List<FieldScript> scripts, ExtractionResult? despachoResult)
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

            var docLabel = !string.IsNullOrWhiteSpace(d.Title) ? d.Title : ExtractDocumentName(d);
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

            var docSummary = BuildDocSummary(d, pdfPath, docText, lastPageText, lastTwoText, header, footer, docBookmarks, analysis.Signatures, docLabel);
            var despachoMatch = FindBestDespachoMatch(d, despachoResult, out var overlapRatio, out var overlapPages);
            if (despachoMatch != null)
            {
                var despachoFields = ConvertDespachoFields(despachoMatch);
                if (despachoFields.Count > 0)
                {
                    // add after script extraction
                }
            }
            var labelIsDespacho = docLabel.Contains("despacho", StringComparison.OrdinalIgnoreCase) ||
                                  docType.Contains("despacho", StringComparison.OrdinalIgnoreCase);
            var isDespacho = labelIsDespacho || despachoMatch != null;
            var forcedBucket = isDespacho ? "principal" : null;
            var extractedFields = ExtractFields(docText, wordsWithCoords, d, pdfPath, scripts, forcedBucket);
            if (despachoMatch != null)
            {
                var despachoFields = ConvertDespachoFields(despachoMatch);
                if (despachoFields.Count > 0)
                    extractedFields.AddRange(despachoFields);
            }
            if (!(_tjpbCfg?.DisableFallbacks ?? false))
                AddDocSummaryFallbacks(extractedFields, docSummary, d.StartPage);
            else
                extractedFields = FilterTemplateOnly(extractedFields);
            var forensics = BuildForensics(d, analysis, docText, wordsWithCoords);
            var despachoInfo = DetectDespachoTipo(docText, lastTwoText);

            return new Dictionary<string, object>
            {
                ["process"] = DeriveProcessName(pdfPath),
                ["pdf_path"] = pdfPath,
                ["doc_label"] = docLabel,
                ["doc_type"] = docType,
                ["is_despacho"] = isDespacho,
                ["despacho_overlap_ratio"] = despachoMatch != null ? overlapRatio : 0.0,
                ["despacho_overlap_pages"] = despachoMatch != null ? overlapPages : 0,
                ["despacho_match_score"] = despachoMatch != null ? despachoMatch.MatchScore : 0.0,
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
                ["bookmarks"] = docBookmarks,
                ["anexos_bookmarks"] = anexos,
                ["doc_summary"] = docSummary,
                ["fields"] = extractedFields,
                ["despacho_extraction"] = despachoMatch,
                ["forensics"] = forensics
            };
        }

        // Doc type classification removida: usamos apenas o nome do bookmark como rótulo.

        private Dictionary<string, object> BuildDocSummary(DocumentBoundary d, string pdfPath, string fullText, string lastPageText, string lastTwoText, string header, string footer, List<Dictionary<string, object>> bookmarks, List<DigitalSignature> signatures, string docLabel)
        {
            string docId = $"{Path.GetFileNameWithoutExtension(pdfPath)}_{d.StartPage}-{d.EndPage}";
            string originMain = ExtractOrigin(header, bookmarks, fullText, excludeGeneric: true);
            string originSub = ExtractSubOrigin(header, bookmarks, fullText, originMain, excludeGeneric: true);
            string originExtra = ExtractExtraOrigin(header, bookmarks, fullText, originMain, originSub);
            var sei = ExtractSeiMetadata(fullText, lastTwoText, footer, docLabel);
            string signer = sei.Signer ?? ExtractSigner(lastTwoText, footer, signatures);
            string signedAt = sei.SignedAt ?? ExtractSignedAt(lastTwoText, footer);
            string template = docLabel; // não classificar; manter o nome do bookmark
            string title = ExtractTitle(header, bookmarks, fullText, originMain, originSub);

            return new Dictionary<string, object>
            {
                ["doc_id"] = docId,
                ["origin_main"] = originMain,
                ["origin_sub"] = originSub,
                ["origin_extra"] = originExtra,
                ["signer"] = signer,
                ["signed_at"] = signedAt,
                ["title"] = title,
                ["template"] = template,
                ["sei_process"] = sei.Process,
                ["sei_doc"] = sei.DocNumber,
                ["sei_crc"] = sei.CRC,
                ["sei_verifier"] = sei.Verifier,
                ["auth_url"] = sei.AuthUrl
            };
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

        private string ExtractSigner(string lastPageText, string footer, List<DigitalSignature> signatures)
        {
            string[] sources =
            {
                $"{lastPageText}\n{footer}",
                ReverseText($"{lastPageText}\n{footer}")
            };

            foreach (var source in sources)
            {
                // Formato mais completo do SEI: "Documento assinado eletronicamente por NOME, <cargo>, em 12/03/2024"
                var docSigned = Regex.Match(source, @"documento\s+assinado\s+eletronicamente\s+por\s+([\\p{L} .'’-]+?)(?:,|\sem\s|\n|$)", RegexOptions.IgnoreCase);
                if (docSigned.Success) return docSigned.Groups[1].Value.Trim();

                var match = Regex.Match(source, @"assinado(?:\s+digitalmente|\s+eletronicamente)?\s+por\s+([\\p{L} .'’-]+)", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();

                // Digital signature block (X.509)
                var sigMatch = Regex.Match(source, @"Assinatura(?:\s+)?:(.+)", RegexOptions.IgnoreCase);
                if (sigMatch.Success) return sigMatch.Groups[1].Value.Trim();
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

        private string ExtractSignedAt(string lastPagesText, string footer)
        {
            var source = $"{lastPagesText}\n{footer}";

            // Prefer datas próximas a termos de assinatura
            var windowMatch = Regex.Match(source, @"assinado[^\\n]{0,120}?(\\d{1,2}[\\/]-?\\d{1,2}[\\/]-?\\d{2,4})", RegexOptions.IgnoreCase);
            if (windowMatch.Success)
            {
                var val = NormalizeDate(windowMatch.Groups[1].Value);
                if (!string.IsNullOrEmpty(val)) return val;
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

            AddFieldIfMissing(fields, "PROCESSO_ADMINISTRATIVO", GetStr("sei_process"), "doc_summary", 0.55, page);
            AddFieldIfMissing(fields, "VARA", GetStr("juizo_vara"), "doc_summary", 0.55, page);
            AddFieldIfMissing(fields, "COMARCA", GetStr("comarca"), "doc_summary", 0.55, page);
            AddFieldIfMissing(fields, "PERITO", GetStr("interested_name"), "doc_summary", 0.50, page);
            AddFieldIfMissing(fields, "ESPECIALIDADE", GetStr("interested_profession"), "doc_summary", 0.45, page);
            AddFieldIfMissing(fields, "DATA", GetStr("signed_at"), "doc_summary", 0.45, page);
            AddFieldIfMissing(fields, "ASSINANTE", GetStr("signer"), "doc_summary", 0.50, page);
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

        private List<Dictionary<string, object>> FilterTemplateOnly(List<Dictionary<string, object>> fields)
        {
            if (fields == null) return new List<Dictionary<string, object>>();
            return fields.Where(f =>
            {
                var method = f.TryGetValue("method", out var m) ? m?.ToString() ?? "" : "";
                return method.StartsWith("template_", StringComparison.OrdinalIgnoreCase) ||
                       method.Equals("not_found", StringComparison.OrdinalIgnoreCase);
            }).ToList();
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

                var titleStr = bms[i]["title"].ToString() ?? "";
                bool isAnexo = Regex.IsMatch(titleStr, "^anexos?$", RegexOptions.IgnoreCase);
                // Mesmo que seja grande, respeitamos o bookmark: não resegmentamos aqui.
                var boundary = new DocumentBoundary
                {
                    StartPage = start,
                    EndPage = end,
                    Title = titleStr,
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

        private DespachoDocumentInfo? FindBestDespachoMatch(DocumentBoundary d, ExtractionResult? result, out double overlapRatio, out int overlapPages)
        {
            overlapRatio = 0.0;
            overlapPages = 0;
            if (result?.Documents == null || result.Documents.Count == 0) return null;

            DespachoDocumentInfo? best = null;
            foreach (var doc in result.Documents)
            {
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

        private List<Dictionary<string, object>> ConvertDespachoFields(DespachoDocumentInfo doc)
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
                    ["source"] = "despacho_extractor"
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

                var obj = BuildDocObject(boundary, analysis, pdfPath, scripts, null);
                obj["parent_doc_label"] = ExtractDocumentName(parent);
                obj["doc_label"] = anexos[i]["title"]?.ToString() ?? "Anexo";
                obj["doc_type"] = "anexo_split";
                list.Add(obj);
            }

            return list;
        }
    }
}
