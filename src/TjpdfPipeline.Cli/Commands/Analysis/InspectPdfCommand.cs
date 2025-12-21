using System;
using System.Collections.Generic;
using System.Linq;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Lista objetos do PDF (id, tipo, subtipo) para diagnóstico rápido.
    /// Uso: tjpdf-cli pdf-objects list --input file.pdf [--limit N]
    /// </summary>
    public class InspectPdfCommand : Command
    {
        public override string Name => "inspect";
        public override string Description => "Lista objetos (id, tipo, subtipo) do PDF (iText7)";

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
                if (obj is PdfDictionary dict)
                {
                    var tp = dict.GetAsName(PdfName.Type);
                    if (tp != null) subtype = tp.ToString();
                    var st = dict.GetAsName(PdfName.Subtype);
                    if (st != null) subtype = $"{subtype}/{st}";
                }
                Console.WriteLine($"{i}: {type} {subtype}".Trim());
                count++;
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pdf-objects list --input file.pdf [--limit N]");
            Console.WriteLine("Lists object ids, type, and subtype (quick diagnostic).");
        }
    }
}
