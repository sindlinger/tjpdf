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

            ShowHelp();
            return 1;
        }

        private static void ShowHelp()
        {
            Console.WriteLine("tjpdf-cli pipeline-tjpb --input-dir <dir> [--config <file>] [--only-despachos] [--signer-contains <texto>]");
        }
    }
}
