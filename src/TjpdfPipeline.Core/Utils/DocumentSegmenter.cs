using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FilterPDF
{
    /// <summary>
    /// Segmentador de documentos - identifica limites entre documentos em PDFs multi-documento
    /// </summary>
    public class DocumentSegmenter
    {
        private readonly DocumentSegmentationConfig config;
        private readonly List<BoundaryScore> pageScores;
        
        public DocumentSegmenter(DocumentSegmentationConfig? config = null)
        {
            this.config = config ?? new DocumentSegmentationConfig();
            this.pageScores = new List<BoundaryScore>();
        }
        
        public List<DocumentBoundary> FindDocuments(PDFAnalysisResult analysis)
        {
            if (analysis?.Pages == null || analysis.Pages.Count == 0)
                return new List<DocumentBoundary>();
            
            if (config.Verbose)
            {
                Console.WriteLine($"[DEBUG] Starting document segmentation for {analysis.Pages.Count} pages");
            }
            
            // Fase 1: Calcular scores para cada página
            CalculatePageScores(analysis);
            
            if (config.Verbose)
            {
                Console.WriteLine($"[DEBUG] Calculated scores for {pageScores.Count} pages");
                foreach (var score in pageScores)
                {
                    Console.WriteLine($"[DEBUG] Page {score.PageNumber}: StartScore={score.StartScore:F2}, EndScore={score.EndScore:F2}");
                }
            }
            
            // Fase 2: Identificar boundaries
            var boundaries = IdentifyBoundaries();
            
            if (config.Verbose)
            {
                Console.WriteLine($"[DEBUG] Identified {boundaries.Count} document boundaries");
            }
            
            // Fase 3: Agrupar páginas adjacentes
            if (config.GroupAdjacentPages)
            {
                boundaries = GroupAdjacentPages(boundaries, analysis.Pages.Count);
            }
            
            // Fase 4: Validar documentos (mesmo tamanho de papel)
            if (config.RequireSamePaperSize)
            {
                boundaries = boundaries.Where(doc => ValidateSamePaperSize(doc, analysis)).ToList();
                
                if (config.Verbose)
                {
                    Console.WriteLine($"[DEBUG] After paper size validation: {boundaries.Count} documents");
                }
            }
            
            // Fase 5: Enriquecer com informações do documento
            EnrichDocumentInfo(boundaries, analysis);
            
            // Fase 5: Validar e ajustar
            boundaries = ValidateAndAdjust(boundaries, analysis.Pages.Count);
            
            if (config.Verbose)
            {
                Console.WriteLine($"[DEBUG] Final document count: {boundaries.Count}");
            }
            
            return boundaries;
        }
        
        private void CalculatePageScores(PDFAnalysisResult analysis)
        {
            pageScores.Clear();
            
            for (int i = 0; i < analysis.Pages.Count; i++)
            {
                var page = analysis.Pages[i];
                var score = new BoundaryScore { PageNumber = page.PageNumber };
                
                // 1. Detecção por padrões textuais
                if (config.DetectByPatterns)
                {
                    score.FeatureScores["patterns"] = CalculatePatternScore(page);
                    score.StartScore += score.FeatureScores["patterns"] * 0.3;
                }
                
                // 2. Detecção por assinaturas
                if (config.DetectBySignatures)
                {
                    score.FeatureScores["signatures"] = CalculateSignatureScore(page);
                    score.EndScore += score.FeatureScores["signatures"] * 0.4;
                }
                
                // 3. Detecção por mudança de densidade
                if (config.DetectByDensity && i > 0)
                {
                    var prevPage = analysis.Pages[i - 1];
                    score.FeatureScores["density"] = CalculateDensityChange(prevPage, page);
                    score.StartScore += score.FeatureScores["density"] * 0.2;
                }
                
                // 4. Detecção por mudança de fontes
                if (config.DetectByFontChanges && i > 0)
                {
                    var prevPage = analysis.Pages[i - 1];
                    score.FeatureScores["fonts"] = CalculateFontChange(prevPage, page);
                    score.StartScore += score.FeatureScores["fonts"] * 0.2;
                }
                
                // 5. Detecção por mudança de tamanho de página
                if (config.DetectByPageSize && i > 0)
                {
                    var prevPage = analysis.Pages[i - 1];
                    score.FeatureScores["pagesize"] = CalculatePageSizeChange(prevPage, page);
                    score.StartScore += score.FeatureScores["pagesize"] * 0.1;
                }
                
                // 6. Detecção por assinatura em imagem
                if (config.DetectByImageSignatures)
                {
                    score.FeatureScores["image_signature"] = DetectImageSignature(page);
                    score.EndScore += score.FeatureScores["image_signature"] * 0.3;
                }
                
                // 7. Análise de vizinhança
                if (config.DetectByNeighborhood)
                {
                    score.NeighborhoodScore = CalculateNeighborhoodScore(analysis.Pages, i);
                    score.StartScore = (score.StartScore + score.NeighborhoodScore) / 2;
                }
                
                // 8. Detecção por texto na margem superior (10%)
                if (config.DetectByTopMarginText)
                {
                    score.FeatureScores["top_margin"] = DetectTopMarginText(page);
                    score.StartScore += score.FeatureScores["top_margin"] * 0.3;
                }
                
                // 9. Detecção por cabeçalhos em maiúsculas
                if (config.DetectByUppercaseHeaders)
                {
                    score.FeatureScores["uppercase_headers"] = DetectUppercaseHeaders(page);
                    score.StartScore += score.FeatureScores["uppercase_headers"] * 0.25;
                }
                
                pageScores.Add(score);
            }
        }
        
        private double CalculatePatternScore(PageAnalysis page)
        {
            if (page.TextInfo == null || string.IsNullOrEmpty(page.TextInfo.PageText))
                return 0;
            
            // Pegar primeiras linhas significativas
            var firstLines = GetFirstSignificantLines(page.TextInfo.PageText, 30);
            double score = 0;
            
            if (config.Verbose)
            {
                Console.WriteLine($"[DEBUG] Pattern analysis for page {page.PageNumber}:");
                Console.WriteLine($"[DEBUG] First lines: {firstLines.Substring(0, Math.Min(100, firstLines.Length))}...");
            }
            
            int matchCount = 0;
            foreach (var pattern in config.StartPatterns)
            {
                if (Regex.IsMatch(firstLines, pattern, RegexOptions.IgnoreCase))
                {
                    matchCount++;
                    if (config.Verbose)
                    {
                        Console.WriteLine($"[DEBUG] Matched pattern: {pattern}");
                    }
                }
            }
            
            // Se encontrou 2+ padrões, é muito provável ser início de documento
            if (matchCount >= 2)
                score = 0.8;
            else if (matchCount == 1)
                score = 0.4;
            
            return score;
        }
        
        private double CalculateSignatureScore(PageAnalysis page)
        {
            if (page.TextInfo == null || string.IsNullOrEmpty(page.TextInfo.PageText))
                return 0;
            
            // Pegar últimas linhas
            var lastLines = GetLastSignificantLines(page.TextInfo.PageText, 30);
            double score = 0;
            
            int matchCount = 0;
            foreach (var pattern in config.EndPatterns)
            {
                if (Regex.IsMatch(lastLines, pattern, RegexOptions.IgnoreCase))
                {
                    matchCount++;
                    if (config.Verbose)
                    {
                        Console.WriteLine($"[DEBUG] Matched end pattern: {pattern}");
                    }
                }
            }
            
            // Se encontrou qualquer padrão de fim, é provável ser fim de documento
            if (matchCount >= 1)
                score = 0.8;
            
            return score;
        }
        
        private double CalculateDensityChange(PageAnalysis prevPage, PageAnalysis currentPage)
        {
            var prevDensity = prevPage.TextInfo?.WordCount ?? 0;
            var currDensity = currentPage.TextInfo?.WordCount ?? 0;
            
            if (prevDensity == 0) return 0;
            
            var change = Math.Abs(currDensity - prevDensity) / (double)prevDensity;
            
            // Grande mudança de densidade (ex: 400 palavras -> 50 palavras)
            if (prevDensity > 300 && currDensity < 100)
                return 0.8;
            
            return change > config.DensityChangeThreshold ? change : 0;
        }
        
        private double CalculateFontChange(PageAnalysis prevPage, PageAnalysis currentPage)
        {
            if (prevPage.FontInfo == null || currentPage.FontInfo == null)
                return 0;
            
            var prevFonts = new HashSet<string>(prevPage.FontInfo.Select(f => f.Name));
            var currFonts = new HashSet<string>(currentPage.FontInfo.Select(f => f.Name));
            
            // Calcular similaridade de Jaccard
            var intersection = prevFonts.Intersect(currFonts).Count();
            var union = prevFonts.Union(currFonts).Count();
            
            if (union == 0) return 0;
            
            var similarity = (double)intersection / union;
            
            // Se muito diferente, é provável novo documento
            double fontChangeScore = similarity < config.FontChangeSimilarityThreshold ? 1 - similarity : 0;
            
            if (config.Verbose && fontChangeScore > 0)
            {
                Console.WriteLine($"[DEBUG] Font change detected between pages {prevPage.PageNumber}-{currentPage.PageNumber}: similarity={similarity:F2}, score={fontChangeScore:F2}");
                Console.WriteLine($"[DEBUG] Previous fonts: {string.Join(", ", prevFonts.Take(5))}");
                Console.WriteLine($"[DEBUG] Current fonts: {string.Join(", ", currFonts.Take(5))}");
            }
            
            return fontChangeScore;
        }
        
        private double CalculatePageSizeChange(PageAnalysis prevPage, PageAnalysis currentPage)
        {
            if (prevPage.Size == null || currentPage.Size == null)
                return 0;
            
            // Mudança de orientação ou tamanho
            bool sizeChanged = Math.Abs(prevPage.Size.Width - currentPage.Size.Width) > 10 ||
                              Math.Abs(prevPage.Size.Height - currentPage.Size.Height) > 10;
            
            // Se config.RequireSamePaperSize está ativo, mudança de tamanho indica limite de documento
            if (config.RequireSamePaperSize && sizeChanged)
            {
                return 0.9; // Alta confiança para mudança de tamanho
            }
            
            return sizeChanged ? 0.8 : 0;
        }
        
        private double DetectImageSignature(PageAnalysis page)
        {
            if (page.Resources?.Images == null || page.Resources.Images.Count == 0)
                return 0;
            
            // Procurar por imagens pequenas no final da página (possíveis assinaturas)
            foreach (var img in page.Resources.Images)
            {
                // Imagem pequena (provável assinatura) e no terço inferior da página
                if (img.Width < 200 && img.Height < 100)
                {
                    // Verificar se está na parte inferior da página
                    // (seria ideal ter a posição Y, mas vamos assumir por enquanto)
                    return 0.7;
                }
            }
            
            return 0;
        }
        
        private double CalculateNeighborhoodScore(List<PageAnalysis> pages, int currentIndex)
        {
            // Comparar com páginas vizinhas
            double score = 0;
            int comparisons = 0;
            
            // Comparar com até 3 páginas antes e depois
            for (int offset = -3; offset <= 3; offset++)
            {
                if (offset == 0) continue;
                
                int neighborIndex = currentIndex + offset;
                if (neighborIndex >= 0 && neighborIndex < pages.Count)
                {
                    var current = pages[currentIndex];
                    var neighbor = pages[neighborIndex];
                    
                    // Diferença significativa = possível boundary
                    var currentWords = current.TextInfo?.WordCount ?? 0;
                    var neighborWords = neighbor.TextInfo?.WordCount ?? 0;
                    
                    if (currentWords > 0 && neighborWords > 0)
                    {
                        var ratio = (double)Math.Min(currentWords, neighborWords) / 
                                   Math.Max(currentWords, neighborWords);
                        
                        if (ratio < 0.3) // Muito diferente
                            score += 0.5;
                        
                        comparisons++;
                    }
                }
            }
            
            return comparisons > 0 ? score / comparisons : 0;
        }
        
        private List<DocumentBoundary> IdentifyBoundaries()
        {
            var documents = new List<DocumentBoundary>();
            DocumentBoundary? currentDoc = null;
            
            if (config.Verbose)
            {
                Console.WriteLine($"[DEBUG] IdentifyBoundaries: Processing {pageScores.Count} pages");
            }
            
            for (int i = 0; i < pageScores.Count; i++)
            {
                var score = pageScores[i];
                
                if (config.Verbose)
                {
                    Console.WriteLine($"[DEBUG] Page {i+1}: IsLikelyStart={score.IsLikelyStart}, IsLikelyEnd={score.IsLikelyEnd}");
                }
                
                // Alta pontuação de início
                if (score.IsLikelyStart)
                {
                    // Se já existe um documento aberto, fechá-lo primeiro
                    if (currentDoc != null)
                    {
                        currentDoc.EndPage = i; // Página anterior
                        documents.Add(currentDoc);
                        
                        if (config.Verbose)
                        {
                            Console.WriteLine($"[DEBUG] Closed previous document at page {i} due to new start detected");
                        }
                    }
                    
                    // Iniciar novo documento
                    currentDoc = new DocumentBoundary
                    {
                        StartPage = i + 1, // PageNumber é 1-based
                        EndPage = i + 1,
                        Confidence = score.StartScore
                    };
                    
                    if (config.Verbose)
                    {
                        Console.WriteLine($"[DEBUG] Started new document at page {i+1} with confidence {score.StartScore:F2}");
                    }
                    
                    // Guardar indicadores
                    foreach (var feature in score.FeatureScores.Where(f => f.Value > 0.5))
                    {
                        currentDoc.StartIndicators.Add(feature.Key);
                    }
                }
                // Alta pontuação de fim
                else if (score.IsLikelyEnd && currentDoc != null)
                {
                    currentDoc.EndPage = i + 1;
                    currentDoc.Confidence = (currentDoc.Confidence + score.EndScore) / 2;
                    
                    if (config.Verbose)
                    {
                        Console.WriteLine($"[DEBUG] Ending document at page {i+1} with confidence {currentDoc.Confidence:F2}");
                    }
                    
                    // Guardar indicadores de fim
                    foreach (var feature in score.FeatureScores.Where(f => f.Value > 0.5))
                    {
                        currentDoc.EndIndicators.Add(feature.Key);
                    }
                    
                    documents.Add(currentDoc);
                    currentDoc = null;
                }
                else if (currentDoc != null)
                {
                    // Página intermediária
                    currentDoc.EndPage = i + 1;
                }
            }
            
            // Fechar último documento se ficou aberto
            if (currentDoc != null)
            {
                documents.Add(currentDoc);
                if (config.Verbose)
                {
                    Console.WriteLine($"[DEBUG] Closed final document at end");
                }
            }
            
            if (config.Verbose)
            {
                Console.WriteLine($"[DEBUG] IdentifyBoundaries found {documents.Count} documents");
            }
            
            return documents;
        }
        
        private List<DocumentBoundary> GroupAdjacentPages(List<DocumentBoundary> documents, int totalPages)
        {
            // Se encontrou página de início mas não de fim próxima,
            // procurar a próxima página de início e usar a anterior como fim
            
            var grouped = new List<DocumentBoundary>();
            
            for (int i = 0; i < documents.Count; i++)
            {
                var doc = documents[i];
                
                // Validação de páginas contíguas se habilitada
                if (config.RequireContiguousPages)
                {
                    // Verificar se as páginas são contíguas (sequenciais)
                    bool isContiguous = doc.EndPage - doc.StartPage == doc.PageCount - 1;
                    
                    if (!isContiguous)
                    {
                        if (config.Verbose)
                        {
                            Console.WriteLine($"[DEBUG] Document {doc.StartPage}-{doc.EndPage} rejected: non-contiguous pages (expected {doc.PageCount} pages)");
                        }
                        continue; // Pular documento não contíguo
                    }
                }
                
                // Se documento tem apenas 1-2 páginas e próximo está muito perto
                if (doc.PageCount <= config.MaxGroupDistance && i + 1 < documents.Count)
                {
                    var nextDoc = documents[i + 1];
                    
                    // Se o próximo documento começa logo após este
                    if (nextDoc.StartPage - doc.EndPage <= config.MaxGroupDistance)
                    {
                        // Usar a página anterior ao próximo como fim deste
                        doc.EndPage = nextDoc.StartPage - 1;
                    }
                }
                
                grouped.Add(doc);
            }
            
            // Mesclar páginas órfãs
            if (config.MergeOrphanPages)
            {
                grouped = MergeOrphanPages(grouped, totalPages);
            }
            
            return grouped;
        }
        
        private List<DocumentBoundary> MergeOrphanPages(List<DocumentBoundary> documents, int totalPages)
        {
            if (documents.Count == 0) return documents;
            
            var merged = new List<DocumentBoundary>();
            
            // Verificar páginas antes do primeiro documento
            if (documents[0].StartPage > 1)
            {
                // Adicionar como documento separado ou anexar ao primeiro
                if (documents[0].StartPage <= 3)
                {
                    documents[0].StartPage = 1;
                }
            }
            
            // Processar documentos e gaps entre eles
            for (int i = 0; i < documents.Count - 1; i++)
            {
                merged.Add(documents[i]);
                
                var gap = documents[i + 1].StartPage - documents[i].EndPage - 1;
                if (gap > 0 && gap <= 2)
                {
                    // Pequeno gap - anexar ao documento anterior
                    documents[i].EndPage = documents[i + 1].StartPage - 1;
                }
            }
            
            // Adicionar último documento
            if (documents.Count > 0)
            {
                var lastDoc = documents[documents.Count - 1];
                
                // Verificar páginas após o último documento
                if (lastDoc.EndPage < totalPages && totalPages - lastDoc.EndPage <= 2)
                {
                    lastDoc.EndPage = totalPages;
                }
                
                merged.Add(lastDoc);
            }
            
            return merged;
        }
        
        private void EnrichDocumentInfo(List<DocumentBoundary> documents, PDFAnalysisResult analysis)
        {
            int docNumber = 1;
            
            foreach (var doc in documents)
            {
                doc.Number = docNumber++;
                
                // Coletar informações das páginas do documento
                var docPages = analysis.Pages
                    .Where(p => p.PageNumber >= doc.StartPage && p.PageNumber <= doc.EndPage)
                    .ToList();
                
                if (docPages.Count == 0) continue;
                
                // Fontes usadas
                foreach (var page in docPages)
                {
                    if (page.FontInfo != null)
                    {
                        foreach (var font in page.FontInfo)
                        {
                            doc.Fonts.Add(font.Name);
                        }
                    }
                    
                    doc.TotalWords += page.TextInfo?.WordCount ?? 0;
                }
                
                // Tamanho da página (primeira página)
                var firstPage = docPages.First();
                if (firstPage.Size != null)
                {
                    doc.PageSize = $"{firstPage.Size.Width:F0}x{firstPage.Size.Height:F0}";
                }
                
                // TEXTO COMPLETO - SEM LIMITAÇÕES
                if (firstPage.TextInfo != null && !string.IsNullOrEmpty(firstPage.TextInfo.PageText))
                {
                    doc.FirstPageText = firstPage.TextInfo.PageText; // TEXTO COMPLETO DA PRIMEIRA PÁGINA
                }
                
                var lastPage = docPages.Last();
                if (lastPage.TextInfo != null && !string.IsNullOrEmpty(lastPage.TextInfo.PageText))
                {
                    doc.LastPageText = lastPage.TextInfo.PageText; // TEXTO COMPLETO DA ÚLTIMA PÁGINA
                }
                
                // TEXTO COMPLETO DE TODAS AS PÁGINAS DO DOCUMENTO
                var fullTextBuilder = new System.Text.StringBuilder();
                foreach (var page in docPages)
                {
                    if (page.TextInfo != null && !string.IsNullOrEmpty(page.TextInfo.PageText))
                    {
                        fullTextBuilder.AppendLine(page.TextInfo.PageText);
                        fullTextBuilder.AppendLine(); // Separador entre páginas
                    }
                }
                doc.FullText = fullTextBuilder.ToString().Trim();
                
                // Detectar tipo de documento
                doc.DetectedType = DetectDocumentType(firstPage.TextInfo?.PageText ?? string.Empty);
                
                // Verificar assinatura em imagem
                doc.HasSignatureImage = docPages.Any(p => 
                    p.Resources?.Images != null && p.Resources.Images.Any(img => img.Width < 200 && img.Height < 100));
            }
        }
        
        private string DetectDocumentType(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "Documento";
            
            var firstLines = GetFirstSignificantLines(text, 20).ToUpper();
            
            if (firstLines.Contains("DESPACHO"))
                return "Despacho";
            if (firstLines.Contains("SENTENÇA"))
                return "Sentença";
            if (firstLines.Contains("DECISÃO"))
                return "Decisão";
            if (firstLines.Contains("CERTIDÃO"))
                return "Certidão";
            if (firstLines.Contains("OFÍCIO"))
                return "Ofício";
            if (firstLines.Contains("ATA"))
                return "Ata";
            if (firstLines.Contains("PROCESSO") || firstLines.Contains("AUTOS"))
                return "Processo Judicial";
            if (firstLines.Contains("RELATÓRIO"))
                return "Relatório";
            
            return "Documento";
        }
        
        private List<DocumentBoundary> ValidateAndAdjust(List<DocumentBoundary> documents, int totalPages)
        {
            var validated = new List<DocumentBoundary>();
            
            if (config.Verbose)
            {
                Console.WriteLine($"[DEBUG] ValidateAndAdjust: Processing {documents.Count} documents");
                Console.WriteLine($"[DEBUG] MinDocumentPages={config.MinDocumentPages}, MinConfidenceScore={config.MinConfidenceScore}");
            }
            
            foreach (var doc in documents)
            {
                // Validar limites
                if (doc.StartPage < 1) doc.StartPage = 1;
                if (doc.EndPage > totalPages) doc.EndPage = totalPages;
                if (doc.StartPage > doc.EndPage) continue;
                
                if (config.Verbose)
                {
                    Console.WriteLine($"[DEBUG] Document {doc.Number}: Pages {doc.StartPage}-{doc.EndPage} ({doc.PageCount}), Confidence={doc.Confidence:F2}");
                }
                
                // Aplicar filtros de configuração
                if (doc.PageCount >= config.MinDocumentPages &&
                    doc.Confidence >= config.MinConfidenceScore)
                {
                    validated.Add(doc);
                    if (config.Verbose)
                    {
                        Console.WriteLine($"[DEBUG] Document {doc.Number} PASSED validation");
                    }
                }
                else
                {
                    if (config.Verbose)
                    {
                        Console.WriteLine($"[DEBUG] Document {doc.Number} FAILED validation");
                    }
                }
            }
            
            if (config.Verbose)
            {
                Console.WriteLine($"[DEBUG] ValidateAndAdjust: {validated.Count} documents passed validation");
            }
            
            return validated.OrderBy(d => d.StartPage).ToList();
        }
        
        private string GetFirstSignificantLines(string text, int lineCount)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            
            var lines = text.Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Take(lineCount);
            
            return string.Join("\n", lines);
        }
        
        private string GetLastSignificantLines(string text, int lineCount)
        {
            if (string.IsNullOrEmpty(text))
                return "";
            
            var lines = text.Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Reverse()
                .Take(lineCount)
                .Reverse();
            
            return string.Join("\n", lines);
        }
        
        /// <summary>
        /// Detecta texto na margem superior (10%) da página
        /// </summary>
        private double DetectTopMarginText(PageAnalysis page)
        {
            if (page.TextInfo == null || string.IsNullOrEmpty(page.TextInfo.PageText))
                return 0;
            
            if (page.Size == null)
                return 0;
            
            // Calcular área da margem superior (10% da altura da página)
            double topMarginHeight = page.Size.Height * config.TopMarginPercentage;
            
            // Verificar se há texto significativo na margem superior
            // Como não temos coordenadas específicas, vamos usar as primeiras linhas como aproximação
            var firstLines = GetFirstSignificantLines(page.TextInfo.PageText, 3);
            
            if (string.IsNullOrEmpty(firstLines))
                return 0;
            
            // Verificar se contém padrões de cabeçalho de documento
            double score = 0;
            string upperText = firstLines.ToUpper();
            
            // Bonus por texto em maiúscula no topo
            if (upperText.Length > 0)
            {
                var uppercaseRatio = upperText.Count(char.IsUpper) / (double)upperText.Length;
                if (uppercaseRatio > 0.7) // 70% em maiúscula
                {
                    score += 0.6;
                }
            }
            
            // Bonus por padrões específicos na margem superior
            foreach (var pattern in config.StartPatterns)
            {
                if (Regex.IsMatch(firstLines, pattern, RegexOptions.IgnoreCase))
                {
                    score += 0.4;
                    break;
                }
            }
            
            if (config.Verbose && score > 0)
            {
                Console.WriteLine($"[DEBUG] Top margin text detected on page {page.PageNumber}: score={score:F2}");
                Console.WriteLine($"[DEBUG] First lines: {firstLines.Substring(0, Math.Min(100, firstLines.Length))}...");
            }
            
            return Math.Min(score, 1.0);
        }
        
        /// <summary>
        /// Detecta cabeçalhos em maiúsculas que indicam início de documento
        /// </summary>
        private double DetectUppercaseHeaders(PageAnalysis page)
        {
            if (page.TextInfo == null || string.IsNullOrEmpty(page.TextInfo.PageText))
                return 0;
            
            // Analisar primeiras linhas significativas
            var firstLines = GetFirstSignificantLines(page.TextInfo.PageText, 5);
            
            if (string.IsNullOrEmpty(firstLines))
                return 0;
            
            var lines = firstLines.Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Take(5)
                .ToArray();
            
            double score = 0;
            int uppercaseHeaderCount = 0;
            
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                
                // Verificar se a linha tem comprimento mínimo
                if (trimmedLine.Length < config.UppercaseTextMinLength)
                    continue;
                
                // Calcular proporção de maiúsculas
                var uppercaseCount = trimmedLine.Count(char.IsUpper);
                var letterCount = trimmedLine.Count(char.IsLetter);
                
                if (letterCount > 0)
                {
                    var uppercaseRatio = uppercaseCount / (double)letterCount;
                    
                    // Linha majoritariamente em maiúscula
                    if (uppercaseRatio > 0.8)
                    {
                        uppercaseHeaderCount++;
                        score += 0.3;
                        
                        // Bonus extra se contém palavras-chave importantes
                        if (trimmedLine.Contains("TRIBUNAL") || 
                            trimmedLine.Contains("PODER") || 
                            trimmedLine.Contains("PROCESSO") ||
                            trimmedLine.Contains("MINISTÉRIO") ||
                            trimmedLine.Contains("DEFENSORIA"))
                        {
                            score += 0.2;
                        }
                    }
                }
            }
            
            // Bonus por múltiplos cabeçalhos em maiúscula
            if (uppercaseHeaderCount >= 2)
            {
                score += 0.2;
            }
            
            if (config.Verbose && score > 0)
            {
                Console.WriteLine($"[DEBUG] Uppercase headers detected on page {page.PageNumber}: score={score:F2}, headers={uppercaseHeaderCount}");
            }
            
            return Math.Min(score, 1.0);
        }
        
        /// <summary>
        /// Valida se todas as páginas de um documento têm o mesmo tamanho de papel
        /// </summary>
        private bool ValidateSamePaperSize(DocumentBoundary document, PDFAnalysisResult analysis)
        {
            if (!config.RequireSamePaperSize)
                return true;
            
            if (document.PageCount <= 1)
                return true;
            
            // Obter tamanho da primeira página como referência
            var firstPageIndex = document.StartPage - 1;
            if (firstPageIndex >= analysis.Pages.Count)
                return false;
            
            var referencePage = analysis.Pages[firstPageIndex];
            if (referencePage.Size == null)
                return false;
            
            // Verificar se todas as páginas têm o mesmo tamanho
            for (int pageNum = document.StartPage; pageNum <= document.EndPage; pageNum++)
            {
                var pageIndex = pageNum - 1;
                if (pageIndex >= analysis.Pages.Count)
                    return false;
                
                var currentPage = analysis.Pages[pageIndex];
                if (currentPage.Size == null)
                    return false;
                
                // Verificar se o tamanho é igual (com tolerância de 10 pontos)
                bool sameSize = Math.Abs(currentPage.Size.Width - referencePage.Size.Width) <= 10 &&
                               Math.Abs(currentPage.Size.Height - referencePage.Size.Height) <= 10;
                
                if (!sameSize)
                {
                    if (config.Verbose)
                    {
                        Console.WriteLine($"[DEBUG] Document {document.StartPage}-{document.EndPage} rejected: different paper sizes");
                        Console.WriteLine($"[DEBUG] Reference page {document.StartPage}: {referencePage.Size.Width}x{referencePage.Size.Height}");
                        Console.WriteLine($"[DEBUG] Current page {pageNum}: {currentPage.Size.Width}x{currentPage.Size.Height}");
                    }
                    return false;
                }
            }
            
            return true;
        }
    }
}