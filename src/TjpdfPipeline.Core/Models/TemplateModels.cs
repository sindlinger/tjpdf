using System.Collections.Generic;

namespace FilterPDF.Models
{
    public class TemplateRegion
    {
        public string Name { get; set; } = "";
        public int? Page { get; set; } = null; // 1-based; null = any page
        public float X0 { get; set; }
        public float Y0 { get; set; }
        public float X1 { get; set; }
        public float Y1 { get; set; }
        public float Tolerance { get; set; } = 0.0f; // optional padding (normalized units)
    }

    public class TemplateExtractionResult
    {
        public string File { get; set; } = "";
        public List<TemplateFieldValue> Fields { get; set; } = new List<TemplateFieldValue>();
    }

    public class TemplateFieldValue
    {
        public string Name { get; set; } = "";
        public int Page { get; set; }
        public string Value { get; set; } = "";
        public int WordCount { get; set; }
    }
}
