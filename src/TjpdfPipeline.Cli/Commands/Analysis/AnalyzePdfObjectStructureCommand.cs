using System;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Lista objetos com suas chaves (Type/Subtype/Length) para inspeção rápida.
    /// Uso: tjpdf-cli pdf-objects analyze --input file.pdf [--limit N]
    /// </summary>
    public class AnalyzePdfObjectStructureCommand : Command
    {
        public override string Name => "analyze-objects";
        public override string Description => "Analisa objetos mostrando Type/Subtype/Length";

        public override void Execute(string[] args)
        {
            if (!ParseCommonOptions(args, out var inputFile, out var opts)) return;
            if (string.IsNullOrWhiteSpace(inputFile)) { Console.WriteLine("Informe --input <file.pdf>"); return; }
            int limit = opts.TryGetValue("--limit", out var l) && int.TryParse(l, out var n) ? n : int.MaxValue;

            using var doc = new PdfDocument(new PdfReader(inputFile));
            int max = doc.GetNumberOfPdfObjects();
            int count = 0;
            for (int i = 0; i < max && count < limit; i++)
            {
                var obj = doc.GetPdfObject(i);
                if (obj == null || obj.IsNull()) continue;
                string type = obj.GetType().Name;
                string tp = "";
                string st = "";
                long len = 0;
                if (obj is PdfDictionary dict)
                {
                    tp = dict.GetAsName(PdfName.Type)?.ToString() ?? "";
                    st = dict.GetAsName(PdfName.Subtype)?.ToString() ?? "";
                    if (dict is PdfStream stream) len = stream.GetLength();
                }
                else if (obj is PdfStream stream)
                {
                    st = stream.GetAsName(PdfName.Subtype)?.ToString() ?? "";
                    len = stream.GetLength();
                }
                Console.WriteLine($"{i}: {type} Type={tp} Subtype={st} Len={len}");
                count++;
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pdf-objects analyze --input file.pdf [--limit N]");
        }
    }
}
