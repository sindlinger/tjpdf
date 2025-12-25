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
using AnchorTemplateExtractor;

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
        private sealed class ProcessGroup
        {
            public string Process { get; }
            public List<Dictionary<string, object>> Documents { get; }

            public ProcessGroup(string process, List<Dictionary<string, object>> documents)
            {
                Process = process;
                Documents = documents;
            }
        }

        public override string Name => "pipeline-tjpb";
        public override string Description => "Etapa FPDF do pipeline-tjpb: consolida documentos em JSON";
        private TjpbDespachoConfig? _tjpbCfg;
        private PeritoCatalog? _peritoCatalog;
        private HonorariosTable? _honorariosTable;
        private LaudoHashDb? _laudoHashDb;
        private string _laudoHashDbPath = "";
        private Dictionary<string, string>? _laudosEspecieByEspecialidade;
        private string _laudosEspeciePath = "";
        private readonly Dictionary<string, Dictionary<string, Dictionary<string, object>>> _docTypeSeedsByProcess
            = new(StringComparer.OrdinalIgnoreCase);
        private ExtractionPlan? _anchorHeadPlan;
        private ExtractionPlan? _anchorTailPlan;
        private string _anchorHeadTemplatePath = "";
        private string _anchorTailTemplatePath = "";

        public override void Execute(string[] args)
        {
            string inputDir = ".";
            bool splitAnexos = false; // backward compat (anexos now covered by bookmark docs)
            int maxBookmarkPages = 30; // agora interno, sem flag na CLI
            bool onlyDespachos = false;
            bool filterDespacho = false;
            bool filterRequerimento = false;
            bool filterCertidao = false;
            string? signerContains = null;
            bool debugDocSummary = false;
            bool debugSigner = false;
            bool printJson = false;
            string? stage = null;
            string stageOutDir = Path.Combine(Directory.GetCurrentDirectory(), "stage7_out");
            bool stagePrintSummary = false;
            string? exportDocDtosDir = null;
            int limit = 0;
            string? exportCsvPath = null;
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
            string[] anchorTemplatesCandidates =
            {
                Path.Combine(cwd, "configs/anchor_templates"),
                Path.Combine(cwd, "../configs/anchor_templates"),
                Path.Combine(cwd, "../../configs/anchor_templates"),
                Path.GetFullPath(Path.Combine(exeBase, "../../../../configs/anchor_templates"))
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
            string anchorTemplatesDir = anchorTemplatesCandidates.FirstOrDefault(Directory.Exists) ?? "";
            string layoutHashesPath = hashCandidates.FirstOrDefault(File.Exists) ?? "";

            var fieldScripts = FieldScripts.LoadScripts(fieldScriptsPath);
            if (!string.IsNullOrEmpty(layoutHashesPath))
                DocIdClassifier.LoadHashes(layoutHashesPath);

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--input-dir" && i + 1 < args.Length) inputDir = args[i + 1];
                if (args[i] == "--split-anexos") splitAnexos = true;
                if (args[i] == "--only-despachos") onlyDespachos = true;
                if (args[i] == "--despacho" || args[i] == "-d") filterDespacho = true;
                if (args[i] == "--requerimento" || args[i] == "-r") filterRequerimento = true;
                if (args[i] == "--certidao" || args[i] == "-c") filterCertidao = true;
                if (args[i] == "--signer-contains" && i + 1 < args.Length) signerContains = args[i + 1];
                if (args[i] == "--config" && i + 1 < args.Length) configPath = args[i + 1];
                if (args[i] == "--debug-docsummary") debugDocSummary = true;
                if (args[i] == "--debug-signer") debugSigner = true;
                if (args[i] == "--print-json") printJson = true;
                if (args[i] == "--stage" && i + 1 < args.Length) stage = args[i + 1];
                if (args[i] == "--out-dir" && i + 1 < args.Length) stageOutDir = args[i + 1];
                if (args[i] == "--print-summary") stagePrintSummary = true;
                if (args[i] == "--export-doc-dtos" && i + 1 < args.Length) exportDocDtosDir = args[i + 1];
                if (args[i] == "--no-pg") pgUri = "";
                if (args[i] == "--limit" && i + 1 < args.Length)
                {
                    if (int.TryParse(args[i + 1], out var lim) && lim > 0)
                        limit = lim;
                }
                if (args[i] == "--export-csv")
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.OrdinalIgnoreCase))
                    {
                        exportCsvPath = args[i + 1];
                        i++;
                    }
                    else
                    {
                        exportCsvPath = Path.Combine(Directory.GetCurrentDirectory(), "tjpb_export.csv");
                    }
                }
            }

            var disablePgEnv = Environment.GetEnvironmentVariable("TJPB_NO_PG");
            bool usePg = !string.IsNullOrWhiteSpace(pgUri) && string.IsNullOrWhiteSpace(disablePgEnv);

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
            EnsureAnchorTemplates(anchorTemplatesDir);

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
                var jsonCaches = dir.GetFiles("*.json")
                                   .Where(f => !IsMetaCacheJson(f.Name))
                                   .OrderBy(f => f.Name)
                                   .ToList();
                var pdfs = dir.GetFiles("*.pdf").OrderBy(f => f.Name).ToList();
                if (pdfs.Count == 0)
                {
                    pdfs = dir.GetFiles("*.pdf", SearchOption.AllDirectories)
                              .OrderBy(f => f.Name)
                              .ToList();
                }
                if (jsonCaches.Count == 0 && pdfs.Count == 0)
                {
                    jsonCaches = dir.GetFiles("*.json", SearchOption.AllDirectories)
                                   .Where(f => !IsMetaCacheJson(f.Name))
                                   .OrderBy(f => f.Name)
                                   .ToList();
                }
                bool useCache = jsonCaches.Count > 0 && pdfs.Count == 0;

                var allDocs = new List<Dictionary<string, object>>();
                var allDocsWords = new List<List<Dictionary<string, object>>>();

                var sources = (useCache ? jsonCaches.Cast<FileInfo>() : pdfs.Cast<FileInfo>()).ToList();
                int processed = 0;

                foreach (var file in sources)
                {
                    if (limit > 0 && processed >= limit)
                        break;
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
                        if (!LooksLikePdf(file.FullName))
                        {
                            Console.Error.WriteLine($"[pipeline-tjpb] WARN {file.Name}: arquivo não é PDF (header inválido).");
                            continue;
                        }
                        analysis = new PDFAnalyzer(file.FullName).AnalyzeFull();
                        pdfPath = file.FullName;
                    }

                    var procName = DeriveProcessName(pdfPath);
                    analysesByProcess[procName] = analysis;
                    EnsureDocTypeSeeds(procName, pdfPath, analysis);
                    if (!useCache && usePg)
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

                    var despachoAnchorState = new DespachoAnchorState();
                    foreach (var d in docs)
                    {
                        var obj = BuildDocObject(d, analysis, pdfPath, fieldScripts, despachoResult, debugDocSummary, debugSigner, despachoAnchorState);
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
                    processed++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[pipeline-tjpb] WARN {file.Name}: {ex.Message}");
                }
            }

                if (string.Equals(stage, "s5", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stage, "s7", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(stage, "s5", StringComparison.OrdinalIgnoreCase))
                        WriteStage5Output(allDocs, stageOutDir, stagePrintSummary);
                    else
                        WriteStage7Output(allDocs, stageOutDir, stagePrintSummary);

                    if (!string.IsNullOrWhiteSpace(exportDocDtosDir))
                        WriteDocTypeDtos(allDocs, exportDocDtosDir);
                    return;
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
                    bool typeFilterAny = filterDespacho || filterRequerimento || filterCertidao;
                    if (typeFilterAny)
                    {
                        bool isDespacho = GetBool(doc, "is_despacho_valid");
                        bool isCertidao = GetBool(doc, "is_certidao_valid");
                        bool isRequerimento = GetBool(doc, "is_requerimento_pagamento_honorarios");
                        pass &= (filterDespacho && isDespacho) ||
                                (filterCertidao && isCertidao) ||
                                (filterRequerimento && isRequerimento);
                    }
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
                              .Select(g => new ProcessGroup(g.Key, g.ToList()))
                              .ToList();

                if (!string.IsNullOrWhiteSpace(exportCsvPath))
                    WriteCsvExport(exportCsvPath, grouped);

                if (!string.IsNullOrWhiteSpace(exportDocDtosDir))
                    WriteDocTypeDtos(allDocs, exportDocDtosDir);

                if (printJson)
                {
                    var outputs = new List<object>();
                    foreach (var grp in grouped)
                    {
                        var procName = grp.Process;
                        var firstDoc = grp.Documents.FirstOrDefault();
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
                        var processFields = BuildProcessRow(procName, grp.Documents);
                        var payload = new { process = procName, pdf = pdfMeta, process_fields = processFields, documents = grp.Documents, paragraph_stats = paragraphStats };
                        outputs.Add(payload);
                    }
                    if (outputs.Count == 1)
                        Console.WriteLine(JsonConvert.SerializeObject(outputs[0], Formatting.Indented));
                    else
                        Console.WriteLine(JsonConvert.SerializeObject(outputs, Formatting.Indented));
                    return;
                }

                if (!string.IsNullOrWhiteSpace(exportDocDtosDir))
                    WriteDocTypeDtos(allDocs, exportDocDtosDir);

                // Persistir por processo no Postgres (tabelas processes + documents)
                foreach (var grp in grouped)
                {
                    var procName = grp.Process ?? "";
                    var firstDoc = grp.Documents.FirstOrDefault();
                    string sourcePath = firstDoc != null && firstDoc.TryGetValue("pdf_path", out var pp)
                        ? (pp?.ToString() ?? procName)
                        : procName;
                    var analysis = analysesByProcess.TryGetValue(procName, out var an) ? an : new PDFAnalysisResult();
                    var pdfMeta = pdfMetaByProcess.TryGetValue(procName, out var meta) ? meta : new Dictionary<string, object>
                    {
                        ["fileName"] = Path.GetFileName(sourcePath ?? ""),
                        ["filePath"] = sourcePath ?? "",
                        ["pages"] = analysis.DocumentInfo.TotalPages,
                        ["sha256"] = "",
                        ["fileSize"] = 0L
                    };
                    var processFields = BuildProcessRow(procName, grp.Documents);
                    var payload = new { process = procName, pdf = pdfMeta, process_fields = processFields, documents = grp.Documents, paragraph_stats = paragraphStats };
                    var tokenProc = JToken.FromObject(payload);
                    foreach (var v in tokenProc.SelectTokens("$..*").OfType<JValue>())
                    {
                        if (v.Type == JTokenType.String && v.Value != null)
                        {
                            var s = v.Value?.ToString() ?? "";
                            s = Regex.Replace(s, @"\p{C}+", " ");
                            v.Value = s;
                        }
                    }
                    var jsonProc = tokenProc.ToString(Formatting.None);
                    if (usePg)
                    {
                        try
                        {
                            PgDocStore.UpsertProcess(pgUri, sourcePath ?? "", analysis, new BookmarkClassifier(),
                                                     storeJson: true, storeDocuments: false, jsonPayload: jsonProc);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"[pipeline-tjpb] WARN PG save {procName}: {ex.Message}");
                        }
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
            Console.WriteLine("fpdf pipeline-tjpb --input-dir <dir> [--limit <n>] [--split-anexos] [--only-despachos] [--signer-contains <texto>]");
            Console.WriteLine("--config: caminho para config.yaml (opcional; usado para hints de despacho).");
            Console.WriteLine("Lê caches (tmp/cache/*.json) ou PDFs; grava no Postgres (processes + documents). Não gera arquivo.");
            Console.WriteLine("Se houver .zip no input-dir, mergeia PDFs internos e cria bookmarks por nome de arquivo.");
            Console.WriteLine("--split-anexos: cria subdocumentos a partir de bookmarks 'Anexo/Anexos' dentro de cada documento.");
            Console.WriteLine("--only-despachos: filtra apenas documentos cujo doc_label/doc_type contenha 'Despacho'.");
            Console.WriteLine("--despacho|-d: filtra somente documentos com is_despacho_valid == true.");
            Console.WriteLine("--certidao|-c: filtra somente documentos com is_certidao_valid == true.");
            Console.WriteLine("--requerimento|-r: filtra somente documentos com is_requerimento_pagamento_honorarios == true.");
            Console.WriteLine("--signer-contains: filtra documentos cujo signer contenha o texto informado (case-insensitive).");
            Console.WriteLine("--debug-docsummary: imprime header/footer + campos de cabeçalho/assinatura por documento e não grava no Postgres.");
            Console.WriteLine("--debug-signer: adiciona signer_candidates (sei/footer/digital/lastline/fulltext) no JSON.");
            Console.WriteLine("--print-json: imprime o JSON completo (por processo) e não grava no Postgres.");
            Console.WriteLine("--export-csv [caminho]: gera CSV consolidado por processo (default: ./tjpb_export.csv).");
            Console.WriteLine("--stage s5 --out-dir <dir>: gera JSON por processo até o S5 (BuildDocObject) e não segue para CSV/PG.");
            Console.WriteLine("--stage s7 --out-dir <dir>: gera JSON por processo até o S7 (BuildDocObject) e não segue para CSV/PG.");
            Console.WriteLine("--export-doc-dtos <dir>: grava 3 DTOs por processo (despacho/certidao_cm/requerimento).");
            Console.WriteLine("--print-summary: imprime o manifest do stage (quando --stage s5/s7 estiver ativo).");
            Console.WriteLine("--limit <n>: processa no máximo <n> PDFs/JSONs da pasta de entrada.");
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

            if (_laudosEspecieByEspecialidade == null)
            {
                try
                {
                    var map = LoadLaudosEspecieMap(baseDir, out var path);
                    _laudosEspecieByEspecialidade = map ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _laudosEspeciePath = path ?? "";
                }
                catch
                {
                    _laudosEspecieByEspecialidade = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _laudosEspeciePath = "";
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

        private Dictionary<string, string> LoadLaudosEspecieMap(string baseDir, out string? usedPath)
        {
            usedPath = null;
            var candidates = new[]
            {
                Path.Combine(baseDir, "src/PipelineTjpb/reference/laudos/laudos_por_especie_revisado.csv"),
                Path.Combine(baseDir, "src/PipelineTjpb/reference/laudos/laudos_por_especie_scored.csv"),
                Path.Combine(baseDir, "src/PipelineTjpb/reference/laudos/laudos_por_especie_classificado.csv"),
                Path.Combine(baseDir, "src/PipelineTjpb/reference/laudos/laudos_por_especie.csv")
            };

            var path = candidates.FirstOrDefault(File.Exists);
            if (string.IsNullOrWhiteSpace(path))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            usedPath = path;
            var counts = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
            using var reader = new StreamReader(path);
            var headerLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(headerLine))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var header = ParseCsvLine(headerLine);
            var headerMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Count; i++)
            {
                var key = NormalizeKey(header[i]);
                if (!headerMap.ContainsKey(key))
                    headerMap[key] = i;
            }

            int idxEspecialidade = PickIndex(headerMap, "ESPECIALIDADE");
            int idxEspecieRev = PickIndex(headerMap, "ESPECIE_REV");
            int idxEspecieAuto = PickIndex(headerMap, "ESPECIE_AUTO");
            int idxEspecie = PickIndex(headerMap, "ESPECIE DE PERICIA");

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var row = ParseCsvLine(line);
                string especialidade = GetCol(row, idxEspecialidade);
                if (string.IsNullOrWhiteSpace(especialidade)) continue;

                string especie = GetCol(row, idxEspecieRev);
                if (string.IsNullOrWhiteSpace(especie)) especie = GetCol(row, idxEspecieAuto);
                if (string.IsNullOrWhiteSpace(especie)) especie = GetCol(row, idxEspecie);
                if (string.IsNullOrWhiteSpace(especie)) continue;

                var key = NormalizeKey(especialidade);
                var val = NormalizeValue(especie);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(val)) continue;

                if (!counts.TryGetValue(key, out var map))
                {
                    map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    counts[key] = map;
                }
                map[val] = map.TryGetValue(val, out var c) ? c + 1 : 1;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in counts)
            {
                var chosen = kv.Value.OrderByDescending(x => x.Value).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(chosen.Key))
                    result[kv.Key] = chosen.Key;
            }
            return result;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            if (line == null) return result;
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                    {
                        inQuotes = true;
                    }
                    else if (c == ',')
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }
            result.Add(sb.ToString());
            return result;
        }

        private int PickIndex(Dictionary<string, int> headerMap, string key)
        {
            var norm = NormalizeKey(key);
            return headerMap.TryGetValue(norm, out var idx) ? idx : -1;
        }

        private static string GetCol(List<string> row, int idx)
        {
            if (idx < 0 || idx >= row.Count) return "";
            return row[idx]?.Trim() ?? "";
        }

        private string NormalizeKey(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = RemoveDiacritics(text).ToUpperInvariant();
            t = Regex.Replace(t, @"\s+", " ").Trim();
            return t;
        }

        private string NormalizeValue(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var t = text.Trim();
            t = Regex.Replace(t, @"\s+", " ");
            return t;
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

        private bool IsMetaCacheJson(string fileName)
        {
            var name = Path.GetFileName(fileName)?.Trim().ToLowerInvariant() ?? "";
            if (name == "index.json" || name == "nodes.json") return true;
            if (name.EndsWith("_outputs.json", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("stage", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return true;
            if (name == "tjpdf_output.json") return true;
            return false;
        }

        private bool LooksLikePdf(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                if (fs.Length < 5) return false;
                Span<byte> buf = stackalloc byte[5];
                var read = fs.Read(buf);
                if (read < 5) return false;
                return buf[0] == (byte)'%' && buf[1] == (byte)'P' && buf[2] == (byte)'D' && buf[3] == (byte)'F' && buf[4] == (byte)'-';
            }
            catch
            {
                return false;
            }
        }

        private void EnsureDocTypeSeeds(string process, string pdfPath, PDFAnalysisResult? analysis)
        {
            if (string.IsNullOrWhiteSpace(process))
                return;

            if (!_docTypeSeedsByProcess.TryGetValue(process, out var map))
            {
                map = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                _docTypeSeedsByProcess[process] = map;
            }

            EnsureDocSeed(map, process, pdfPath, analysis, "despacho", "Despacho");
            EnsureDocSeed(map, process, pdfPath, analysis, "certidao_cm", "Certidao CM");
            EnsureDocSeed(map, process, pdfPath, analysis, "requerimento_pagamento_honorarios", "Requerimento de Pagamento de Honorarios");
        }

        private void EnsureDocSeed(Dictionary<string, Dictionary<string, object>> map, string process, string pdfPath, PDFAnalysisResult? analysis, string docTypeKey, string label)
        {
            if (map.ContainsKey(docTypeKey))
                return;

            var seed = CreateDocSeed(process, pdfPath, analysis, docTypeKey, label);
            map[docTypeKey] = seed;
        }

        private Dictionary<string, object> CreateDocSeed(string process, string pdfPath, PDFAnalysisResult? analysis, string docTypeKey, string label)
        {
            var totalPages = analysis?.DocumentInfo?.TotalPages ?? 0;
            var seed = new Dictionary<string, object>
            {
                ["process"] = process ?? "",
                ["pdf_path"] = pdfPath ?? "",
                ["doc_label"] = label ?? "",
                ["doc_label_original"] = label ?? "",
                ["doc_type"] = docTypeKey ?? "",
                ["start_page"] = 0,
                ["end_page"] = 0,
                ["doc_pages"] = 0,
                ["total_pages"] = totalPages,
                ["word_count"] = 0L,
                ["char_count"] = 0L,
                ["percentual_blank"] = 0.0
            };
            EnsureDocPayloadDefaults(seed);
            return seed;
        }

        private Dictionary<string, object> GetDocSeed(string process, string docTypeKey)
        {
            if (!_docTypeSeedsByProcess.TryGetValue(process, out var map))
            {
                map = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                _docTypeSeedsByProcess[process] = map;
            }

            if (!map.TryGetValue(docTypeKey, out var seed))
            {
                seed = CreateDocSeed(process, "", null, docTypeKey, docTypeKey);
                map[docTypeKey] = seed;
            }

            return seed;
        }

        private void MergeDocIntoSeed(Dictionary<string, object> seed, Dictionary<string, object> doc)
        {
            if (seed == null || doc == null) return;
            foreach (var kv in doc)
            {
                if (kv.Value == null) continue;
                seed[kv.Key] = kv.Value;
            }
            EnsureDocPayloadDefaults(seed);
        }

        private Dictionary<string, object> BuildDocObject(DocumentBoundary d, PDFAnalysisResult analysis, string pdfPath, List<FieldScript> scripts, ExtractionResult? despachoResult, bool debug = false, bool debugSigner = false, DespachoAnchorState? anchorState = null)
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
            int filledCharCount = 0;
            double totalLineCapacity = 0;
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

                var pageWordDicts = new List<Dictionary<string, object>>();
                foreach (var w in page.TextInfo.Words)
                {
                    var dict = new Dictionary<string, object>
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
                    };
                    pageWordDicts.Add(dict);
                    wordsWithCoords.Add(dict);
                }

                var lineObjs = BuildLines(pageWordDicts);
                var textLines = lineObjs
                    .Select(l => new { Line = l, SolidCount = CountSolidChars(l.Text) })
                    .Where(x => x.SolidCount > 0)
                    .ToList();

                filledCharCount += textLines.Sum(x => x.SolidCount);

                if (textLines.Count > 0)
                {
                    var lineHeights = textLines
                        .Select(x => Math.Max(0, x.Line.NY1 - x.Line.NY0))
                        .Where(h => h > 0)
                        .OrderBy(h => h)
                        .ToList();
                    double lineHeight = lineHeights.Count > 0 ? lineHeights[lineHeights.Count / 2] : 0;

                    var ordered = textLines
                        .OrderByDescending(x => x.Line.NY1)
                        .ToList();

                    int blankLines = 0;
                    if (lineHeight > 0 && ordered.Count > 1)
                    {
                        for (int i = 0; i < ordered.Count - 1; i++)
                        {
                            var current = ordered[i].Line;
                            var next = ordered[i + 1].Line;
                            var gap = current.NY0 - next.NY1;
                            if (gap > lineHeight)
                            {
                                var extra = (int)Math.Round(gap / lineHeight) - 1;
                                if (extra > 0) blankLines += extra;
                            }
                        }
                    }

                    var lineWidths = textLines
                        .Select(x => Math.Max(0, x.Line.NX1 - x.Line.NX0))
                        .ToList();
                    var charWidths = textLines
                        .Select(x =>
                        {
                            var w = Math.Max(0, x.Line.NX1 - x.Line.NX0);
                            return x.SolidCount > 0 ? (w / x.SolidCount) : 0;
                        })
                        .Where(v => v > 0)
                        .ToList();

                    double avgCharWidth = charWidths.Count > 0 ? charWidths.Average() : 0;
                    double maxLineWidth = lineWidths.Count > 0 ? lineWidths.Max() : 0;
                    double maxCharsPerLine = (avgCharWidth > 0 && maxLineWidth > 0) ? (maxLineWidth / avgCharWidth) : 0;

                    double lineSlots = textLines.Count + blankLines;
                    totalLineCapacity += maxCharsPerLine * lineSlots;
                }

                if (string.IsNullOrEmpty(header) && page.TextInfo.Headers.Any()) header = page.TextInfo.Headers.First();
                if (string.IsNullOrEmpty(footer) && page.TextInfo.Footers.Any()) footer = page.TextInfo.Footers.First();
                if (page.TextInfo.Footers != null && page.TextInfo.Footers.Count > 0)
                    footerLines.AddRange(page.TextInfo.Footers);
            }

            var headerCfg = _tjpbCfg ?? new TjpbDespachoConfig();
            var headerTopPct = headerCfg.Thresholds?.Bands?.HeaderTopPct ?? 0.15;
            var footerBandPct = Math.Max(0.30, headerCfg.Thresholds?.Bands?.FooterBottomPct ?? 0.15);
            var footerSignatureRaw = ExtractFooterSignatureRaw(lastPageText);
            var footerSignatureFromWords = ExtractFooterSignatureRawFromWords(wordsWithCoords, d.EndPage, footerBandPct);
            footerSignatureRaw = PickBestFooterSignatureRaw(footerSignatureRaw, footerSignatureFromWords);
            if (string.IsNullOrWhiteSpace(header))
            {
                var firstPage = analysis.Pages[d.StartPage - 1];
                header = ExtractHeaderFromLines(firstPage.TextInfo.Lines, headerTopPct);
                if (string.IsNullOrWhiteSpace(header))
                    header = ExtractHeaderFromWords(wordsWithCoords, d.StartPage, headerTopPct);
                if (string.IsNullOrWhiteSpace(header))
                    header = ExtractHeaderFromText(firstPage.TextInfo.PageText);
            }

            string subHeader = "";
            var headerMeta = ExtractHeaderAndSubheader(wordsWithCoords, d.StartPage, headerTopPct);
            if (!string.IsNullOrWhiteSpace(headerMeta.Header))
            {
                var headerLines = headerMeta.Header.Split('\n').Length;
                var headerKey = NormalizeHeaderKey(headerMeta.Header);
                if (headerLines >= 2 || headerKey.Contains("PODERJUDICIARIO") || headerKey.Contains("TRIBUNALDEJUSTICA"))
                    header = headerMeta.Header;
            }
            if (!string.IsNullOrWhiteSpace(headerMeta.Subheader))
                subHeader = headerMeta.Subheader;

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
            double percentualBlank = totalLineCapacity > 0
                ? (filledCharCount / totalLineCapacity) * 100.0
                : 0;
            if (percentualBlank < 0) percentualBlank = 0;
            if (percentualBlank > 100) percentualBlank = 100;

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

            var isRequerimentoDoc = IsRequerimentoPagamentoHonorarios(originalLabel, d.FirstPageText, docText);
            if (isRequerimentoDoc)
            {
                docLabel = "Requerimento de Pagamento de Honorarios";
                docType = docLabel;
            }

            var scriptLabel = docLabel;
            var docMeta = BuildDocMeta(d, pdfPath, docText, lastPageText, lastTwoText, header, subHeader, footer, footerSignatureRaw, docBookmarks, analysis.Signatures, docLabel, wordsWithCoords, debugSigner);
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
            var isDespachoCandidate = labelIsDespacho || attachDespacho;
            var isCertidaoCandidate = labelIsCertidao || attachCertidao;
            var dateFooter = docMeta.TryGetValue("date_footer", out var df) ? df?.ToString() ?? "" : "";
            var signedAtSummary = docMeta.TryGetValue("signed_at", out var sa) ? sa?.ToString() ?? "" : "";
            if (!string.IsNullOrWhiteSpace(signedAtSummary))
                dateFooter = signedAtSummary;
            if (!string.IsNullOrWhiteSpace(dateFooter))
            {
                if (isCertidaoCandidate)
                    docMeta["certidao_date"] = dateFooter;
                if (isDespachoCandidate)
                    docMeta["despacho_date"] = dateFooter;
            }
            var cfg = _tjpbCfg ?? new TjpbDespachoConfig();
            var minPages = cfg.Thresholds.MinPages > 0 ? cfg.Thresholds.MinPages : 2;
            var blankMaxPct = cfg.Thresholds.BlankMaxPct > 0 ? cfg.Thresholds.BlankMaxPct : 15;
            var densityOk = percentualBlank <= blankMaxPct;
            var isDespachoTarget = IsTargetDespachoTemplate(docText);
            docMeta["blank_threshold"] = blankMaxPct;
            docMeta["density_ok"] = densityOk;
            docMeta["is_despacho_target"] = isDespachoTarget;
            var isDespachoValid = isDespachoCandidate && d.PageCount >= minPages && MatchesOrigin(docMeta, "despacho") && MatchesSigner(docMeta);
            var isCertidaoValid = isCertidaoCandidate && MatchesOrigin(docMeta, "certidao") && MatchesSigner(docMeta);
            var isDespachoShort = isDespachoCandidate && d.PageCount < 2;
            var paragraphs = BuildParagraphsFromWords(wordsWithCoords);
            var bookmarkSlices = BuildBookmarkParagraphSlices(header, subHeader, footer, paragraphs, d.StartPage, d.EndPage);
            docMeta["bookmark_first_paragraph"] = bookmarkSlices.First;
            docMeta["bookmark_last_paragraph"] = bookmarkSlices.Last;
            if (isDespachoValid)
            {
                ApplyDespachoHeadTailText(docMeta, header, subHeader, footer, footerSignatureRaw, paragraphs, d.StartPage, d.EndPage);
            }
            else
            {
                docMeta["doc_head"] = "";
                docMeta["doc_tail"] = "";
            }
            var shouldApplyAnchor = isDespachoValid && isDespachoTarget && (anchorState?.Applied != true);
            if (shouldApplyAnchor)
            {
                ApplyDespachoHeadTailBBoxes(docMeta, despachoMatch, wordsWithCoords, d.StartPage, d.EndPage);
                ApplyAnchorExtraction(docMeta);
                if (anchorState != null) anchorState.Applied = true;
            }

            var forcedBucket = isDespachoValid ? "principal" : null;
            var extractedFields = ExtractFields(docText, wordsWithCoords, d, pdfPath, scripts, forcedBucket, scriptLabel);
            var bandFields = new List<Dictionary<string, object>>();
            var directedFields = ExtractDirectedValues(analysis, d, docType, docText);
            var requerimentoFields = isRequerimentoDoc
                ? ExtractRequerimentoFields(docText, wordsWithCoords, d.StartPage, d.EndPage)
                : new List<Dictionary<string, object>>();
            if (attachDespacho && despachoMatch != null)
            {
                var despachoFields = ConvertDespachoFields(despachoMatch, "despacho_extractor");
                if (despachoFields.Count > 0)
                    extractedFields.AddRange(despachoFields);
            }
            if (attachCertidao && certidaoMatch != null)
            {
                var certidaoFields = ConvertDespachoFields(certidaoMatch, "certidao_extractor");
                if (certidaoFields.Count > 0)
                    extractedFields.AddRange(certidaoFields);
            }
            if (isDespachoValid)
            {
                bandFields = ExtractBandFields(docText, wordsWithCoords, d.StartPage);
                extractedFields = MergeFields(extractedFields, bandFields);
            }
            if (directedFields.Count > 0)
                extractedFields = MergeFields(extractedFields, directedFields);
            if (requerimentoFields.Count > 0)
                extractedFields = MergeFields(extractedFields, requerimentoFields);
            if (isDespachoValid)
                extractedFields = FilterDespachoValorPages(extractedFields, d.StartPage, d.EndPage);
            if (isRequerimentoDoc)
                extractedFields = FilterRequerimentoFields(extractedFields);
            AddDocMetaFallbacks(extractedFields, docMeta, d.StartPage, wordsWithCoords);
            RefineFieldsWithParagraphs(extractedFields, paragraphs, docMeta);
            var normalizedFields = NormalizeAndValidateFields(extractedFields);
            var missingFields = EnsureRequiredFields(normalizedFields, isDespachoValid, isCertidaoValid, isRequerimentoDoc);
            var forensics = BuildForensics(d, analysis, docText, wordsWithCoords, paragraphs);
            var despachoInfo = DetectDespachoTipo(docText, lastTwoText);
            var isDespachoAutorizacao = isDespachoValid && (despachoInfo.Categoria == "autorizacao" || despachoInfo.Destino == "georc");
            var isDespachoEncaminhamento = isDespachoValid && (despachoInfo.Categoria == "encaminhamento" || despachoInfo.Destino == "conselho");
            var isRequerimento = isRequerimentoDoc;
            var requerimentoFieldsOnly = isRequerimento
                ? normalizedFields.Where(f => string.Equals(f.GetValueOrDefault("method")?.ToString(), "requerimento_extractor", StringComparison.OrdinalIgnoreCase)).ToList()
                : new List<Dictionary<string, object>>();
            if (isRequerimento)
            {
                var dataReq = PickBestFieldValue(requerimentoFieldsOnly, "DATA_REQUISICAO");
                if (string.IsNullOrWhiteSpace(dataReq))
                    dataReq = PickBestFieldValue(requerimentoFieldsOnly, "DATA");
                if (!string.IsNullOrWhiteSpace(dataReq))
                    docMeta["data_requisicao"] = dataReq;
            }

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
                ["is_despacho"] = isDespachoCandidate,
                ["is_certidao"] = isCertidaoCandidate,
                ["is_despacho_valid"] = isDespachoValid,
                ["is_despacho_short"] = isDespachoShort,
                ["is_certidao_valid"] = isCertidaoValid,
                ["is_requerimento_pagamento_honorarios"] = isRequerimento,
                ["requerimento_fields"] = requerimentoFieldsOnly,
                ["despacho_overlap_ratio"] = despachoMatch != null ? overlapRatio : 0.0,
                ["despacho_overlap_pages"] = despachoMatch != null ? overlapPages : 0,
                ["despacho_match_score"] = despachoMatch != null ? despachoMatch.MatchScore : 0.0,
                ["certidao_overlap_ratio"] = certidaoMatch != null ? certOverlapRatio : 0.0,
                ["certidao_overlap_pages"] = certidaoMatch != null ? certOverlapPages : 0,
                ["certidao_match_score"] = certidaoMatch != null ? certidaoMatch.MatchScore : 0.0,
                ["is_despacho_autorizacao"] = isDespachoAutorizacao,
                ["is_despacho_encaminhamento"] = isDespachoEncaminhamento,
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
                ["percentual_blank"] = percentualBlank,
                ["words"] = wordsWithCoords,
                ["header"] = header,
                ["footer"] = footer,
                ["footer_signature_raw"] = footerSignatureRaw,
                ["bookmarks"] = docBookmarks,
                ["anexos_bookmarks"] = anexos,
                ["fields"] = normalizedFields,
                ["fields_missing"] = missingFields,
                ["band_fields"] = bandFields,
                ["despacho_extraction"] = attachDespacho ? (despachoMatch ?? (object)JValue.CreateNull()) : JValue.CreateNull(),
                ["certidao_extraction"] = attachCertidao ? (certidaoMatch ?? (object)JValue.CreateNull()) : JValue.CreateNull(),
                ["forensics"] = forensics
            };
            foreach (var kv in docMeta)
            {
                if (!obj.ContainsKey(kv.Key))
                    obj[kv.Key] = kv.Value;
            }
            EnsureDocPayloadDefaults(obj);
            var fullPayload = new Dictionary<string, object>(obj);
            obj["doc_payload"] = fullPayload;
            if (isDespachoValid) obj["despacho_payload"] = fullPayload;
            if (isCertidaoValid) obj["certidao_payload"] = fullPayload;
            if (isRequerimento) obj["requerimento_payload"] = fullPayload;
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
                ["promovente"] = Get("PROMOVENTE"),
                ["promovido"] = Get("PROMOVIDO"),
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

        private void WriteCsvExport(string path, List<ProcessGroup> groups)
        {
            if (groups == null || groups.Count == 0)
            {
                Console.WriteLine("[pipeline-tjpb] CSV: nenhum processo para exportar.");
                return;
            }

            var headers = new[]
            {
                "processo_administrativo",
                "processo_judicial",
                "comarca",
                "vara",
                "promovente",
                "promovido",
                "perito",
                "perito_cpf",
                "especialidade",
                "especie_pericia",
                "valor_arbitrado_jz",
                "valor_arbitrado_de",
                "valor_arbitrado_cm",
                "valor_arbitrado_final",
                "data_arbitrado_final",
                "data_requisicao"
            };

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(";", headers.Select(CsvEscape)));

            foreach (var grp in groups)
            {
                var row = BuildProcessRow(grp.Process, grp.Documents);
                var line = string.Join(";", headers.Select(h => CsvEscape(row.TryGetValue(h, out var v) ? v : "")));
                sb.AppendLine(line);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Console.WriteLine($"[pipeline-tjpb] CSV exportado: {path} ({groups.Count} processos).");
        }

        private void WriteStage7Output(List<Dictionary<string, object>> docs, string outDir, bool printSummary)
        {
            docs ??= new List<Dictionary<string, object>>();
            Directory.CreateDirectory(outDir);

            var outputs = new List<Dictionary<string, object>>();
            var grouped = docs.GroupBy(d => d.TryGetValue("process", out var p) ? p?.ToString() ?? "sem_processo" : "sem_processo")
                              .ToList();

            foreach (var grp in grouped)
            {
                var procName = grp.Key ?? "sem_processo";
                var outPath = Path.Combine(outDir, $"{procName}.json");
                var processFields = BuildProcessRow(procName, grp.ToList());
                var payload = new
                {
                    stage = "S7",
                    process = procName,
                    process_fields = processFields,
                    documents = grp.ToList()
                };
                File.WriteAllText(outPath, JsonConvert.SerializeObject(payload, Formatting.Indented));
                outputs.Add(new Dictionary<string, object>
                {
                    ["process"] = procName,
                    ["output"] = outPath,
                    ["documents"] = grp.Count()
                });
            }

            var manifest = new Dictionary<string, object>
            {
                ["stage"] = "S7",
                ["output_dir"] = Path.GetFullPath(outDir),
                ["outputs"] = outputs
            };
            var manifestPath = Path.Combine(outDir, "stage7_outputs.json");
            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));

            Console.WriteLine($"[pipeline-tjpb] S7 output: {Path.GetFullPath(outDir)}");
            Console.WriteLine($"[pipeline-tjpb] S7 manifest: {manifestPath}");
            if (printSummary)
                Console.WriteLine(JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        private void WriteStage5Output(List<Dictionary<string, object>> docs, string outDir, bool printSummary)
        {
            docs ??= new List<Dictionary<string, object>>();
            Directory.CreateDirectory(outDir);

            var outputs = new List<Dictionary<string, object>>();
            var grouped = docs.GroupBy(d => d.TryGetValue("process", out var p) ? p?.ToString() ?? "sem_processo" : "sem_processo")
                              .ToList();

            foreach (var grp in grouped)
            {
                var procName = grp.Key ?? "sem_processo";
                var outPath = Path.Combine(outDir, $"{procName}.json");
                var processFields = BuildProcessRow(procName, grp.ToList());
                var payload = new
                {
                    stage = "S5",
                    process = procName,
                    process_fields = processFields,
                    documents = grp.ToList()
                };
                File.WriteAllText(outPath, JsonConvert.SerializeObject(payload, Formatting.Indented));
                outputs.Add(new Dictionary<string, object>
                {
                    ["process"] = procName,
                    ["output"] = outPath,
                    ["documents"] = grp.Count()
                });
            }

            var manifest = new Dictionary<string, object>
            {
                ["stage"] = "S5",
                ["output_dir"] = Path.GetFullPath(outDir),
                ["outputs"] = outputs
            };
            var manifestPath = Path.Combine(outDir, "stage5_outputs.json");
            File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));

            Console.WriteLine($"[pipeline-tjpb] S5 output: {Path.GetFullPath(outDir)}");
            Console.WriteLine($"[pipeline-tjpb] S5 manifest: {manifestPath}");
            if (printSummary)
                Console.WriteLine(JsonConvert.SerializeObject(manifest, Formatting.Indented));
        }

        private void WriteDocTypeDtos(List<Dictionary<string, object>> docs, string outDir)
        {
            docs ??= new List<Dictionary<string, object>>();
            Directory.CreateDirectory(outDir);

            var grouped = docs.GroupBy(d => d.TryGetValue("process", out var p) ? p?.ToString() ?? "sem_processo" : "sem_processo")
                              .ToList();

            foreach (var grp in grouped)
            {
                var procName = grp.Key ?? "sem_processo";
                var procDir = Path.Combine(outDir, procName);
                Directory.CreateDirectory(procDir);

                // Despacho: documento com is_despacho_valid == true (label/overlap + origem + assinatura).
                var despachoDoc = PickBestDoc(grp.ToList(), d => GetBool(d, "is_despacho_valid"));
                // Certidão CM: documento com is_certidao_valid == true.
                var certidaoDoc = PickBestDoc(grp.ToList(), d => GetBool(d, "is_certidao_valid"));
                // Requerimento: documento com is_requerimento_pagamento_honorarios == true.
                var requerimentoDoc = PickBestDoc(grp.ToList(), d => GetBool(d, "is_requerimento_pagamento_honorarios"));

                WriteDocTypeDto(procDir, procName, "despacho", "is_despacho_valid == true", despachoDoc);
                WriteDocTypeDto(procDir, procName, "certidao_cm", "is_certidao_valid == true", certidaoDoc);
                WriteDocTypeDto(procDir, procName, "requerimento_pagamento_honorarios", "is_requerimento_pagamento_honorarios == true", requerimentoDoc);
            }
        }

        private void WriteDocTypeDto(string procDir, string process, string docType, string criteria, Dictionary<string, object>? doc)
        {
            var seed = GetDocSeed(process, docType);
            if (doc != null)
                MergeDocIntoSeed(seed, doc);
            var payload = new Dictionary<string, object>
            {
                ["process"] = process,
                ["doc_type"] = docType,
                ["selection_criteria"] = criteria,
                ["found"] = doc != null,
                ["document"] = seed
            };

            var outPath = Path.Combine(procDir, $"{docType}.json");
            File.WriteAllText(outPath, JsonConvert.SerializeObject(payload, Formatting.Indented));
        }

        private Dictionary<string, string> BuildProcessRow(string process, List<Dictionary<string, object>> docs)
        {
            docs ??= new List<Dictionary<string, object>>();
            var despachoDoc = PickBestDoc(docs, d => GetBool(d, "is_despacho_valid"));
            var certidaoDoc = PickBestDoc(docs, d => GetBool(d, "is_certidao_valid"));
            var requerimentoDoc = PickBestDoc(docs, d => GetBool(d, "is_requerimento_pagamento_honorarios"));
            var fallbackDoc = docs.FirstOrDefault();

            string PickField(string field, params Dictionary<string, object>?[] priorityDocs)
            {
                foreach (var doc in priorityDocs)
                {
                    if (doc == null) continue;
                    var fields = GetFieldsFromDoc(doc);
                    var value = PickBestFieldValue(fields, field);
                    if (!string.IsNullOrWhiteSpace(value)) return value;
                }
                return "";
            }

            string PickMeta(string metaKey, params Dictionary<string, object>?[] priorityDocs)
            {
                foreach (var doc in priorityDocs)
                {
                    if (doc == null) continue;
                    if (doc.TryGetValue(metaKey, out var v) && v != null)
                    {
                        var s = v.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(s)) return s;
                    }
                }
                return "";
            }

            var processoAdministrativo = PickField("PROCESSO_ADMINISTRATIVO", despachoDoc, requerimentoDoc, certidaoDoc, fallbackDoc);
            var processoAdministrativoSei = PickMeta("sei_process", despachoDoc, requerimentoDoc, certidaoDoc, fallbackDoc);
            if (string.IsNullOrWhiteSpace(processoAdministrativo))
                processoAdministrativo = processoAdministrativoSei;

            var processoJudicial = PickField("PROCESSO_JUDICIAL", despachoDoc, requerimentoDoc, certidaoDoc, fallbackDoc);
            if (string.IsNullOrWhiteSpace(processoJudicial))
                processoJudicial = PickMeta("process_cnj", despachoDoc, requerimentoDoc, certidaoDoc, fallbackDoc);


            var perito = PickField("PERITO", despachoDoc, requerimentoDoc, certidaoDoc, fallbackDoc);
            if (string.IsNullOrWhiteSpace(perito))
                perito = PickMeta("interested_name", despachoDoc, requerimentoDoc, certidaoDoc, fallbackDoc);

            var peritoCpf = PickField("CPF_PERITO", despachoDoc, requerimentoDoc, certidaoDoc, fallbackDoc);

            var especialidade = PickField("ESPECIALIDADE", despachoDoc, requerimentoDoc, certidaoDoc, fallbackDoc);
            if (string.IsNullOrWhiteSpace(especialidade))
                especialidade = PickMeta("interested_profession", despachoDoc, requerimentoDoc, certidaoDoc, fallbackDoc);

            var especiePericia = PickField("ESPECIE_DA_PERICIA", despachoDoc, requerimentoDoc, certidaoDoc, fallbackDoc);

            var promovente = PickField("PROMOVENTE", despachoDoc, certidaoDoc, fallbackDoc);
            var promovido = PickField("PROMOVIDO", despachoDoc, certidaoDoc, fallbackDoc);

            var valorJz = PickField("VALOR_ARBITRADO_JZ", requerimentoDoc, despachoDoc, fallbackDoc);
            var valorDe = PickField("VALOR_ARBITRADO_DE", despachoDoc, fallbackDoc);
            var valorCm = PickField("VALOR_ARBITRADO_CM", certidaoDoc, fallbackDoc);
            var valorFinal = "";
            var dataFinal = "";
            if (!string.IsNullOrWhiteSpace(valorCm))
            {
                valorFinal = valorCm;
                dataFinal = PickMeta("certidao_date", certidaoDoc) ?? "";
                if (string.IsNullOrWhiteSpace(dataFinal))
                    dataFinal = PickMeta("signed_at", certidaoDoc) ?? "";
            }
            else if (!string.IsNullOrWhiteSpace(valorDe))
            {
                valorFinal = valorDe;
                dataFinal = PickMeta("despacho_date", despachoDoc) ?? "";
                if (string.IsNullOrWhiteSpace(dataFinal))
                    dataFinal = PickMeta("signed_at", despachoDoc) ?? "";
            }
            else if (!string.IsNullOrWhiteSpace(valorJz))
            {
                valorFinal = valorJz;
                dataFinal = PickMeta("despacho_date", despachoDoc, requerimentoDoc) ?? "";
                if (string.IsNullOrWhiteSpace(dataFinal))
                    dataFinal = PickMeta("signed_at", despachoDoc, requerimentoDoc) ?? "";
            }

            var dataRequisicao = PickField("DATA_REQUISICAO", requerimentoDoc);
            if (string.IsNullOrWhiteSpace(dataRequisicao))
                dataRequisicao = PickMeta("data_requisicao", requerimentoDoc);

            var comarca = PickField("COMARCA", despachoDoc, certidaoDoc, fallbackDoc);
            if (string.IsNullOrWhiteSpace(comarca))
                comarca = PickMeta("comarca", despachoDoc, certidaoDoc, fallbackDoc);

            var vara = PickField("VARA", despachoDoc, certidaoDoc, fallbackDoc);
            if (string.IsNullOrWhiteSpace(vara))
                vara = PickMeta("juizo_vara", despachoDoc, certidaoDoc, fallbackDoc);

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["process"] = process ?? "",
                ["processo_administrativo"] = processoAdministrativo ?? "",
                ["processo_judicial"] = processoJudicial ?? "",
                ["comarca"] = comarca ?? "",
                ["vara"] = vara ?? "",
                ["promovente"] = promovente ?? "",
                ["promovido"] = promovido ?? "",
                ["perito"] = perito ?? "",
                ["perito_cpf"] = peritoCpf ?? "",
                ["especialidade"] = especialidade ?? "",
                ["especie_pericia"] = especiePericia ?? "",
                ["valor_arbitrado_jz"] = valorJz ?? "",
                ["valor_arbitrado_de"] = valorDe ?? "",
                ["valor_arbitrado_cm"] = valorCm ?? "",
                ["valor_arbitrado_final"] = valorFinal ?? "",
                ["data_arbitrado_final"] = dataFinal ?? "",
                ["data_requisicao"] = dataRequisicao ?? "",
                ["despacho_label"] = despachoDoc?.GetValueOrDefault("doc_label")?.ToString() ?? "",
                ["certidao_label"] = certidaoDoc?.GetValueOrDefault("doc_label")?.ToString() ?? "",
                ["requerimento_label"] = requerimentoDoc?.GetValueOrDefault("doc_label")?.ToString() ?? ""
            };
        }

        private Dictionary<string, object>? PickBestDoc(List<Dictionary<string, object>> docs, Func<Dictionary<string, object>, bool> predicate)
        {
            if (docs == null || docs.Count == 0) return null;
            var candidates = docs.Where(predicate).ToList();
            if (candidates.Count == 0) return null;
            return candidates
                .OrderByDescending(d => GetInt(d, "doc_pages"))
                .ThenByDescending(d => GetInt(d, "end_page"))
                .ThenBy(d => GetDouble(d, "percentual_blank"))
                .FirstOrDefault();
        }

        private List<Dictionary<string, object>> GetFieldsFromDoc(Dictionary<string, object> doc)
        {
            if (doc == null) return new List<Dictionary<string, object>>();
            if (doc.TryGetValue("fields", out var obj))
            {
                if (obj is List<Dictionary<string, object>> list) return list;
                if (obj is JArray arr)
                    return arr.ToObject<List<Dictionary<string, object>>>() ?? new List<Dictionary<string, object>>();
            }
            return new List<Dictionary<string, object>>();
        }

        private int GetInt(Dictionary<string, object> doc, string key)
        {
            if (doc == null || !doc.TryGetValue(key, out var v) || v == null) return 0;
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (int.TryParse(v.ToString(), out var r)) return r;
            return 0;
        }

        private double GetDouble(Dictionary<string, object> doc, string key)
        {
            if (doc == null || !doc.TryGetValue(key, out var v) || v == null) return double.MaxValue;
            if (v is double d) return d;
            if (v is float f) return f;
            if (v is decimal m) return (double)m;
            if (v is int i) return i;
            if (v is long l) return l;
            var s = v.ToString();
            if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            if (double.TryParse(s, NumberStyles.Any, new CultureInfo("pt-BR"), out parsed)) return parsed;
            return double.MaxValue;
        }

        private bool GetBool(Dictionary<string, object> doc, string key)
        {
            if (doc == null || !doc.TryGetValue(key, out var v) || v == null) return false;
            if (v is bool b) return b;
            if (bool.TryParse(v.ToString(), out var r)) return r;
            if (int.TryParse(v.ToString(), out var i)) return i != 0;
            return false;
        }

        private string CsvEscape(string value)
        {
            var v = value ?? "";
            if (v.Contains('"'))
                v = v.Replace("\"", "\"\"");
            if (v.Contains(';') || v.Contains('\n') || v.Contains('\r') || v.Contains('"'))
                return $"\"{v}\"";
            return v;
        }

        private void EnsureDocPayloadDefaults(Dictionary<string, object> doc)
        {
            void Ensure(string key, object value)
            {
                if (!doc.ContainsKey(key) || doc[key] == null)
                    doc[key] = value;
            }

            Ensure("certidao_date", "");
            Ensure("despacho_date", "");
            Ensure("data_requisicao", "");
            Ensure("process_bbox", new Dictionary<string, double>());
            Ensure("interested_bbox", new Dictionary<string, double>());
            Ensure("juizo_bbox", new Dictionary<string, double>());
            Ensure("origin_main", "");
            Ensure("origin_sub", "");
            Ensure("origin_extra", "");
            Ensure("signer", "");
            Ensure("signed_at", "");
            Ensure("header_hash", "");
            Ensure("bookmark_first_paragraph", "");
            Ensure("bookmark_last_paragraph", "");
            Ensure("title", "");
            Ensure("template", "");
            Ensure("sei_process", "");
            Ensure("sei_doc", "");
            Ensure("sei_crc", "");
            Ensure("sei_verifier", "");
            Ensure("auth_url", "");
            Ensure("date_footer", "");
            Ensure("process_line", "");
            Ensure("interested_line", "");
            Ensure("interested_name", "");
            Ensure("interested_profession", "");
            Ensure("interested_email", "");
            Ensure("juizo_line", "");
            Ensure("juizo_vara", "");
            Ensure("comarca", "");
            Ensure("forensics", new Dictionary<string, object>());
            Ensure("fields", new List<Dictionary<string, object>>());
            Ensure("fields_missing", new List<string>());
            Ensure("band_fields", new List<Dictionary<string, object>>());
            Ensure("bookmarks", new List<Dictionary<string, object>>());
            Ensure("anexos_bookmarks", new List<Dictionary<string, object>>());
        }

        private Dictionary<string, object> BuildRequerimentoPayload(List<Dictionary<string, object>> fields)
        {
            if (fields == null) fields = new List<Dictionary<string, object>>();
            string Get(string name) => PickBestFieldValue(fields, name);

            var vJz = Get("VALOR_ARBITRADO_JZ");
            return new Dictionary<string, object>
            {
                ["processo_administrativo"] = Get("PROCESSO_ADMINISTRATIVO"),
                ["processo_judicial"] = Get("PROCESSO_JUDICIAL"),
                ["data_requisicao"] = Get("DATA_REQUISICAO"),
                ["perito"] = Get("PERITO"),
                ["perito_cpf"] = Get("CPF_PERITO"),
                ["especialidade"] = Get("ESPECIALIDADE"),
                ["especie_pericia"] = Get("ESPECIE_DA_PERICIA"),
                ["valor_arbitrado_jz"] = vJz,
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

        private Dictionary<string, object> BuildDocMeta(DocumentBoundary d, string pdfPath, string fullText, string lastPageText, string lastTwoText, string header, string subHeader, string footer, string footerSignatureRaw, List<Dictionary<string, object>> bookmarks, List<DigitalSignature> signatures, string docLabel, List<Dictionary<string, object>> words, bool debugSigner)
        {
            string originMain = ExtractOrigin(header, bookmarks, fullText, excludeGeneric: true);
            string originSub = !string.IsNullOrWhiteSpace(subHeader)
                ? subHeader
                : ExtractSubOrigin(header, bookmarks, fullText, originMain, excludeGeneric: true);
            string originExtra = ExtractExtraOrigin(header, bookmarks, fullText, originMain, originSub);
            var sei = ExtractSeiMetadata(fullText, lastTwoText, footer, docLabel);
            string signatureBlock = footerSignatureRaw ?? "";
            var signerSei = sei.Signer ?? "";
            var scanSources = BuildSignerSources(lastPageText, footer, signatureBlock);
            var signerFooter = ExtractSignerFromSources(scanSources);
            if (string.IsNullOrWhiteSpace(signerFooter))
                signerFooter = ExtractSignerFromSignatureBlock(signatureBlock);
            var signerDigital = ExtractSignerDigital(signatures);
            var signerFullText = ExtractSignerFromDocumentText(fullText);
            var allowLastline = Regex.IsMatch(docLabel ?? "", @"despacho|certidao|requerimento", RegexOptions.IgnoreCase);
            var signerLastLine = allowLastline ? ExtractSignerFromLastLine(lastPageText) : "";

            string signer = !string.IsNullOrWhiteSpace(signerDigital) ? signerDigital
                : !string.IsNullOrWhiteSpace(signerSei) ? signerSei
                : !string.IsNullOrWhiteSpace(signerFooter) ? signerFooter
                : !string.IsNullOrWhiteSpace(signerFullText) ? signerFullText
                : !string.IsNullOrWhiteSpace(signerLastLine) ? signerLastLine
                : "";
            string signedAt = sei.SignedAt ?? ExtractSignedAt(lastTwoText, footer ?? "", footerSignatureRaw ?? "");
            string dateFooter = ExtractDateFromFooter(lastTwoText, footer ?? "", header ?? "", footerSignatureRaw ?? "");
            string headerHash = HashText(header ?? "");
            string template = docLabel ?? ""; // não classificar; manter o nome do bookmark
            string title = ExtractTitle(header ?? "", bookmarks, fullText, originMain, originSub);
            var paras = BuildParagraphsFromWords(words);
            var orderedParas = paras.OrderBy(p => p.Page).ThenByDescending(p => p.Ny0).ToArray();
            var firstParaObj = orderedParas.FirstOrDefault();
            var lastParaObj = orderedParas.LastOrDefault();
            var firstPara = firstParaObj?.Text ?? "";
            var lastPara = lastParaObj?.Text ?? "";
            bool isDespachoLabel = Regex.IsMatch(docLabel ?? "", "despacho", RegexOptions.IgnoreCase);
            string docHead = "";
            string docTail = "";
            if (isDespachoLabel)
            {
                var headParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(header)) headParts.Add(header);
                if (!string.IsNullOrWhiteSpace(subHeader)) headParts.Add(subHeader);
                if (!string.IsNullOrWhiteSpace(firstPara)) headParts.Add(firstPara);
                docHead = string.Join("\n", headParts.Where(p => !string.IsNullOrWhiteSpace(p)));

                var tailParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(lastPara)) tailParts.Add(lastPara);
                if (!string.IsNullOrWhiteSpace(footer)) tailParts.Add(footer);
                if (!string.IsNullOrWhiteSpace(footerSignatureRaw)) tailParts.Add(footerSignatureRaw);
                docTail = string.Join("\n", tailParts.Where(p => !string.IsNullOrWhiteSpace(p)));
            }
            var party = ExtractPartyInfo(fullText);
            var partyBBoxes = ExtractPartyBBoxes(paras, d.StartPage, d.EndPage);
            var procInfo = ExtractProcessInfo(paras, sei.Process);
            var cnj = ExtractProcessCnj(fullText, procInfo.ProcessNumber, procInfo.ProcessLine);

            var meta = new Dictionary<string, object>
            {
                ["origin_main"] = originMain,
                ["origin_sub"] = originSub,
                ["origin_extra"] = originExtra,
                ["signer"] = signer,
                ["signed_at"] = signedAt,
                ["header_hash"] = headerHash,
                ["title"] = title,
                ["template"] = template,
                ["footer_signature_raw"] = footerSignatureRaw ?? "",
                ["sei_process"] = string.IsNullOrWhiteSpace(sei.Process) ? procInfo.ProcessNumber : sei.Process,
                ["process_cnj"] = cnj,
                ["sei_doc"] = sei.DocNumber ?? "",
                ["sei_crc"] = sei.CRC ?? "",
                ["sei_verifier"] = sei.Verifier ?? "",
                ["auth_url"] = sei.AuthUrl ?? "",
                ["date_footer"] = dateFooter,
                ["doc_head"] = docHead,
                ["doc_tail"] = docTail,
                ["process_line"] = procInfo.ProcessLine,
                ["process_bbox"] = procInfo.ProcessBBox ?? (object)JValue.CreateNull(),
                ["interested_line"] = party.InterestedLine,
                ["interested_name"] = party.InterestedName,
                ["interested_profession"] = party.InterestedProfession,
                ["interested_email"] = party.InterestedEmail,
                ["juizo_line"] = party.JuizoLine,
                ["juizo_vara"] = party.JuizoVara,
                ["comarca"] = party.Comarca,
                ["interested_bbox"] = partyBBoxes.InterestedBBox ?? (object)JValue.CreateNull(),
                ["juizo_bbox"] = partyBBoxes.JuizoBBox ?? (object)JValue.CreateNull()
            };

            if (debugSigner)
            {
                meta["signer_candidates"] = new Dictionary<string, object>
                {
                    ["sei"] = signerSei,
                    ["footer"] = signerFooter,
                    ["digital"] = signerDigital,
                    ["lastline"] = signerLastLine,
                    ["fulltext"] = signerFullText,
                    ["picked"] = signer
                };
            }

            return meta;
        }

        private void ApplyDespachoHeadTailText(Dictionary<string, object> meta, string header, string subHeader, string footer, string footerSignatureRaw, ParagraphObj[] paragraphs, int startPage, int endPage)
        {
            if (meta == null) return;

            var ordered = (paragraphs ?? Array.Empty<ParagraphObj>())
                .OrderBy(p => p.Page)
                .ThenByDescending(p => p.Ny0)
                .ToList();

            var firstPara = ordered.FirstOrDefault(p => p.Page == startPage) ?? ordered.FirstOrDefault();
            var lastPageParas = ordered.Where(p => p.Page == endPage).ToList();
            ParagraphObj? lastPara = null;
            if (endPage > startPage && lastPageParas.Count > 0)
            {
                lastPara = lastPageParas
                    .Where(p => !IsFooterParagraph(p.Text))
                    .OrderBy(p => p.Ny0)
                    .LastOrDefault() ?? lastPageParas.OrderBy(p => p.Ny0).LastOrDefault();
            }

            var headParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(header)) headParts.Add(header);
            if (!string.IsNullOrWhiteSpace(subHeader)) headParts.Add(subHeader);
            if (!string.IsNullOrWhiteSpace(firstPara?.Text)) headParts.Add(firstPara.Text);
            meta["doc_head"] = string.Join("\n", headParts.Where(p => !string.IsNullOrWhiteSpace(p)));

            if (endPage > startPage)
            {
                var tailParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(lastPara?.Text)) tailParts.Add(lastPara.Text);
                if (!string.IsNullOrWhiteSpace(footer)) tailParts.Add(footer);
                if (!string.IsNullOrWhiteSpace(footerSignatureRaw)) tailParts.Add(footerSignatureRaw);
                meta["doc_tail"] = string.Join("\n", tailParts.Where(p => !string.IsNullOrWhiteSpace(p)));
            }
            else
            {
                meta["doc_tail"] = "";
            }
        }

        private ParagraphSlice BuildBookmarkParagraphSlices(string header, string subHeader, string footer, ParagraphObj[] paragraphs, int startPage, int endPage)
        {
            var ordered = (paragraphs ?? Array.Empty<ParagraphObj>())
                .OrderBy(p => p.Page)
                .ThenByDescending(p => p.Ny0)
                .ToList();

            var firstPara = ordered.FirstOrDefault(p => p.Page == startPage) ?? ordered.FirstOrDefault();
            var lastPara = ordered.LastOrDefault(p => p.Page == endPage) ?? ordered.LastOrDefault();

            var firstText = firstPara?.Text ?? "";
            var lastText = lastPara?.Text ?? "";
            firstText = DespaceIfNeeded(firstText);
            lastText = DespaceIfNeeded(lastText);

            return new ParagraphSlice { First = firstText, Last = lastText };
        }

        private readonly struct ParagraphSlice
        {
            public string First { get; init; }
            public string Last { get; init; }
        }

        private void ApplyDespachoHeadTailBBoxes(Dictionary<string, object> meta, DespachoDocumentInfo? despachoMatch, List<Dictionary<string, object>> words, int startPage, int endPage)
        {
            if (meta == null || words == null || words.Count == 0)
                return;

            if (despachoMatch == null)
            {
                ApplyHeadTailFromWords(meta, words, startPage, endPage);
                return;
            }

            int start = despachoMatch.StartPage1 > 0 ? despachoMatch.StartPage1 : startPage;
            int end = despachoMatch.EndPage1 > 0 ? despachoMatch.EndPage1 : endPage;

            var headPara = PickHeadParagraphFromDespacho(despachoMatch, start);
            var head = ExtractHeadBBox(words, headPara);
            if (!string.IsNullOrWhiteSpace(head.Text))
                meta["doc_head_bbox_text"] = head.Text;
            if (head.BBox != null)
                meta["doc_head_bbox"] = head.BBox;

            if (end <= start)
                return;

            var tailPara = PickTailParagraphFromDespacho(despachoMatch, end);
            var tail = ExtractTailBBox(words, tailPara, end);
            if (!string.IsNullOrWhiteSpace(tail.Text))
                meta["doc_tail_bbox_text"] = tail.Text;
            if (tail.BBox != null)
                meta["doc_tail_bbox"] = tail.BBox;
        }

        private void EnsureAnchorTemplates(string anchorTemplatesDir)
        {
            if (string.IsNullOrWhiteSpace(anchorTemplatesDir))
                return;

            _anchorHeadTemplatePath = Path.Combine(anchorTemplatesDir, "tjpb_despacho_head_annotated.txt");
            _anchorTailTemplatePath = Path.Combine(anchorTemplatesDir, "tjpb_despacho_tail_annotated.txt");

            if (_anchorHeadPlan == null && File.Exists(_anchorHeadTemplatePath))
                _anchorHeadPlan = LoadAnchorPlan(_anchorHeadTemplatePath);

            if (_anchorTailPlan == null && File.Exists(_anchorTailTemplatePath))
                _anchorTailPlan = LoadAnchorPlan(_anchorTailTemplatePath);
        }

        private ExtractionPlan? LoadAnchorPlan(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return null;

                var annotated = File.ReadAllText(path);
                var parser = new TemplateAnnotatedParser();
                var typed = parser.BuildTypedTemplate(annotated);
                return new TemplateCompiler().Compile(typed, new CompileOptions());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[pipeline-tjpb] WARN anchor-template '{path}': {ex.Message}");
                return null;
            }
        }

        private void ApplyAnchorExtraction(Dictionary<string, object> docMeta)
        {
            if (docMeta == null) return;
            if (_anchorHeadPlan == null && _anchorTailPlan == null) return;

            if (_anchorHeadPlan != null &&
                docMeta.TryGetValue("doc_head_bbox_text", out var headObj) &&
                !string.IsNullOrWhiteSpace(headObj?.ToString()))
            {
                var headText = headObj?.ToString() ?? "";
                docMeta["despacho_head_anchor_fields"] = ExtractAnchorFields(_anchorHeadPlan, headText, "despacho_head");
                if (!string.IsNullOrWhiteSpace(_anchorHeadTemplatePath))
                    docMeta["despacho_head_template_path"] = _anchorHeadTemplatePath;
            }

            if (_anchorTailPlan != null &&
                docMeta.TryGetValue("doc_tail_bbox_text", out var tailObj) &&
                !string.IsNullOrWhiteSpace(tailObj?.ToString()))
            {
                var tailText = tailObj?.ToString() ?? "";
                docMeta["despacho_tail_anchor_fields"] = ExtractAnchorFields(_anchorTailPlan, tailText, "despacho_tail");
                if (!string.IsNullOrWhiteSpace(_anchorTailTemplatePath))
                    docMeta["despacho_tail_template_path"] = _anchorTailTemplatePath;
            }
        }

        private List<Dictionary<string, object>> ExtractAnchorFields(ExtractionPlan plan, string text, string section)
        {
            var engine = new AnchorExtractionEngine();
            var normalized = TextUtils.CollapseSpacedLettersText(text);
            if (string.IsNullOrWhiteSpace(normalized))
                normalized = text ?? "";

            var results = engine.Extract(plan, normalized);
            var list = new List<Dictionary<string, object>>(results.Count);
            foreach (var f in results)
            {
                var item = new Dictionary<string, object>
                {
                    ["section"] = section,
                    ["field_id"] = f.FieldId,
                    ["field_key"] = f.FieldKey,
                    ["occurrence"] = f.OccurrenceIndex,
                    ["type"] = f.Type.ToString(),
                    ["value"] = f.Value ?? (object)JValue.CreateNull(),
                    ["start"] = f.StartIndex,
                    ["end"] = f.EndIndex,
                    ["missing"] = f.Missing,
                    ["confidence"] = f.Confidence
                };
                if (!string.IsNullOrWhiteSpace(f.Notes))
                    item["notes"] = f.Notes;
                list.Add(item);
            }
            return list;
        }

        private void ApplyHeadTailFromWords(Dictionary<string, object> meta, List<Dictionary<string, object>> words, int startPage, int endPage)
        {
            var paras = BuildParagraphSegmentsFromWordDicts(words, startPage, endPage);
            var ordered = paras
                .OrderBy(p => p.Page1)
                .ThenByDescending(p => p.BBox?.Y1 ?? 0)
                .ToList();

            var headPara = ordered.FirstOrDefault(p => p.Page1 == startPage && !IsHeaderOrMetaParagraph(p.Text))
                           ?? ordered.FirstOrDefault(p => p.Page1 == startPage);

            var head = ExtractHeadBBox(words, headPara);
            if (!string.IsNullOrWhiteSpace(head.Text))
                meta["doc_head_bbox_text"] = head.Text;
            if (head.BBox != null)
                meta["doc_head_bbox"] = head.BBox;

            if (endPage <= startPage)
                return;

            var tailCandidates = ordered.Where(p => p.Page1 == endPage).ToList();
            if (tailCandidates.Count == 0)
                return;
            var scored = tailCandidates
                .Select(p => new { Para = p, Score = ScoreTailParagraph(p.Text) })
                .Where(x => x.Score > 0 && !IsFooterParagraph(x.Para.Text))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Para.Page1)
                .ThenByDescending(x => x.Para.BBox?.Y1 ?? 0)
                .ToList();

            var tailPara = scored.FirstOrDefault()?.Para
                           ?? tailCandidates.FirstOrDefault(p => !IsFooterParagraph(p.Text) && (p.Text ?? "").Length >= 60)
                           ?? tailCandidates.FirstOrDefault(p => !IsFooterParagraph(p.Text))
                           ?? tailCandidates.FirstOrDefault();

            var tail = ExtractTailBBox(words, tailPara, endPage);
            if (!string.IsNullOrWhiteSpace(tail.Text))
                meta["doc_tail_bbox_text"] = tail.Text;
            if (tail.BBox != null)
                meta["doc_tail_bbox"] = tail.BBox;
        }

        private List<ParagraphSegment> BuildParagraphSegmentsFromWordDicts(List<Dictionary<string, object>> words, int startPage, int endPage)
        {
            var result = new List<ParagraphSegment>();
            if (words == null || words.Count == 0) return result;

            var cfg = _tjpbCfg ?? new TjpbDespachoConfig();
            var lineMergeY = cfg.Thresholds?.Paragraph?.LineMergeY ?? 0.015;
            var wordGapX = cfg.Thresholds?.Paragraph?.WordGapX ?? cfg.TemplateRegions?.WordGapX ?? 0.012;
            var paragraphGapY = cfg.Thresholds?.Paragraph?.ParagraphGapY ?? 0.03;

            for (int p = startPage; p <= endPage; p++)
            {
                var pageWords = new List<WordInfo>();
                foreach (var w in words)
                {
                    if (!w.TryGetValue("page", out var pw) || pw == null) continue;
                    if (!int.TryParse(pw.ToString(), out var page) || page != p) continue;
                    var text = w.GetValueOrDefault("text")?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    pageWords.Add(new WordInfo
                    {
                        Text = text,
                        NormX0 = Convert.ToSingle(w.GetValueOrDefault("nx0") ?? 0f),
                        NormY0 = Convert.ToSingle(w.GetValueOrDefault("ny0") ?? 0f),
                        NormX1 = Convert.ToSingle(w.GetValueOrDefault("nx1") ?? 0f),
                        NormY1 = Convert.ToSingle(w.GetValueOrDefault("ny1") ?? 0f)
                    });
                }
                if (pageWords.Count == 0) continue;
                var lines = LineBuilder.BuildLines(pageWords, p, lineMergeY, wordGapX);
                var paras = ParagraphBuilder.BuildParagraphs(lines, paragraphGapY);
                result.AddRange(paras);
            }
            return result;
        }

        private ParagraphInfo? PickHeadParagraphFromDespacho(DespachoDocumentInfo despachoMatch, int startPage)
        {
            if (despachoMatch == null) return null;
            var paras = despachoMatch.Paragraphs
                .Where(p => p.BBoxN != null && p.Page1 == startPage)
                .OrderByDescending(p => p.BBoxN!.Y1)
                .ToList();

            if (paras.Count == 0)
            {
                paras = despachoMatch.Paragraphs
                    .Where(p => p.BBoxN != null)
                    .OrderByDescending(p => p.Page1)
                    .ThenByDescending(p => p.BBoxN!.Y1)
                    .ToList();
            }

            if (paras.Count == 0) return null;

            var preferred = paras.FirstOrDefault(p => IsPreferredHeadParagraph(p.Text));
            if (preferred != null) return preferred;

            var longPara = paras.FirstOrDefault(p => (p.Text ?? "").Length >= 120 && !IsHeaderOrMetaParagraph(p.Text));
            if (longPara != null) return longPara;

            var candidate = paras.FirstOrDefault(p => !IsHeaderOrMetaParagraph(p.Text));
            return candidate ?? paras.FirstOrDefault();
        }

        private ParagraphInfo? PickTailParagraphFromDespacho(DespachoDocumentInfo despachoMatch, int endPage)
        {
            if (despachoMatch == null) return null;
            var paras = despachoMatch.Paragraphs
                .Where(p => p.BBoxN != null && p.Page1 == endPage)
                .OrderByDescending(p => p.Page1)
                .ThenBy(p => p.BBoxN!.Y0)
                .ToList();

            if (paras.Count == 0) return null;

            var scored = paras
                .Select(p => new { Para = p, Score = ScoreTailParagraph(p.Text) })
                .Where(x => x.Score > 0 && !IsFooterParagraph(x.Para.Text))
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Para.Page1)
                .ThenByDescending(x => x.Para.BBoxN!.Y1)
                .ToList();

            if (scored.Count > 0)
                return scored.First().Para;

            var candidate = paras.FirstOrDefault(p => !IsFooterParagraph(p.Text) && (p.Text ?? "").Length >= 60);
            if (candidate != null) return candidate;

            candidate = paras.FirstOrDefault(p => !IsFooterParagraph(p.Text));
            return candidate ?? paras.FirstOrDefault();
        }

        private bool IsPreferredHeadParagraph(string? text)
        {
            var norm = NormalizeSimple(text ?? "");
            if (string.IsNullOrWhiteSpace(norm)) return false;
            return ContainsLoose(norm, "os presentes autos") ||
                   ContainsLoose(norm, "presentes autos") ||
                   ContainsLoose(norm, "versam sobre");
        }

        private bool IsHeaderOrMetaParagraph(string? text)
        {
            var norm = NormalizeSimple(text ?? "");
            if (string.IsNullOrWhiteSpace(norm)) return true;
            if (norm.Length < 40) return true;
            if (ContainsLoose(norm, "despacho")) return true;
            if (ContainsLoose(norm, "processo")) return true;
            if (ContainsLoose(norm, "requerente")) return true;
            if (ContainsLoose(norm, "interessado")) return true;
            if (ContainsLoose(norm, "poder judiciario")) return true;
            if (ContainsLoose(norm, "tribunal de justica")) return true;
            if (ContainsLoose(norm, "diretoria especial")) return true;
            return false;
        }

        private bool IsFooterParagraph(string? text)
        {
            var norm = NormalizeSimple(text ?? "");
            if (string.IsNullOrWhiteSpace(norm)) return true;
            if (ContainsLoose(norm, "documento assinado eletronicamente")) return true;
            if (ContainsLoose(norm, "assinado eletronicamente")) return true;
            if (ContainsLoose(norm, "diretor") && norm.Length < 120) return true;
            if (ContainsLoose(norm, "juiz") && norm.Length < 120) return true;
            if (ContainsLoose(norm, "diretoria especial") && norm.Length < 120) return true;
            if (ContainsLoose(norm, "joao pessoa") && ContainsLoose(norm, "pb")) return true;
            if (ContainsLoose(norm, "codigo verificador")) return true;
            if (ContainsLoose(norm, "crc")) return true;
            if (ContainsLoose(norm, "autenticidade deste documento")) return true;
            if (ContainsLoose(norm, "sei") && ContainsLoose(norm, "/ pg")) return true;
            return false;
        }

        private int ScoreTailParagraph(string? text)
        {
            var norm = NormalizeSimple(text ?? "");
            if (string.IsNullOrWhiteSpace(norm)) return 0;
            int score = 0;
            if (ContainsLoose(norm, "em razao do exposto")) score += 10;
            if (ContainsLoose(norm, "encaminhem-se")) score += 8;
            if (ContainsLoose(norm, "gerencia de programacao orcamentaria")) score += 6;
            if (ContainsLoose(norm, "georc")) score += 6;
            if (ContainsLoose(norm, "reserva orcamentaria")) score += 4;
            if (ContainsLoose(norm, "arbitrad")) score += 1;
            if (ContainsLoose(norm, "perito")) score += 1;
            return score;
        }

        private bool ContainsLoose(string norm, string phrase)
        {
            if (string.IsNullOrWhiteSpace(norm) || string.IsNullOrWhiteSpace(phrase)) return false;
            var p = phrase.ToLowerInvariant();
            if (norm.Contains(p)) return true;
            var tightNorm = norm.Replace(" ", "");
            var tightPhrase = p.Replace(" ", "");
            return tightNorm.Contains(tightPhrase);
        }

        private (string Text, Dictionary<string, object>? BBox) ExtractHeadBBox(List<Dictionary<string, object>> words, ParagraphInfo? headPara)
        {
            if (words == null || words.Count == 0 || headPara?.BBoxN == null) return ("", null);
            int page = headPara.Page1;
            double cutoff = headPara.BBoxN.Y0;
            var selected = words
                .Where(w => Convert.ToInt32(w["page"]) == page && Convert.ToDouble(w["ny0"]) >= cutoff)
                .ToList();
            return BuildTextAndBBox(selected, "doc_head_bbox");
        }

        private (string Text, Dictionary<string, object>? BBox) ExtractTailBBox(List<Dictionary<string, object>> words, ParagraphInfo? tailPara, int endPage)
        {
            if (words == null || words.Count == 0 || tailPara?.BBoxN == null) return ("", null);
            int page = tailPara.Page1;
            double cutoff = tailPara.BBoxN.Y1;
            int end = endPage > 0 ? endPage : page;
            var selected = words
                .Where(w =>
                {
                    int p = Convert.ToInt32(w["page"]);
                    if (p > end) return false;
                    if (p > page) return true;
                    if (p < page) return false;
                    return Convert.ToDouble(w["ny1"]) <= cutoff;
                })
                .ToList();
            return BuildTextAndBBox(selected, "doc_tail_bbox");
        }

        private (string Text, Dictionary<string, object>? BBox) ExtractHeadBBox(List<Dictionary<string, object>> words, ParagraphSegment? headPara)
        {
            if (words == null || words.Count == 0 || headPara?.BBox == null) return ("", null);
            int page = headPara.Page1;
            double cutoff = headPara.BBox.Y0;
            var selected = words
                .Where(w => Convert.ToInt32(w["page"]) == page && Convert.ToDouble(w["ny0"]) >= cutoff)
                .ToList();
            return BuildTextAndBBox(selected, "doc_head_bbox");
        }

        private (string Text, Dictionary<string, object>? BBox) ExtractTailBBox(List<Dictionary<string, object>> words, ParagraphSegment? tailPara, int endPage)
        {
            if (words == null || words.Count == 0 || tailPara?.BBox == null) return ("", null);
            int page = tailPara.Page1;
            double cutoff = tailPara.BBox.Y1;
            int end = endPage > 0 ? endPage : page;
            var selected = words
                .Where(w =>
                {
                    int p = Convert.ToInt32(w["page"]);
                    if (p > end) return false;
                    if (p > page) return true;
                    if (p < page) return false;
                    return Convert.ToDouble(w["ny1"]) <= cutoff;
                })
                .ToList();
            return BuildTextAndBBox(selected, "doc_tail_bbox");
        }
        private (string Text, Dictionary<string, object>? BBox) ExtractHeadBBox(List<Dictionary<string, object>> words, ParagraphObj? headPara)
        {
            if (words == null || words.Count == 0 || headPara == null) return ("", null);
            int page = headPara.Page;
            double cutoff = headPara.Ny0;
            var selected = words
                .Where(w => Convert.ToInt32(w["page"]) == page && Convert.ToDouble(w["ny0"]) >= cutoff)
                .ToList();
            return BuildTextAndBBox(selected, "doc_head_bbox");
        }

        private (string Text, Dictionary<string, object>? BBox) ExtractTailBBox(List<Dictionary<string, object>> words, ParagraphObj? tailPara, int endPage)
        {
            if (words == null || words.Count == 0 || tailPara == null) return ("", null);
            int page = tailPara.Page;
            double cutoff = tailPara.Ny1;
            int end = endPage > 0 ? endPage : page;
            var selected = words
                .Where(w =>
                {
                    int p = Convert.ToInt32(w["page"]);
                    if (p > end) return false;
                    if (p > page) return true;
                    if (p < page) return false;
                    return Convert.ToDouble(w["ny1"]) <= cutoff;
                })
                .ToList();
            return BuildTextAndBBox(selected, "doc_tail_bbox");
        }

        private (string Text, Dictionary<string, object>? BBox) BuildTextAndBBox(List<Dictionary<string, object>> words, string tag)
        {
            if (words == null || words.Count == 0) return ("", null);

            var lines = BuildLines(words)
                .OrderBy(l => l.Page)
                .ThenByDescending(l => l.NY0)
                .Select(l => l.Text.Trim())
                .Where(t => t.Length > 0)
                .ToList();

            var text = string.Join("\n", lines);

            double nx0 = words.Min(w => Convert.ToDouble(w["nx0"]));
            double ny0 = words.Min(w => Convert.ToDouble(w["ny0"]));
            double nx1 = words.Max(w => Convert.ToDouble(w["nx1"]));
            double ny1 = words.Max(w => Convert.ToDouble(w["ny1"]));
            var pages = words.Select(w => Convert.ToInt32(w["page"])).Distinct().OrderBy(p => p).ToList();

            var bbox = new Dictionary<string, object>
            {
                ["nx0"] = nx0,
                ["ny0"] = ny0,
                ["nx1"] = nx1,
                ["ny1"] = ny1,
                ["page_start"] = pages.First(),
                ["page_end"] = pages.Last(),
                ["tag"] = tag
            };

            return (text, bbox);
        }

        private string ExtractProcessCnj(string fullText, string processNumber, string processLine)
        {
            var m = CnjRegex.Match(fullText ?? "");
            if (m.Success) return m.Value;
            if (!string.IsNullOrWhiteSpace(processNumber) && CnjRegex.IsMatch(processNumber))
                return CnjRegex.Match(processNumber).Value;
            if (!string.IsNullOrWhiteSpace(processLine) && CnjRegex.IsMatch(processLine))
                return CnjRegex.Match(processLine).Value;
            return "";
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

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var m = Regex.Match(line, @"\b(ju[ií]zo|vara)\b", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    juizoLine = line;
                    var vm = Regex.Match(line, @"(ju[ií]zo\s+da\s+|ju[ií]zo\s+do\s+|vara\s+)([^,]+)", RegexOptions.IgnoreCase);
                    if (vm.Success) juizoVara = vm.Groups[2].Value.Trim();
                    var cm = Regex.Match(line, @"comarca\s+de\s+([^,\-]+)", RegexOptions.IgnoreCase);
                    if (cm.Success) comarca = cm.Groups[1].Value.Trim();
                    if (string.IsNullOrWhiteSpace(comarca) && Regex.IsMatch(line, @"comarca\s+de\s*$", RegexOptions.IgnoreCase))
                    {
                        var next = (i + 1) < lines.Count ? lines[i + 1].Trim() : "";
                        if (!string.IsNullOrWhiteSpace(next))
                            comarca = next;
                    }
                    if (!string.IsNullOrWhiteSpace(juizoVara) && Regex.IsMatch(juizoVara, @"comarca\s+de", RegexOptions.IgnoreCase))
                    {
                        var cut = Regex.Split(juizoVara, @"(?i)comarca\s+de").FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(cut))
                            juizoVara = cut.Trim().TrimEnd('-', ',', '.');
                    }
                    if (string.IsNullOrWhiteSpace(comarca) && !string.IsNullOrWhiteSpace(juizoVara) &&
                        Regex.IsMatch(juizoVara, @"comarca\s+de", RegexOptions.IgnoreCase))
                    {
                        var parts = Regex.Split(juizoVara, @"(?i)comarca\s+de");
                        if (parts.Length >= 2)
                        {
                            juizoVara = parts[0].Trim().TrimEnd('-', ',', '.');
                            comarca = parts[1].Trim().TrimEnd('-', ',', '.');
                        }
                    }
                    break;
                }
            }

            return (interestedLine, interestedName, interestedProf, interestedEmail, juizoLine, juizoVara, comarca);
        }

        private (Dictionary<string, double>? InterestedBBox, Dictionary<string, double>? JuizoBBox) ExtractPartyBBoxes(ParagraphObj[] paras, int startPage, int endPage)
        {
            Dictionary<string, double>? interested = null;
            Dictionary<string, double>? juizo = null;

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

        private (string ProcessLine, string ProcessNumber, Dictionary<string, double>? ProcessBBox) ExtractProcessInfo(ParagraphObj[] paras, string? fallbackProcess)
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
            public string? Process { get; set; }
            public string? DocNumber { get; set; }
            public string? CRC { get; set; }
            public string? Verifier { get; set; }
            public string? SignedAt { get; set; }
            public string? Signer { get; set; }
            public string? AuthUrl { get; set; }
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
                    var line = lastTwoText.Split('\n')
                        .Select(l => l.Trim())
                        .Reverse()
                        .FirstOrDefault(l => l.Contains("–") && Regex.IsMatch(l, @"assinad|assinatura|documento", RegexOptions.IgnoreCase));
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
        private Dictionary<string, object> BuildForensics(DocumentBoundary d, PDFAnalysisResult analysis, string docText, List<Dictionary<string, object>> words, ParagraphObj[]? paragraphs = null)
        {
            var result = new Dictionary<string, object>();

            // clusterizar linhas (y) por página
            var lineObjs = BuildLines(words);
            // parágrafos (forensics)
            var paragraphsLocal = paragraphs ?? BuildParagraphsFromWords(words);

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
            result["paragraphs"] = paragraphsLocal.Select(p => new
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
                    if (ordered.Count == 0) continue;

                    var charWidths = ordered
                        .Select(w =>
                        {
                            var t = w["text"]?.ToString() ?? "";
                            if (t.Length == 0) return 0.0;
                            return (Convert.ToDouble(w["x1"]) - Convert.ToDouble(w["x0"])) / t.Length;
                        })
                        .Where(v => v > 0)
                        .ToList();

                    double avgChar = Median(charWidths);
                    if (avgChar <= 0) avgChar = ordered.Average(w => Convert.ToDouble(w["x1"]) - Convert.ToDouble(w["x0"]));

                    double singleRatio = ordered.Count(w => (w["text"]?.ToString() ?? "").Trim().Length == 1) / (double)ordered.Count;
                    var gaps = new List<double>();
                    for (int i = 1; i < ordered.Count; i++)
                    {
                        double gap = Convert.ToDouble(ordered[i]["x0"]) - Convert.ToDouble(ordered[i - 1]["x1"]);
                        if (gap > 0) gaps.Add(gap);
                    }
                    double spaceThreshold = ComputeSpaceThreshold(gaps, avgChar, singleRatio > 0.5);

                    var sb = new System.Text.StringBuilder();
                    Dictionary<string, object>? prev = null;
                    foreach (var w in ordered)
                    {
                        if (prev != null)
                        {
                            double gap = Convert.ToDouble(w["x0"]) - Convert.ToDouble(prev["x1"]);
                            if (gap > spaceThreshold) sb.Append(' ');
                        }
                        sb.Append(w["text"]?.ToString() ?? "");
                        prev = w;
                    }

                    string text = TextUtils.FixMissingSpaces(sb.ToString().Trim());
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
                        .FirstOrDefault() ?? "";
        }

        private double Median(List<double> items)
        {
            if (items == null || items.Count == 0) return 0;
            items = items.OrderBy(x => x).ToList();
            int n = items.Count;
            if (n % 2 == 1) return items[n / 2];
            return (items[n / 2 - 1] + items[n / 2]) / 2.0;
        }

        private double ComputeSpaceThreshold(List<double> gaps, double avgChar, bool preferMergeLetters)
        {
            double fallback = avgChar * (preferMergeLetters ? 2.2 : 0.8);
            if (gaps == null || gaps.Count == 0) return fallback;

            gaps = gaps.OrderBy(x => x).ToList();
            double maxRatio = 0;
            int splitIndex = -1;
            for (int i = 1; i < gaps.Count; i++)
            {
                if (gaps[i - 1] <= 0) continue;
                double ratio = gaps[i] / gaps[i - 1];
                if (ratio > maxRatio)
                {
                    maxRatio = ratio;
                    splitIndex = i;
                }
            }

            if (maxRatio >= 1.6 && splitIndex > 0)
                return (gaps[splitIndex - 1] + gaps[splitIndex]) / 2.0;

            return fallback;
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
            public string Text { get; set; } = "";
            public string TextNorm { get; set; } = "";
            public double NX0 { get; set; }
            public double NX1 { get; set; }
            public double NY0 { get; set; }
            public double NY1 { get; set; }
            public string Font { get; set; } = "";
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

        private bool IsGenericNormalized(string text)
        {
            var norm = NormalizeHeaderKey(text);
            if (string.IsNullOrWhiteSpace(norm)) return false;
            string[] tokens =
            {
                "PODERJUDICIARIO",
                "TRIBUNALDEJUSTICA",
                "TRIBUNALDEJUSTICADAPARAIBA",
                "ESTADODAPARAIBA",
                "MINISTERIOPUBLICO",
                "DEFENSORIAPUBLICA",
                "PROCURADORIA",
                "GOVERNODOESTADO"
            };
            return tokens.Any(t => norm == t);
        }

        private bool IsCandidateTitle(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            var lower = line.ToLowerInvariant();
            string[] kws = { "despacho", "certidão", "certidao", "sentença", "sentenca", "decisão", "decisao", "ofício", "oficio", "laudo", "nota de empenho", "autorização", "autorizacao", "requisição", "requisicao" };
            return kws.Any(k => lower.Contains(k));
        }

        private bool IsCandidateTitleNormalized(string line)
        {
            var norm = NormalizeHeaderKey(line);
            if (string.IsNullOrWhiteSpace(norm)) return false;
            string[] kws =
            {
                "DESPACHO",
                "CERTIDAO",
                "SENTENCA",
                "DECISAO",
                "OFICIO",
                "LAUDO",
                "NOTADEEMPENHO",
                "AUTORIZACAO",
                "REQUISICAO"
            };
            return kws.Any(k => norm.Contains(k));
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

        private string ExtractFooterSignatureRawFromWords(List<Dictionary<string, object>> words, int page, double footerBandPct = 0.30, int tailLines = 8)
        {
            if (words == null || words.Count == 0) return "";
            var lines = BuildLines(words)
                .Where(l => l.Page == page && l.NY0 <= footerBandPct)
                .OrderByDescending(l => l.NY0) // top -> bottom
                .Select(l => NormalizeFooterLineText(l.Text))
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (lines.Count == 0) return "";

            var sigRegex = new Regex(@"(assinado|assinatura|assinado\s+eletronicamente\s+por:|documento\s+\d+\s+p[aá]gina\s+\d+\s+assinado|verificador|crc|c[oó]digo)", RegexOptions.IgnoreCase);
            int lastSigIdx = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (sigRegex.IsMatch(lines[i]))
                {
                    lastSigIdx = i;
                    break;
                }
            }

            if (lastSigIdx >= 0)
                return string.Join("\n", lines.Skip(lastSigIdx));

            int fallbackStart = Math.Max(0, lines.Count - Math.Max(4, tailLines));
            return string.Join("\n", lines.Skip(fallbackStart));
        }

        private string PickBestFooterSignatureRaw(string textA, string textB)
        {
            var a = (textA ?? "").Trim();
            var b = (textB ?? "").Trim();
            if (string.IsNullOrWhiteSpace(a)) return b;
            if (string.IsNullOrWhiteSpace(b)) return a;

            bool aSig = LooksLikeSignatureBlock(a);
            bool bSig = LooksLikeSignatureBlock(b);
            if (aSig && !bSig) return a;
            if (bSig && !aSig) return b;

            return b.Length > a.Length ? b : a;
        }

        private bool LooksLikeSignatureBlock(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return Regex.IsMatch(text, @"assinad|assinatura|verificador|crc|documento|c[oó]digo", RegexOptions.IgnoreCase);
        }

        private string NormalizeFooterLineText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var collapsedSingles = CollapseSingleLetterSpacings(text);
            var collapsed = TextUtils.CollapseSpacedLettersText(collapsedSingles);
            return TextUtils.FixMissingSpaces(collapsed).Trim();
        }

        private string CollapseSingleLetterSpacings(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            // Une letras isoladas separadas por espaços (ex.: "D o c u m e n t o" -> "Documento")
            return Regex.Replace(text, @"(?<=\b[\p{L}])\s+(?=[\p{L}]\b)", "");
        }

        private string RestoreNameSpaces(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            var t = name.Trim();
            // Reintroduzir separadores comuns (de/da/do/dos/das)
            t = Regex.Replace(t, @"(?i)(?<=\p{L})(de|da|do|dos|das)(?=\p{L})", " $1 ");
            // Separar por CamelCase quando existir
            t = Regex.Replace(t, @"(?<=[a-zà-ÿ])(?=[A-ZÁÀÂÃÉÊÍÓÚÇ])", " ");
            t = Regex.Replace(t, "\\s+", " ").Trim();
            return t;
        }

        private string ExtractSignerFromSignatureLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "";
            var matches = Regex.Matches(line, @"[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+(?:\s+(?:de|da|do|dos|das|e|d')\s+)?[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+(?:\s+[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+){0,4}");
            if (matches.Count == 0) return "";
            var candidate = matches[matches.Count - 1].Value.Trim();
            if (candidate.Length < 6) return "";
            if (!Regex.IsMatch(candidate, @"[A-ZÁÉÍÓÚÂÊÔÃÕÇ]")) return "";
            if (IsGenericSignerLabel(candidate)) return "";
            if (IsGeneric(candidate)) return "";
            if (candidate.Any(char.IsDigit)) return "";
            return candidate;
        }

        private bool IsGenericSignerLabel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var t = TextUtils.NormalizeWhitespace(text).ToLowerInvariant();
            if (t == "assinatura" || t == "assinado" || t == "documento") return true;
            if (t.StartsWith("assinatura ") || t.StartsWith("assinado ") || t.StartsWith("documento "))
                return true;
            return false;
        }

        private string ExtractSigner(string lastPageText, string footer, string footerSignatureRaw, List<DigitalSignature> signatures)
        {
            string signatureBlock = footerSignatureRaw ?? "";
            var scanSources = BuildSignerSources(lastPageText, footer, signatureBlock);
            var candidate = ExtractSignerFromSources(scanSources);
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;

            candidate = ExtractSignerFromSignatureBlock(signatureBlock);
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;

            candidate = ExtractSignerDigital(signatures);
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate;

            candidate = ExtractSignerFromLastLine(lastPageText);
            return candidate;
        }

        private List<string> BuildSignerSources(string lastPageText, string footer, string signatureBlock)
        {
            string[] sources =
            {
                $"{lastPageText}\n{footer}\n{signatureBlock}",
                ReverseText($"{lastPageText}\n{footer}\n{signatureBlock}")
            };

            var scanSources = new List<string>();
            foreach (var s in sources)
            {
                if (string.IsNullOrWhiteSpace(s)) continue;
                scanSources.Add(s);
                var collapsedSingles = CollapseSingleLetterSpacings(s);
                var collapsed = TextUtils.CollapseSpacedLettersText(collapsedSingles);
                if (!string.Equals(collapsed, s, StringComparison.Ordinal))
                    scanSources.Add(collapsed);
            }
            return scanSources.Distinct().ToList();
        }

        private string ExtractSignerFromSources(IEnumerable<string> scanSources)
        {
            if (scanSources == null) return "";
            foreach (var source in scanSources)
            {
                if (string.IsNullOrWhiteSpace(source)) continue;

                var docPageSigned = Regex.Match(source, @"documento\s+\d+[^\n]{0,120}?assinado[^\n]{0,60}?por\s*[:\-]?\s*([\p{L} .'’\-]+?)(?=\s*(?:,|\(|\bcpf\b|\bem\b|\n|$|-\s*\d))", RegexOptions.IgnoreCase);
                if (docPageSigned.Success) return docPageSigned.Groups[1].Value.Trim();

                var docSigned = Regex.Match(source, @"documento\s+assinado\s+eletronicamente\s+por\s*[:\-]?\s*([\p{L} .'’\-]+?)(?=\s*(?:,|\(|\bcpf\b|\bem\b|\n|$|-\s*\d))", RegexOptions.IgnoreCase);
                if (docSigned.Success) return docSigned.Groups[1].Value.Trim();

                var match = Regex.Match(source, @"assinado(?:\s+digitalmente|\s+eletronicamente)?\s+por\s*[:\-]?\s*([\p{L} .'’\-]+?)(?=\s*(?:,|\(|\bcpf\b|\bem\b|\n|$|-\s*\d))", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value.Trim();

                var sigMatch = Regex.Match(source, @"Assinatura(?:\s+)?:(.+)", RegexOptions.IgnoreCase);
                if (sigMatch.Success) return sigMatch.Groups[1].Value.Trim();

                var compact = Regex.Replace(source, @"\s+", "");
                var compactMatch = Regex.Match(compact,
                    @"assinado(?:digitalmente|eletronicamente)?por(?<name>[\p{L}]{5,80})(?=(?:,|em\d|tecnico|judiciario|$))",
                    RegexOptions.IgnoreCase);
                if (compactMatch.Success)
                    return RestoreNameSpaces(compactMatch.Groups["name"].Value);
            }
            return "";
        }

        private string ExtractSignerFromSignatureBlock(string signatureBlock)
        {
            if (string.IsNullOrWhiteSpace(signatureBlock)) return "";
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
            return "";
        }

        private string ExtractSignerDigital(List<DigitalSignature> signatures)
        {
            if (signatures == null || signatures.Count == 0) return "";
            var sigName = signatures.Select(s => s.SignerName).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (!string.IsNullOrWhiteSpace(sigName)) return sigName.Trim();
            var sigField = signatures.Select(s => s.Name).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            if (!string.IsNullOrWhiteSpace(sigField)) return sigField.Trim();
            return "";
        }

        private string ExtractSignerFromLastLine(string lastPageText)
        {
            if (string.IsNullOrWhiteSpace(lastPageText)) return "";
            var lines = lastPageText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var cargoKeywords = new[] { "diretor", "diretora", "presidente", "juiz", "juíza", "desembargador", "desembargadora", "secretário", "secretaria", "chefe", "coordenador", "coordenadora", "gerente", "perito", "analista", "assessor", "assessora", "procurador", "procuradora" };
            var namePattern = new Regex(@"^[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+(\s+[A-ZÁÉÍÓÚÂÊÔÃÕÇ][A-Za-zÁÉÍÓÚÂÊÔÃÕÇçãõâêîôûäëïöüàèìòùÿ'`\-]+){1,4}(\s*[,–-]\s*.+)?$", RegexOptions.Compiled);
            foreach (var line in lines.AsEnumerable().Reverse())
            {
                if (line.Length < 8 || line.Length > 120) continue;
                if (Regex.IsMatch(line, @"\d{2}[\\/]\d{2}[\\/]\d{2,4}")) continue;
                if (line.IndexOf("SEI", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (line.IndexOf("pg.", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("página", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                if (IsGeneric(line)) continue;
                if (line.ToLowerInvariant() == line) continue;
                if (line.Equals(lastPageText, StringComparison.OrdinalIgnoreCase)) continue;
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2) continue;
                var lower = line.ToLowerInvariant();
                if (cargoKeywords.Any(k => lower.Contains(k)) || namePattern.IsMatch(line))
                    return line;
            }
            return "";
        }

        private string ExtractSignerFromDocumentText(string fullText)
        {
            if (string.IsNullOrWhiteSpace(fullText)) return "";
            var sources = new List<string> { fullText };
            var collapsedSingles = CollapseSingleLetterSpacings(fullText);
            var collapsed = TextUtils.CollapseSpacedLettersText(collapsedSingles);
            if (!string.Equals(collapsed, fullText, StringComparison.Ordinal))
                sources.Add(collapsed);

            foreach (var source in sources)
            {
                var docPageSigned = Regex.Match(source, @"documento\s+\d+[^\n]{0,120}?assinado[^\n]{0,60}?por\s*[:\-]?\s*([\p{L} .'’\-]+?)(?=\s*(?:,|\(|\bcpf\b|\bem\b|\n|$|-\s*\d))", RegexOptions.IgnoreCase);
                if (docPageSigned.Success)
                {
                    var name = docPageSigned.Groups[1].Value.Trim();
                    if (!IsGenericSignerLabel(name)) return name;
                }

                var docSigned = Regex.Match(source, @"documento\s+assinado\s+eletronicamente\s+por\s*[:\-]?\s*([\p{L} .'’\-]+?)(?=\s*(?:,|\(|\bcpf\b|\bem\b|\n|$|-\s*\d))", RegexOptions.IgnoreCase);
                if (docSigned.Success)
                {
                    var name = docSigned.Groups[1].Value.Trim();
                    if (!IsGenericSignerLabel(name)) return name;
                }

                var match = Regex.Match(source, @"assinado(?:\s+digitalmente|\s+eletronicamente)?\s+por\s*[:\-]?\s*([\p{L} .'’\-]+?)(?=\s*(?:,|\(|\bcpf\b|\bem\b|\n|$|-\s*\d))", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var name = match.Groups[1].Value.Trim();
                    if (!IsGenericSignerLabel(name)) return name;
                }
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
            var scanSources = new List<string>();
            foreach (var src in preferredSources)
            {
                if (string.IsNullOrWhiteSpace(src)) continue;
                scanSources.Add(src);
                var collapsedSingles = CollapseSingleLetterSpacings(src);
                var collapsed = TextUtils.CollapseSpacedLettersText(collapsedSingles);
                if (!string.Equals(collapsed, src, StringComparison.Ordinal))
                    scanSources.Add(collapsed);
            }

            foreach (var src in scanSources.Distinct())
            {
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

        private List<Dictionary<string, object>> ExtractFields(string fullText, List<Dictionary<string, object>> words, DocumentBoundary d, string pdfPath, List<FieldScript> scripts, string? forcedBucket = null, string? nameOverride = null)
        {
            var name = !string.IsNullOrWhiteSpace(nameOverride) ? nameOverride : ExtractDocumentName(d);
            var bucket = forcedBucket ?? ClassifyBucket(name, fullText);
            var namePdf = name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? name : name + ".pdf";
            return FieldScripts.RunScripts(scripts, namePdf, fullText ?? string.Empty, words, d.StartPage, bucket);
        }

        private void AddDocMetaFallbacks(List<Dictionary<string, object>> fields, Dictionary<string, object> docMeta, int page, List<Dictionary<string, object>> words)
        {
            if (fields == null) return;
            string GetStr(string key)
            {
                return docMeta.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
            }

            AddFieldIfMissing(fields, "PROCESSO_ADMINISTRATIVO", GetStr("sei_process"), "doc_meta", 0.55, page, words);
            AddFieldIfMissing(fields, "PROCESSO_JUDICIAL", GetStr("process_cnj"), "doc_meta", 0.55, page, words);
            AddFieldIfMissing(fields, "VARA", GetStr("juizo_vara"), "doc_meta", 0.55, page, words);
            AddFieldIfMissing(fields, "COMARCA", GetStr("comarca"), "doc_meta", 0.55, page, words);
            AddFieldIfMissing(fields, "PERITO", GetStr("interested_name"), "doc_meta", 0.50, page, words);
            AddFieldIfMissing(fields, "ESPECIALIDADE", GetStr("interested_profession"), "doc_meta", 0.45, page, words);
            AddFieldIfMissing(fields, "DATA", GetStr("signed_at"), "doc_meta", 0.45, page, words);
            AddFieldIfMissing(fields, "ASSINANTE", GetStr("signer"), "doc_meta", 0.50, page, words);
        }

        private void RefineFieldsWithParagraphs(List<Dictionary<string, object>> fields, ParagraphObj[] paragraphs, Dictionary<string, object> docMeta)
        {
            if (fields == null) return;

            bool HasGood(string name)
            {
                return fields.Any(f =>
                {
                    var n = f.GetValueOrDefault("name")?.ToString() ?? "";
                    if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return false;
                    var v = f.GetValueOrDefault("value")?.ToString() ?? "";
                    return !IsNoisyFieldValue(name, v);
                });
            }

            void RemoveNoisy(string name)
            {
                fields.RemoveAll(f =>
                {
                    var n = f.GetValueOrDefault("name")?.ToString() ?? "";
                    if (!string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return false;
                    var v = f.GetValueOrDefault("value")?.ToString() ?? "";
                    return IsNoisyFieldValue(name, v);
                });
            }

            void AddOrReplace(string name, string value, int page, string method)
            {
                if (string.IsNullOrWhiteSpace(value)) return;
                if (HasGood(name)) return;
                RemoveNoisy(name);
                fields.Add(new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["value"] = value.Trim(),
                    ["method"] = method,
                    ["weight"] = 0.65,
                    ["page"] = page
                });
            }

            string GetMeta(string key) => docMeta.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";

            // Preferir dados do DTO (derivados de parágrafos/linhas) quando o valor atual é ruidoso
            AddOrReplace("VARA", GetMeta("juizo_vara"), 0, "doc_meta_refine");
            AddOrReplace("COMARCA", GetMeta("comarca"), 0, "doc_meta_refine");
            AddOrReplace("PERITO", GetMeta("interested_name"), 0, "doc_meta_refine");
            AddOrReplace("ESPECIALIDADE", GetMeta("interested_profession"), 0, "doc_meta_refine");

            // Refinos por parágrafos (quando ainda está faltando ou ruidoso)
            if (!HasGood("VARA"))
            {
                var hit = FindParagraphValue(paragraphs, new Regex(@"\b(ju[ií]zo|vara)\b", RegexOptions.IgnoreCase),
                    (m, t) => ExtractVaraFromText(t), 80);
                if (!string.IsNullOrWhiteSpace(hit.value))
                    AddOrReplace("VARA", NormalizeVara(hit.value), hit.page, "paragraph_refine");
            }

            if (!HasGood("COMARCA"))
            {
                var hit = FindParagraphValue(paragraphs, new Regex(@"comarca\s+de\s+([^\n,;]{3,80})", RegexOptions.IgnoreCase),
                    (m, _) => m.Groups[1].Value, 60);
                if (!string.IsNullOrWhiteSpace(hit.value))
                    AddOrReplace("COMARCA", NormalizeComarca(hit.value), hit.page, "paragraph_refine");
            }

            if (!HasGood("PERITO") || !HasGood("ESPECIALIDADE"))
            {
                var hit = FindParagraphValue(paragraphs, new Regex(@"interessad[oa]\s*:\s*([^\n]{5,120})", RegexOptions.IgnoreCase),
                    (m, _) => m.Groups[1].Value, 120);
                if (!string.IsNullOrWhiteSpace(hit.value))
                {
                    var parts = Regex.Split(hit.value, @"\s*[–-]\s*");
                    if (!HasGood("PERITO") && parts.Length > 0)
                        AddOrReplace("PERITO", NormalizeName(parts[0].Trim()), hit.page, "paragraph_refine");
                    if (!HasGood("ESPECIALIDADE") && parts.Length > 1)
                        AddOrReplace("ESPECIALIDADE", NormalizeShortSpecialty(parts[1].Trim()), hit.page, "paragraph_refine");
                }
            }

            if (!HasGood("ESPECIALIDADE"))
            {
                var hit = FindParagraphValue(paragraphs, new Regex(@"(?:especialidade|profiss[aã]o)\s*[:\-]\s*([^\n]{3,80})", RegexOptions.IgnoreCase),
                    (m, _) => m.Groups[1].Value, 80);
                if (!string.IsNullOrWhiteSpace(hit.value))
                    AddOrReplace("ESPECIALIDADE", NormalizeShortSpecialty(hit.value), hit.page, "paragraph_refine");
            }
        }

        private (string value, int page) FindParagraphValue(ParagraphObj[] paragraphs, Regex pattern, Func<Match, string, string> extract, int maxLen)
        {
            if (paragraphs == null || paragraphs.Length == 0) return ("", 0);
            foreach (var p in paragraphs)
            {
                var text = p.Text ?? "";
                if (string.IsNullOrWhiteSpace(text)) continue;
                var m = pattern.Match(text);
                if (!m.Success) continue;
                var val = extract(m, text) ?? "";
                val = Regex.Replace(val, @"\s+", " ").Trim();
                if (string.IsNullOrWhiteSpace(val)) continue;
                if (maxLen > 0 && val.Length > maxLen) val = val.Substring(0, maxLen);
                return (val, p.Page);
            }
            return ("", 0);
        }

        private string ExtractAfterLabel(string labelValue, string fullText, string labelsPattern)
        {
            var text = fullText ?? "";
            var m = Regex.Match(text, $@"\b(?:{labelsPattern})\b\s*(?:da|do|de)?\s*[:\-]?\s*([^\n,;]{{3,120}})", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
            return labelValue;
        }

        private string ExtractVaraFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var m = Regex.Match(text, @"\b(ju[ií]zo|vara)\b[\s\S]{0,120}", RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            var tail = text.Substring(m.Index);
            tail = Regex.Replace(tail, @"(?i)^(ju[ií]zo|vara)\s*(da|do|de)?\s*", "");
            tail = Regex.Split(tail, @"(?i)\b(comarca|processo|assunto|refer[êe]ncia|interessad[oa])\b").FirstOrDefault() ?? tail;
            return tail.Trim().TrimEnd('.', ',', ';', '-');
        }

        private string NormalizeVara(string val)
        {
            var v = Regex.Replace(val ?? "", @"(?i)\bju[ií]zo\s+da\s+|\bju[ií]zo\s+do\s+|\bvara\s+", "");
            v = Regex.Split(v, @"(?i)\b(comarca|processo|assunto|refer[êe]ncia|interessad[oa])\b").FirstOrDefault() ?? v;
            v = v.Trim().TrimEnd('.', ',', ';', '-');
            return NormalizeTitle(v);
        }

        private bool IsNoisyFieldValue(string field, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            var v = value.Trim();
            var f = (field ?? "").Trim().ToUpperInvariant();
            var words = v.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

            if (v.Length > 140) return true;
            if (words > 12 && (f == "VARA" || f == "COMARCA" || f == "ESPECIALIDADE" || f == "PERITO")) return true;
            if (Regex.IsMatch(v, @"R\$", RegexOptions.IgnoreCase) && f != "VALOR_ARBITRADO_JZ" && f != "VALOR_ARBITRADO_DE" && f != "VALOR_ARBITRADO_CM")
                return true;
            if (Regex.IsMatch(v, @"documento\s+assinado|verificador|crc|autentica|sei", RegexOptions.IgnoreCase))
                return true;
            if (Regex.IsMatch(v, @"\d{2}/\d{2}/\d{4}"))
                return true;
            if ((f == "VARA" || f == "COMARCA") && Regex.IsMatch(v, @"interessad[oa]|promovente|promovido", RegexOptions.IgnoreCase))
                return true;
            if (f == "VARA" && Regex.IsMatch(v, @"^\s*(ju[ií]zo|vara)\s*$", RegexOptions.IgnoreCase))
                return true;
            if (f == "ESPECIALIDADE" && Regex.IsMatch(v, @"pagamento|honor[aá]ri|requis", RegexOptions.IgnoreCase))
                return true;
            if (f == "PERITO" && Regex.IsMatch(v, @"trata|requis|pagamento|honor[aá]ri", RegexOptions.IgnoreCase))
                return true;
            return false;
        }

        private void AddFieldIfMissing(List<Dictionary<string, object>> fields, string name, string value, string method, double weight, int page, List<Dictionary<string, object>> words)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(value)) return;
            var bbox = FindBBoxForBand(words, value);
            if (bbox != null) page = bbox.Value.page;
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
                if (!existing.ContainsKey("bbox"))
                {
                    existing["bbox"] = bbox != null ? new Dictionary<string, double>
                    {
                        ["nx0"] = bbox.Value.nx0,
                        ["ny0"] = bbox.Value.ny0,
                        ["nx1"] = bbox.Value.nx1,
                        ["ny1"] = bbox.Value.ny1
                    } : JValue.CreateNull();
                }
                return;
            }

            var item = new Dictionary<string, object>
            {
                ["name"] = name,
                ["value"] = value.Trim(),
                ["method"] = method,
                ["weight"] = weight,
                ["page"] = page
            };
            item["bbox"] = bbox != null ? new Dictionary<string, double>
            {
                ["nx0"] = bbox.Value.nx0,
                ["ny0"] = bbox.Value.ny0,
                ["nx1"] = bbox.Value.nx1,
                ["ny1"] = bbox.Value.ny1
            } : JValue.CreateNull();
            fields.Add(item);
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
            int secondPage = d.StartPage + 1;
            string page1Text = SafePageText(analysis, firstPage);
            string lastPageText = SafePageText(analysis, lastPage);

            bool isDespacho = !string.IsNullOrWhiteSpace(docType) &&
                              docType.Contains("despacho", StringComparison.OrdinalIgnoreCase);
            bool isCertidao = !string.IsNullOrWhiteSpace(docType) &&
                              (docType.Contains("certidao_cm", StringComparison.OrdinalIgnoreCase) ||
                               docType.Contains("certidao", StringComparison.OrdinalIgnoreCase) ||
                               docType.Contains("certidão", StringComparison.OrdinalIgnoreCase));
            bool isRequerimento = !string.IsNullOrWhiteSpace(docType) &&
                                  (docType.Contains("requerimento", StringComparison.OrdinalIgnoreCase) ||
                                   docType.Contains("pagamento de honor", StringComparison.OrdinalIgnoreCase) ||
                                   docType.Contains("reserva orçamentária", StringComparison.OrdinalIgnoreCase));

            if (isRequerimento || (!isDespacho && !isCertidao))
            {
                var mJz = Regex.Match(page1Text, @"honor[aá]rios[^\n]{0,120}?R\$\s*([0-9\.]{1,3}(?:\.[0-9]{3})*,\d{2})", RegexOptions.IgnoreCase);
                if (mJz.Success)
                {
                    AddValueHit(hits, "VALOR_ARBITRADO_JZ", mJz.Groups[1].Value, firstPage, "direct_jz_page1");
                }
                else
                {
                    var mValor = Regex.Match(page1Text, @"valor\s+arbitrad[oa][^\n]{0,40}?(?:R\$\s*)?([0-9\.]{1,3}(?:\.[0-9]{3})*,\d{2})", RegexOptions.IgnoreCase);
                    if (!mValor.Success)
                        mValor = Regex.Match(fullText ?? "", @"valor\s+arbitrad[oa][^\n]{0,40}?(?:R\$\s*)?([0-9\.]{1,3}(?:\.[0-9]{3})*,\d{2})", RegexOptions.IgnoreCase);
                    if (mValor.Success)
                        AddValueHit(hits, "VALOR_ARBITRADO_JZ", mValor.Groups[1].Value, firstPage, "direct_jz_valor_arbitrado");
                    else
                    {
                        var mImporte = Regex.Match(page1Text, @"(?:importe|import[eê]ncia)[^\n]{0,40}?R\$\s*([0-9\.]{1,3}(?:\.[0-9]{3})*,\d{2})", RegexOptions.IgnoreCase);
                        if (mImporte.Success)
                            AddValueHit(hits, "VALOR_ARBITRADO_JZ", mImporte.Groups[1].Value, firstPage, "direct_jz_importe");
                    }
                }
            }

            if (isDespacho)
            {
                if (secondPage <= d.EndPage)
                {
                    var page2Text = SafePageText(analysis, secondPage);
                    var mDe = Regex.Match(page2Text, @"(?:(?:autorizo a despesa)|(?:reserva or[cç]ament[áa]ria)|(?:encaminh?em-se[^\n]{0,80}?GEORC)|(?:proceder à reserva or[cç]ament[áa]ria))[^\n]{0,200}?R\$\s*([0-9\.]{1,3}(?:\.[0-9]{3})*,\d{2})", RegexOptions.IgnoreCase);
                    if (mDe.Success)
                        AddValueHit(hits, "VALOR_ARBITRADO_DE", mDe.Groups[1].Value, secondPage, "direct_de_page2");
                }
            }

            if (isCertidao)
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

            if (!string.IsNullOrWhiteSpace(specialtyCandidate) && !IsNoisyFieldValue("ESPECIALIDADE", specialtyCandidate))
            {
                cleaned = cleaned.Where(x => !string.Equals(x.GetValueOrDefault("name")?.ToString(), "ESPECIALIDADE", StringComparison.OrdinalIgnoreCase)).ToList();
                cleaned.Add(new Dictionary<string, object>
                {
                    ["name"] = "ESPECIALIDADE",
                    ["value"] = specialtyCandidate,
                    ["page"] = 0,
                    ["pattern"] = "profissao_map",
                    ["method"] = "profissao_map",
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

            string? perito = cleaned.FirstOrDefault(f => string.Equals(f.GetValueOrDefault("name")?.ToString(), "PERITO", StringComparison.OrdinalIgnoreCase))?.GetValueOrDefault("value")?.ToString();
            string? cpf = cleaned.FirstOrDefault(f => string.Equals(f.GetValueOrDefault("name")?.ToString(), "CPF_PERITO", StringComparison.OrdinalIgnoreCase))?.GetValueOrDefault("value")?.ToString();
            string? especialidade = cleaned.FirstOrDefault(f => string.Equals(f.GetValueOrDefault("name")?.ToString(), "ESPECIALIDADE", StringComparison.OrdinalIgnoreCase))?.GetValueOrDefault("value")?.ToString();
            string? profissao = profissaoVal;

            // Fallback de espécie por referência (quando não houver match de honorários)
            var especieAtual = cleaned.FirstOrDefault(f =>
                    string.Equals(f.GetValueOrDefault("name")?.ToString(), "ESPECIE_DA_PERICIA", StringComparison.OrdinalIgnoreCase))
                ?.GetValueOrDefault("value")?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(especieAtual) && _laudosEspecieByEspecialidade != null && _laudosEspecieByEspecialidade.Count > 0)
            {
                var espKey = "";
                if (!string.IsNullOrWhiteSpace(especialidade))
                    espKey = NormalizeKey(especialidade);
                if (string.IsNullOrWhiteSpace(espKey) && !string.IsNullOrWhiteSpace(profissaoVal))
                    espKey = NormalizeKey(profissaoVal);
                if (string.IsNullOrWhiteSpace(espKey) && !string.IsNullOrWhiteSpace(specialtyCandidate))
                    espKey = NormalizeKey(specialtyCandidate);

                if (!string.IsNullOrWhiteSpace(espKey) && _laudosEspecieByEspecialidade.TryGetValue(espKey, out var especieRef))
                {
                    cleaned.Add(new Dictionary<string, object>
                    {
                        ["name"] = "ESPECIE_DA_PERICIA",
                        ["value"] = especieRef,
                        ["page"] = 0,
                        ["pattern"] = "laudos_especie_ref",
                        ["weight"] = 0.55
                    });
                }
            }

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
                string? promA = cleaned.FirstOrDefault(f => string.Equals(f.GetValueOrDefault("name")?.ToString(), "PROMOVENTE", StringComparison.OrdinalIgnoreCase))?.GetValueOrDefault("value")?.ToString();
                string? promB = cleaned.FirstOrDefault(f => string.Equals(f.GetValueOrDefault("name")?.ToString(), "PROMOVIDO", StringComparison.OrdinalIgnoreCase))?.GetValueOrDefault("value")?.ToString();
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

            foreach (var f in dedup)
            {
                if (!f.ContainsKey("bbox"))
                    f["bbox"] = JValue.CreateNull();
            }

            return dedup;
        }

        private static readonly string[] RequiredCommonFields = new[]
        {
            "PROCESSO_ADMINISTRATIVO",
            "PROCESSO_JUDICIAL",
            "PERITO",
            "CPF_PERITO",
            "ESPECIALIDADE",
            "ESPECIE_DA_PERICIA",
            "COMARCA",
            "VARA"
        };

        private static readonly string[] RequiredDespachoFields = new[]
        {
            "VALOR_ARBITRADO_DE",
            "VALOR_ARBITRADO_JZ"
        };

        private static readonly string[] RequiredCertidaoFields = new[]
        {
            "VALOR_ARBITRADO_CM"
        };

        private static readonly string[] RequiredRequerimentoFields = new[]
        {
            "VALOR_ARBITRADO_JZ"
        };

        private List<string> EnsureRequiredFields(List<Dictionary<string, object>> fields, bool isDespacho, bool isCertidao, bool isRequerimento)
        {
            var missing = new List<string>();
            var existing = new HashSet<string>(fields.Select(f => f.GetValueOrDefault("name")?.ToString() ?? ""), StringComparer.OrdinalIgnoreCase);

            var required = new List<string>();

            // Campos comuns: aparecem tipicamente em despacho/requerimento (certidão costuma trazer só o CM).
            if (isDespacho || isRequerimento)
                required.AddRange(RequiredCommonFields);

            if (isDespacho) required.AddRange(RequiredDespachoFields);
            if (isCertidao) required.AddRange(RequiredCertidaoFields);
            if (isRequerimento) required.AddRange(RequiredRequerimentoFields);

            // fallback: se nenhum tipo foi identificado, mantém conjunto comum
            if (!isDespacho && !isCertidao && !isRequerimento)
                required.AddRange(RequiredCommonFields);

            foreach (var name in required.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (existing.Contains(name)) continue;
                missing.Add(name);
                fields.Add(new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["value"] = "",
                    ["method"] = "missing_required",
                    ["weight"] = 0.0,
                    ["page"] = 0,
                    ["bbox"] = JValue.CreateNull()
                });
            }

            return missing;
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
                "DATA DO REQUERIMENTO" => "DATA_REQUISICAO",
                "DATA DO REQUERIMENTO DE PAGAMENTO" => "DATA_REQUISICAO",
                "DATA DO REQUERIMENTO DE PAGAMENTO DE HONORARIOS" => "DATA_REQUISICAO",
                "DATA DA REQUISICAO" => "DATA_REQUISICAO",
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
                case "DATA_REQUISICAO":
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
                "CPF_PERITO" => Regex.Replace(value ?? "", @"\D", "").Length == 11,
                "PERITO" => value.Length >= 5,
                "PROFISSAO" or "ESPECIALIDADE" or "ESPECIE_DA_PERICIA" or "PROMOVENTE" or "PROMOVIDO" or "COMARCA" or "VARA"
                    => value.Length >= 3,
                "PROCESSO_JUDICIAL"
                    => Regex.IsMatch(value, @"^\d{7}-\d{2}\.\d{4}\.\d\.\d{2}\.\d{4}$"),
                "DATA" or "DATA_REQUISICAO"
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
            val = Regex.Split(val, @"(?i)\b(assunto|processo|referência|referencia|parte|interessad[oa])\b").FirstOrDefault() ?? val;
            val = Regex.Replace(val, @"\b([A-Za-zÀ-ÿ])\s+(?=[A-Za-zÀ-ÿ]\b)", "$1");
            val = val.Trim().TrimEnd('.', ',', ';', '-');
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
                if (f == "VARA")
                    val = Regex.Replace(val, @"\s+\b(da|do|de)$", "", RegexOptions.IgnoreCase).Trim();
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

        private List<Dictionary<string, object>> FilterRequerimentoFields(List<Dictionary<string, object>> fields)
        {
            if (fields == null || fields.Count == 0) return fields ?? new List<Dictionary<string, object>>();
            var strictFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PROCESSO_JUDICIAL",
                "PERITO",
                "CPF_PERITO",
                "ESPECIALIDADE",
                "ESPECIE_DA_PERICIA",
                "COMARCA",
                "VARA"
            };
            var allowDocMeta = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "PROCESSO_ADMINISTRATIVO",
                "DATA",
                "ASSINANTE"
            };

            var filtered = new List<Dictionary<string, object>>();
            foreach (var f in fields)
            {
                var name = f.GetValueOrDefault("name")?.ToString() ?? "";
                var method = f.GetValueOrDefault("method")?.ToString() ?? "";
                if (strictFields.Contains(name))
                {
                    if (string.Equals(method, "requerimento_extractor", StringComparison.OrdinalIgnoreCase))
                    {
                        filtered.Add(f);
                        continue;
                    }
                    if (allowDocMeta.Contains(name) && string.Equals(method, "doc_meta", StringComparison.OrdinalIgnoreCase))
                    {
                        filtered.Add(f);
                        continue;
                    }
                    continue;
                }
                filtered.Add(f);
            }
            return filtered;
        }

        private List<Dictionary<string, object>> FilterDespachoValorPages(List<Dictionary<string, object>> fields, int startPage, int endPage)
        {
            if (fields == null || fields.Count == 0) return fields ?? new List<Dictionary<string, object>>();
            var page1 = startPage;
            var page2 = startPage + 1;
            var filtered = new List<Dictionary<string, object>>();

            foreach (var f in fields)
            {
                var name = f.GetValueOrDefault("name")?.ToString() ?? "";
                if (string.Equals(name, "VALOR_ARBITRADO_JZ", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryGetPage(f, out var pg) && pg == page1)
                        filtered.Add(f);
                    continue;
                }
                if (string.Equals(name, "VALOR_ARBITRADO_DE", StringComparison.OrdinalIgnoreCase))
                {
                    if (page2 <= endPage && TryGetPage(f, out var pg) && pg == page2)
                        filtered.Add(f);
                    continue;
                }
                filtered.Add(f);
            }

            return filtered;
        }

        private bool TryGetPage(Dictionary<string, object> field, out int page)
        {
            page = 0;
            if (field == null) return false;
            if (!field.TryGetValue("page", out var p) || p == null) return false;
            if (p is int pi)
            {
                page = pi;
                return true;
            }
            if (int.TryParse(p.ToString(), out var parsed))
            {
                page = parsed;
                return true;
            }
            return false;
        }

        private string BuildNormalizedTextFromWords(List<Dictionary<string, object>> words, int startPage, int endPage)
        {
            if (words == null || words.Count == 0) return "";
            var cfg = _tjpbCfg ?? new TjpbDespachoConfig();
            var lineMergeY = cfg.Thresholds?.Paragraph?.LineMergeY ?? 0.015;
            var wordGapX = cfg.Thresholds?.Paragraph?.WordGapX ?? cfg.TemplateRegions?.WordGapX ?? 0.012;

            var sb = new StringBuilder();
            for (int p = startPage; p <= endPage; p++)
            {
                var pageWords = new List<WordInfo>();
                foreach (var w in words)
                {
                    if (!w.TryGetValue("page", out var pw) || pw == null) continue;
                    if (!int.TryParse(pw.ToString(), out var page) || page != p) continue;
                    var text = w.GetValueOrDefault("text")?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    pageWords.Add(new WordInfo
                    {
                        Text = text,
                        NormX0 = Convert.ToSingle(w.GetValueOrDefault("nx0") ?? 0f),
                        NormY0 = Convert.ToSingle(w.GetValueOrDefault("ny0") ?? 0f),
                        NormX1 = Convert.ToSingle(w.GetValueOrDefault("nx1") ?? 0f),
                        NormY1 = Convert.ToSingle(w.GetValueOrDefault("ny1") ?? 0f)
                    });
                }
                if (pageWords.Count == 0) continue;
                var pageText = TextUtils.BuildTextFromWords(pageWords, lineMergeY, wordGapX);
                if (string.IsNullOrWhiteSpace(pageText)) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(pageText);
            }
            return sb.ToString().Trim();
        }

        private bool TryExtractVaraComarcaFromLine(string line, out string vara, out string comarca)
        {
            vara = "";
            comarca = "";
            if (string.IsNullOrWhiteSpace(line)) return false;
            var l = Regex.Replace(line, @"\s+", " ").Trim();
            l = Regex.Split(l, @"(?i)\brequerimento\b|\bsei\b|/\\s*pg").FirstOrDefault()?.Trim() ?? l;

            var m = Regex.Match(l, @"(?i)ju[ií]zo\s+da\s+([^,\n]{3,80}?vara[^,\n]{0,40})\s+de\s+([A-Za-zÀ-ÿ\s\-]{3,80})");
            if (m.Success)
            {
                vara = m.Groups[1].Value.Trim();
                comarca = m.Groups[2].Value.Trim();
            }
            else
            {
                var m2 = Regex.Match(l, @"(?i)([0-9ªºA-Za-zÀ-ÿ\s]{3,80}?vara[^,\n]{0,40})\s+de\s+([A-Za-zÀ-ÿ\s\-]{3,80})");
                if (m2.Success)
                {
                    vara = m2.Groups[1].Value.Trim();
                    comarca = m2.Groups[2].Value.Trim();
                }
            }

            if (!string.IsNullOrWhiteSpace(vara))
                vara = Regex.Replace(vara, @"\s*-\s*[A-Z]{2}$", "").Trim().TrimEnd('-', ',', '.');
            if (!string.IsNullOrWhiteSpace(comarca))
                comarca = Regex.Replace(comarca, @"\s*-\s*[A-Z]{2}$", "").Trim().TrimEnd('-', ',', '.');

            return !string.IsNullOrWhiteSpace(vara) || !string.IsNullOrWhiteSpace(comarca);
        }

        private string TrimReqNome(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value ?? "";
            var trimmed = Regex.Split(value, @"(?i)\b1\.2\.2\b|\b1\.2\.3\b|\bendere[cç]o\b|\bcpf\b").FirstOrDefault() ?? value;
            return trimmed.Trim().TrimEnd('-', ':', ';', ',', '.');
        }

        private string ExtractCpfCandidate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value ?? "";
            var m = Regex.Match(value, @"\d{3}\.\d{3}\.\d{3}-\d{2}");
            if (m.Success) return m.Value;
            var digits = Regex.Replace(value, @"\D", "");
            if (digits.Length >= 11) return digits.Substring(0, 11);
            return value;
        }

        private List<string> BuildLinesFromWordDicts(List<Dictionary<string, object>> words, int startPage, int endPage)
        {
            var linesOut = new List<string>();
            if (words == null || words.Count == 0) return linesOut;
            var cfg = _tjpbCfg ?? new TjpbDespachoConfig();
            var lineMergeY = cfg.Thresholds?.Paragraph?.LineMergeY ?? 0.015;
            var wordGapX = cfg.Thresholds?.Paragraph?.WordGapX ?? cfg.TemplateRegions?.WordGapX ?? 0.012;

            for (int p = startPage; p <= endPage; p++)
            {
                var pageWords = new List<WordInfo>();
                foreach (var w in words)
                {
                    if (!w.TryGetValue("page", out var pw) || pw == null) continue;
                    if (!int.TryParse(pw.ToString(), out var page) || page != p) continue;
                    var text = w.GetValueOrDefault("text")?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    pageWords.Add(new WordInfo
                    {
                        Text = text,
                        NormX0 = Convert.ToSingle(w.GetValueOrDefault("nx0") ?? 0f),
                        NormY0 = Convert.ToSingle(w.GetValueOrDefault("ny0") ?? 0f),
                        NormX1 = Convert.ToSingle(w.GetValueOrDefault("nx1") ?? 0f),
                        NormY1 = Convert.ToSingle(w.GetValueOrDefault("ny1") ?? 0f)
                    });
                }
                if (pageWords.Count == 0) continue;
                var ordered = TextUtils.DeduplicateWords(pageWords)
                    .OrderByDescending(w => (w.NormY0 + w.NormY1) / 2.0)
                    .ThenBy(w => w.NormX0)
                    .ToList();

                var current = new List<WordInfo>();
                double prevCy = double.MaxValue;
                foreach (var w in ordered)
                {
                    var cy = (w.NormY0 + w.NormY1) / 2.0;
                    if (current.Count == 0 || Math.Abs(cy - prevCy) <= lineMergeY)
                    {
                        current.Add(w);
                    }
                    else
                    {
                        var line = TextUtils.BuildLineText(current, wordGapX);
                        if (!string.IsNullOrWhiteSpace(line)) linesOut.Add(line);
                        current = new List<WordInfo> { w };
                    }
                    prevCy = cy;
                }
                if (current.Count > 0)
                {
                    var line = TextUtils.BuildLineText(current, wordGapX);
                    if (!string.IsNullOrWhiteSpace(line)) linesOut.Add(line);
                }
            }
            return linesOut;
        }

        private List<Dictionary<string, object>> ExtractRequerimentoFields(string fullText, List<Dictionary<string, object>> words, int startPage, int endPage)
        {
            var hits = new List<Dictionary<string, object>>();
            if (string.IsNullOrWhiteSpace(fullText)) return hits;

            var text = fullText;
            var scanText = BuildNormalizedTextFromWords(words, startPage, endPage);
            if (string.IsNullOrWhiteSpace(scanText))
                scanText = TextUtils.CollapseSpacedLettersText(text);
            var scanLines = BuildLinesFromWordDicts(words, startPage, endPage);
            void AddReq(string name, string value, string pattern, double weight = 1.2)
            {
                value = value?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(value)) return;
                value = CleanStandard(name, value);
                if (!ValidateStandard(name, value)) return;
                var bbox = FindBBoxForBand(words, value);
                int page = bbox?.page ?? startPage;
                var hit = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["value"] = value,
                    ["page"] = page,
                    ["pattern"] = pattern,
                    ["weight"] = weight,
                    ["method"] = "requerimento_extractor"
                };
                hit["bbox"] = bbox != null ? new Dictionary<string, double>
                {
                    ["nx0"] = bbox.Value.nx0,
                    ["ny0"] = bbox.Value.ny0,
                    ["nx1"] = bbox.Value.nx1,
                    ["ny1"] = bbox.Value.ny1
                } : JValue.CreateNull();
                hits.Add(hit);
            }

            // Processo judicial (CNJ)
            var mCnj = CnjRegex.Match(scanText);
            if (mCnj.Success) AddReq("PROCESSO_JUDICIAL", mCnj.Value, "req_cnj", 1.25);

            // Processo administrativo (SEI)
            var mSei = Regex.Match(scanText, @"(?i)processo\s+administrativo\s*(?:sei\s*)?n[ºo]?\s*([0-9\.\-\/]{10,})");
            if (mSei.Success) AddReq("PROCESSO_ADMINISTRATIVO", mSei.Groups[1].Value, "req_sei", 1.1);
            var mPa = Regex.Match(scanText, @"(?i)processo\s+administrativo[\s\S]{0,80}?processo\s+n[ºo]\s*([0-9]{6,})");
            if (mPa.Success) AddReq("PROCESSO_ADMINISTRATIVO", mPa.Groups[1].Value, "req_pa_num", 1.05);

            // Vara / Comarca
            var mUnidade = Regex.Match(scanText, @"(?i)unidade\s+judici[aá]ria\s+requisitante\s*[:\-]\s*([^\n]{6,140})");
            if (mUnidade.Success && TryExtractVaraComarcaFromLine(mUnidade.Groups[1].Value, out var vUn, out var cUn))
            {
                if (!string.IsNullOrWhiteSpace(vUn)) AddReq("VARA", vUn, "req_unidade_requisitante", 1.2);
                if (!string.IsNullOrWhiteSpace(cUn)) AddReq("COMARCA", cUn, "req_unidade_requisitante", 1.2);
            }
            if (!mUnidade.Success && scanLines.Count > 0)
            {
                foreach (var line in scanLines)
                {
                    if (!Regex.IsMatch(line, @"(?i)unidade\s+judici[aá]ria\s+requisitante")) continue;
                    if (TryExtractVaraComarcaFromLine(line, out var vLine, out var cLine))
                    {
                        if (!string.IsNullOrWhiteSpace(vLine)) AddReq("VARA", vLine, "req_unidade_line", 1.15);
                        if (!string.IsNullOrWhiteSpace(cLine)) AddReq("COMARCA", cLine, "req_unidade_line", 1.15);
                        break;
                    }
                }
            }
            var mVaraComarca = Regex.Match(scanText, @"(?i)ju[ií]zo\s+(?:da|de)\s+([0-9ªºA-Za-zÀ-ÿ\s]{3,80}?vara[^,\n]{0,40})\s+da\s+comarca\s+de\s+([A-Za-zÀ-ÿ\s\-]{3,80})");
            if (mVaraComarca.Success)
            {
                AddReq("VARA", mVaraComarca.Groups[1].Value, "req_vara_comarca", 1.2);
                AddReq("COMARCA", mVaraComarca.Groups[2].Value, "req_vara_comarca", 1.2);
            }
            else
            {
                var mOrg = Regex.Match(scanText, @"(?i)órgão\s+julgador\s*:\s*([^\n]{3,80})");
                if (mOrg.Success)
                {
                    var org = mOrg.Groups[1].Value.Trim();
                    if (org.Contains("Vara", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = org.Split(" de ", StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2)
                        {
                            AddReq("VARA", parts[0].Trim(), "req_org_julgador", 1.1);
                            AddReq("COMARCA", parts[1].Trim(), "req_org_julgador", 1.1);
                        }
                    }
                }
                var mComarca = Regex.Match(scanText, @"(?i)comarca\s+de\s+([A-Za-zÀ-ÿ\s\-]{3,80})");
                if (mComarca.Success) AddReq("COMARCA", mComarca.Groups[1].Value, "req_comarca", 1.1);
                var mVara = Regex.Match(scanText, @"(?i)(?:ju[ií]zo\s+da|vara)\s+([0-9ªºA-Za-zÀ-ÿ\s]{3,80}?vara[^,\n]{0,40})");
                if (mVara.Success) AddReq("VARA", mVara.Groups[1].Value, "req_vara", 1.05);
            }

            // Perito + CPF
            var mPeritoCpf = Regex.Match(scanText, @"(?i)(?:ao|à)\s+(?:sr\.?|sra\.?|senhor|senhora)\s+([^,\n]{3,80})\s*,\s*cpf\s*(?:n\.?|nº)?\s*([0-9\.\-]{11,14})");
            if (mPeritoCpf.Success)
            {
                AddReq("PERITO", mPeritoCpf.Groups[1].Value, "req_perito_cpf", 1.35);
                AddReq("CPF_PERITO", mPeritoCpf.Groups[2].Value, "req_perito_cpf", 1.35);
            }
            else
            {
                var mPeritoCpf2 = Regex.Match(scanText, @"(?i)perito\s+([^,\n]{3,80})\s*,\s*cpf\s*(?:n\.?|nº)?\s*([0-9\.\-]{11,14})");
                if (mPeritoCpf2.Success)
                {
                    AddReq("PERITO", mPeritoCpf2.Groups[1].Value, "req_perito_cpf2", 1.2);
                    AddReq("CPF_PERITO", mPeritoCpf2.Groups[2].Value, "req_perito_cpf2", 1.2);
                }
            }

            // Nome / CPF em linhas separadas
            var mNome = Regex.Match(scanText, @"(?i)\bnome\s*[:\-]\s*([^\n]{3,120}?)(?:\s+cpf|$)");
            if (mNome.Success) AddReq("PERITO", TrimReqNome(mNome.Groups[1].Value), "req_nome", 1.25);
            var mCpfLabel = Regex.Match(scanText, @"(?i)\bcpf\s*[:\-]\s*([0-9\. -]{11,20})");
            if (mCpfLabel.Success) AddReq("CPF_PERITO", ExtractCpfCandidate(mCpfLabel.Groups[1].Value), "req_cpf_label", 1.15);

            // Dr. <nome> aceitou o encargo...
            var mDr = Regex.Match(scanText, @"(?i)dr\.?\s+([A-ZÁÀÂÃÉÊÍÓÔÕÚÇ][A-Za-zÀ-ÿ\s]{3,80})\s*,\s*aceitou\s+o\s+encargo");
            if (mDr.Success) AddReq("PERITO", mDr.Groups[1].Value, "req_dr_encargo", 1.1);

            // Campos numerados do requerimento
            var mNomeNum = Regex.Match(scanText, @"(?i)1\s*\.\s*2\s*\.\s*1\s*nome\s*[:\-]\s*([^\n]{3,120})");
            if (mNomeNum.Success) AddReq("PERITO", TrimReqNome(mNomeNum.Groups[1].Value), "req_nome_1_2_1", 1.3);
            var mCpfNum = Regex.Match(scanText, @"(?i)1\s*\.\s*2\s*\.\s*4\s*cpf\s*[:\-]\s*([0-9\. -]{11,20})");
            if (mCpfNum.Success) AddReq("CPF_PERITO", ExtractCpfCandidate(mCpfNum.Groups[1].Value), "req_cpf_1_2_4", 1.3);
            if (!mNomeNum.Success && scanLines.Count > 0)
            {
                foreach (var line in scanLines)
                {
                    if (!Regex.IsMatch(line, @"(?i)\b1\.2\.1\b.*\bnome\b")) continue;
                    var mLine = Regex.Match(line, @"(?i)nome\s*[:\-]\s*([^\n]{3,120})");
                    if (mLine.Success)
                    {
                        AddReq("PERITO", TrimReqNome(mLine.Groups[1].Value), "req_nome_line", 1.2);
                        break;
                    }
                }
            }
            if (!mCpfNum.Success && scanLines.Count > 0)
            {
                foreach (var line in scanLines)
                {
                    if (!Regex.IsMatch(line, @"(?i)\b1\.2\.4\b.*\bcpf\b")) continue;
                    var mLine = Regex.Match(line, @"(?i)cpf\s*[:\-]\s*([0-9\. -]{11,20})");
                    if (mLine.Success)
                    {
                        AddReq("CPF_PERITO", ExtractCpfCandidate(mLine.Groups[1].Value), "req_cpf_line", 1.2);
                        break;
                    }
                }
            }

            // CPF isolado (fallback)
            var mCpf = CpfRegex.Match(scanText);
            if (mCpf.Success) AddReq("CPF_PERITO", mCpf.Value, "req_cpf", 1.0);

            // Especialidade / Espécie
            var mEspLabel = Regex.Match(scanText, @"(?i)especialidade\s*[:\-]\s*([A-Za-zÀ-ÿ\s]{3,80})");
            if (mEspLabel.Success) AddReq("ESPECIALIDADE", mEspLabel.Groups[1].Value, "req_especialidade_label", 1.1);
            var mPeritoEsp = Regex.Match(scanText, @"(?i)perito\s+([A-Za-zÀ-ÿ\s]{3,40})\s*,\s*([A-ZÁÀÂÃÉÊÍÓÔÕÚÇ][^,\n]{3,80})");
            if (mPeritoEsp.Success)
            {
                AddReq("ESPECIALIDADE", mPeritoEsp.Groups[1].Value, "req_perito_especialidade", 1.2);
                AddReq("PERITO", mPeritoEsp.Groups[2].Value, "req_perito_especialidade", 1.1);
            }
            else
            {
                var mEsp = Regex.Match(scanText, @"(?i)perito\s+(?:judicial\s+)?(?:em\s+)?([A-Za-zÀ-ÿ\s]{3,40})");
                if (mEsp.Success && IsLikelySpecialty(mEsp.Groups[1].Value))
                    AddReq("ESPECIALIDADE", mEsp.Groups[1].Value, "req_especialidade", 1.05);
            }

            var mEspecie = Regex.Match(scanText, @"(?i)per[ií]cia\s+([A-Za-zÀ-ÿ\s]{3,40})");
            if (mEspecie.Success && IsLikelySpecialty(mEspecie.Groups[1].Value))
                AddReq("ESPECIE_DA_PERICIA", mEspecie.Groups[1].Value, "req_especie", 1.0);
            var mEspecie2 = Regex.Match(scanText, @"(?i)esp[eé]cie\s+(?:da\s+)?per[ií]cia\s*[:\-]\s*([A-Za-zÀ-ÿ\s]{3,80})");
            if (mEspecie2.Success && IsLikelySpecialty(mEspecie2.Groups[1].Value))
                AddReq("ESPECIE_DA_PERICIA", mEspecie2.Groups[1].Value, "req_especie_label", 1.05);

            // Valor arbitrado (JZ)
            var mValor = Regex.Match(scanText, @"(?i)valor\s+arbitrad[oa][^\n]{0,60}?(?:R\$\s*)?([0-9\s\.]+,\s*[0-9\s]{2,})");
            if (mValor.Success) AddReq("VALOR_ARBITRADO_JZ", mValor.Groups[1].Value, "req_valor_arbitrado", 1.35);
            var mImporte = Regex.Match(scanText, @"(?i)importe[^\n]{0,80}?R\$\s*([0-9\s\.]+,\s*[0-9\s]{2,})");
            if (mImporte.Success) AddReq("VALOR_ARBITRADO_JZ", mImporte.Groups[1].Value, "req_importe", 1.3);
            var mHonor = Regex.Match(scanText, @"(?i)honor[aá]rios\s+periciais[^\n]{0,100}?R\$\s*([0-9\s\.]+,\s*[0-9\s]{2,})");
            if (mHonor.Success) AddReq("VALOR_ARBITRADO_JZ", mHonor.Groups[1].Value, "req_honorarios", 1.2);
            if (!mValor.Success && !mImporte.Success && !mHonor.Success)
            {
                IEnumerable<string> lines = scanLines.Count > 0 ? scanLines : scanText.Split('\n');
                foreach (var line in lines)
                {
                    if (!Regex.IsMatch(line, @"(?i)valor\s+arbitrad")) continue;
                    var mLine = Regex.Match(line, @"([0-9\s\.]+,\s*[0-9\s]{2,})");
                    if (mLine.Success)
                    {
                        AddReq("VALOR_ARBITRADO_JZ", mLine.Groups[1].Value, "req_valor_line", 1.25);
                        break;
                    }
                }
            }

            // Data / Assinante
            var mAss = Regex.Match(scanText, @"(?i)assinado\s+eletronicamente\s+por\s*:\s*([A-ZÁÀÂÃÉÊÍÓÔÕÚÇ][A-Za-zÀ-ÿ\s]{3,80})");
            if (mAss.Success) AddReq("ASSINANTE", mAss.Groups[1].Value, "req_assinante", 1.1);

            var mDate = Regex.Match(scanText, @"(?i)(\d{1,2}\s+de\s+[A-Za-zçãéêíóôõú]+\s+de\s+\d{4})");
            if (mDate.Success)
            {
                var iso = NormalizeDateFlexible(mDate.Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(iso))
                {
                    AddReq("DATA_REQUISICAO", iso, "req_data_extenso", 1.05);
                    AddReq("DATA", iso, "req_data_extenso", 1.0);
                }
            }
            else
            {
                var mDateNum = Regex.Match(scanText, @"\b\d{1,2}/\d{1,2}/\d{2,4}\b");
                if (mDateNum.Success)
                {
                    var iso = NormalizeDateFlexible(mDateNum.Value);
                    if (!string.IsNullOrWhiteSpace(iso))
                    {
                        AddReq("DATA_REQUISICAO", iso, "req_data_num", 1.0);
                        AddReq("DATA", iso, "req_data_num", 0.95);
                    }
                }
            }

            return hits;
        }

        private bool IsLikelySpecialty(string value)
        {
            var v = NormalizeSimple(value);
            if (string.IsNullOrWhiteSpace(v)) return false;
            if (v.Length < 3 || v.Length > 40) return false;
            if (Regex.IsMatch(v, @"\d")) return false;
            if (v.Contains("cpf") || v.Contains("requerimento") || v.Contains("processo")) return false;
            if (v.Contains("juiz") || v.Contains("vara") || v.Contains("comarca")) return false;
            return true;
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
            hit["bbox"] = bbox != null ? new Dictionary<string, double>
            {
                ["nx0"] = bbox.Value.nx0,
                ["ny0"] = bbox.Value.ny0,
                ["nx1"] = bbox.Value.nx1,
                ["ny1"] = bbox.Value.ny1
            } : JValue.CreateNull();
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
            string[] kws = { "despacho", "decisão", "decisao", "senten", "certidao", "certidão", "oficio", "ofício", "nota de empenho", "autorizacao", "autorização", "requisição", "requisicao", "requerimento" };
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

        private string ExtractHeaderFromWords(List<Dictionary<string, object>> words, int page, double headerTopPct)
        {
            if (words == null || words.Count == 0) return "";
            if (headerTopPct <= 0 || headerTopPct > 0.5) headerTopPct = 0.15;
            double threshold = 1.0 - headerTopPct;

            var pageWords = words.Where(w => Convert.ToInt32(w["page"]) == page).ToList();
            if (pageWords.Count == 0) return "";

            var lines = BuildLines(pageWords);
            var headerLines = lines
                .Where(l => l.NY0 >= threshold)
                .OrderByDescending(l => l.NY0)
                .Select(l => l.Text.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct()
                .ToList();

            if (headerLines.Count == 0)
            {
                headerLines = lines
                    .OrderByDescending(l => l.NY0)
                    .Select(l => l.Text.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Distinct()
                    .Take(3)
                    .ToList();
            }

            return string.Join("\n", headerLines);
        }

        private string ExtractHeaderFromLines(List<LineInfo> lines, double headerTopPct)
        {
            if (lines == null || lines.Count == 0) return "";
            if (headerTopPct <= 0 || headerTopPct > 0.5) headerTopPct = 0.15;
            double threshold = 1.0 - headerTopPct;

            var headerLines = lines
                .Where(l => l.NormY0 >= threshold)
                .OrderByDescending(l => l.NormY0)
                .Select(l => l.Text.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Distinct()
                .ToList();

            if (headerLines.Count == 0)
            {
                headerLines = lines
                    .OrderByDescending(l => l.NormY0)
                    .Select(l => l.Text.Trim())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Distinct()
                    .Take(3)
                    .ToList();
            }

            return string.Join("\n", headerLines);
        }

        private string ExtractHeaderFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var lines = text.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Take(3)
                .ToList();
            return string.Join("\n", lines);
        }

        private (string Header, string Subheader) ExtractHeaderAndSubheader(List<Dictionary<string, object>> words, int page, double headerTopPct)
        {
            if (words == null || words.Count == 0) return ("", "");
            if (headerTopPct <= 0 || headerTopPct > 0.5) headerTopPct = 0.15;

            var pageWords = words.Where(w => Convert.ToInt32(w["page"]) == page).ToList();
            if (pageWords.Count == 0) return ("", "");

            var lineObjs = BuildLines(pageWords);
            if (lineObjs == null || lineObjs.Count == 0) return ("", "");

            return ExtractHeaderAndSubheaderFromLines(lineObjs, headerTopPct);
        }

        private (string Header, string Subheader) ExtractHeaderAndSubheaderFromLines(List<LineObj> lines, double headerTopPct)
        {
            if (lines == null || lines.Count == 0) return ("", "");

            var ordered = lines.OrderByDescending(l => l.NY0).ToList();
            var scanTopPct = Math.Min(headerTopPct + 0.35, 0.6);
            var scanThreshold = 1.0 - scanTopPct;
            var scanLines = ordered
                .Where(l => l.NY0 >= scanThreshold)
                .Select(l => l.Text.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            var estadoLine = FindHeaderLine(scanLines, "ESTADODAPARAIBA");
            var poderLine = FindHeaderLine(scanLines, "PODERJUDICIARIO");
            var tribunalLine = FindHeaderLine(scanLines, "TRIBUNALDEJUSTICA");

            var headerLines = new List<string>();
            if (!string.IsNullOrWhiteSpace(estadoLine)) headerLines.Add(CanonicalHeaderLine(estadoLine, "ESTADO"));
            if (!string.IsNullOrWhiteSpace(poderLine)) headerLines.Add(CanonicalHeaderLine(poderLine, "PODER"));
            if (!string.IsNullOrWhiteSpace(tribunalLine)) headerLines.Add(CanonicalHeaderLine(tribunalLine, "TRIBUNAL"));
            if (!string.IsNullOrWhiteSpace(tribunalLine) && NormalizeHeaderKey(tribunalLine).Contains("PARAIBA"))
            {
                headerLines = new List<string>
                {
                    "ESTADO DA PARAÍBA",
                    "PODER JUDICIÁRIO",
                    CanonicalHeaderLine(tribunalLine, "TRIBUNAL")
                };
            }

            if (headerLines.Count < 2)
            {
                var headerThreshold = 1.0 - headerTopPct;
                headerLines = ordered
                    .Where(l => l.NY0 >= headerThreshold)
                    .Select(l => l.Text.Trim())
                    .Where(l => l.Length > 0)
                    .Distinct()
                    .Take(3)
                    .ToList();
            }

            var header = string.Join("\n", headerLines);

            // Subheader: procurar um pouco abaixo do topo (ex.: Diretoria Especial)
            var headerBandMin = 1.0 - headerTopPct;
            var subScanPct = Math.Min(headerTopPct + 0.12, 0.35);
            var subThreshold = 1.0 - subScanPct;
            var subLines = ordered
                .Where(l => l.NY0 >= subThreshold && l.NY0 < (headerBandMin - 0.005))
                .Select(l => l.Text.Trim())
                .Where(l => l.Length > 0)
                .ToList();

            var diretoriaLine = subLines.FirstOrDefault(l => NormalizeHeaderKey(l).Contains("DIRETORIAESPECIAL") && l.Length <= 60);
            var subHeader = !string.IsNullOrWhiteSpace(diretoriaLine)
                ? "Diretoria Especial"
                : subLines.FirstOrDefault(l =>
                    !headerLines.Contains(l) &&
                    !IsGeneric(l) &&
                    !IsGenericNormalized(l) &&
                    !IsCandidateTitle(l) &&
                    !IsCandidateTitleNormalized(l));

            return (header, subHeader ?? "");
        }

        private string FindHeaderLine(List<string> lines, string token)
        {
            if (lines == null || lines.Count == 0) return "";
            return lines.FirstOrDefault(l => NormalizeHeaderKey(l).Contains(token)) ?? "";
        }

        private string NormalizeHeaderKey(string text)
        {
            var t = RemoveDiacritics(text ?? "").ToUpperInvariant();
            t = Regex.Replace(t, @"[^A-Z]", "");
            return t;
        }

        private string CanonicalHeaderLine(string line, string kind)
        {
            var norm = NormalizeHeaderKey(line);
            if (kind == "ESTADO") return "ESTADO DA PARAÍBA";
            if (kind == "PODER") return "PODER JUDICIÁRIO";
            if (kind == "TRIBUNAL")
                return norm.Contains("PARAIBA") ? "TRIBUNAL DE JUSTIÇA DA PARAÍBA" : "TRIBUNAL DE JUSTIÇA";
            return line.Trim();
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

        private bool IsRequerimentoPagamentoHonorarios(string label, string? firstPageText, string? fullText)
        {
            var normLabel = NormalizeSimple(label);
            var bookmarkHint = normLabel.StartsWith("requerimento");

            var firstChunk = firstPageText ?? "";
            if (firstChunk.Length > 3000)
                firstChunk = firstChunk.Substring(0, 3000);
            var normFirst = NormalizeSimple(firstChunk);

            var textOk = HasRequerimentoHeading(firstPageText)
                         || MatchesRequerimentoSignals(normFirst);
            if (!textOk)
            {
                var normFull = NormalizeSimple(fullText ?? "");
                textOk = HasRequerimentoHeading(fullText) || MatchesRequerimentoSignals(normFull);
            }

            // Requerimento vem no bookmark; exige confirmação no texto interno
            return bookmarkHint && textOk;
        }

        private bool MatchesRequerimentoSignals(string norm)
        {
            if (string.IsNullOrWhiteSpace(norm)) return false;
            if (Regex.IsMatch(norm, @"requerimento\\s+de\\s+pagamento\\s+de\\s+honor"))
                return true;
            if (norm.Contains("requerimento") && norm.Contains("honor") && (norm.Contains("pagamento") || norm.Contains("pagamentos")))
                return true;
            if (norm.Contains("requerimento") && norm.Contains("honor") && (norm.Contains("solicito o pagamento") || norm.Contains("requisito o pagamento")))
                return true;
            if (Regex.IsMatch(norm, @"solicito\\s+o\\s+pagamento\\s+dos?\\s+honor"))
                return true;
            if (Regex.IsMatch(norm, @"cumprindo\\s+determina[cç][aã]o[\\s\\S]{0,80}?solicito\\s+o\\s+pagamento"))
                return true;
            return false;
        }

        private bool HasRequerimentoHeading(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lines = text.Split('\n').Select(l => NormalizeSimple(l)).Where(l => l.Length > 0).ToList();
            foreach (var line in lines.Take(12))
            {
                if (line == "requerimento") return true;
                if (line.StartsWith("requerimento de pagamento") && line.Contains("honor"))
                    return true;
            }
            return false;
        }

        private string NormalizeSimple(string text)
        {
            var collapsed = TextUtils.CollapseSpacedLettersText(text ?? "");
            var t = RemoveDiacritics(collapsed).ToLowerInvariant();
            t = Regex.Replace(t, @"\\s+", " ").Trim();
            return t;
        }

        private int CountSolidChars(string? text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0;
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                    count++;
            }
            return count;
        }


        private bool MatchesOrigin(Dictionary<string, object> docMeta, string docType)
        {
            if (docMeta == null) return false;
            var origin = $"{docMeta.GetValueOrDefault("origin_main")} {docMeta.GetValueOrDefault("origin_sub")} {docMeta.GetValueOrDefault("origin_extra")} {docMeta.GetValueOrDefault("title")}";
            origin = RemoveDiacritics(origin ?? "").ToLowerInvariant();
            var cfg = _tjpbCfg ?? new TjpbDespachoConfig();

            List<string> hints;
            if (docType.Equals("certidao", StringComparison.OrdinalIgnoreCase))
                hints = cfg.Certidao.HeaderHints.Concat(cfg.Certidao.TitleHints).ToList();
            else
                hints = cfg.Anchors.Header.Concat(cfg.Anchors.Subheader).ToList();

            if (hints == null || hints.Count == 0)
            {
                return !string.IsNullOrWhiteSpace(origin);
            }
            foreach (var h in hints)
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                var hh = RemoveDiacritics(h).ToLowerInvariant();
                if (origin.Contains(hh)) return true;
            }
            return false;
        }

        private bool MatchesSigner(Dictionary<string, object> docMeta)
        {
            if (docMeta == null) return false;
            var signer = docMeta.GetValueOrDefault("signer")?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(signer)) return false;
            var norm = RemoveDiacritics(signer).ToLowerInvariant();
            var cfg = _tjpbCfg ?? new TjpbDespachoConfig();
            if (cfg.Anchors.SignerHints == null || cfg.Anchors.SignerHints.Count == 0)
            {
                return !string.IsNullOrWhiteSpace(signer);
            }
            foreach (var h in cfg.Anchors.SignerHints)
            {
                if (string.IsNullOrWhiteSpace(h)) continue;
                var hh = RemoveDiacritics(h).ToLowerInvariant();
                if (norm.Contains(hh)) return true;
            }
            return false;
        }

        private bool IsTargetDespachoTemplate(string docText)
        {
            if (string.IsNullOrWhiteSpace(docText)) return false;
            var norm = RemoveDiacritics(docText).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(norm)) return false;
            var hasDiretoria = ContainsLoose(norm, "diretoria especial");
            var hasGeorc = ContainsLoose(norm, "georc") || ContainsLoose(norm, "gerencia de programacao orcamentaria");
            var hasDespachoReserva = ContainsLoose(norm, "despacho requisicao de reserva orcamentaria") ||
                                     ContainsLoose(norm, "despacho reserva orcamentaria");
            var hasRobson = ContainsLoose(norm, "robson de lima");
            return hasDiretoria && hasRobson && (hasDespachoReserva || hasGeorc);
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
                    ["bboxN"] = ev?.BBoxN ?? (object)JValue.CreateNull(),
                    ["bbox"] = ev?.BBoxN != null ? new Dictionary<string, double>
                    {
                        ["nx0"] = ev.BBoxN.X0,
                        ["ny0"] = ev.BBoxN.Y0,
                        ["nx1"] = ev.BBoxN.X1,
                        ["ny1"] = ev.BBoxN.Y1
                    } : JValue.CreateNull(),
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

                var obj = BuildDocObject(boundary, analysis, pdfPath, scripts, null, false, false, null);
                obj["parent_doc_label"] = ExtractDocumentName(parent);
                obj["doc_label"] = anexos[i]["title"]?.ToString() ?? "Anexo";
                obj["doc_type"] = "anexo_split";
                list.Add(obj);
            }

            return list;
        }

        private sealed class DespachoAnchorState
        {
            public bool Applied { get; set; }
        }
    }
}
