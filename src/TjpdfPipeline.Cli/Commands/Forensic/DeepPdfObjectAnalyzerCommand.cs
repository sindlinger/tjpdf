using System;
using System.Collections.Generic;
using System.Linq;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Inspeção profunda de objetos: para cada objeto do PDF, mostra tipo, subtipo, tamanho e referências básicas.
    /// Uso: tjpdf-cli pdf-objects deep --input file.pdf [--limit N]
    /// </summary>
    public class DeepPdfObjectAnalyzerCommand : Command
    {
        public override string Name => "deep-objects";
        public override string Description => "Inspeção profunda de objetos do PDF (iText7)";

        public override void Execute(string[] args)
        {
            if (!ParseCommonOptions(args, out var inputFile, out var opts))
                return;
            if (string.IsNullOrWhiteSpace(inputFile))
            {
                Console.WriteLine("Informe --input <file.pdf>");
                return;
            }
            int limit = opts.TryGetValue("--limit", out var l) && int.TryParse(l, out var n) ? n : int.MaxValue;

            using var doc = new PdfDocument(new PdfReader(inputFile));
            int max = doc.GetNumberOfPdfObjects();
            int count = 0;
            for (int i = 0; i < max && count < limit; i++)
            {
                var obj = doc.GetPdfObject(i);
                if (obj == null || obj.IsNull()) continue;
                string type = obj.GetType().Name;
                string subtype = "";
                long length = 0;
                if (obj is PdfStream stream)
                {
                    subtype = stream.GetAsName(PdfName.Subtype)?.ToString() ?? "";
                    length = stream.GetLength();
                }
                else if (obj is PdfDictionary dict)
                {
                    var tp = dict.GetAsName(PdfName.Type);
                    if (tp != null) subtype = tp.ToString();
                    var st = dict.GetAsName(PdfName.Subtype);
                    if (st != null) subtype = string.IsNullOrEmpty(subtype) ? st.ToString() : $"{subtype}/{st}";
                }

                Console.WriteLine($"{i}: {type} {subtype} len={length}");
                count++;
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pdf-objects deep --input file.pdf [--limit N]");
            Console.WriteLine("Lists objects with type/subtype and stream size.");
        }
    }
}
