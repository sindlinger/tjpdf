using System;
using System.Collections.Generic;
using System.Linq;
using FilterPDF.Models;

namespace FilterPDF.Models
{
    /// <summary>
    /// Options for PNG conversion operations
    /// </summary>
    public class PngConversionOptions
    {
        /// <summary>
        /// Output directory for PNG files
        /// </summary>
        public string OutputDirectory { get; set; } = "./extracted_pages";
        
        /// <summary>
        /// DPI resolution for conversion (default: 150)
        /// </summary>
        public int Resolution { get; set; } = 150;
        
        /// <summary>
        /// Maximum number of parallel conversions
        /// </summary>
        public int MaxConcurrency { get; set; } = Environment.ProcessorCount;
        
        /// <summary>
        /// Timeout for each conversion operation in seconds
        /// </summary>
        public int TimeoutSeconds { get; set; } = 30;
        
        /// <summary>
        /// Naming pattern for output files
        /// </summary>
        public string FileNamePattern { get; set; } = "{pdfName}_page_{pageNumber:D3}.png";
        
        /// <summary>
        /// Whether to overwrite existing files
        /// </summary>
        public bool OverwriteExisting { get; set; } = true;
        
        /// <summary>
        /// Preferred conversion tool (pdftoppm, imagemagick, auto)
        /// </summary>
        public ConversionTool PreferredTool { get; set; } = ConversionTool.Auto;
        
        /// <summary>
        /// Whether to show progress information
        /// </summary>
        public bool ShowProgress { get; set; } = true;
        
        /// <summary>
        /// Additional quality settings
        /// </summary>
        public PngQualitySettings QualitySettings { get; set; } = new PngQualitySettings();
    }

    /// <summary>
    /// PNG quality and format settings
    /// </summary>
    public class PngQualitySettings
    {
        /// <summary>
        /// Image quality (1-100 for JPEG compatibility, affects file size)
        /// </summary>
        public int Quality { get; set; } = 95;
        
        /// <summary>
        /// Enable antialiasing
        /// </summary>
        public bool Antialiasing { get; set; } = true;
        
        /// <summary>
        /// Color depth (8, 16, 24, 32 bits)
        /// </summary>
        public int ColorDepth { get; set; } = 24;
        
        /// <summary>
        /// Enable PNG compression optimization
        /// </summary>
        public bool OptimizeCompression { get; set; } = true;
    }

    /// <summary>
    /// Available conversion tools
    /// </summary>
    public enum ConversionTool
    {
        Auto,
        Pdftoppm,
        ImageMagick,
        Ghostscript
    }

    /// <summary>
    /// Result of PNG conversion operation
    /// </summary>
    public class PngConversionResult
    {
        /// <summary>
        /// List of successfully converted files
        /// </summary>
        public List<ConvertedPageInfo> ConvertedPages { get; set; } = new List<ConvertedPageInfo>();
        
        /// <summary>
        /// List of conversion errors
        /// </summary>
        public List<ConversionError> Errors { get; set; } = new List<ConversionError>();
        
        /// <summary>
        /// Total pages processed
        /// </summary>
        public int TotalPages { get; set; }
        
        /// <summary>
        /// Number of successful conversions
        /// </summary>
        public int SuccessCount => ConvertedPages.Count;
        
        /// <summary>
        /// Number of failed conversions
        /// </summary>
        public int FailureCount => Errors.Count;
        
        /// <summary>
        /// Conversion start time
        /// </summary>
        public DateTime StartTime { get; set; }
        
        /// <summary>
        /// Conversion end time
        /// </summary>
        public DateTime EndTime { get; set; }
        
        /// <summary>
        /// Total conversion duration
        /// </summary>
        public TimeSpan Duration => EndTime - StartTime;
        
        /// <summary>
        /// Output directory used
        /// </summary>
        public string OutputDirectory { get; set; } = "";
        
        /// <summary>
        /// Tool used for conversion
        /// </summary>
        public ConversionTool ToolUsed { get; set; }
        
        /// <summary>
        /// Whether the operation completed successfully
        /// </summary>
        public bool IsSuccess => FailureCount == 0 && SuccessCount > 0;
        
        /// <summary>
        /// Success rate percentage
        /// </summary>
        public double SuccessRate => TotalPages > 0 ? (double)SuccessCount / TotalPages * 100 : 0;
    }

    /// <summary>
    /// Information about a successfully converted page
    /// </summary>
    public class ConvertedPageInfo
    {
        /// <summary>
        /// Source PDF file path
        /// </summary>
        public string SourcePdfPath { get; set; } = "";
        
        /// <summary>
        /// Page number in source PDF
        /// </summary>
        public int PageNumber { get; set; }
        
        /// <summary>
        /// Output PNG file path
        /// </summary>
        public string OutputFilePath { get; set; } = "";
        
        /// <summary>
        /// File size of generated PNG in bytes
        /// </summary>
        public long FileSizeBytes { get; set; }
        
        /// <summary>
        /// Conversion duration for this page
        /// </summary>
        public TimeSpan ConversionDuration { get; set; }
        
        /// <summary>
        /// Tool used for this conversion
        /// </summary>
        public ConversionTool ToolUsed { get; set; }
        
        /// <summary>
        /// Image dimensions
        /// </summary>
        public ImageDimensions Dimensions { get; set; } = new ImageDimensions();
    }


    /// <summary>
    /// Information about a conversion error
    /// </summary>
    public class ConversionError
    {
        /// <summary>
        /// Source PDF file path
        /// </summary>
        public string SourcePdfPath { get; set; } = "";
        
        /// <summary>
        /// Page number that failed
        /// </summary>
        public int PageNumber { get; set; }
        
        /// <summary>
        /// Error message
        /// </summary>
        public string ErrorMessage { get; set; } = "";
        
        /// <summary>
        /// Exception details if available
        /// </summary>
        public string? ExceptionDetails { get; set; }
        
        /// <summary>
        /// Tool that was attempted
        /// </summary>
        public ConversionTool AttemptedTool { get; set; }
        
        /// <summary>
        /// Timestamp when error occurred
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;
        
        /// <summary>
        /// Error severity level
        /// </summary>
        public ErrorSeverity Severity { get; set; } = ErrorSeverity.Error;
    }

    /// <summary>
    /// Error severity levels
    /// </summary>
    public enum ErrorSeverity
    {
        Warning,
        Error,
        Critical
    }

    /// <summary>
    /// Tool availability check result
    /// </summary>
    public class ToolAvailabilityResult
    {
        /// <summary>
        /// Available conversion tools
        /// </summary>
        public Dictionary<ConversionTool, ToolInfo> AvailableTools { get; set; } = new Dictionary<ConversionTool, ToolInfo>();
        
        /// <summary>
        /// Recommended tool based on availability and performance
        /// </summary>
        public ConversionTool RecommendedTool { get; set; }
        
        /// <summary>
        /// Whether any tools are available
        /// </summary>
        public bool HasAvailableTools => AvailableTools.Values.Any(t => t.IsAvailable);
        
        /// <summary>
        /// System information relevant to PNG conversion
        /// </summary>
        public SystemInfo SystemInfo { get; set; } = new SystemInfo();
    }

    /// <summary>
    /// Information about a specific tool
    /// </summary>
    public class ToolInfo
    {
        /// <summary>
        /// Whether the tool is available
        /// </summary>
        public bool IsAvailable { get; set; }
        
        /// <summary>
        /// Tool version if available
        /// </summary>
        public string? Version { get; set; }
        
        /// <summary>
        /// Tool executable path
        /// </summary>
        public string? ExecutablePath { get; set; }
        
        /// <summary>
        /// Performance rating (1-10, higher is better)
        /// </summary>
        public int PerformanceRating { get; set; }
        
        /// <summary>
        /// Supported features
        /// </summary>
        public List<string> SupportedFeatures { get; set; } = new List<string>();
        
        /// <summary>
        /// Any limitations or notes
        /// </summary>
        public string? Notes { get; set; }
    }

    /// <summary>
    /// System information for PNG conversion
    /// </summary>
    public class SystemInfo
    {
        public int ProcessorCount { get; set; } = Environment.ProcessorCount;
        public long AvailableMemoryMB { get; set; }
        public string OperatingSystem { get; set; } = Environment.OSVersion.ToString();
        public bool Is64BitProcess { get; set; } = Environment.Is64BitProcess;
    }

    /// <summary>
    /// Request for external process execution
    /// </summary>
    public class ProcessExecutionRequest
    {
        /// <summary>
        /// Executable name or path
        /// </summary>
        public string FileName { get; set; } = "";
        
        /// <summary>
        /// Command line arguments
        /// </summary>
        public string Arguments { get; set; } = "";
        
        /// <summary>
        /// Working directory for the process
        /// </summary>
        public string? WorkingDirectory { get; set; }
        
        /// <summary>
        /// Timeout in milliseconds
        /// </summary>
        public int TimeoutMilliseconds { get; set; } = 30000;
        
        /// <summary>
        /// Environment variables to set
        /// </summary>
        public Dictionary<string, string> EnvironmentVariables { get; set; } = new Dictionary<string, string>();
        
        /// <summary>
        /// Whether to capture standard output
        /// </summary>
        public bool CaptureOutput { get; set; } = true;
        
        /// <summary>
        /// Whether to capture standard error
        /// </summary>
        public bool CaptureError { get; set; } = true;
        
        /// <summary>
        /// Input data to send to the process
        /// </summary>
        public string? InputData { get; set; }
    }

    /// <summary>
    /// Result of external process execution
    /// </summary>
    public class ProcessExecutionResult
    {
        /// <summary>
        /// Process exit code
        /// </summary>
        public int ExitCode { get; set; }
        
        /// <summary>
        /// Standard output content
        /// </summary>
        public string StandardOutput { get; set; } = "";
        
        /// <summary>
        /// Standard error content
        /// </summary>
        public string StandardError { get; set; } = "";
        
        /// <summary>
        /// Whether the process completed successfully
        /// </summary>
        public bool IsSuccess => ExitCode == 0;
        
        /// <summary>
        /// Process execution duration
        /// </summary>
        public TimeSpan Duration { get; set; }
        
        /// <summary>
        /// Whether the process timed out
        /// </summary>
        public bool TimedOut { get; set; }
        
        /// <summary>
        /// Exception if process execution failed
        /// </summary>
        public Exception? Exception { get; set; }
        
        /// <summary>
        /// Process start time
        /// </summary>
        public DateTime StartTime { get; set; }
        
        /// <summary>
        /// Process end time
        /// </summary>
        public DateTime EndTime { get; set; }
    }
}