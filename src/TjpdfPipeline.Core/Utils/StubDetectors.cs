using System.Collections.Generic;

namespace FilterPDF.Utils
{
    public class LaudoDetectionResult
    {
        public bool IsLaudo { get; set; }
        public string? Especie { get; set; }
        public string? Perito { get; set; }
        public string? Cpf { get; set; }
        public string? Especialidade { get; set; }
        public string Hash { get; set; } = "";
        public double Score { get; set; }
    }

    public static class LaudoDetector
    {
        public static LaudoDetectionResult Detect(string docLabel, List<string> pagesText, string header, string footer, string fullText)
        {
            return new LaudoDetectionResult
            {
                IsLaudo = false,
                Especie = "",
                Perito = "",
                Cpf = "",
                Especialidade = "",
                Hash = "",
                Score = 0.0
            };
        }
    }

    public static class DocIdClassifier
    {
        public static void LoadHashes(string path)
        {
            // stub
        }
    }
}
