using System;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Mostra a data de modificação (ModDate) do PDF.
    /// Uso: fpdf show-moddate --input file.pdf
    /// </summary>
    public class ShowModDateCommand : Command
    {
        public override string Name => "show-moddate";
        public override string Description => "Exibe ModDate do PDF (iText7)";

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
            var info = doc.GetDocumentInfo();
            var mod = info.GetMoreInfo(PdfName.ModDate.GetValue());
            var creation = info.GetMoreInfo(PdfName.CreationDate.GetValue());
            Console.WriteLine($"CreationDate: {creation}");
            Console.WriteLine($"ModDate: {mod}");
        }

        public override void ShowHelp()
        {
            Console.WriteLine("fpdf show-moddate --input file.pdf");
        }
    }
}
