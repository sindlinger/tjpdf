using System;
using iText.Kernel.Pdf;

namespace FilterPDF.Commands
{
    /// <summary>
    /// Inspeção detalhada de um stream específico por id.
    /// Uso: tjpdf-cli pdf-streams show --input file.pdf --id N
    /// </summary>
    public class InspectStreamsCommand : Command
    {
        public override string Name => "inspect-stream";
        public override string Description => "Mostra subtipo e bytes hex de um stream por id";

        public override void Execute(string[] args)
        {
            if (!ParseCommonOptions(args, out var inputFile, out var opts)) return;
            if (string.IsNullOrWhiteSpace(inputFile) || !opts.ContainsKey("--id"))
            {
                ShowHelp();
                return;
            }
            if (!int.TryParse(opts["--id"], out int id))
            {
                Console.WriteLine("--id inválido");
                return;
            }

            using var doc = new PdfDocument(new PdfReader(inputFile));
            var obj = doc.GetPdfObject(id) as PdfStream;
            if (obj == null)
            {
                Console.WriteLine($"Objeto {id} não é stream");
                return;
            }
            var subtype = obj.GetAsName(PdfName.Subtype)?.ToString() ?? "";
            Console.WriteLine($"Stream {id} subtype={subtype} len={obj.GetLength()}");
            var bytes = obj.GetBytes();
            Console.WriteLine(BitConverter.ToString(bytes).Replace("-", " "));
        }

        public override void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pdf-streams show --input file.pdf --id N");
        }
    }
}
