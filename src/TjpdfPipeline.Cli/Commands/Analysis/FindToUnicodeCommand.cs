using System;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Lista as fontes que possuem ToUnicode e as que não possuem.
    /// Uso: tjpdf-cli pdf-unicode list --input file.pdf
    /// </summary>
    public class FindToUnicodeCommand : Command
    {
        public override string Name => "find-to-unicode";
        public override string Description => "Lista fontes com/sem ToUnicode (iText7)";

        public override void Execute(string[] args)
        {
            if (!ParseCommonOptions(args, out var inputFile, out var opts)) return;
            if (string.IsNullOrWhiteSpace(inputFile)) { Console.WriteLine("Informe --input <file.pdf>"); return; }

            using var doc = new PdfDocument(new PdfReader(inputFile));
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var fonts = page.GetResources()?.GetResource(PdfName.Font) as PdfDictionary;
                if (fonts == null) continue;
                Console.WriteLine($"Página {p}:");
                foreach (var k in fonts.KeySet())
                {
                    var fdict = fonts.GetAsDictionary(k);
                    var has = fdict?.GetAsStream(PdfName.ToUnicode) != null;
                    Console.WriteLine($"  {k}: {(has ? "ToUnicode" : "sem ToUnicode")}");
                }
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pdf-unicode list --input file.pdf");
        }
    }
}
