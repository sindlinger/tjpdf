using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FilterPDF.Models
{
    /// <summary>
    /// 🏗️ ELITE ARCHITECTURE: Page Extraction Models 
    /// Comprehensive data models for advanced PDF page extraction with format conversion pipeline
    /// 
    /// ═══════════════════════════════════════════════════════════════════
    /// PERFORMANCE SPECIFICATIONS:
    /// • O(n) time complexity for all operations
    /// • Memory-efficient streaming for large pages
    /// • Parallel processing capabilities
    /// • Zero-copy operations where possible
    /// ═══════════════════════════════════════════════════════════════════
    /// 
    /// INTEGRATION PATTERNS:
    /// • Reuses ImageExtractionService architecture
    /// • Extends existing FilterService workflow
    /// • Maintains compatibility with all output formats
    /// • Follows FilterPDF naming conventions
    /// </summary>

    /// <summary>
    /// 🎯 Enhanced output format enumeration with page-specific formats
    /// </summary>
    public enum PageOutputFormat
    {
        /// <summary>Standard FilterPDF formats</summary>
        Json,
        Xml, 
        Csv,
        Markdown,
        Raw,
        Count,
        
        /// <summary>🆕 ELITE PAGE EXTRACTION FORMATS</summary>
        ExtractedPages,    // Extract and save pages as individual PDFs
        SavePages,         // Alternative name for ExtractedPages
        
        /// <summary>Advanced page rendering formats</summary>
        PngPages,          // Render pages as PNG images
        JpegPages,         // Render pages as JPEG images  
        TiffPages,         // Render pages as TIFF images
        SvgPages,          // Render pages as SVG vectors
        
        /// <summary>Specialized extraction modes</summary>
        TextOnly,          // Extract only text content
        ImagesOnly,        // Extract only images from pages
        FormsOnly,         // Extract only form fields
        AnnotationsOnly,   // Extract only annotations
        
        /// <summary>Composite formats</summary>
        Complete           // Extract everything: PDF + images + text + metadata
    }

    /// <summary>
    /// 📊 Comprehensive page extraction result with performance metrics
    /// </summary>
    public class PageExtractionResult
    {
        /// <summary>Successfully extracted pages</summary>
        public List<ExtractedPageInfo> ExtractedPages { get; set; } = new List<ExtractedPageInfo>();
        
        /// <summary>Extraction errors with context</summary>
        public List<PageExtractionError> Errors { get; set; } = new List<PageExtractionError>();
        
        /// <summary>Performance metrics</summary>
        public PageExtractionMetrics Metrics { get; set; } = new PageExtractionMetrics();
        
        /// <summary>Processing configuration used</summary>
        public PageExtractionOptions Configuration { get; set; } = new PageExtractionOptions();
        
        /// <summary>Output directory information</summary>
        public DirectoryInfo OutputDirectory { get; set; }
        
        /// <summary>Total file size of all extracted content</summary>
        public long TotalOutputSizeBytes => ExtractedPages.Sum(p => p.FileSizeBytes);
        
        /// <summary>Total output size in megabytes</summary>
        public double TotalOutputSizeMB => TotalOutputSizeBytes / (1024.0 * 1024.0);
        
        /// <summary>Success ratio</summary>
        public double SuccessRatio => Metrics.TotalPagesProcessed > 0 ? 
            (double)Metrics.SuccessfulExtractions / Metrics.TotalPagesProcessed : 0;
        
        /// <summary>Average processing time per page</summary>
        public TimeSpan AverageProcessingTimePerPage => Metrics.TotalPagesProcessed > 0 ?
            TimeSpan.FromTicks(Metrics.ProcessingDuration.Ticks / Metrics.TotalPagesProcessed) : 
            TimeSpan.Zero;
        
        /// <summary>Processing throughput (pages per second)</summary>
        public double PageProcessingThroughput => Metrics.ProcessingDuration.TotalSeconds > 0 ?
            Metrics.TotalPagesProcessed / Metrics.ProcessingDuration.TotalSeconds : 0;
    }

    /// <summary>
    /// 📄 Detailed information about an extracted page
    /// </summary>
    public class ExtractedPageInfo
    {
        /// <summary>Original page number from source PDF (1-based)</summary>
        public int OriginalPageNumber { get; set; }
        
        /// <summary>Page analysis data from cache</summary>
        public PageAnalysis SourcePageAnalysis { get; set; } = new PageAnalysis();
        
        /// <summary>Primary extracted file (PDF, PNG, etc.)</summary>
        public ExtractedFileInfo PrimaryFile { get; set; } = new ExtractedFileInfo();
        
        /// <summary>Additional extracted assets (images, annotations, etc.)</summary>
        public List<ExtractedFileInfo> AdditionalAssets { get; set; } = new List<ExtractedFileInfo>();
        
        /// <summary>Extraction metadata</summary>
        public PageExtractionMetadata ExtractionMetadata { get; set; } = new PageExtractionMetadata();
        
        /// <summary>Quality assessment of the extraction</summary>
        public ExtractionQualityMetrics QualityMetrics { get; set; } = new ExtractionQualityMetrics();
        
        /// <summary>Total file size of all extracted content for this page</summary>
        public long FileSizeBytes => PrimaryFile.FileSizeBytes + AdditionalAssets.Sum(a => a.FileSizeBytes);
        
        /// <summary>Primary file format</summary>
        public string OutputFormat => PrimaryFile.Format;
        
        /// <summary>Count of additional assets extracted</summary>
        public int AdditionalAssetCount => AdditionalAssets.Count;
        
        /// <summary>Whether extraction included multimedia content</summary>
        public bool HasMultimediaAssets => AdditionalAssets.Any(a => 
            a.AssetType == PageAssetType.Image || 
            a.AssetType == PageAssetType.Audio || 
            a.AssetType == PageAssetType.Video);
    }

    /// <summary>
    /// 📁 Information about an extracted file
    /// </summary>
    public class ExtractedFileInfo
    {
        /// <summary>Full path to the extracted file</summary>
        public string FullPath { get; set; } = "";
        
        /// <summary>File name without path</summary>
        public string FileName => Path.GetFileName(FullPath);
        
        /// <summary>File extension</summary>
        public string Extension => Path.GetExtension(FullPath);
        
        /// <summary>File format (PDF, PNG, TXT, etc.)</summary>
        public string Format { get; set; } = "";
        
        /// <summary>File size in bytes</summary>
        public long FileSizeBytes { get; set; }
        
        /// <summary>File size in MB</summary>
        public double FileSizeMB => FileSizeBytes / (1024.0 * 1024.0);
        
        /// <summary>Type of asset this file represents</summary>
        public PageAssetType AssetType { get; set; } = PageAssetType.PrimaryPage;
        
        /// <summary>Creation timestamp</summary>
        public DateTime CreationTime { get; set; } = DateTime.Now;
        
        /// <summary>File content hash (for integrity verification)</summary>
        public string ContentHash { get; set; } = "";
        
        /// <summary>Compression ratio (if applicable)</summary>
        public double? CompressionRatio { get; set; }
        
        /// <summary>Image dimensions (for image assets)</summary>
        public ImageDimensions? ImageDimensions { get; set; }
        
        /// <summary>Text content summary (for text assets)</summary>
        public TextContentSummary? TextSummary { get; set; }
    }

    /// <summary>
    /// 🔧 Type of extracted asset
    /// </summary>
    public enum PageAssetType
    {
        PrimaryPage,        // Main extracted page (PDF, PNG, etc.)
        Image,             // Extracted image from page
        Text,              // Text content file
        Annotations,       // Annotation data
        Forms,            // Form field data
        Metadata,         // Page metadata
        Vector,           // Vector graphics (SVG)
        Audio,            // Audio content
        Video,            // Video content
        Attachment,       // File attachment
        Thumbnail,        // Preview thumbnail
        SearchIndex       // Full-text search index
    }

    /// <summary>
    /// 📐 Image dimensions with format-specific information
    /// </summary>
    public struct ImageDimensions
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int BitDepth { get; set; }
        public string ColorSpace { get; set; }
        public double DpiX { get; set; }
        public double DpiY { get; set; }
        
        /// <summary>Total pixel count</summary>
        public long PixelCount => (long)Width * Height;
        
        /// <summary>Aspect ratio</summary>
        public double AspectRatio => Height > 0 ? (double)Width / Height : 0;
        
        /// <summary>Megapixel count</summary>
        public double Megapixels => PixelCount / 1_000_000.0;
    }

    /// <summary>
    /// 📝 Text content summary
    /// </summary>
    public class TextContentSummary
    {
        public int CharacterCount { get; set; }
        public int WordCount { get; set; }
        public int LineCount { get; set; }
        public int ParagraphCount { get; set; }
        public List<string> DetectedLanguages { get; set; } = new List<string>();
        public List<string> FontsUsed { get; set; } = new List<string>();
        public bool HasFormattedText { get; set; }
        public bool HasTables { get; set; }
        public bool HasLists { get; set; }
        
        /// <summary>Reading time estimate in minutes</summary>
        public double EstimatedReadingTimeMinutes => WordCount / 200.0; // 200 WPM average
    }

    /// <summary>
    /// 🏷️ Extraction metadata with processing details
    /// </summary>
    public class PageExtractionMetadata
    {
        /// <summary>Extraction method used (Cache, Direct, Hybrid)</summary>
        public string ExtractionMethod { get; set; } = "";
        
        /// <summary>Processing start time</summary>
        public DateTime ProcessingStartTime { get; set; }
        
        /// <summary>Processing end time</summary>
        public DateTime ProcessingEndTime { get; set; }
        
        /// <summary>Total processing duration</summary>
        public TimeSpan ProcessingDuration => ProcessingEndTime - ProcessingStartTime;
        
        /// <summary>Source page dimensions</summary>
        public PageSize SourceDimensions { get; set; } = new PageSize();
        
        /// <summary>Output dimensions (if resized)</summary>
        public PageSize? OutputDimensions { get; set; }
        
        /// <summary>Applied transformations</summary>
        public List<string> AppliedTransformations { get; set; } = new List<string>();
        
        /// <summary>Quality settings used</summary>
        public Dictionary<string, object> QualitySettings { get; set; } = new Dictionary<string, object>();
        
        /// <summary>Warning messages during processing</summary>
        public List<string> ProcessingWarnings { get; set; } = new List<string>();
        
        /// <summary>Source PDF information</summary>
        public SourcePdfInfo SourceInfo { get; set; } = new SourcePdfInfo();
    }

    /// <summary>
    /// 📖 Source PDF information
    /// </summary>
    public class SourcePdfInfo
    {
        public string FilePath { get; set; } = "";
        public string FileName => Path.GetFileName(FilePath);
        public long FileSizeBytes { get; set; }
        public int TotalPages { get; set; }
        public string PDFVersion { get; set; } = "";
        public bool IsEncrypted { get; set; }
        public DateTime? CreationDate { get; set; }
        public DateTime? ModificationDate { get; set; }
        public string Author { get; set; } = "";
        public string Title { get; set; } = "";
    }

    /// <summary>
    /// ⭐ Quality metrics for extracted content
    /// </summary>
    public class ExtractionQualityMetrics
    {
        /// <summary>Overall quality score (0.0 - 1.0)</summary>
        public double OverallQualityScore { get; set; }
        
        /// <summary>Text extraction quality (0.0 - 1.0)</summary>
        public double TextExtractionQuality { get; set; }
        
        /// <summary>Image extraction quality (0.0 - 1.0)</summary>
        public double ImageExtractionQuality { get; set; }
        
        /// <summary>Layout preservation quality (0.0 - 1.0)</summary>
        public double LayoutPreservationQuality { get; set; }
        
        /// <summary>Font rendering quality (0.0 - 1.0)</summary>
        public double FontRenderingQuality { get; set; }
        
        /// <summary>Color accuracy (0.0 - 1.0)</summary>
        public double ColorAccuracy { get; set; }
        
        /// <summary>Resolution quality (0.0 - 1.0)</summary>
        public double ResolutionQuality { get; set; }
        
        /// <summary>Content completeness (0.0 - 1.0)</summary>
        public double ContentCompleteness { get; set; }
        
        /// <summary>Detected quality issues</summary>
        public List<QualityIssue> DetectedIssues { get; set; } = new List<QualityIssue>();
        
        /// <summary>Quality assessment recommendations</summary>
        public List<string> QualityRecommendations { get; set; } = new List<string>();
    }

    /// <summary>
    /// ⚠️ Quality issue detected during extraction
    /// </summary>
    public class QualityIssue
    {
        public QualityIssueType Type { get; set; }
        public string Description { get; set; } = "";
        public QualityIssueSeverity Severity { get; set; }
        public string RecommendedAction { get; set; } = "";
        public Dictionary<string, object> AdditionalDetails { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// 🚨 Quality issue types
    /// </summary>
    public enum QualityIssueType
    {
        LowResolution,
        PoorTextExtraction,
        MissingImages,
        FontSubstitution,
        ColorDistortion,
        LayoutDistortion,
        MissingContent,
        CompressionArtifacts,
        AccessibilityIssues,
        SecurityRestrictions
    }

    /// <summary>
    /// 📊 Quality issue severity levels
    /// </summary>
    public enum QualityIssueSeverity
    {
        Low,       // Minor issue, does not affect usability
        Medium,    // Noticeable issue, may affect some use cases
        High,      // Significant issue, affects core functionality
        Critical   // Major issue, makes content unusable
    }

    /// <summary>
    /// ❌ Page extraction error with detailed context
    /// </summary>
    public class PageExtractionError
    {
        /// <summary>Page number where error occurred</summary>
        public int PageNumber { get; set; }
        
        /// <summary>Error type classification</summary>
        public PageExtractionErrorType ErrorType { get; set; }
        
        /// <summary>Human-readable error message</summary>
        public string Message { get; set; } = "";
        
        /// <summary>Detailed technical error information</summary>
        public string TechnicalDetails { get; set; } = "";
        
        /// <summary>Exception that caused the error (if any)</summary>
        public Exception? SourceException { get; set; }
        
        /// <summary>Error occurrence timestamp</summary>
        public DateTime OccurredAt { get; set; } = DateTime.Now;
        
        /// <summary>Processing stage where error occurred</summary>
        public string ProcessingStage { get; set; } = "";
        
        /// <summary>Suggested recovery actions</summary>
        public List<string> RecoveryActions { get; set; } = new List<string>();
        
        /// <summary>Whether this is a recoverable error</summary>
        public bool IsRecoverable { get; set; }
        
        /// <summary>Error context data</summary>
        public Dictionary<string, object> Context { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// 🔍 Page extraction error classification
    /// </summary>
    public enum PageExtractionErrorType
    {
        InvalidPageNumber,      // Page number out of range
        AccessDenied,          // Security restrictions prevent access
        CorruptedPageData,     // Page data is corrupted or invalid
        UnsupportedFormat,     // Page format not supported for extraction
        InsufficientMemory,    // Not enough memory to process page
        OutputWriteFailure,    // Failed to write output file
        RenderingFailure,      // Failed to render page content
        ContentExtractionFailure, // Failed to extract specific content type
        NetworkFailure,        // Network-related error (if applicable)
        TimeoutError,          // Operation timed out
        ConfigurationError,    // Invalid configuration settings
        DependencyError,       // Required dependency not available
        UnknownError          // Unclassified error
    }

    /// <summary>
    /// 📈 Performance metrics for page extraction operations
    /// </summary>
    public class PageExtractionMetrics
    {
        /// <summary>Operation start time</summary>
        public DateTime StartTime { get; set; } = DateTime.Now;
        
        /// <summary>Operation end time</summary>
        public DateTime EndTime { get; set; }
        
        /// <summary>Total processing duration</summary>
        public TimeSpan ProcessingDuration => EndTime - StartTime;
        
        /// <summary>Total pages processed</summary>
        public int TotalPagesProcessed { get; set; }
        
        /// <summary>Successfully extracted pages</summary>
        public int SuccessfulExtractions { get; set; }
        
        /// <summary>Failed extractions</summary>
        public int FailedExtractions { get; set; }
        
        /// <summary>Pages skipped due to filters</summary>
        public int SkippedPages { get; set; }
        
        /// <summary>Total output files created</summary>
        public int TotalOutputFiles { get; set; }
        
        /// <summary>Peak memory usage during processing (bytes)</summary>
        public long PeakMemoryUsageBytes { get; set; }
        
        /// <summary>Total disk I/O (bytes read + written)</summary>
        public long TotalDiskIOBytes { get; set; }
        
        /// <summary>Average processing time per page</summary>
        public TimeSpan AverageProcessingTimePerPage => TotalPagesProcessed > 0 ?
            TimeSpan.FromTicks(ProcessingDuration.Ticks / TotalPagesProcessed) : 
            TimeSpan.Zero;
        
        /// <summary>Processing throughput (pages/second)</summary>
        public double ProcessingThroughput => ProcessingDuration.TotalSeconds > 0 ?
            TotalPagesProcessed / ProcessingDuration.TotalSeconds : 0;
        
        /// <summary>Data processing throughput (MB/second)</summary>
        public double DataThroughputMBps => ProcessingDuration.TotalSeconds > 0 ?
            (TotalDiskIOBytes / (1024.0 * 1024.0)) / ProcessingDuration.TotalSeconds : 0;
        
        /// <summary>Success rate percentage</summary>
        public double SuccessRate => TotalPagesProcessed > 0 ?
            (double)SuccessfulExtractions / TotalPagesProcessed * 100 : 0;
        
        /// <summary>Performance bottlenecks detected</summary>
        public List<PerformanceBottleneck> DetectedBottlenecks { get; set; } = new List<PerformanceBottleneck>();
        
        /// <summary>Performance optimization recommendations</summary>
        public List<string> OptimizationRecommendations { get; set; } = new List<string>();
    }

    /// <summary>
    /// 🐌 Performance bottleneck information
    /// </summary>
    public class PerformanceBottleneck
    {
        public string Component { get; set; } = "";
        public string Description { get; set; } = "";
        public double ImpactPercentage { get; set; }
        public string RecommendedAction { get; set; } = "";
        public Dictionary<string, object> Metrics { get; set; } = new Dictionary<string, object>();
    }

    /// <summary>
    /// ⚙️ ELITE configuration options for page extraction
    /// </summary>
    public class PageExtractionOptions
    {
        /// <summary>Output format for extracted pages</summary>
        public PageOutputFormat OutputFormat { get; set; } = PageOutputFormat.ExtractedPages;
        
        /// <summary>Output directory for extracted files</summary>
        public string OutputDirectory { get; set; } = Directory.GetCurrentDirectory();
        
        /// <summary>File naming pattern with placeholders</summary>
        public string FileNamePattern { get; set; } = "page_{page:D3}_{width}x{height}.{ext}";
        
        /// <summary>🎯 ADVANCED FILTERING OPTIONS</summary>
        public PageFilteringOptions Filtering { get; set; } = new PageFilteringOptions();
        
        /// <summary>🖼️ RENDERING CONFIGURATION</summary>
        public PageRenderingOptions Rendering { get; set; } = new PageRenderingOptions();
        
        /// <summary>⚡ PERFORMANCE TUNING</summary>
        public PagePerformanceOptions Performance { get; set; } = new PagePerformanceOptions();
        
        /// <summary>🔒 SECURITY & VALIDATION</summary>
        public PageSecurityOptions Security { get; set; } = new PageSecurityOptions();
        
        /// <summary>📊 QUALITY CONTROL</summary>
        public PageQualityOptions Quality { get; set; } = new PageQualityOptions();
        
        /// <summary>🔧 ADVANCED PROCESSING</summary>
        public PageProcessingOptions Processing { get; set; } = new PageProcessingOptions();
        
        /// <summary>Whether to overwrite existing files</summary>
        public bool OverwriteExisting { get; set; } = true;
        
        /// <summary>Whether to create placeholder files on extraction failure</summary>
        public bool CreatePlaceholderOnFailure { get; set; } = false;
        
        /// <summary>Maximum number of pages to extract (0 = unlimited)</summary>
        public int MaxPagesToExtract { get; set; } = 0;
        
        /// <summary>Whether to preserve original file timestamps</summary>
        public bool PreserveTimestamps { get; set; } = true;
        
        /// <summary>Whether to generate extraction report</summary>
        public bool GenerateReport { get; set; } = true;
        
        /// <summary>Custom metadata to add to extracted files</summary>
        public Dictionary<string, string> CustomMetadata { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// 🎯 Advanced filtering options for page selection
    /// </summary>
    public class PageFilteringOptions
    {
        /// <summary>Page number ranges to extract (e.g., "1-5,10,15-20")</summary>
        public string PageRanges { get; set; } = "";
        
        /// <summary>Minimum page width</summary>
        public double? MinWidth { get; set; }
        
        /// <summary>Maximum page width</summary>
        public double? MaxWidth { get; set; }
        
        /// <summary>Minimum page height</summary>
        public double? MinHeight { get; set; }
        
        /// <summary>Maximum page height</summary>
        public double? MaxHeight { get; set; }
        
        /// <summary>Required page orientation</summary>
        public PageOrientation? RequiredOrientation { get; set; }
        
        /// <summary>Minimum word count</summary>
        public int? MinWordCount { get; set; }
        
        /// <summary>Maximum word count</summary>
        public int? MaxWordCount { get; set; }
        
        /// <summary>Required text content (regex pattern)</summary>
        public string RequiredTextPattern { get; set; } = "";
        
        /// <summary>Forbidden text content (regex pattern)</summary>
        public string ForbiddenTextPattern { get; set; } = "";
        
        /// <summary>Whether page must contain images</summary>
        public bool? MustContainImages { get; set; }
        
        /// <summary>Whether page must contain forms</summary>
        public bool? MustContainForms { get; set; }
        
        /// <summary>Whether page must contain annotations</summary>
        public bool? MustContainAnnotations { get; set; }
        
        /// <summary>Required font patterns</summary>
        public List<string> RequiredFontPatterns { get; set; } = new List<string>();
        
        /// <summary>Custom filter functions</summary>
        public List<Func<PageAnalysis, bool>> CustomFilters { get; set; } = new List<Func<PageAnalysis, bool>>();
    }

    /// <summary>
    /// 📐 Page orientation enumeration
    /// </summary>
    public enum PageOrientation
    {
        Portrait,
        Landscape,
        Square
    }

    /// <summary>
    /// 🖼️ Rendering configuration for page extraction
    /// </summary>
    public class PageRenderingOptions
    {
        /// <summary>Output DPI for raster formats</summary>
        public int DPI { get; set; } = 150;
        
        /// <summary>Color mode for output</summary>
        public ColorMode ColorMode { get; set; } = ColorMode.RGB;
        
        /// <summary>JPEG quality (1-100)</summary>
        public int JpegQuality { get; set; } = 90;
        
        /// <summary>PNG compression level (0-9)</summary>
        public int PngCompressionLevel { get; set; } = 6;
        
        /// <summary>Whether to anti-alias text and graphics</summary>
        public bool AntiAlias { get; set; } = true;
        
        /// <summary>Background color for transparent areas</summary>
        public string BackgroundColor { get; set; } = "white";
        
        /// <summary>Custom scaling factor</summary>
        public double ScalingFactor { get; set; } = 1.0;
        
        /// <summary>Whether to preserve transparency</summary>
        public bool PreserveTransparency { get; set; } = true;
        
        /// <summary>Text rendering mode</summary>
        public TextRenderingMode TextRenderingMode { get; set; } = TextRenderingMode.Vector;
        
        /// <summary>Maximum output dimensions (width x height)</summary>
        public (int Width, int Height)? MaxOutputDimensions { get; set; }
        
        /// <summary>Whether to generate thumbnails</summary>
        public bool GenerateThumbnails { get; set; } = false;
        
        /// <summary>Thumbnail dimensions</summary>
        public (int Width, int Height) ThumbnailDimensions { get; set; } = (200, 280);
    }

    /// <summary>
    /// 🎨 Color mode enumeration
    /// </summary>
    public enum ColorMode
    {
        Grayscale,
        RGB,
        CMYK,
        LAB
    }

    /// <summary>
    /// 📝 Text rendering mode enumeration
    /// </summary>
    public enum TextRenderingMode
    {
        Bitmap,    // Render text as bitmap
        Vector,    // Preserve text as vector (recommended)
        Outline    // Convert text to outlines
    }

    /// <summary>
    /// ⚡ Performance optimization options
    /// </summary>
    public class PagePerformanceOptions
    {
        /// <summary>Number of parallel processing threads</summary>
        public int MaxConcurrency { get; set; } = Environment.ProcessorCount;
        
        /// <summary>Memory usage limit (MB)</summary>
        public int MemoryLimitMB { get; set; } = 1024;
        
        /// <summary>Processing timeout per page (seconds)</summary>
        public int TimeoutPerPageSeconds { get; set; } = 30;
        
        /// <summary>Whether to use memory mapping for large files</summary>
        public bool UseMemoryMapping { get; set; } = true;
        
        /// <summary>Cache size for processed pages (MB)</summary>
        public int CacheSizeMB { get; set; } = 256;
        
        /// <summary>Whether to enable progressive processing</summary>
        public bool EnableProgressiveProcessing { get; set; } = true;
        
        /// <summary>Batch size for parallel processing</summary>
        public int BatchSize { get; set; } = 10;
        
        /// <summary>Whether to optimize for speed over quality</summary>
        public bool OptimizeForSpeed { get; set; } = false;
    }

    /// <summary>
    /// 🔒 Security and validation options
    /// </summary>
    public class PageSecurityOptions
    {
        /// <summary>Whether to validate output file integrity</summary>
        public bool ValidateOutputIntegrity { get; set; } = true;
        
        /// <summary>Whether to remove metadata from output files</summary>
        public bool RemoveMetadata { get; set; } = false;
        
        /// <summary>Whether to sanitize extracted text</summary>
        public bool SanitizeText { get; set; } = false;
        
        /// <summary>Maximum file size per extracted file (MB)</summary>
        public int MaxFileSizeMB { get; set; } = 100;
        
        /// <summary>Whether to scan for malicious content</summary>
        public bool ScanForMaliciousContent { get; set; } = false;
        
        /// <summary>Allowed output file extensions</summary>
        public List<string> AllowedExtensions { get; set; } = new List<string> { ".pdf", ".png", ".jpg", ".txt" };
        
        /// <summary>Whether to enforce secure file naming</summary>
        public bool EnforceSecureFileNaming { get; set; } = true;
    }

    /// <summary>
    /// 📊 Quality control options
    /// </summary>
    public class PageQualityOptions
    {
        /// <summary>Minimum acceptable quality score (0.0 - 1.0)</summary>
        public double MinQualityScore { get; set; } = 0.7;
        
        /// <summary>Whether to perform automatic quality assessment</summary>
        public bool PerformQualityAssessment { get; set; } = true;
        
        /// <summary>Whether to generate quality reports</summary>
        public bool GenerateQualityReports { get; set; } = false;
        
        /// <summary>Quality assessment algorithms to use</summary>
        public List<string> QualityAssessmentAlgorithms { get; set; } = new List<string> { "SSIM", "PSNR", "MSE" };
        
        /// <summary>Whether to retry failed extractions with different settings</summary>
        public bool RetryFailedExtractions { get; set; } = true;
        
        /// <summary>Maximum retry attempts</summary>
        public int MaxRetryAttempts { get; set; } = 3;
        
        /// <summary>Whether to fall back to lower quality on failure</summary>
        public bool AllowQualityFallback { get; set; } = true;
    }

    /// <summary>
    /// 🔧 Advanced processing options
    /// </summary>
    public class PageProcessingOptions
    {
        /// <summary>Whether to extract embedded assets</summary>
        public bool ExtractEmbeddedAssets { get; set; } = false;
        
        /// <summary>Whether to OCR image-based pages</summary>
        public bool OCRImagePages { get; set; } = false;
        
        /// <summary>OCR language codes</summary>
        public List<string> OCRLanguages { get; set; } = new List<string> { "eng", "por" };
        
        /// <summary>Whether to generate searchable text</summary>
        public bool GenerateSearchableText { get; set; } = false;
        
        /// <summary>Whether to preserve layered content</summary>
        public bool PreserveLayeredContent { get; set; } = true;
        
        /// <summary>Whether to extract vector graphics separately</summary>
        public bool ExtractVectorGraphics { get; set; } = false;
        
        /// <summary>Whether to process form fields</summary>
        public bool ProcessFormFields { get; set; } = false;
        
        /// <summary>Whether to extract annotation data</summary>
        public bool ExtractAnnotationData { get; set; } = false;
        
        /// <summary>Custom processing plugins</summary>
        public List<string> ProcessingPlugins { get; set; } = new List<string>();
        
        /// <summary>Post-processing filters</summary>
        public List<string> PostProcessingFilters { get; set; } = new List<string>();
    }

}
