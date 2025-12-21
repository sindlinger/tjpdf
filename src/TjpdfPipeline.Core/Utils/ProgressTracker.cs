using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace FilterPDF.Utils
{
    /// <summary>
    /// Tracks progress and statistics for batch operations
    /// </summary>
    public class ProgressTracker
    {
        private readonly int _totalItems;
        private int _processedItems;
        private int _successCount;
        private int _failureCount;
        private int _skippedCount;
        private readonly Stopwatch _stopwatch;
        private readonly ConcurrentBag<FileInfo> _cacheFiles;
        private long _totalCacheSize;
        private readonly object _lock = new object();
        private DateTime _lastUpdate = DateTime.Now;
        private readonly bool _showProgress;
        
        public ProgressTracker(int totalItems, bool showProgress = true)
        {
            _totalItems = totalItems;
            _stopwatch = Stopwatch.StartNew();
            _cacheFiles = new ConcurrentBag<FileInfo>();
            _showProgress = showProgress;
        }
        
        public void Start()
        {
            if (_showProgress)
            {
                Console.WriteLine();
                Console.WriteLine("┌─────────────────────────────────────────────────────────────────┐");
                Console.WriteLine($"│ Processing {_totalItems:N0} files                                      │");
                Console.WriteLine("└─────────────────────────────────────────────────────────────────┘");
                Console.WriteLine();
            }
        }
        
        public void UpdateProgress(bool success, bool skipped = false, string? cacheFile = null)
        {
            lock (_lock)
            {
                _processedItems++;
                
                if (skipped)
                    _skippedCount++;
                else if (success)
                    _successCount++;
                else
                    _failureCount++;
                
                if (!string.IsNullOrEmpty(cacheFile) && File.Exists(cacheFile))
                {
                    var fileInfo = new FileInfo(cacheFile);
                    _cacheFiles.Add(fileInfo);
                    _totalCacheSize += fileInfo.Length;
                }
                
                // Update display every 100ms or every 10 items
                if (_showProgress && (DateTime.Now - _lastUpdate).TotalMilliseconds > 100 || _processedItems % 10 == 0)
                {
                    DisplayProgress();
                    _lastUpdate = DateTime.Now;
                }
            }
        }
        
        private void DisplayProgress()
        {
            var percentage = (_processedItems * 100.0) / _totalItems;
            var elapsed = _stopwatch.Elapsed;
            var filesPerSecond = _processedItems / Math.Max(1, elapsed.TotalSeconds);
            var estimatedTotal = TimeSpan.FromSeconds(_totalItems / Math.Max(0.1, filesPerSecond));
            var remaining = estimatedTotal - elapsed;
            
            // Build progress bar
            var barWidth = 50;
            var filled = (int)(barWidth * percentage / 100);
            var progressBar = new string('█', filled) + new string('░', barWidth - filled);
            
            // Clear current line and write progress
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r");
            
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"[{progressBar}] ");
            Console.ResetColor();
            
            Console.Write($"{percentage:F1}% ");
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write($"✓{_successCount} ");
            Console.ResetColor();
            
            if (_skippedCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"⊘{_skippedCount} ");
                Console.ResetColor();
            }
            
            if (_failureCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"✗{_failureCount} ");
                Console.ResetColor();
            }
            
            Console.Write($"| {filesPerSecond:F1} files/s | ");
            Console.Write($"Cache: {FormatFileSize(_totalCacheSize)} | ");
            
            if (remaining.TotalSeconds > 0)
            {
                Console.Write($"ETA: {FormatTimeSpan(remaining)}");
            }
        }
        
        public void Finish()
        {
            _stopwatch.Stop();
            
            if (!_showProgress)
                return;
                
            // Clear the progress line
            Console.WriteLine();
            Console.WriteLine();
            
            // Display summary
            Console.WriteLine("┌─────────────────────────────────────────────────────────────────┐");
            Console.WriteLine("│                         PROCESSING COMPLETE                        │");
            Console.WriteLine("├─────────────────────────────────────────────────────────────────┤");
            
            Console.WriteLine($"│ Total Files:     {_totalItems,10:N0}                                      │");
            Console.WriteLine($"│ Processed:       {_processedItems,10:N0} ({(_processedItems * 100.0 / _totalItems):F1}%)                          │");
            Console.WriteLine($"│ Successful:      {_successCount,10:N0} ({(_successCount * 100.0 / Math.Max(1, _processedItems)):F1}%)                          │");
            
            if (_skippedCount > 0)
                Console.WriteLine($"│ Skipped (cached): {_skippedCount,9:N0} ({(_skippedCount * 100.0 / Math.Max(1, _processedItems)):F1}%)                          │");
                
            if (_failureCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"│ Failed:          {_failureCount,10:N0} ({(_failureCount * 100.0 / Math.Max(1, _processedItems)):F1}%)                          │");
                Console.ResetColor();
            }
            
            Console.WriteLine("├─────────────────────────────────────────────────────────────────┤");
            
            // Cache statistics
            Console.WriteLine($"│ Cache Files:     {_cacheFiles.Count,10:N0}                                      │");
            Console.WriteLine($"│ Total Cache Size: {FormatFileSize(_totalCacheSize),9}                                       │");
            
            if (_cacheFiles.Count > 0)
            {
                var avgSize = _totalCacheSize / _cacheFiles.Count;
                var minSize = _cacheFiles.Min(f => f.Length);
                var maxSize = _cacheFiles.Max(f => f.Length);
                
                Console.WriteLine($"│ Average Size:     {FormatFileSize(avgSize),9}                                       │");
                Console.WriteLine($"│ Min/Max Size:     {FormatFileSize(minSize),9} / {FormatFileSize(maxSize),-9}                   │");
            }
            
            Console.WriteLine("├─────────────────────────────────────────────────────────────────┤");
            
            // Performance statistics
            var totalTime = _stopwatch.Elapsed;
            var filesPerSecond = _processedItems / Math.Max(1, totalTime.TotalSeconds);
            
            Console.WriteLine($"│ Total Time:      {FormatTimeSpan(totalTime),10}                                      │");
            Console.WriteLine($"│ Processing Rate: {filesPerSecond,10:F1} files/second                        │");
            
            if (_processedItems > 0)
            {
                var avgTimePerFile = totalTime.TotalMilliseconds / _processedItems;
                Console.WriteLine($"│ Avg Time/File:   {avgTimePerFile,10:F1} ms                                  │");
            }
            
            Console.WriteLine("└─────────────────────────────────────────────────────────────────┘");
            
            // Cache directory info
            if (_cacheFiles.Count > 0)
            {
                var cacheDir = Path.GetDirectoryName(_cacheFiles.First().FullName);
                if (!string.IsNullOrEmpty(cacheDir))
                {
                    Console.WriteLine();
                    Console.WriteLine($"📁 Cache location: {cacheDir}");
                    
                    // OTIMIZAÇÃO: Não verificar tamanho total para evitar lentidão
                    if (Directory.Exists(cacheDir))
                    {
                        Console.WriteLine($"📊 Cache directory: {cacheDir} (size check disabled for performance)");
                    }
                }
            }
            
            Console.WriteLine();
        }
        
        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double size = bytes;
            int order = 0;
            
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }
            
            return $"{size:F2} {sizes[order]}";
        }
        
        private string FormatTimeSpan(TimeSpan ts)
        {
            if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
            else if (ts.TotalMinutes >= 1)
                return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            else
                return $"{ts.Seconds}s";
        }
    }
}