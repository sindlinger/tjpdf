using System;
using System.Collections.Generic;
using System.Linq;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Function;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Extrai mapas ToUnicode das fontes do PDF.
    /// Uso: tjpdf-cli pdf-unicode dump --input file.pdf
    /// </summary>
    public class ToUnicodeExtractCommand : Command
    {
        public override string Name => "to-unicode";
        public override string Description => "Extrai ToUnicode das fontes (iText7)";

        public override void Execute(string[] args)
        {
            if (!ParseCommonOptions(args, out var inputFile, out var opts))
                return;
            if (string.IsNullOrWhiteSpace(inputFile))
            {
                Console.WriteLine("Informe --input <file.pdf>");
                return;
            }

            using var doc = new PdfDocument(new PdfReader(inputFile));
            for (int p = 1; p <= doc.GetNumberOfPages(); p++)
            {
                var page = doc.GetPage(p);
                var resources = page.GetResources();
                var fonts = resources?.GetResource(PdfName.Font) as PdfDictionary;
                if (fonts == null) continue;
                Console.WriteLine($"Página {p}:");
                foreach (var key in fonts.KeySet())
                {
                    var fontDict = fonts.GetAsDictionary(key);
                    if (fontDict == null) continue;
                    var toUnicode = fontDict.GetAsStream(PdfName.ToUnicode);
                    Console.WriteLine($"  Fonte {key}: {(toUnicode != null ? "ToUnicode presente" : "(sem ToUnicode)")}");
                    if (toUnicode != null)
                    {
                        var bytes = toUnicode.GetBytes();
                        string cmap = System.Text.Encoding.UTF8.GetString(bytes);
                        Console.WriteLine(cmap);
                    }
                }
            }
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pdf-unicode dump --input file.pdf");
            Console.WriteLine("Dumps ToUnicode maps for all fonts.");
        }
    }
}
