using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;
using FilterPDF;
using FilterPDF.Commands;
using FilterPDF.Configuration;
using FilterPDF.Utils;
using System.Text.RegularExpressions;


namespace FilterPDF.Commands
{
    /// <summary>
    /// Load command - Pre-process PDFs into cached JSON for fast access
    /// </summary>
    public class FpdfLoadCommand : Command
    {
        public override string Name => "load";
        public override string Description => "Pre-process PDF files into cached JSON for ultra-fast operations";
        
        // Simplified help for subcommands (legacy detail removed)
        private void ShowSubcommandHelp(string subcmd) => ShowHelp();

        private class LoadOptions
        {
            public bool ExtractRaw { get; set; } = true;  // salva sempre o raw (camada hard)
            public bool ExtractText { get; set; } = false;
            public bool ExtractUltra { get; set; } = true; // padrão: coleta completa (camada soft)
            public bool ExtractCustom { get; set; } = false;
            public int TextStrategy { get; set; } = 2; // Default: advanced
            public string OutputPath { get; set; } = "";
            public string OutputDir { get; set; } = "";
            public int MaxFiles { get; set; } = int.MaxValue; // Limita quantos PDFs processar
            public int NumWorkers { get; set; } = 1;
            public bool Overwrite { get; set; } = false;
            public bool Verbose { get; set; } = false;
            public bool ShowProgress { get; set; } = true;
            public string OutputFormat { get; set; } = "json";
            // FileList removed - no longer supporting file lists
            
            // Custom mode options
            public bool CustomIncludeText { get; set; } = true;
            public bool CustomIncludeObjects { get; set; } = false;
            public bool CustomIncludeStreams { get; set; } = false;
            public bool CustomIncludeFonts { get; set; } = true;
            public bool CustomIncludeImages { get; set; } = true;
            public bool CustomIncludeImageData { get; set; } = false; // Include base64 data
            public bool CustomIncludeMultimedia { get; set; } = true;
            public bool CustomIncludeEmbeddedFiles { get; set; } = true;
            public bool CustomIncludeJavaScript { get; set; } = true;
            public bool CustomIncludeForms { get; set; } = true;
            public bool CustomIncludeAnnotations { get; set; } = true;
            public bool CustomIncludeMetadata { get; set; } = true;
            public bool CustomIncludeBookmarks { get; set; } = true;
            public bool CustomIncludeHeaders { get; set; } = true;
            public bool CustomIncludeFooters { get; set; } = true;
            public int CustomMaxStreamSize { get; set; } = 10000; // 10KB max for stream content
        }
        
        public override void Execute(string[] args)
        {
            // Check for subcommand help first
            if (args.Length >= 3 && !args[0].StartsWith("-") && !args[1].StartsWith("-") && 
                (args[2] == "--help" || args[2] == "-h"))
            {
                string subcmd = args[1].ToLower();
                if (subcmd == "ultra" || subcmd == "text" || subcmd == "custom" || subcmd == "images-only" || subcmd == "base64-only")
                {
                    ShowSubcommandHelp(subcmd);
                    return;
                }
            }
            
            // Check for general --help
            if (args.Length > 0 && args.Any(a => a == "--help" || a == "-h"))
            {
                ShowHelp();
                return;
            }
            
            // Check if --input-dir or --input-file is used
            bool hasInputDir = false;
            bool hasInputFile = false;
            string? inputDirPath = null;
            string? inputFilePath = null;
            
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--input-dir" && i + 1 < args.Length)
                {
                    hasInputDir = true;
                    inputDirPath = args[i + 1];
                    break;
                }
                else if (args[i] == "--input-file" && i + 1 < args.Length)
                {
                    hasInputFile = true;
                    inputFilePath = args[i + 1];
                    break;
                }
            }
            
            string? inputFile = null;
            string? subcommand = null;
            int optionsStartIndex = 0;
            
            if (hasInputDir || hasInputFile)
            {
                // When using --input-dir or --input-file, we don't need a file argument
                // Set the input file from the flag
                if (hasInputFile)
                {
                    inputFile = inputFilePath;
                }
                
                // Check if first argument is a subcommand
                if (args.Length > 0 && !args[0].StartsWith("-"))
                {
                    subcommand = args[0].ToLower();
                    optionsStartIndex = 1;
                    
                    // Validate subcommand
                    switch (subcommand)
                    {
                        case "ultra":
                        case "text":
                        case "custom":
                        case "images-only":
                        case "base64-only":
                            // Valid subcommands
                            break;
                        default:
                            Console.Error.WriteLine($"Error: Unknown subcommand '{subcommand}'");
                            Console.Error.WriteLine("Valid subcommands: ultra, text, custom, images-only, base64-only");
                            Environment.Exit(1);
                            break;
                    }
                }
            }
            else
            {
                // Normal operation - first argument is file
                if (args.Length == 0)
                {
                    Console.Error.WriteLine("Error: No input file specified");
                    ShowHelp();
                    Environment.Exit(1);
                }
                
                inputFile = args[0];
                
                // Verify file/directory exists
                if (Directory.Exists(inputFile))
                {
                    Console.WriteLine($"Processing directory: {inputFile}");
                }
                else if (!File.Exists(inputFile))
                {
                    Console.Error.WriteLine($"Error: File or directory '{inputFile}' not found");
                    Environment.Exit(1);
                }
                
                // Check if second argument is a subcommand
                optionsStartIndex = 1;
                
                if (args.Length > 1 && !args[1].StartsWith("-"))
                {
                    subcommand = args[1].ToLower();
                    optionsStartIndex = 2;
                    
                    
                    // Validate subcommand
                    switch (subcommand)
                    {
                        case "ultra":
                        case "text":
                        case "custom":
                        case "images-only":
                        case "base64-only":
                            // Valid subcommands
                            break;
                        default:
                            Console.Error.WriteLine($"Error: Unknown subcommand '{subcommand}'");
                            Console.Error.WriteLine("Valid subcommands: ultra, text, custom, images-only, base64-only");
                            Environment.Exit(1);
                            break;
                    }
                }
            }
            
            // Parse options from correct index
            var options = ParseOptions(args.Skip(optionsStartIndex).ToArray(), out var additionalFiles);

            // Sem cache em disco: raw vai para Postgres (raw_processes) e parse vai para processes
            options.OutputDir = "";
            
            // When using --input-dir or --input-file, ignore other files
            if (hasInputDir || hasInputFile)
            {
                additionalFiles.Clear(); // Ignore any other files when using --input-dir or --input-file
            }
            
            // List of files to process
            var inputFiles = new List<string>();
            
            // Add files based on input method
            if (hasInputDir)
            {
                // Already processed in ParseOptions, files are in additionalFiles
                // But we need to get them from the inputDirPath
                if (Directory.Exists(inputDirPath))
                {
                    var searchOption = args.Contains("--recursive") || args.Contains("-r") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    var pdfsInDir = Directory.GetFiles(inputDirPath, "*.pdf", searchOption);
                    inputFiles.AddRange(pdfsInDir);
                    Console.WriteLine($"Found {pdfsInDir.Length} PDF files in directory");
                    if (options.MaxFiles != int.MaxValue && inputFiles.Count > options.MaxFiles)
                    {
                        inputFiles = inputFiles.Take(options.MaxFiles).ToList();
                        Console.WriteLine($"Limiting to first {inputFiles.Count} files (--max-files).");
                    }
                    if (searchOption == SearchOption.AllDirectories)
                    {
                        Console.WriteLine("   (recursive search enabled)");
                    }
                }
                else
                {
                    Console.Error.WriteLine($"Error: Directory '{inputDirPath}' not found");
                    Environment.Exit(1);
                }
            }
            else if (!string.IsNullOrEmpty(inputFile))
            {
                // Process single file or directory
                if (Directory.Exists(inputFile))
                {
                    var searchOption = args.Contains("--recursive") || args.Contains("-r") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    var pdfsInDir = Directory.GetFiles(inputFile, "*.pdf", searchOption);
                    inputFiles.AddRange(pdfsInDir);
                    Console.WriteLine($"Found {pdfsInDir.Length} PDF files in directory");
                    if (options.MaxFiles != int.MaxValue && inputFiles.Count > options.MaxFiles)
                    {
                        inputFiles = inputFiles.Take(options.MaxFiles).ToList();
                        Console.WriteLine($"Limiting to first {inputFiles.Count} files (--max-files).");
                    }
                    if (searchOption == SearchOption.AllDirectories)
                    {
                        Console.WriteLine("   (recursive search enabled)");
                    }
                }
                else
                {
                    inputFiles.Add(inputFile);
                }
                
                inputFiles.AddRange(additionalFiles);
            }
            
            // Aplicar configurações baseadas no subcomando
            if (!string.IsNullOrEmpty(subcommand))
            {
                ApplySubcommandSettings(subcommand, options);
            }
            else
            {
                // Sem subcomando - usar ultra por default (conforme documentação)
                Console.WriteLine("No subcommand specified, using 'ultra' mode (default)");
                ApplySubcommandSettings("ultra", options);
            }
            
            Console.WriteLine("[INFO] Cache local em tmp/cache (Postgres desativado)");

            Console.WriteLine($"Load: Pre-processing {inputFiles.Count} file(s)");
            Console.WriteLine($"   Options: {(options.ExtractRaw ? "RAW" : "")} {(options.ExtractText ? "TEXT" : "")} {(options.ExtractUltra ? "ULTRA" : "")}");
            Console.WriteLine($"   Workers: {options.NumWorkers}");
            Console.WriteLine();
            
            // Process files - always use ProcessFilesInBatches for proper error handling
            if (inputFiles.Count > 0)
            {
                ProcessFilesInBatches(inputFiles, options);
            }
        }
        
        private void ProcessFilesInBatches(List<string> files, LoadOptions options)
        {
            int totalFiles = files.Count;
            int batchSize = options.NumWorkers;
            
            // Create progress tracker
            var progressTracker = new ProgressTracker(totalFiles, options.ShowProgress);
            progressTracker.Start();
            
            // Suppress recursion warnings when using multiple workers to avoid console spam
            if (batchSize > 1)
            {
                ImageDataExtractor.SuppressRecursionWarnings = true;
            }
            
            // Process files in batches with proper resource management
            var allResults = new ConcurrentBag<(string file, bool success, bool skipped, string message, string cacheFile)>();
            
            // Use SemaphoreSlim to limit concurrent operations and prevent thread pool exhaustion
            using var semaphore = new SemaphoreSlim(batchSize, batchSize);
            
            // Force garbage collection periodically to prevent memory buildup
            int filesProcessed = 0;
            object gcLock = new object();
            
            // CancellationTokenSource for graceful shutdown if needed
            using var cancellationTokenSource = new CancellationTokenSource();
            
            Parallel.ForEach(files, new ParallelOptions 
            { 
                MaxDegreeOfParallelism = batchSize,
                CancellationToken = cancellationTokenSource.Token
            }, file =>
            {
                // Use semaphore to control concurrent access and prevent resource exhaustion
                semaphore.Wait(cancellationTokenSource.Token);
                try
                {
                    bool success = false;
                    bool skipped = false;
                    string message = "";
                    string cacheFile = "";
                    
                    try
                    {
                        // FIXED: Remove nested Task.Run that caused thread pool exhaustion
                        // Process directly in the current thread (already in parallel context)
                        var result = ProcessSingleFileWithResult(file, options);
                        success = result.success;
                        skipped = result.skipped;
                        message = result.message;
                        cacheFile = result.cacheFile;
                    }
                    catch (Exception ex)
                    {
                        success = false;
                        message = ex.Message;
                    }
                    
                    allResults.Add((file, success, skipped, message, cacheFile));
                    progressTracker.UpdateProgress(success, skipped, cacheFile);
                    
                    // Periodic garbage collection to prevent memory buildup
                    lock (gcLock)
                    {
                        filesProcessed++;
                        if (filesProcessed % 25 == 0) // More frequent GC for better memory management
                        {
                            GC.Collect(0, GCCollectionMode.Optimized);
                            if (filesProcessed % 100 == 0) // Full GC less frequently
                            {
                                GC.Collect();
                                GC.WaitForPendingFinalizers();
                                GC.Collect();
                            }
                        }
                    }
                }
                finally
                {
                    // Always release semaphore to prevent deadlock
                    semaphore.Release();
                }
            });
            
            // Re-enable warnings after batch processing
            ImageDataExtractor.SuppressRecursionWarnings = false;
            
            // Show final summary
            progressTracker.Finish();
            
            // Show failed files if any
            var failures = allResults.Where(r => !r.success && !r.skipped).ToList();
            if (failures.Any())
            {
                Console.WriteLine();
                
                // Group security failures to show only once
                var securityFailures = failures.Where(f => 
                    f.message.Contains("Access denied") || 
                    f.message.Contains("not in allowed paths") ||
                    f.message.Contains("failed security validation")
                ).ToList();
                var otherFailures = failures.Where(f => 
                    !f.message.Contains("Access denied") && 
                    !f.message.Contains("not in allowed paths") &&
                    !f.message.Contains("failed security validation")
                ).ToList();
                
                if (securityFailures.Any())
                {
                    Console.WriteLine(LanguageManager.GetMessage("security_files_blocked", securityFailures.Count));
                    Console.WriteLine();
                    Console.WriteLine(LanguageManager.GetMessage("security_to_fix"));
                    
                    // Get the actual directory from the first failed file
                    var firstFailedFile = securityFailures.First().file;
                    var actualDirectory = Path.GetDirectoryName(firstFailedFile) ?? "(unknown)";
                    
                    Console.WriteLine(LanguageManager.GetMessage("security_config_json"));
                    Console.WriteLine(LanguageManager.GetMessage("security_config_content", actualDirectory));
                    Console.WriteLine(LanguageManager.GetMessage("security_or_env", actualDirectory));
                    Console.WriteLine(LanguageManager.GetMessage("security_help_config"));
                    Console.WriteLine();
                }
                
                if (otherFailures.Any())
                {
                    Console.WriteLine($"⚠️  {otherFailures.Count} files failed to process:");
                    foreach (var (file, _, _, message, _) in otherFailures.Take(10))
                    {
                        Console.WriteLine($"   ✗ {Path.GetFileName(file)}: {message}");
                    }
                    if (otherFailures.Count > 10)
                    {
                        Console.WriteLine($"   ... and {otherFailures.Count - 10} more");
                    }
                }
            }
        }
        
        private void ApplySubcommandSettings(string subcommand, LoadOptions options)
        {
            switch (subcommand)
            {
                case "ultra":
                    Console.WriteLine("Using ULTRA mode: Full forensic analysis");
                    options.ExtractUltra = true;
                    options.ExtractRaw = false;
                    options.ExtractText = false;
                    options.ExtractCustom = false;
                    break;
                    
                case "text":
                    Console.WriteLine("Using TEXT mode: Fast text-only extraction");
                    options.ExtractText = true;
                    options.ExtractRaw = false;
                    options.ExtractUltra = false;
                    options.ExtractCustom = false;
                    break;
                    
                case "custom":
                    Console.WriteLine("Using CUSTOM mode: Balanced extraction");
                    options.ExtractCustom = true;
                    options.ExtractRaw = false;
                    options.ExtractText = false;
                    options.ExtractUltra = false;
                    
                    // Carregar configuração custom se existir
                    string configPath = Path.Combine(".cache", "custom_config.json");
                    if (File.Exists(configPath))
                    {
                        try
                        {
                            var configJson = File.ReadAllText(configPath);
                            var config = JsonConvert.DeserializeObject<Dictionary<string, object>>(configJson);
                            
                            if (config?.ContainsKey("no_multimedia") == true && (bool)config["no_multimedia"])
                                options.CustomIncludeMultimedia = false;
                            if (config?.ContainsKey("no_embedded_files") == true && (bool)config["no_embedded_files"])
                                options.CustomIncludeEmbeddedFiles = false;
                            if (config?.ContainsKey("workers") == true)
                                options.NumWorkers = Convert.ToInt32(config["workers"]);
                            if (config?.ContainsKey("include_objects") == true)
                                options.CustomIncludeObjects = (bool)config["include_objects"];
                            if (config?.ContainsKey("include_streams") == true)
                                options.CustomIncludeStreams = (bool)config["include_streams"];
                            if (config?.ContainsKey("show_progress") == true)
                                options.ShowProgress = (bool)config["show_progress"];
                            if (config?.ContainsKey("output_dir") == true && string.IsNullOrEmpty(options.OutputDir))
                                options.OutputDir = config["output_dir"]?.ToString() ?? "";
                            
                            Console.WriteLine($"  Loaded custom config from {configPath}");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"  Warning: Could not load custom config: {ex.Message}");
                        }
                    }
                    break;
                    
                case "images-only":
                    Console.WriteLine("Using IMAGES-ONLY mode: Extract only images and scanned pages");
                    options.ExtractCustom = true;
                    options.ExtractRaw = false;
                    options.ExtractText = false;
                    options.ExtractUltra = false;
                    // Configure for images only
                    options.CustomIncludeText = false;
                    options.CustomIncludeObjects = false;
                    options.CustomIncludeStreams = false;
                    options.CustomIncludeFonts = false;
                    options.CustomIncludeImages = true;
                    options.CustomIncludeImageData = true; // Include base64 data
                    options.CustomIncludeMultimedia = false;
                    options.CustomIncludeEmbeddedFiles = false;
                    options.CustomIncludeJavaScript = false;
                    options.CustomIncludeForms = false;
                    options.CustomIncludeAnnotations = false;
                    options.CustomIncludeMetadata = true; // Keep basic metadata
                    options.CustomIncludeBookmarks = false;
                    options.CustomIncludeHeaders = false;
                    options.CustomIncludeFooters = false;
                    break;
                    
                case "base64-only":
                    Console.WriteLine("Using BASE64-ONLY mode: Extract only base64 encoded content");
                    options.ExtractCustom = true;
                    options.ExtractRaw = false;
                    options.ExtractText = true; // Need text to find base64
                    options.ExtractUltra = false;
                    // Configure for base64 detection
                    options.CustomIncludeText = true; // Need text to search for base64
                    options.CustomIncludeObjects = false;
                    options.CustomIncludeStreams = true; // Base64 might be in streams
                    options.CustomIncludeFonts = false;
                    options.CustomIncludeImages = false;
                    options.CustomIncludeImageData = false;
                    options.CustomIncludeMultimedia = false;
                    options.CustomIncludeEmbeddedFiles = false;
                    options.CustomIncludeJavaScript = false;
                    options.CustomIncludeForms = false;
                    options.CustomIncludeAnnotations = false;
                    options.CustomIncludeMetadata = true; // Keep basic metadata
                    options.CustomIncludeBookmarks = false;
                    options.CustomIncludeHeaders = false;
                    options.CustomIncludeFooters = false;
                    break;
            }
        }
        
        private LoadOptions ParseOptions(string[] args, out List<string> inputFiles)
        {
            var options = new LoadOptions();
            
            // Track if user explicitly set workers
            bool userSpecifiedWorkers = false;
            
            inputFiles = new List<string>();
            
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                
                if (arg.StartsWith("-"))
                {
                    switch (arg)
                    {
                        case "--raw":
                            options.ExtractRaw = true;
                            options.ExtractText = false; // Only raw unless also specified
                            break;
                            
                        case "--text":
                        case "-t":
                            options.ExtractText = true;
                            options.ExtractRaw = false; // Only text unless also specified
                            break;
                            
                        case "--both":
                        case "-b":
                            options.ExtractRaw = true;
                            options.ExtractText = true;
                            break;
                            
                        case "--ultra":
                        case "-u":
                            options.ExtractUltra = true;
                            // Não setar Raw e Text para garantir que use ExtractUltraComplete
                            break;
                            
                        case "--output":
                        case "-o":
                            if (i + 1 < args.Length)
                            {
                                options.OutputPath = args[++i];
                            }
                            break;
                            
                        case "--output-dir":
                        case "-d":
                            if (i + 1 < args.Length)
                            {
                                options.OutputDir = args[++i];
                                if (!Directory.Exists(options.OutputDir))
                                {
                                    Directory.CreateDirectory(options.OutputDir);
                                }
                            }
                            break;

                        case "--max-files":
                            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var mf))
                            {
                                options.MaxFiles = Math.Max(1, mf);
                                i++;
                            }
                            break;

                        case "--input-dir":
                            if (i + 1 < args.Length)
                            {
                                // Skip the directory path - it's already handled in Execute
                                i++;
                            }
                            break;
                            
                        case "--input-file":
                            if (i + 1 < args.Length)
                            {
                                // Skip the file path - it's already handled in Execute
                                i++;
                            }
                            break;
                            
                        case "--num-workers":
                        case "--workers":
                        case "-w":
                            if (i + 1 < args.Length && int.TryParse(args[++i], out int workers))
                            {
                                options.NumWorkers = Math.Max(1, workers);
                                userSpecifiedWorkers = true;
                            }
                            break;
                            
                        case "--strategy":
                        case "-s":
                            if (i + 1 < args.Length && int.TryParse(args[++i], out int strategy))
                            {
                                options.TextStrategy = Math.Max(1, Math.Min(strategy, 3));
                            }
                            break;
                            
                        case "--overwrite":
                            options.Overwrite = true;
                            break;
                            
                        case "--verbose":
                        case "-v":
                            options.Verbose = true;
                            break;
                            
                        case "--format":
                        case "-F":
                            if (i + 1 < args.Length)
                            {
                                options.OutputFormat = args[++i].ToLower();
                                if (options.OutputFormat != "json")
                                {
                                    Console.Error.WriteLine($"Warning: Format '{options.OutputFormat}' not supported for load command. Using 'json'.");
                                    options.OutputFormat = "json";
                                }
                            }
                            break;
                            
                        case "--no-progress":
                            options.ShowProgress = false;
                            break;
                            
                        // Custom mode options
                        case "--no-text":
                            options.CustomIncludeText = false;
                            break;
                            
                        case "--include-objects":
                            options.CustomIncludeObjects = true;
                            break;
                            
                        case "--include-streams":
                            options.CustomIncludeStreams = true;
                            break;
                            
                        case "--no-fonts":
                            options.CustomIncludeFonts = false;
                            break;
                            
                        case "--no-images":
                            options.CustomIncludeImages = false;
                            break;
                            
                        case "--include-image-data":
                        case "--with-base64":
                            options.CustomIncludeImageData = true;
                            break;
                            
                        case "--no-multimedia":
                            options.CustomIncludeMultimedia = false;
                            break;
                            
                        case "--no-embedded-files":
                            options.CustomIncludeEmbeddedFiles = false;
                            break;
                            
                        case "--no-javascript":
                            options.CustomIncludeJavaScript = false;
                            break;
                            
                        case "--no-forms":
                            options.CustomIncludeForms = false;
                            break;
                            
                        case "--no-annotations":
                            options.CustomIncludeAnnotations = false;
                            break;
                            
                        case "--no-metadata":
                            options.CustomIncludeMetadata = false;
                            break;
                            
                        case "--no-bookmarks":
                            options.CustomIncludeBookmarks = false;
                            break;
                            
                        case "--max-stream-size":
                            if (i + 1 < args.Length && int.TryParse(args[++i], out int maxSize))
                            {
                                options.CustomMaxStreamSize = maxSize;
                            }
                            break;
                            
                        case "--recursive":
                        case "-r":
                            // Recursive flag - handled in directory processing logic
                            break;
                    }
                }
                else
                {
                    // Handle wildcards and file paths para arquivos adicionais
                    if (arg.Contains("*"))
                    {
                        var directory = Path.GetDirectoryName(arg) ?? ".";
                        var pattern = Path.GetFileName(arg);
                        var files = Directory.GetFiles(directory, pattern)
                            .Where(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));
                        inputFiles.AddRange(files);
                    }
                    else if (File.Exists(arg) && arg.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        inputFiles.Add(arg);
                    }
                    else if (Directory.Exists(arg))
                    {
                        var searchOption = args.Contains("--recursive") || args.Contains("-r") ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                        var files = Directory.GetFiles(arg, "*.pdf", searchOption);
                        inputFiles.AddRange(files);
                    }
                }
            }
            
            // Apply default configuration only if user didn't specify workers
            if (!userSpecifiedWorkers)
            {
                var config = FpdfConfig.Instance;
                if (config.Performance != null)
                {
                    // Use a more conservative default of 4 workers instead of config default
                    // This prevents system overload when user doesn't specify
                    options.NumWorkers = 4;
                }
            }
            
            return options;
        }
        
        
        private (bool success, bool skipped, string message, string cacheFile) ProcessSingleFileWithResult(string inputFile, LoadOptions options)
        {
            // FIXED: Removed nested Task.Run pattern that caused thread pool exhaustion
            // Process directly since we're already in a parallel context
            const int TIMEOUT_SECONDS = 120; // 2 minutes default timeout
            
            try
            {
                if (!inputFile.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, false, "Not a PDF file", "");
                }

                var cacheName = Path.GetFileNameWithoutExtension(inputFile);
                string cacheLocator = GetOutputPath(inputFile, options);

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(TIMEOUT_SECONDS));

                try
                {
                    ProcessSingleFile(inputFile, options, timeoutCts.Token);
                    return (true, false, "OK", cacheLocator);
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    return (false, false, $"Timeout after {TIMEOUT_SECONDS} seconds", "");
                }
            }
            catch (OperationCanceledException)
            {
                return (false, false, $"Processing cancelled (timeout: {TIMEOUT_SECONDS}s)", "");
            }
            catch (Exception ex)
            {
                return (false, false, ex.Message, "");
            }
        }
        
        private string GetOutputPath(string inputFile, LoadOptions options)
        {
            string baseName = DeriveProcessName(inputFile);

            // Sem cache em disco: só retornamos um nome lógico (não será gravado)
            return baseName + ".json";
        }

        private string DeriveProcessName(string inputFile)
        {
            var name = Path.GetFileNameWithoutExtension(inputFile);
            var m = Regex.Match(name, @"\d+");
            if (m.Success)
                return m.Value;
            // se não achar dígitos, use o nome limpo
            return name;
        }
        
        private void ProcessSingleFile(string inputFile, LoadOptions options, CancellationToken cancellationToken = default)
        {
            // Check for cancellation before starting
            cancellationToken.ThrowIfCancellationRequested();
            
            // Verificar se é um arquivo PDF
            if (!inputFile.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"O comando load só aceita arquivos PDF. Recebido: {inputFile}");
            }
            
            var cacheName = Path.GetFileNameWithoutExtension(inputFile);
            
            if (options.Verbose)
            {
                Console.WriteLine($"  Processing: {Path.GetFileName(inputFile)}");
                Console.WriteLine($"  Mode: {(options.ExtractUltra ? "ULTRA" : (options.ExtractCustom ? "CUSTOM" : (options.ExtractRaw ? "RAW" : "")))} {(options.ExtractText && !options.ExtractUltra ? "TEXT" : "")} {(!options.ExtractRaw && !options.ExtractText && !options.ExtractUltra && !options.ExtractCustom ? "BOTH" : "")}");
            }
            
            // Check for cancellation before heavy processing
            cancellationToken.ThrowIfCancellationRequested();
            
            // Generate cache based on extraction mode
            object cacheData;
            PDFAnalysisResult? analysisModel = null;
            
            // Unificamos: sempre usamos PDFAnalyzer (iText7) para gerar o cache.
            cancellationToken.ThrowIfCancellationRequested();
            var analyzer = new PDFAnalyzer(inputFile);
            cancellationToken.ThrowIfCancellationRequested();
            var analysis = analyzer.AnalyzeFull();
            analysisModel = analysis;

            var analysisDict = JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(analysis)) ?? new Dictionary<string, object>();
            analysisDict["version"] = "3.7.0";
            analysisDict["source"] = inputFile;
            analysisDict["created"] = DateTime.UtcNow;
            analysisDict["extractionMode"] = options.ExtractUltra ? "ultra" :
                                            options.ExtractCustom ? "custom" :
                                            options.ExtractRaw ? "raw" :
                                            options.ExtractText ? "text" : "both";
            analysisDict["textStrategy"] = options.TextStrategy;
            analysisDict["process"] = DeriveProcessName(inputFile);
            cacheData = analysisDict;
            
            // Check for cancellation before saving
            cancellationToken.ThrowIfCancellationRequested();
            
            // Serialize to JSON (will be stored in SQLite, not on disk)
            var settings = new JsonSerializerSettings
            {
                DateFormatHandling = DateFormatHandling.MicrosoftDateFormat,
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore
            };
            
            var json = JsonConvert.SerializeObject(cacheData, settings);
            string extractionMode = options.ExtractUltra ? "ultra" : 
                                   options.ExtractCustom ? "custom" :
                                   options.ExtractRaw && !options.ExtractText ? "raw" :
                                   options.ExtractText && !options.ExtractRaw ? "text" : "both";

            // Salvar bruto no Postgres (raw_processes). Sem arquivos em disco.
            try
            {
                var procName = DeriveProcessName(inputFile);
                // Salva o PDF bruto no Postgres (raw_files) para permitir reprocessamento sem arquivo local
                try
                {
                    var bytes = File.ReadAllBytes(inputFile);
                    FilterPDF.Utils.PgDocStore.UpsertRawFile(FilterPDF.Utils.PgDocStore.DefaultPgUri, procName, inputFile, bytes);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[WARN] Não foi possível salvar PDF bruto no Postgres: {ex.Message}");
                }
                FilterPDF.Utils.PgDocStore.UpsertRawProcess(FilterPDF.Utils.PgDocStore.DefaultPgUri, procName, inputFile, json);
                if (options.Verbose)
                    Console.WriteLine($"  Bruto salvo em raw_processes (processo {procName})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[WARN] Não foi possível salvar bruto no Postgres: {ex.Message}");
            }

            // Persistência em Postgres desativada
        }
        
        #if false // LEGACY ITEXT5 BLOCK (excluded from build)
        private Dictionary<string, object> ExtractRawDataComplete(string inputFile, LoadOptions options, CancellationToken cancellationToken = default)
        {
            if (options.Verbose) Console.WriteLine("  Extracting COMPLETE PDF structure...");
            
            // Check for cancellation before starting
            cancellationToken.ThrowIfCancellationRequested();
            
            var rawData = new Dictionary<string, object>
            {
                ["version"] = "2.12.0",
                ["source"] = inputFile,
                ["created"] = DateTime.UtcNow,
                ["extractionMode"] = "raw"
            };
            
            // Use PdfAccessManager for centralized access with using statement
            using (var reader = PdfAccessManager7.CreateTemporaryDocument(inputFile))
            {
                // Extract catalog
                var catalog = reader.Catalog;
                rawData["catalog"] = SerializePdfObject(catalog) ?? new object();
                
                // Extract info dictionary
                var info = reader.Info;
                rawData["info"] = (object?)info ?? new Dictionary<string, object>();
                
                // Extract trailer
                var trailer = reader.Trailer;
                rawData["trailer"] = SerializePdfObject(trailer) ?? new object();
                
                // Extract page tree with COMPLETE data
                var pages = new List<object>();
                for (int i = 1; i <= reader.NumberOfPages; i++)
                {
                    // Check for cancellation during page processing
                    cancellationToken.ThrowIfCancellationRequested();
                    var pageDict = reader.GetPageN(i);
                    var pageData = new Dictionary<string, object>
                    {
                        ["pageNumber"] = i,
                        ["dictionary"] = SerializePdfObject(pageDict) ?? new object(),
                        ["size"] = reader.GetPageSize(i),
                        ["rotation"] = reader.GetPageRotation(i),
                        ["mediaBox"] = pageDict.GetAsArray(PdfName.MEDIABOX) ?? new PdfArray(),
                        ["cropBox"] = pageDict.GetAsArray(PdfName.CROPBOX) ?? new PdfArray(),
                        ["resources"] = SerializePdfObject(pageDict.GetAsDict(PdfName.RESOURCES) ?? new PdfDictionary()) ?? new object(),
                        ["contents"] = ExtractPageContents(reader, i),
                        ["annotations"] = ExtractPageAnnotations(pageDict)
                    };
                    pages.Add(pageData);
                }
                rawData["pages"] = pages;
                
                // Extract ALL objects with COMPLETE details
                var objects = new List<object>();
                var xrefSize = reader.XrefSize;
                for (int i = 0; i < xrefSize; i++)
                {
                    // Check for cancellation during object processing (every 100 objects)
                    if (i % 100 == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    var obj = reader.GetPdfObjectRelease(i);
                    if (obj != null)
                    {
                        var objData = new Dictionary<string, object>
                        {
                            ["number"] = i,
                            ["generation"] = 0, // TODO: Extract real generation
                            ["type"] = obj.GetType().Name,
                            ["isDictionary"] = obj.IsDictionary(),
                            ["isArray"] = obj.IsArray(),
                            ["isStream"] = obj.IsStream(),
                            ["isString"] = obj.IsString(),
                            ["isNumber"] = obj.IsNumber(),
                            ["isIndirect"] = obj.IsIndirect(),
                            ["value"] = SerializePdfObject(obj) ?? "null"
                        };
                        objects.Add(objData);
                    }
                }
                rawData["objects"] = objects;
                
                // Extract advanced features
                rawData["hasXFA"] = reader.AcroFields.Xfa.XfaPresent;
                rawData["isEncrypted"] = reader.IsEncrypted();
                rawData["permissions"] = reader.Permissions;
                rawData["fileLength"] = reader.FileLength;
                rawData["pdfVersion"] = reader.PdfVersion;
                rawData["isRebuilt"] = reader.IsRebuilt();
                rawData["xrefSize"] = reader.XrefSize;
                
                // Extract bookmarks structure
                rawData["bookmarks"] = ExtractBookmarksRaw(reader) ?? new object();
                
                // Extract form fields
                rawData["acroForm"] = ExtractAcroFormRaw(reader) ?? new object();
                
                // Extract metadata
                rawData["metadata"] = ExtractMetadataRaw(reader) ?? new object();
            } // End of using block - reader is automatically disposed
            
            return rawData;
        }
        
        private Dictionary<string, object> ExtractTextDataComplete(string inputFile, LoadOptions options, CancellationToken cancellationToken = default)
        {
            if (options.Verbose) Console.WriteLine($"  Extracting text with strategy {options.TextStrategy}...");
            
            // Check for cancellation before starting
            cancellationToken.ThrowIfCancellationRequested();
            
            // Criar estrutura compatível com PDFAnalysisResult
            // Use PdfAccessManager for centralized access with using statement
            using (var reader = PdfAccessManager7.CreateTemporaryDocument(inputFile))
            {
            try
            {
                // Extrair informações básicas do PDF
                var fileInfo = new FileInfo(inputFile);
                var documentInfo = new Dictionary<string, object>
                {
                    ["TotalPages"] = reader.NumberOfPages,
                    ["IsEncrypted"] = reader.IsEncrypted(),
                    ["IsLinearized"] = false, // iTextSharp não expõe isso diretamente
                    ["HasAcroForm"] = reader.AcroForm != null,
                    ["HasXFA"] = reader.AcroFields.Xfa.XfaPresent,
                    ["FileStructure"] = reader.IsRebuilt() ? "Rebuilt" : "Original"
                };
                
                // Extrair metadata
                var info = reader.Info;
                var metadata = new Dictionary<string, object>
                {
                    ["Title"] = info.ContainsKey("Title") ? info["Title"] : "",
                    ["Author"] = info.ContainsKey("Author") ? info["Author"] : "",
                    ["Subject"] = info.ContainsKey("Subject") ? info["Subject"] : "",
                    ["Keywords"] = info.ContainsKey("Keywords") ? info["Keywords"] : "",
                    ["Creator"] = info.ContainsKey("Creator") ? info["Creator"] : "",
                    ["Producer"] = info.ContainsKey("Producer") ? info["Producer"] : "",
                    ["CreationDate"] = info.ContainsKey("CreationDate") ? info["CreationDate"] : "",
                    ["ModificationDate"] = info.ContainsKey("ModDate") ? info["ModDate"] : "",
                    ["PDFVersion"] = reader.PdfVersion.ToString(),
                    ["IsTagged"] = false // iTextSharp não tem IsTagged diretamente
                };
                
                // Extrair páginas com texto
                var pages = new List<object>();
                var extractedPages = ExtractUsingAdvancedMethod(inputFile, options.TextStrategy, cancellationToken);
                
                foreach (var extractedPage in extractedPages)
                {
                    // Check for cancellation during page processing
                    cancellationToken.ThrowIfCancellationRequested();
                    var pageSize = reader.GetPageSize(extractedPage.PageNumber);
                    var rotation = reader.GetPageRotation(extractedPage.PageNumber);
                    
                    // Extract images for this page
                    var pageImages = ExtractPageImages(reader, extractedPage.PageNumber, options.CustomIncludeImageData);
                    var imageTypes = new HashSet<string>();
                    var totalImageSize = 0;
                    
                    // Collect image statistics
                    foreach (var img in pageImages)
                    {
                        if (img is Dictionary<string, object> imgDict)
                        {
                            if (imgDict.ContainsKey("CompressionType") && imgDict["CompressionType"] is string compType && !string.IsNullOrEmpty(compType))
                            {
                                imageTypes.Add(compType);
                            }
                            if (imgDict.ContainsKey("EstimatedSize") && imgDict["EstimatedSize"] is long size)
                            {
                                totalImageSize += (int)size;
                            }
                        }
                    }
                    
                    var pageData = new Dictionary<string, object>
                    {
                        ["PageNumber"] = extractedPage.PageNumber,
                        ["Size"] = new Dictionary<string, object>
                        {
                            ["Width"] = pageSize.Width,
                            ["Height"] = pageSize.Height,
                            ["WidthPoints"] = pageSize.Width,
                            ["HeightPoints"] = pageSize.Height,
                            ["WidthInches"] = pageSize.Width / 72.0,
                            ["HeightInches"] = pageSize.Height / 72.0,
                            ["WidthMM"] = pageSize.Width * 0.352778,
                            ["HeightMM"] = pageSize.Height * 0.352778
                        },
                        ["Rotation"] = rotation,
                        ["TextInfo"] = new Dictionary<string, object>
                        {
                            ["PageText"] = extractedPage.Text,
                            ["CharacterCount"] = extractedPage.CharacterCount,
                            ["WordCount"] = extractedPage.WordCount,
                            ["LineCount"] = extractedPage.Text.Split('\n').Length,
                            ["Fonts"] = new List<object>() // Lista vazia para compatibilidade
                        },
                        ["FontInfo"] = new List<object>(), // Lista vazia para compatibilidade
                        ["Resources"] = new Dictionary<string, object>
                        {
                            ["Images"] = pageImages
                        },
                        ["ImagesInfo"] = new Dictionary<string, object>
                        {
                            ["Count"] = pageImages.Count,
                            ["TotalSize"] = totalImageSize,
                            ["Types"] = imageTypes.ToList(),
                            ["Images"] = pageImages
                        }
                    };
                    
                    pages.Add(pageData);
                }
                
                // Criar estrutura PDFAnalysisResult
                var analysisResult = new Dictionary<string, object>
                {
                    ["FilePath"] = inputFile,
                    ["FileSize"] = fileInfo.Length,
                    ["AnalysisDate"] = DateTime.UtcNow,
                    ["Metadata"] = metadata,
                    ["XMPMetadata"] = new Dictionary<string, object>(), // Vazio para compatibilidade
                    ["DocumentInfo"] = documentInfo,
                    ["Pages"] = pages, // IMPORTANTE: Com P maiúsculo!
                    // Adicionar metadados de cache
                    ["version"] = "2.12.0",
                    ["source"] = inputFile,
                    ["created"] = DateTime.UtcNow,
                    ["extractionMode"] = "text",
                    ["textStrategy"] = options.TextStrategy
                };
                
                return analysisResult;
            }
            finally
            {
                // Reader will be disposed by using statement
            }
            } // End of using block
        }
        
        private List<object> ExtractPageImages(PdfReader reader, int pageNumber, bool includeBase64 = false)
        {
            var images = new List<object>();
            try
            {
                // Use ImageDataExtractor to get complete image information with optional base64 data
                var detailedImages = ImageDataExtractor.ExtractImagesWithData(reader, pageNumber, includeBase64);
                
                foreach (var img in detailedImages)
                {
                    var imageData = new Dictionary<string, object>
                    {
                        ["Name"] = img.Name,
                        ["Width"] = img.Width,
                        ["Height"] = img.Height,
                        ["BitsPerComponent"] = img.BitsPerComponent,
                        ["ColorSpace"] = img.ColorSpace ?? "",
                        ["CompressionType"] = img.CompressionType ?? "",
                        ["EstimatedSize"] = img.EstimatedSize,
                        ["MimeType"] = img.MimeType ?? "",
                        ["IsFullPage"] = img.IsFullPage
                    };
                    
                    // Include base64 data if requested and available
                    if (includeBase64 && !string.IsNullOrEmpty(img.Base64Data))
                    {
                        imageData["Base64Data"] = img.Base64Data;
                        imageData["HasData"] = true;
                    }
                    else
                    {
                        imageData["HasData"] = false;
                    }
                    
                    images.Add(imageData);
                }
                
                // Check if this is a scanned page
                var isScannedPage = ImageDataExtractor.IsScannedPage(reader, pageNumber);
                if (isScannedPage && images.Count > 0)
                {
                    // Mark the page as scanned in the first image metadata
                    if (images[0] is Dictionary<string, object> firstImage)
                    {
                        firstImage["PageIsScanned"] = true;
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error but don't fail the entire extraction
                Console.WriteLine($"Warning: Could not extract images from page {pageNumber}: {ex.Message}");
            }
            
            return images;
        }
        
        // Legacy text extraction strategies (iTextSharp) removed from active build
        
        private object? SerializePdfObject(PdfObject? obj)
        {
            if (obj == null) return null;
            
            if (obj.IsDictionary())
            {
                var dict = (PdfDictionary)obj;
                var result = new Dictionary<string, object>();
                
                foreach (PdfName key in dict.Keys)
                {
                    result[key.ToString()] = SerializePdfObject(dict.Get(key)) ?? "null";
                }
                
                return result;
            }
            else if (obj.IsArray())
            {
                var array = (PdfArray)obj;
                return array.ArrayList.Select(SerializePdfObject).ToList();
            }
            else if (obj.IsName())
            {
                return obj.ToString();
            }
            else if (obj.IsNumber())
            {
                return ((PdfNumber)obj).DoubleValue;
            }
            else if (obj.IsString())
            {
                return ((PdfString)obj).ToUnicodeString();
            }
            else if (obj.IsBoolean())
            {
                return ((PdfBoolean)obj).BooleanValue;
            }
            else
            {
                return obj.ToString();
            }
        }
        
        private List<object> ExtractPageContents(PdfReader reader, int pageNum)
        {
            var contents = new List<object>();
            try
            {
                var pageDict = reader.GetPageN(pageNum);
                var contentsObj = pageDict.Get(PdfName.CONTENTS);
                
                if (contentsObj != null)
                {
                    if (contentsObj.IsArray())
                    {
                        var contentsArray = (PdfArray)contentsObj;
                        for (int i = 0; i < contentsArray.Size; i++)
                        {
                            var streamRef = contentsArray.GetAsIndirectObject(i);
                            if (streamRef != null)
                            {
                                contents.Add(new { streamReference = streamRef.ToString(), index = i });
                            }
                        }
                    }
                    else if (contentsObj.IsIndirect())
                    {
                        contents.Add(new { streamReference = contentsObj.ToString(), index = 0 });
                    }
                }
            }
            catch { /* Ignore errors */ }
            return contents;
        }
        
        private List<object> ExtractPageAnnotations(PdfDictionary pageDict)
        {
            var annotations = new List<object>();
            try
            {
                var annotsArray = pageDict.GetAsArray(PdfName.ANNOTS);
                if (annotsArray != null)
                {
                    for (int i = 0; i < annotsArray.Size; i++)
                    {
                        var annotDict = annotsArray.GetAsDict(i);
                        if (annotDict != null)
                        {
                            var annotation = new Dictionary<string, object>
                            {
                                ["subtype"] = annotDict.GetAsName(PdfName.SUBTYPE)?.ToString() ?? string.Empty,
                                ["rect"] = annotDict.GetAsArray(PdfName.RECT)?.ToString() ?? string.Empty,
                                ["contents"] = annotDict.GetAsString(PdfName.CONTENTS)?.ToString() ?? string.Empty
                            };
                            annotations.Add(annotation);
                        }
                    }
                }
            }
            catch { /* Ignore errors */ }
            return annotations;
        }
        
        private object? ExtractBookmarksRaw(PdfReader reader)
        {
            try
            {
                var catalog = reader.Catalog;
                var outlines = catalog.GetAsDict(PdfName.OUTLINES);
                if (outlines != null)
                {
                    return SerializePdfObject(outlines);
                }
            }
            catch { /* Ignore errors */ }
            return null;
        }
        
        private object? ExtractAcroFormRaw(PdfReader reader)
        {
            try
            {
                var catalog = reader.Catalog;
                var acroForm = catalog.GetAsDict(PdfName.ACROFORM);
                if (acroForm != null)
                {
                    return SerializePdfObject(acroForm);
                }
            }
            catch { /* Ignore errors */ }
            return null;
        }
        
        private object? ExtractMetadataRaw(PdfReader reader)
        {
            try
            {
                var info = reader.Info;
                var metadata = new Dictionary<string, object>();
                
                if (info != null)
                {
                    foreach (string key in info.Keys)
                    {
                        metadata[key] = info[key];
                    }
                }
                
                // Extract XMP metadata
                var catalog = reader.Catalog;
                var xmpStream = catalog.GetAsStream(PdfName.METADATA);
                if (xmpStream != null)
                {
                    try
                    {
                        var xmpBytes = PdfReader.GetStreamBytes((PRStream)xmpStream);
                        var xmpString = System.Text.Encoding.UTF8.GetString(xmpBytes);
                        metadata["XMP"] = xmpString;
                    }
                    catch { /* Ignore XMP errors */ }
                }
                
                return metadata;
            }
            catch { /* Ignore errors */ }
            return null;
        }
        
        private Dictionary<string, object> ExtractUltraComplete(string inputFile, LoadOptions options, CancellationToken cancellationToken = default)
        {
            if (options.Verbose) Console.WriteLine("  Extracting ULTRA-COMPLETE PDF data (this may take a while)...");
            
            // Check for cancellation before starting
            cancellationToken.ThrowIfCancellationRequested();
            
            // Start with PDFAnalyzer.AnalyzeFull() as base
            if (options.Verbose) Console.WriteLine("    Creating PDFAnalyzer...");
            var analyzer = new PDFAnalyzer(inputFile);
            if (options.Verbose) Console.WriteLine("    Calling AnalyzeFull()...");
            
            // Check for cancellation before the heavy AnalyzeFull call
            cancellationToken.ThrowIfCancellationRequested();
            var analysis = analyzer.AnalyzeFull();
            if (options.Verbose) Console.WriteLine("    AnalyzeFull() completed.");
            
            // Convert to dictionary for manipulation
            var ultraData = JsonConvert.DeserializeObject<Dictionary<string, object>>(JsonConvert.SerializeObject(analysis)) ?? new Dictionary<string, object>();
            
            // Add cache metadata
            ultraData["version"] = "2.11.0";
            ultraData["source"] = inputFile;
            ultraData["created"] = DateTime.UtcNow;
            ultraData["extractionMode"] = "ultra";
            ultraData["textStrategy"] = options.TextStrategy;
            
            // Use PdfAccessManager for centralized access
            var reader = PdfAccessManager7.CreateTemporaryDocument(inputFile);
            try
            {
                // Check for cancellation before raw structure processing
                cancellationToken.ThrowIfCancellationRequested();
                
                if (options.Verbose) Console.WriteLine("    Adding raw PDF structure...");
                
                // Add COMPLETE raw PDF structure
                ultraData["rawStructure"] = new Dictionary<string, object>
                {
                    ["catalog"] = SerializePdfObject(reader.Catalog) ?? new object(),
                    ["trailer"] = SerializePdfObject(reader.Trailer) ?? new object(),
                    ["info"] = reader.Info,
                    ["fileLength"] = reader.FileLength,
                    ["pdfVersion"] = reader.PdfVersion,
                    ["isRebuilt"] = reader.IsRebuilt(),
                    ["xrefSize"] = reader.XrefSize,
                    ["isEncrypted"] = reader.IsEncrypted(),
                    ["permissions"] = reader.Permissions
                };
                
                if (options.Verbose) Console.WriteLine("    Adding ALL PDF objects...");
                
                // Add ALL objects with COMPLETE details
                var allObjects = new List<object>();
                for (int i = 0; i < reader.XrefSize; i++)
                {
                    // Check for cancellation during object processing (every 100 objects)
                    if (i % 100 == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    
                    try
                    {
                        var obj = reader.GetPdfObjectRelease(i);
                        if (obj != null)
                        {
                            var objData = new Dictionary<string, object>
                            {
                                ["number"] = i,
                                ["type"] = obj.GetType().Name,
                                ["isDictionary"] = obj.IsDictionary(),
                                ["isArray"] = obj.IsArray(),
                                ["isStream"] = obj.IsStream(),
                                ["isString"] = obj.IsString(),
                                ["isNumber"] = obj.IsNumber(),
                                ["isName"] = obj.IsName(),
                                ["isBoolean"] = obj.IsBoolean(),
                                ["isIndirect"] = obj.IsIndirect(),
                                ["value"] = SerializePdfObject(obj) ?? "null"
                            };
                            
                            // Add stream content for streams
                            if (obj.IsStream())
                            {
                                try
                                {
                                    var stream = (PRStream)obj;
                                    var streamBytes = PdfReader.GetStreamBytes(stream);
                                    objData["streamLength"] = streamBytes.Length;
                                    
                                    // For small streams, include decoded content
                                    if (streamBytes.Length < 50000)
                                    {
                                        try
                                        {
                                            var decoded = System.Text.Encoding.UTF8.GetString(streamBytes);
                                            if (decoded.All(c => c < 127))
                                            {
                                                objData["streamContent"] = decoded;
                                            }
                                        }
                                        catch { /* Ignore decode errors */ }
                                    }
                                }
                                catch { /* Ignore stream errors */ }
                            }
                            
                            allObjects.Add(objData);
                        }
                    }
                    catch { /* Ignore object errors */ }
                }
                ultraData["allObjects"] = allObjects;
                
                if (options.Verbose) Console.WriteLine("    Adding detailed page analysis...");
                
                // Add DETAILED page-by-page analysis
                var detailedPages = new List<object>();
                for (int pageNum = 1; pageNum <= reader.NumberOfPages; pageNum++)
                {
                    // Check for cancellation during page processing
                    cancellationToken.ThrowIfCancellationRequested();
                    var pageDict = reader.GetPageN(pageNum);
                    var pageData = new Dictionary<string, object>
                    {
                        ["pageNumber"] = pageNum,
                        ["pageDictionary"] = SerializePdfObject(pageDict) ?? new object(),
                        ["size"] = reader.GetPageSize(pageNum),
                        ["rotation"] = reader.GetPageRotation(pageNum),
                        ["mediaBox"] = pageDict.GetAsArray(PdfName.MEDIABOX)?.ToString() ?? string.Empty,
                        ["cropBox"] = pageDict.GetAsArray(PdfName.CROPBOX)?.ToString() ?? string.Empty,
                        ["bleedBox"] = pageDict.GetAsArray(new PdfName("BleedBox"))?.ToString() ?? string.Empty,
                        ["trimBox"] = pageDict.GetAsArray(new PdfName("TrimBox"))?.ToString() ?? string.Empty,
                        ["artBox"] = pageDict.GetAsArray(new PdfName("ArtBox"))?.ToString() ?? string.Empty,
                        ["resources"] = SerializePdfObject(pageDict.GetAsDict(PdfName.RESOURCES)) ?? new object(),
                        ["contents"] = ExtractPageContents(reader, pageNum),
                        ["annotations"] = ExtractPageAnnotations(pageDict)
                    };
                    
                    // Extract text with ALL strategies
                    var textExtractions = new Dictionary<string, object>();
                    
                    var strategies = new[] {
                        new { name = "LayoutPreserving", strategy = GetExtractionStrategy(1) },
                        new { name = "AdvancedLayout", strategy = GetExtractionStrategy(2) },
                        new { name = "ColumnDetection", strategy = GetExtractionStrategy(3) }
                    };
                    
                    foreach (var strat in strategies)
                    {
                        try
                        {
                            var text = PdfTextExtractor.GetTextFromPage(reader, pageNum, strat.strategy);
                            textExtractions[strat.name] = new
                            {
                                text = text,
                                wordCount = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries).Length,
                                characterCount = text.Length
                            };
                        }
                        catch { /* Ignore extraction errors */ }
                    }
                    
                    pageData["textExtractions"] = textExtractions;
                    detailedPages.Add(pageData);
                }
                ultraData["detailedPages"] = detailedPages;
                
                if (options.Verbose) Console.WriteLine("    Adding extended metadata...");
                
                // Add EXTENDED metadata
                ultraData["extendedMetadata"] = ExtractMetadataRaw(reader) ?? new object();
                ultraData["bookmarksRaw"] = ExtractBookmarksRaw(reader) ?? new object();
                ultraData["acroFormRaw"] = ExtractAcroFormRaw(reader) ?? new object();
                
                if (options.Verbose) Console.WriteLine("    Adding processing statistics...");
                
                // Add processing statistics
                ultraData["processingStats"] = new Dictionary<string, object>
                {
                    ["totalObjects"] = allObjects.Count,
                    ["totalPages"] = detailedPages.Count,
                    ["extractionStrategies"] = 3,
                    ["processingTime"] = DateTime.UtcNow,
                    ["cacheSize"] = "calculated after serialization"
                };
            }
            finally
            {
                reader.Close();
            }
            
            return ultraData;
        }
        
        private Dictionary<string, object> ExtractCustomComplete(string inputFile, LoadOptions options, CancellationToken cancellationToken = default)
        {
            if (options.Verbose) Console.WriteLine("  Extracting CUSTOM data (ultra mode with exclusions)...");
            
            // Começar com análise ULTRA completa
            var ultraData = ExtractUltraComplete(inputFile, options, cancellationToken);
            
            // Adicionar informações sobre o modo custom
            ultraData["extractionMode"] = "custom";
            ultraData["customOptions"] = new Dictionary<string, object>
            {
                ["includeText"] = options.CustomIncludeText,
                ["includeObjects"] = options.CustomIncludeObjects,
                ["includeStreams"] = options.CustomIncludeStreams,
                ["includeFonts"] = options.CustomIncludeFonts,
                ["includeImages"] = options.CustomIncludeImages,
                ["includeMultimedia"] = options.CustomIncludeMultimedia,
                ["includeEmbeddedFiles"] = options.CustomIncludeEmbeddedFiles,
                ["includeJavaScript"] = options.CustomIncludeJavaScript,
                ["includeForms"] = options.CustomIncludeForms,
                ["includeAnnotations"] = options.CustomIncludeAnnotations,
                ["includeMetadata"] = options.CustomIncludeMetadata,
                ["includeBookmarks"] = options.CustomIncludeBookmarks,
                ["maxStreamSize"] = options.CustomMaxStreamSize
            };
            
            // Agora aplicar exclusões baseadas nas opções
            if (options.Verbose) Console.WriteLine("    Applying custom exclusions...");
            
            // Remover objetos se não solicitado
            if (!options.CustomIncludeObjects && ultraData.ContainsKey("allObjects"))
            {
                ultraData.Remove("allObjects");
                if (options.Verbose) Console.WriteLine("      - Removed PDF objects");
            }
            
            // Remover conteúdo de streams se não solicitado
            if (!options.CustomIncludeStreams && ultraData.ContainsKey("allObjects"))
            {
                // Percorrer objetos e remover streamContent
                var objects = ultraData["allObjects"] as List<object>;
                if (objects != null)
                {
                    foreach (var obj in objects)
                    {
                        var dict = obj as Dictionary<string, object>;
                        if (dict != null && dict.ContainsKey("streamContent"))
                        {
                            dict.Remove("streamContent");
                        }
                    }
                }
                if (options.Verbose) Console.WriteLine("      - Removed stream contents");
            }
            
            // Remover multimidia se não solicitado
            if (!options.CustomIncludeMultimedia)
            {
                // Remover de Pages
                if (ultraData.ContainsKey("Pages"))
                {
                    var pagesJson = JsonConvert.SerializeObject(ultraData["Pages"]);
                    var pages = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(pagesJson);
                    if (pages != null)
                    {
                        foreach (var page in pages)
                        {
                            if (page.ContainsKey("Multimedia"))
                            {
                                page.Remove("Multimedia");
                            }
                        }
                        ultraData["Pages"] = pages;
                    }
                }
                if (options.Verbose) Console.WriteLine("      - Removed multimedia");
            }
            
            // Remover arquivos embarcados se não solicitado
            if (!options.CustomIncludeEmbeddedFiles && ultraData.ContainsKey("EmbeddedFiles"))
            {
                ultraData.Remove("EmbeddedFiles");
                if (options.Verbose) Console.WriteLine("      - Removed embedded files");
            }
            
            // Remover JavaScript se não solicitado
            if (!options.CustomIncludeJavaScript && ultraData.ContainsKey("JavaScript"))
            {
                ultraData.Remove("JavaScript");
                if (options.Verbose) Console.WriteLine("      - Removed JavaScript");
            }
            
            // Remover formulários se não solicitado
            if (!options.CustomIncludeForms)
            {
                if (ultraData.ContainsKey("Forms"))
                {
                    ultraData.Remove("Forms");
                }
                // Remover de Pages
                if (ultraData.ContainsKey("Pages"))
                {
                    var pagesJson = JsonConvert.SerializeObject(ultraData["Pages"]);
                    var pages = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(pagesJson);
                    if (pages != null)
                    {
                        foreach (var page in pages)
                        {
                            if (page.ContainsKey("FormFields"))
                            {
                                page.Remove("FormFields");
                            }
                        }
                        ultraData["Pages"] = pages;
                    }
                }
                if (options.Verbose) Console.WriteLine("      - Removed forms");
            }
            
            // Remover anotações se não solicitado
            if (!options.CustomIncludeAnnotations)
            {
                if (ultraData.ContainsKey("Annotations"))
                {
                    ultraData.Remove("Annotations");
                }
                // Remover de Pages
                if (ultraData.ContainsKey("Pages"))
                {
                    var pagesJson = JsonConvert.SerializeObject(ultraData["Pages"]);
                    var pages = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(pagesJson);
                    if (pages != null)
                    {
                        foreach (var page in pages)
                        {
                            if (page.ContainsKey("Annotations"))
                            {
                                page.Remove("Annotations");
                            }
                        }
                        ultraData["Pages"] = pages;
                    }
                }
                if (options.Verbose) Console.WriteLine("      - Removed annotations");
            }
            
            // Remover metadata se não solicitado
            if (!options.CustomIncludeMetadata)
            {
                if (ultraData.ContainsKey("DocumentInfo"))
                {
                    ultraData.Remove("DocumentInfo");
                }
                if (ultraData.ContainsKey("Metadata"))
                {
                    ultraData.Remove("Metadata");
                }
                if (options.Verbose) Console.WriteLine("      - Removed metadata");
            }
            
            // Remover bookmarks se não solicitado
            if (!options.CustomIncludeBookmarks && ultraData.ContainsKey("Bookmarks"))
            {
                ultraData.Remove("Bookmarks");
                if (options.Verbose) Console.WriteLine("      - Removed bookmarks");
            }
            
            // Remover fontes se não solicitado
            if (!options.CustomIncludeFonts)
            {
                if (ultraData.ContainsKey("Fonts"))
                {
                    ultraData.Remove("Fonts");
                }
                // Remover de Pages
                if (ultraData.ContainsKey("Pages"))
                {
                    var pagesJson = JsonConvert.SerializeObject(ultraData["Pages"]);
                    var pages = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(pagesJson);
                    if (pages != null)
                    {
                        foreach (var page in pages)
                        {
                            if (page.ContainsKey("Fonts"))
                            {
                                page.Remove("Fonts");
                            }
                        }
                        ultraData["Pages"] = pages;
                    }
                }
                if (options.Verbose) Console.WriteLine("      - Removed fonts");
            }
            
            // Remover imagens se não solicitado
            if (!options.CustomIncludeImages)
            {
                if (ultraData.ContainsKey("Images"))
                {
                    ultraData.Remove("Images");
                }
                // Remover de Pages
                if (ultraData.ContainsKey("Pages"))
                {
                    var pagesJson = JsonConvert.SerializeObject(ultraData["Pages"]);
                    var pages = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(pagesJson);
                    if (pages != null)
                    {
                        foreach (var page in pages)
                        {
                            if (page.ContainsKey("Images"))
                            {
                                page.Remove("Images");
                            }
                        }
                        ultraData["Pages"] = pages;
                    }
                }
                if (options.Verbose) Console.WriteLine("      - Removed images");
            }
            else if (options.CustomIncludeImages && options.CustomIncludeImageData)
            {
                // If images are requested with base64 data, extract them properly
                if (options.Verbose) Console.WriteLine("    Extracting images with base64 data...");
                
                // Extract images with base64 data for each page
                using (var reader = PdfAccessManager7.CreateTemporaryDocument(inputFile))
                {
                    if (ultraData.ContainsKey("Pages"))
                    {
                        var pagesJson = JsonConvert.SerializeObject(ultraData["Pages"]);
                        var pages = JsonConvert.DeserializeObject<List<Dictionary<string, object>>>(pagesJson);
                        if (pages != null)
                        {
                            for (int i = 0; i < pages.Count; i++)
                            {
                                var page = pages[i];
                                int pageNum = i + 1;
                                if (page.ContainsKey("PageNumber") && page["PageNumber"] is int pn)
                                {
                                    pageNum = pn;
                                }
                                
                                // Extract images with base64 data
                                var pageImages = ExtractPageImages(reader, pageNum, true);
                                
                                // Add or update Resources/Images
                                if (!page.ContainsKey("Resources"))
                                {
                                    page["Resources"] = new Dictionary<string, object>();
                                }
                                
                                if (page["Resources"] is Dictionary<string, object> resources)
                                {
                                    resources["Images"] = pageImages;
                                }
                                else
                                {
                                    // If Resources is not a dictionary, replace it
                                    page["Resources"] = new Dictionary<string, object>
                                    {
                                        ["Images"] = pageImages
                                    };
                                }
                            }
                            ultraData["Pages"] = pages;
                        }
                    }
                }
                
                if (options.Verbose) Console.WriteLine("      - Added images with base64 data");
            }
            
            if (options.Verbose) Console.WriteLine("    Custom extraction completed.");
            
            return ultraData;
        }
        
        private void ShowSubcommandHelp(string subcommand)
        {
            switch (subcommand)
            {
                case "ultra":
                    Console.WriteLine("SUBCOMMAND: load ultra");
                    Console.WriteLine("    Full forensic analysis - extracts EVERYTHING from the PDF");
                    Console.WriteLine();
                    Console.WriteLine("DESCRIPTION:");
                    Console.WriteLine("    The 'ultra' mode performs the most comprehensive analysis possible.");
                    Console.WriteLine("    It extracts all text, images, fonts, metadata, bookmarks, annotations,");
                    Console.WriteLine("    forms, JavaScript, embedded files, multimedia, and raw PDF objects.");
                    Console.WriteLine("    This mode is the slowest but provides complete forensic-level detail.");
                    Console.WriteLine();
                    Console.WriteLine("USAGE:");
                    Console.WriteLine("    fpdf load ultra --input-file document.pdf [options]");
                    Console.WriteLine("    fpdf load ultra --input-dir /path/to/pdfs [options]");
                    Console.WriteLine();
                    Console.WriteLine("OPTIONS:");
                    Console.WriteLine("    -d, --output-dir <dir>    Output directory for cache files");
                    Console.WriteLine("    -o, --output <file>       Output file for single PDF");
                    Console.WriteLine("    -w, --num-workers <n>     Number of parallel workers");
                    Console.WriteLine("    -v, --verbose             Show detailed progress");
                    Console.WriteLine("    --overwrite               Overwrite existing cache files");
                    Console.WriteLine();
                    Console.WriteLine("EXAMPLE:");
                    Console.WriteLine("    fpdf load ultra --input-file report.pdf");
                    Console.WriteLine("    fpdf load ultra --input-dir . --num-workers 4");
                    break;
                    
                case "text":
                    Console.WriteLine("SUBCOMMAND: load text");
                    Console.WriteLine("    Fast text-only extraction");
                    Console.WriteLine();
                    Console.WriteLine("DESCRIPTION:");
                    Console.WriteLine("    The 'text' mode performs quick text extraction without analyzing");
                    Console.WriteLine("    other PDF elements. Ideal for text search and content analysis.");
                    Console.WriteLine("    This is the fastest extraction mode.");
                    Console.WriteLine();
                    Console.WriteLine("USAGE:");
                    Console.WriteLine("    fpdf load text --input-file document.pdf [options]");
                    Console.WriteLine("    fpdf load text --input-dir /path/to/pdfs [options]");
                    Console.WriteLine();
                    Console.WriteLine("OPTIONS:");
                    Console.WriteLine("    -d, --output-dir <dir>    Output directory for cache files");
                    Console.WriteLine("    -o, --output <file>       Output file for single PDF");
                    Console.WriteLine("    -s, --strategy <1|2|3>    Text extraction strategy (default: 2)");
                    Console.WriteLine("    -w, --num-workers <n>     Number of parallel workers");
                    Console.WriteLine("    -v, --verbose             Show detailed progress");
                    Console.WriteLine("    --overwrite               Overwrite existing cache files");
                    Console.WriteLine();
                    Console.WriteLine("TEXT EXTRACTION STRATEGIES:");
                    Console.WriteLine("    1 = Simple location-based");
                    Console.WriteLine("    2 = Advanced layout-preserving (default)");
                    Console.WriteLine("    3 = Column detection");
                    Console.WriteLine();
                    Console.WriteLine("EXAMPLE:");
                    Console.WriteLine("    fpdf load document.pdf text");
                    Console.WriteLine("    fpdf load document.pdf text --strategy 3");
                    break;
                    
                case "custom":
                    Console.WriteLine("SUBCOMMAND: load custom");
                    Console.WriteLine("    Balanced extraction with customizable exclusions");
                    Console.WriteLine();
                    Console.WriteLine("DESCRIPTION:");
                    Console.WriteLine("    The 'custom' mode starts with ultra extraction but allows you to");
                    Console.WriteLine("    exclude specific elements to reduce cache size and processing time.");
                    Console.WriteLine("    Perfect for when you need most data but want to skip large elements");
                    Console.WriteLine("    like images, multimedia, or embedded files.");
                    Console.WriteLine();
                    Console.WriteLine("USAGE:");
                    Console.WriteLine("    fpdf load custom --input-file document.pdf [exclusion options]");
                    Console.WriteLine("    fpdf load custom --input-dir /path/to/pdfs [exclusion options]");
                    Console.WriteLine();
                    Console.WriteLine("EXCLUSION OPTIONS:");
                    Console.WriteLine("    --no-text                Don't extract text content");
                    Console.WriteLine("    --no-fonts               Don't extract font information");
                    Console.WriteLine("    --no-images              Don't extract images");
                    Console.WriteLine("    --no-multimedia          Don't extract multimedia");
                    Console.WriteLine("    --no-embedded-files      Don't extract embedded files");
                    Console.WriteLine("    --no-javascript          Don't extract JavaScript");
                    Console.WriteLine("    --no-forms               Don't extract form fields");
                    Console.WriteLine("    --no-annotations         Don't extract annotations");
                    Console.WriteLine("    --no-metadata            Don't extract metadata");
                    Console.WriteLine("    --no-bookmarks           Don't extract bookmarks");
                    Console.WriteLine();
                    Console.WriteLine("INCLUSION OPTIONS:");
                    Console.WriteLine("    --include-objects        Include raw PDF objects");
                    Console.WriteLine("    --include-streams        Include stream contents");
                    Console.WriteLine("    --max-stream-size <n>    Max stream size to decode (default: 10000)");
                    Console.WriteLine();
                    Console.WriteLine("OTHER OPTIONS:");
                    Console.WriteLine("    -d, --output-dir <dir>    Output directory for cache files");
                    Console.WriteLine("    -o, --output <file>       Output file for single PDF");
                    Console.WriteLine("    -w, --num-workers <n>     Number of parallel workers");
                    Console.WriteLine("    -v, --verbose             Show detailed progress");
                    Console.WriteLine("    --overwrite               Overwrite existing cache files");
                    Console.WriteLine();
                    Console.WriteLine("EXAMPLES:");
                    Console.WriteLine("    # Extract everything except images and multimedia");
                    Console.WriteLine("    fpdf load custom --input-file document.pdf --no-images --no-multimedia");
                    Console.WriteLine();
                    Console.WriteLine("    # Minimal extraction for text search");
                    Console.WriteLine("    fpdf load custom --input-dir /pdfs --no-images --no-multimedia --no-embedded-files --no-javascript");
                    Console.WriteLine();
                    Console.WriteLine("    # Include PDF objects for forensic analysis");
                    Console.WriteLine("    fpdf load document.pdf custom --include-objects --no-images");
                    break;
                    
                case "images-only":
                    Console.WriteLine("SUBCOMMAND: load images-only");
                    Console.WriteLine("    Extract only images and scanned pages");
                    Console.WriteLine();
                    Console.WriteLine("DESCRIPTION:");
                    Console.WriteLine("    The 'images-only' mode focuses on extracting images from PDFs,");
                    Console.WriteLine("    particularly useful for identifying scanned pages like notas de empenho.");
                    Console.WriteLine("    This mode:");
                    Console.WriteLine("    - Extracts all images with base64 encoding");
                    Console.WriteLine("    - Detects pages that are entirely scanned (images)");
                    Console.WriteLine("    - Skips text extraction to save time and space");
                    Console.WriteLine("    - Keeps basic metadata for reference");
                    Console.WriteLine();
                    Console.WriteLine("USAGE:");
                    Console.WriteLine("    fpdf load images-only --input-file document.pdf");
                    Console.WriteLine("    fpdf load images-only --input-dir /path/to/pdfs");
                    Console.WriteLine();
                    Console.WriteLine("EXAMPLES:");
                    Console.WriteLine("    # Extract only images from a process PDF");
                    Console.WriteLine("    fpdf load images-only --input-file processo.pdf");
                    Console.WriteLine();
                    Console.WriteLine("    # Process multiple PDFs for images");
                    Console.WriteLine("    fpdf load images-only --input-dir /pdfs --output-dir .cache");
                    Console.WriteLine();
                    Console.WriteLine("    # After loading, find scanned pages");
                    Console.WriteLine("    fpdf 1 pages -F json | grep IsScannedPage");
                    break;
                    
                case "base64-only":
                    Console.WriteLine("SUBCOMMAND: load base64-only");
                    Console.WriteLine("    Extract only base64 encoded content");
                    Console.WriteLine();
                    Console.WriteLine("DESCRIPTION:");
                    Console.WriteLine("    The 'base64-only' mode searches for base64 encoded content in PDFs.");
                    Console.WriteLine("    This mode:");
                    Console.WriteLine("    - Extracts text to search for base64 patterns");
                    Console.WriteLine("    - Includes streams that might contain base64");
                    Console.WriteLine("    - Skips images, fonts, and other heavy content");
                    Console.WriteLine("    - Optimized for finding embedded base64 data");
                    Console.WriteLine();
                    Console.WriteLine("USAGE:");
                    Console.WriteLine("    fpdf load base64-only --input-file document.pdf");
                    Console.WriteLine("    fpdf load base64-only --input-dir /path/to/pdfs");
                    Console.WriteLine();
                    Console.WriteLine("EXAMPLES:");
                    Console.WriteLine("    # Search for base64 in a PDF");
                    Console.WriteLine("    fpdf load base64-only --input-file document.pdf");
                    Console.WriteLine();
                    Console.WriteLine("    # After loading, find base64 content");
                    Console.WriteLine("    fpdf 1 base64");
                    Console.WriteLine();
                    Console.WriteLine("    # Search base64 with context");
                    Console.WriteLine("    fpdf 1 base64 --word 'empenho'");
                    break;
                    
                default:
                    Console.Error.WriteLine($"Error: Unknown subcommand '{subcommand}'");
                    Console.Error.WriteLine("Valid subcommands: ultra, text, custom, images-only, base64-only");
                    break;
            }
        }
        
        #endif // LEGACY ITEXT5 BLOCK

        public override void ShowHelp()
        {
            Console.WriteLine($"COMMAND: {Name}");
            Console.WriteLine($"    {Description}");
            Console.WriteLine();
            Console.WriteLine("USO SIMPLES (sem banco, cache em tmp/cache):");
            Console.WriteLine($"    fpdf {Name} --input-dir <pasta-com-pdfs>");
            Console.WriteLine("Saída: tmp/cache/<arquivo>.json (um por PDF, modo ULTRA padrão).");
            Console.WriteLine();
            Console.WriteLine("OPÇÕES PRINCIPAIS:");
            Console.WriteLine("    --input-dir <dir>        Pasta com PDFs (processa *.pdf)");
            Console.WriteLine("    --output-dir <dir>       Onde salvar (default: tmp/cache)");
            Console.WriteLine("    --recursive | -r         Incluir subpastas");
            Console.WriteLine("    --num-workers | -w <n>   Paralelismo (default: 4 se não informado)");
            Console.WriteLine("    --text                   Somente texto (rápido)");
            Console.WriteLine("    --raw                    Estrutura bruta");
            Console.WriteLine("    --ultra                  Completo (default)");
            Console.WriteLine("    --overwrite              Regrava se já existir");
            Console.WriteLine("    --verbose                Verbose");
            Console.WriteLine("    (Somente JSON local; sem Postgres)");
            Console.WriteLine("    # Ultra mode - full analysis (slow but complete)");
            Console.WriteLine($"    fpdf {Name} ultra --input-file document.pdf");
            Console.WriteLine();
            Console.WriteLine("    # Custom mode - extract text only, no multimedia or images");
            Console.WriteLine($"    fpdf {Name} custom --input-dir /pdfs --no-multimedia --no-images");
            Console.WriteLine();
            Console.WriteLine("    # Recursive directory processing");
            Console.WriteLine($"    fpdf {Name} ultra --input-dir /pdfs --recursive --num-workers 8");
            Console.WriteLine();
            Console.WriteLine("    # Custom mode - minimal extraction for text search");
            Console.WriteLine($"    fpdf document.pdf {Name} custom --no-multimedia --no-images --no-embedded-files --no-javascript");
            Console.WriteLine();
            Console.WriteLine("    # Text mode - fast extraction");
            Console.WriteLine($"    fpdf document.pdf {Name} text");
            Console.WriteLine();
            Console.WriteLine("    # Custom mode - balanced with options");
            Console.WriteLine($"    fpdf document.pdf {Name} custom");
            Console.WriteLine($"    fpdf document.pdf {Name} custom --include-objects");
            Console.WriteLine($"    fpdf document.pdf {Name} custom --no-text --include-streams");
            Console.WriteLine();
            Console.WriteLine("    # Default (no subcommand) - uses ultra mode");
            Console.WriteLine($"    fpdf document.pdf {Name}");
        }
    }
}
