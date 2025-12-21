using System;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Lista streams do PDF com id, subtipo e tamanho (para diagnóstico de imagens/forms/javscript).
    /// Uso: tjpdf-cli pdf-streams list --input file.pdf [--limit N]
    /// </summary>
    public class VisualizeStreamsCommand : Command
    {
        public override string Name => "streams";
        public override string Description => "Lista streams com subtipo e tamanho (iText7)";

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
                if (obj is PdfStream stream)
                {
                    var subtype = stream.GetAsName(PdfName.Subtype)?.ToString() ?? "";
                    long len = stream.GetLength();
                    Console.WriteLine($"{i}: Stream {subtype} len={len}");
                    count++;
                }
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pdf-streams list --input file.pdf [--limit N]");
            Console.WriteLine("Lists streams with subtype/size.");
        }
    }
}
