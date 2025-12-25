using System;
using System.Collections.Generic;

namespace FilterPDF
{
    /// <summary>
    /// Configuração para segmentação de documentos
    /// </summary>
    public class DocumentSegmentationConfig
    {
        // Estratégias de detecção
        public bool DetectByPatterns { get; set; } = true;
        public bool DetectBySignatures { get; set; } = true;
        public bool DetectByDensity { get; set; } = true;
        public bool DetectByStructureReset { get; set; } = true;
        public bool DetectByNeighborhood { get; set; } = true;
        public bool DetectByFontChanges { get; set; } = true;
        public bool DetectByPageSize { get; set; } = true;
        public bool DetectByImageSignatures { get; set; } = true;
        public bool DetectByTopMarginText { get; set; } = true;
        public bool DetectByUppercaseHeaders { get; set; } = true;
        
        // Configurações de agrupamento
        public bool GroupAdjacentPages { get; set; } = true;
        public int MaxGroupDistance { get; set; } = 3; // Máximo de páginas entre início e fim
        public bool MergeOrphanPages { get; set; } = true; // Anexar páginas soltas ao documento anterior
        public bool RequireSamePaperSize { get; set; } = true; // Exigir mesmo tamanho de papel para documentos
        public bool RequireContiguousPages { get; set; } = true; // Exigir páginas contíguas
        
        // Thresholds
        public double MinConfidenceScore { get; set; } = 0.5;
        public int MinDocumentPages { get; set; } = 1;
        public double FontChangeSimilarityThreshold { get; set; } = 0.7;
        public double DensityChangeThreshold { get; set; } = 0.6;
        public double TopMarginPercentage { get; set; } = 0.1; // 10% da altura da página
        public double UppercaseTextMinLength { get; set; } = 5; // Mínimo de caracteres em maiúscula
        
        // Padrões de detecção (português)
        public List<string> StartPatterns { get; set; } = new List<string>
        {
            "PODER JUDICIÁRIO",
            "TRIBUNAL DE JUSTIÇA",
            "MINISTÉRIO PÚBLICO",
            "DEFENSORIA PÚBLICA",
            "PROCURADORIA",
            "Processo n[º°]",
            "Autos n[º°]",
            "CERTIDÃO",
            "DESPACHO",
            "SENTENÇA",
            "DECISÃO",
            "OFÍCIO",
            "ATA DE"
        };
        
        public List<string> EndPatterns { get; set; } = new List<string>
        {
            "Documento assinado eletronicamente",
            "assinado digitalmente",
            "código verificador",
            "autenticidade.*conferida",
            "Atenciosamente",
            "Cordialmente",
            "dou fé",
            "nada mais"
        };
        
        // Configurações de saída
        public bool Verbose { get; set; } = false;
        public string OutputFormat { get; set; } = "summary"; // summary, json, detailed
    }
    
    /// <summary>
    /// Resultado da segmentação
    /// </summary>
    public class DocumentBoundary
    {
        public int Number { get; set; }
        public int StartPage { get; set; }
        public int EndPage { get; set; }
        public int PageCount => EndPage - StartPage + 1;
        public double Confidence { get; set; }
        public string DetectedType { get; set; } = "";
        public string Title { get; set; } = "";
        public string RawTitle { get; set; } = "";
        public List<string> StartIndicators { get; set; } = new List<string>();
        public List<string> EndIndicators { get; set; } = new List<string>();
        public string FirstPageText { get; set; } = ""; // TEXTO COMPLETO da primeira página
        public string LastPageText { get; set; } = "";  // TEXTO COMPLETO da última página
        public string FullText { get; set; } = "";       // TEXTO COMPLETO de todas as páginas do documento
        
        // Características do documento
        public HashSet<string> Fonts { get; set; } = new HashSet<string>();
        public string PageSize { get; set; } = "";
        public bool HasSignatureImage { get; set; }
        public int TotalWords { get; set; }
        public double AverageWordsPerPage => PageCount > 0 ? (double)TotalWords / PageCount : 0;
    }
    
    /// <summary>
    /// Score de detecção para uma página
    /// </summary>
    public class BoundaryScore
    {
        public int PageNumber { get; set; }
        public double StartScore { get; set; }
        public double EndScore { get; set; }
        public double NeighborhoodScore { get; set; }
        public Dictionary<string, double> FeatureScores { get; set; } = new Dictionary<string, double>();
        
        public bool IsLikelyStart => StartScore > 0.6;
        public bool IsLikelyEnd => EndScore > 0.4;
        public bool IsLikelyBoundary => Math.Max(StartScore, EndScore) > 0.5;
    }
}
