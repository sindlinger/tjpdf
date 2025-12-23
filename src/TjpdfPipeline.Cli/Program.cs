using System;
using System.Linq;
using FilterPDF.Commands;

namespace TjpdfPipeline.Cli
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                ShowHelp();
                return 1;
            }

            var cmdName = args[0];
            var cmdArgs = args.Skip(1).ToArray();

            if (cmdName.Equals("pipeline-tjpb", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new PipelineTjpbCommand();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("load", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new FpdfLoadCommand();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("pdf-objects", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new PdfObjectsCommand();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("pdf-info", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new PdfInfoCommand();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("pdf-streams", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new PdfStreamsCommand();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("pdf-unicode", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new PdfUnicodeCommand();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("show-moddate", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new ShowModDateCommand();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("footer", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new FooterCommand();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("preprocess-inputs", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new PreprocessInputsCommand();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("tjpb-s1", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new TjpbStage1Command();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("tjpb-s2", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new TjpbStage2Command();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("tjpb-s3", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new TjpbStage3Command();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("fetch-bookmark-titles", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new FetchBookmarkTitlesCommand();
                cmd.Execute(cmdArgs);
                return 0;
            }
            if (cmdName.Equals("bookmark-paragraphs", StringComparison.OrdinalIgnoreCase))
            {
                var cmd = new BookmarkParagraphsCommand();
                cmd.Execute(cmdArgs);
                return 0;
            }
            ShowHelp();
            return 1;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pipeline-tjpb --input-dir <dir> [--config <file>] [--only-despachos] [--signer-contains <texto>]");
            Console.WriteLine("tjpdf-cli load --input-dir <dir> [subcmds: ultra|text|custom|images-only|base64-only]");
            Console.WriteLine("tjpdf-cli pdf-objects list --input file.pdf [--limit N]");
            Console.WriteLine("tjpdf-cli pdf-objects analyze --input file.pdf [--limit N]");
            Console.WriteLine("tjpdf-cli pdf-objects deep --input file.pdf [--limit N]");
            Console.WriteLine("tjpdf-cli pdf-info page-boxes --input file.pdf");
            Console.WriteLine("tjpdf-cli pdf-info bookmark-tree --input file.pdf");
            Console.WriteLine("tjpdf-cli pdf-streams list --input file.pdf [--limit N]");
            Console.WriteLine("tjpdf-cli pdf-streams show --input file.pdf --id N");
            Console.WriteLine("tjpdf-cli show-moddate --input file.pdf");
            Console.WriteLine("tjpdf-cli footer --input file.pdf [--page N] [--all] [--tail-lines N] [--json]");
            Console.WriteLine("tjpdf-cli preprocess-inputs --input-dir <dir> [--out-dir <dir>] [--max N] [--per-process-dir] [--flat]");
            Console.WriteLine("tjpdf-cli tjpb-s1 --input-dir <dir> [--out-dir <dir>] [--max N] [--print-json]");
            Console.WriteLine("tjpdf-cli tjpb-s2 --input-dir <dir> [--out <file>] [--print-json]");
            Console.WriteLine("tjpdf-cli tjpb-s3 [--input-dir <dir>] [--input-manifest <file>] [--out-dir <dir>] [--max N] [--print-summary]");
            Console.WriteLine("tjpdf-cli fetch-bookmark-titles --input-file <pdf> [--json] [--tree]");
            Console.WriteLine("tjpdf-cli bookmark-paragraphs --input-file <pdf> [--bookmark <texto>] [--all] [--first] [--last] [--json]");
            Console.WriteLine("tjpdf-cli pdf-unicode list --input file.pdf");
            Console.WriteLine("tjpdf-cli pdf-unicode dump --input file.pdf");
        }
    }
}
