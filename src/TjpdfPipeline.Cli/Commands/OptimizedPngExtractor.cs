using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FilterPDF;
using FilterPDF.Utils;

namespace FilterPDF.Commands
{
    /// <summary>
    /// High-performance PNG extraction engine optimized for parallel processing
    /// Performance targets: Sub-second per page, minimal memory footprint, optimal resource utilization
    /// </summary>
    public static class OptimizedPngExtractor
    {
        private static readonly object ConsoleLock = new object();
        private static readonly SemaphoreSlim ExternalToolSemaphore = new SemaphoreSlim(Environment.ProcessorCount * 2);
        
        // Performance metrics tracking
        private static int _totalPages = 0;
        private static int _processedPages = 0;
        private static int _successfulPages = 0;
        private static int _failedPages = 0;
        private static DateTime _startTime;
        private static volatile bool _isRunning = false;

        public class ExtractionTask
        {
            public string PdfPath { get; set; }
            public int PageNumber { get; set; }
            public string OutputPath { get; set; }
            public string PdfBaseName { get; set; }
        }

        public class ExtractionConfig
        {
            public string OutputDirectory { get; set; } = "./extracted_pages";
            public int MaxWorkers { get; set; } = Math.Max(2, Environment.ProcessorCount);
            public int ProcessTimeoutMs { get; set; } = 30000;
            public bool ShowProgress { get; set; } = true;
            public int PngQuality { get; set; } = 150;
            public string PreferredTool { get; set; } = "pdftoppm";
        }

        /// <summary>
        /// High-performance parallel PNG extraction entry point
        /// </summary>
        public static void ExtractPagesAsPng(List<PageMatch> foundPages, Dictionary<string, string> outputOptions, 
            string currentPdfPath = null, string inputFilePath = null, bool isUsingCache = false)
        {
            var config = BuildConfigFromOptions(outputOptions);
            
            try
            {
                // Initialize extraction context
                InitializeExtraction(foundPages.Count, config);
                
                // Build extraction tasks
                var tasks = BuildExtractionTasks(foundPages, config, currentPdfPath, inputFilePath, isUsingCache);
                
                if (tasks.Count == 0)
                {
                    Console.WriteLine("⚠️  No valid PDF files found for PNG extraction");
                    return;
                }

                // Show extraction summary
                ShowExtractionSummary(tasks, config);
                
                // Execute optimized parallel extraction
                ExecuteParallelExtraction(tasks, config);
                
                // Show final results
                ShowFinalResults(config);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ PNG extraction error: {ex.Message}");
            }
            finally
            {
                _isRunning = false;
            }
        }

        private static ExtractionConfig BuildConfigFromOptions(Dictionary<string, string> outputOptions)
        {
            var config = new ExtractionConfig();
            
            // Output directory
            if (outputOptions.ContainsKey("--output-dir"))
            {
                config.OutputDirectory = outputOptions["--output-dir"];
            }
            else if (outputOptions.ContainsKey("-o") || outputOptions.ContainsKey("--output"))
            {
                string outputPath = outputOptions.ContainsKey("-o") ? outputOptions["-o"] : outputOptions["--output"];
                config.OutputDirectory = Directory.Exists(outputPath) ? outputPath : 
                                        Path.GetDirectoryName(outputPath) ?? "./extracted_pages";
            }

            // Worker count
            if (outputOptions.ContainsKey("--num-workers") && int.TryParse(outputOptions["--num-workers"], out int workers))
            {
                config.MaxWorkers = Math.Max(1, Math.Min(16, workers));
            }
            
            // PNG quality
            if (outputOptions.ContainsKey("--png-quality") && int.TryParse(outputOptions["--png-quality"], out int quality))
            {
                config.PngQuality = Math.Max(72, Math.Min(300, quality));
            }

            return config;
        }

        private static void InitializeExtraction(int totalPages, ExtractionConfig config)
        {
            _totalPages = totalPages;
            _processedPages = 0;
            _successfulPages = 0;
            _failedPages = 0;
            _startTime = DateTime.Now;
            _isRunning = true;

            if (!Directory.Exists(config.OutputDirectory))
            {
                Directory.CreateDirectory(config.OutputDirectory);
            }
        }

        private static List<ExtractionTask> BuildExtractionTasks(List<PageMatch> foundPages, ExtractionConfig config, 
            string currentPdfPath, string inputFilePath, bool isUsingCache)
        {
            var tasks = new List<ExtractionTask>();
            var pagesByPdf = new Dictionary<string, List<PageMatch>>();
            
            // Group pages by PDF
            foreach (var page in foundPages)
            {
                string pdfPath = ResolvePdfPath(page, currentPdfPath, inputFilePath, isUsingCache);
                
                if (!string.IsNullOrEmpty(pdfPath) && File.Exists(pdfPath))
                {
                    if (!pagesByPdf.ContainsKey(pdfPath))
                    {
                        pagesByPdf[pdfPath] = new List<PageMatch>();
                    }
                    pagesByPdf[pdfPath].Add(page);
                }
            }

            // Create extraction tasks
            foreach (var kvp in pagesByPdf)
            {
                string pdfPath = kvp.Key;
                var pages = kvp.Value;
                string pdfBaseName = Path.GetFileNameWithoutExtension(pdfPath);
                
                foreach (var page in pages)
                {
                    string outputPath = Path.Combine(config.OutputDirectory, 
                        $"{pdfBaseName}_page_{page.PageNumber:D3}.png");
                    
                    tasks.Add(new ExtractionTask
                    {
                        PdfPath = pdfPath,
                        PageNumber = page.PageNumber,
                        OutputPath = outputPath,
                        PdfBaseName = pdfBaseName
                    });
                }
            }

            return tasks;
        }

        private static string ResolvePdfPath(PageMatch page, string currentPdfPath, string inputFilePath, bool isUsingCache)
        {
            // Use currentPdfPath directly since PageMatch doesn't store source path
            if (!string.IsNullOrEmpty(currentPdfPath))
            {
                return currentPdfPath;
            }
            
            if (isUsingCache && !string.IsNullOrEmpty(inputFilePath))
            {
                // CacheManager is not available in this build; skip resolving cache paths.
                return null;
            }
            
            return null;
        }

        private static void ShowExtractionSummary(List<ExtractionTask> tasks, ExtractionConfig config)
        {
            // Ensure output directory exists before starting extraction
            try
            {
                if (!Directory.Exists(config.OutputDirectory))
                {
                    Console.WriteLine($"📁 Creating output directory: {config.OutputDirectory}");
                    Directory.CreateDirectory(config.OutputDirectory);
                }
                
                // Test write permissions
                var testFile = Path.Combine(config.OutputDirectory, $".test_{Guid.NewGuid()}.tmp");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ERROR: Cannot write to output directory: {config.OutputDirectory}");
                Console.WriteLine($"   {ex.Message}");
                Console.WriteLine($"   Please ensure the directory exists and you have write permissions.");
                throw new InvalidOperationException($"Output directory not writable: {config.OutputDirectory}", ex);
            }
            
            var uniquePdfs = tasks.Select(t => t.PdfBaseName).Distinct().Count();
            
            Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                  OPTIMIZED PNG EXTRACTION ENGINE                    ║");
            Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║  📄 PDFs: {uniquePdfs,-58}║");
            Console.WriteLine($"║  📑 Pages: {tasks.Count,-57}║");
            Console.WriteLine($"║  ⚙️  Workers: {config.MaxWorkers,-56}║");
            Console.WriteLine($"║  🎯 Output: {config.OutputDirectory,-56}║");
            Console.WriteLine($"║  📐 Quality: {config.PngQuality} DPI{"",-53}║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
            Console.WriteLine();
        }

        private static void ExecuteParallelExtraction(List<ExtractionTask> tasks, ExtractionConfig config)
        {
            var taskQueue = new ConcurrentQueue<ExtractionTask>(tasks);
            var workers = new Task[config.MaxWorkers];
            var cancellationToken = new CancellationTokenSource();

            // Start progress reporter
            Task progressTask = null;
            if (config.ShowProgress)
            {
                progressTask = Task.Run(() => ProgressReporter(cancellationToken.Token));
            }

            try
            {
                // Start worker tasks
                for (int i = 0; i < config.MaxWorkers; i++)
                {
                    int workerId = i + 1;
                    workers[i] = Task.Run(async () => await ExtractionWorker(
                        workerId, taskQueue, config, cancellationToken.Token));
                }

                // Wait for completion
                Task.WaitAll(workers);
            }
            finally
            {
                cancellationToken.Cancel();
                progressTask?.Wait(1000);
            }
        }

        private static async Task ExtractionWorker(int workerId, ConcurrentQueue<ExtractionTask> taskQueue, 
            ExtractionConfig config, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && taskQueue.TryDequeue(out var task))
            {
                try
                {
                    // Throttle external processes to prevent resource exhaustion
                    await ExternalToolSemaphore.WaitAsync(cancellationToken);
                    
                    try
                    {
                        bool success = await ExtractPageOptimized(task, config, cancellationToken);
                        
                        // Update counters atomically
                        Interlocked.Increment(ref _processedPages);
                        if (success)
                        {
                            Interlocked.Increment(ref _successfulPages);
                        }
                        else
                        {
                            Interlocked.Increment(ref _failedPages);
                        }
                    }
                    finally
                    {
                        ExternalToolSemaphore.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref _processedPages);
                    Interlocked.Increment(ref _failedPages);
                }
            }
        }

        private static async Task<bool> ExtractPageOptimized(ExtractionTask task, ExtractionConfig config, 
            CancellationToken cancellationToken)
        {
            // Try pdftoppm first (fastest and most reliable)
            if (config.PreferredTool == "pdftoppm" || config.PreferredTool == "auto")
            {
                if (await ExtractWithPdftoppm(task, config, cancellationToken))
                    return true;
            }

            // Fallback to ImageMagick
            if (config.PreferredTool == "imagemagick" || config.PreferredTool == "auto")
            {
                if (await ExtractWithImageMagick(task, config, cancellationToken))
                    return true;
            }

            return false;
        }

        private static async Task<bool> ExtractWithPdftoppm(ExtractionTask task, ExtractionConfig config, 
            CancellationToken cancellationToken)
        {
            try
            {
                // Use a temporary name for pdftoppm output
                string tempBaseName = Path.GetFileNameWithoutExtension(task.OutputPath);
                string tempOutputPath = Path.Combine(Path.GetDirectoryName(task.OutputPath), tempBaseName + ".png");
                
                // Processing page extraction
                
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "pdftoppm",
                        Arguments = $"-png -r {config.PngQuality} -f {task.PageNumber} -l {task.PageNumber} -singlefile \"{task.PdfPath}\" \"{Path.Combine(Path.GetDirectoryName(task.OutputPath), tempBaseName)}\"",
                        WorkingDirectory = Path.GetDirectoryName(task.OutputPath),
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                
                // Read error output for debugging
                string errorOutput = await process.StandardError.ReadToEndAsync();
                
                var processTask = Task.Run(() => process.WaitForExit(config.ProcessTimeoutMs));
                await Task.WhenAny(processTask, Task.Delay(config.ProcessTimeoutMs, cancellationToken));
                
                if (cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                // Check if the file was created (pdftoppm adds .png automatically)
                if (!File.Exists(tempOutputPath) && process.ExitCode == 0)
                {
                    // Sometimes pdftoppm creates the file but it may not be immediately visible
                    await Task.Delay(200);
                }
                
                bool fileExists = File.Exists(tempOutputPath);
                if (!fileExists)
                {
                    // Log for debugging
                    if (!string.IsNullOrEmpty(errorOutput))
                    {
                        Console.WriteLine($"pdftoppm error for page {task.PageNumber}: {errorOutput.Trim()}");
                    }
                    else if (process.ExitCode == 0)
                    {
                        Console.WriteLine($"Warning: pdftoppm reported success but file not found: {tempOutputPath}");
                    }
                }
                
                return process.ExitCode == 0 && fileExists;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> ExtractWithImageMagick(ExtractionTask task, ExtractionConfig config, 
            CancellationToken cancellationToken)
        {
            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "convert",
                        Arguments = $"-density {config.PngQuality} \"{task.PdfPath}[{task.PageNumber - 1}]\" \"{task.OutputPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                process.Start();
                
                var processTask = Task.Run(() => process.WaitForExit(config.ProcessTimeoutMs));
                await Task.WhenAny(processTask, Task.Delay(config.ProcessTimeoutMs, cancellationToken));
                
                if (cancellationToken.IsCancellationRequested)
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                return process.ExitCode == 0 && File.Exists(task.OutputPath);
            }
            catch
            {
                return false;
            }
        }

        private static void ProgressReporter(CancellationToken cancellationToken)
        {
            var lastUpdate = DateTime.Now;
            
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                try
                {
                    if ((DateTime.Now - lastUpdate).TotalMilliseconds >= 500)
                    {
                        lock (ConsoleLock)
                        {
                            UpdateProgressDisplay();
                            lastUpdate = DateTime.Now;
                        }
                    }
                    
                    Thread.Sleep(100);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static void UpdateProgressDisplay()
        {
            if (_totalPages == 0) return;
            
            var elapsed = DateTime.Now - _startTime;
            var percentage = (_processedPages * 100.0) / _totalPages;
            var pagesPerSecond = _processedPages / Math.Max(1, elapsed.TotalSeconds);
            
            var remainingPages = _totalPages - _processedPages;
            var etaSeconds = remainingPages / Math.Max(0.1, pagesPerSecond);
            var eta = TimeSpan.FromSeconds(etaSeconds);
            
            var barWidth = 40;
            var filled = (int)(barWidth * percentage / 100);
            var progressBar = new string('█', filled) + new string('░', barWidth - filled);
            
            Console.Write($"\r🔄 [{progressBar}] {percentage:F1}% " +
                         $"({_processedPages}/{_totalPages}) " +
                         $"✅{_successfulPages} ❌{_failedPages} " +
                         $"⚡{pagesPerSecond:F1}/s " +
                         $"⏱️{eta:mm\\:ss}     ");
        }

        private static void ShowFinalResults(ExtractionConfig config)
        {
            var elapsed = DateTime.Now - _startTime;
            var averageSpeed = _processedPages / Math.Max(1, elapsed.TotalSeconds);
            
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("╔══════════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║                        EXTRACTION COMPLETE                          ║");
            Console.WriteLine("╠══════════════════════════════════════════════════════════════════════╣");
            Console.WriteLine($"║  ✅ Successful: {_successfulPages,-54}║");
            Console.WriteLine($"║  ❌ Failed: {_failedPages,-58}║");
            Console.WriteLine($"║  ⏱️  Total time: {elapsed:mm\\:ss\\.ff}{"",-51}║");
            Console.WriteLine($"║  ⚡ Average speed: {averageSpeed:F2} pages/sec{"",-44}║");
            Console.WriteLine($"║  📁 Output: {config.OutputDirectory,-56}║");
            Console.WriteLine("╚══════════════════════════════════════════════════════════════════════╝");
            
            if (_successfulPages > 0)
            {
                Console.WriteLine($"\n🎯 PNG files successfully saved to: {config.OutputDirectory}");
            }
            
            if (_failedPages > 0)
            {
                var successRate = (_successfulPages * 100.0) / (_successfulPages + _failedPages);
                Console.WriteLine($"⚠️  Success rate: {successRate:F1}%");
                
                if (successRate < 90)
                {
                    Console.WriteLine("💡 Performance tips:");
                    Console.WriteLine("   • Verify PDF file accessibility");
                    Console.WriteLine("   • Check available disk space");
                    Console.WriteLine("   • Ensure external tools (pdftoppm, convert) are installed");
                    Console.WriteLine("   • Monitor system resource usage");
                }
            }
        }
    }
}
