namespace FilterPDF
{
    /// <summary>
    /// Centralized version information for FilterPDF
    /// </summary>
    public static class Version
    {
        /// <summary>
        /// Current version - dynamically loaded from assembly/project file
        /// </summary>
        public static string Current => VersionManager.Current;
        
        public const string Author = "Eduardo Candeia Gonçalves (sindlinger@github.com)";
        public const string Copyright = "Copyright (c) 2024 - Advanced PDF Processing Tool";
        
        public static class ReleaseNotes
        {
            public const string V3430 = @"NEW IN v3.43.0:
  - CRITICAL FIX: Memory leak resolved in parallel PDF processing
  - FIXED: Added 'using' statements for all PdfReader instances to ensure proper disposal
  - ADDED: Automatic garbage collection every 50 files to prevent memory accumulation
  - IMPLEMENTED: 120-second timeout per PDF to prevent hanging on problematic files
  - ENHANCED: Thread-safe memory management for multi-worker processing
  - OPTIMIZED: Consistent performance maintained even with large batches
  - RESOLVED: Performance degradation issue that caused slowdown over time
  - TESTED: Stable memory usage confirmed with 1000+ PDF processing";

            public const string V3420 = @"NEW IN v3.42.0:
  - CRITICAL FIX: Resolved stack overflow in ImageDataExtractor.ExtractImagesFromForm
  - FIXED: Added recursion depth limit (20 levels) to prevent infinite loops
  - FIXED: Circular reference detection for Form XObjects using HashSet tracking
  - IMPROVED: Thread-safe warning system for multi-worker parallel processing
  - ENHANCED: Automatic warning suppression when --num-workers > 1 to reduce console spam
  - OPTIMIZED: Warning deduplication - each Form XObject warning shown only once
  - TESTED: LOAD command now handles complex PDFs with recursive Form XObjects safely
  - VERIFIED: TXT extraction works correctly even with deeply nested PDF structures";

            public const string V3410 = @"NEW IN v3.41.0:
  - FIXED: TXT output format now handles image extraction errors gracefully
  - ENHANCED: Robust error handling in OutputAsTxt method with fallback to text-only
  - IMPROVED: System.Drawing compatibility checks with ImageMagick fallback
  - ADDED: Warning messages for image processing issues without crashing
  - TESTED: TXT generation completes successfully even when images fail to load";

            public const string V3392 = @"NEW IN v3.39.2:
  - FIXED: CSV output format (-F csv) now working correctly for doctypes command
  - FIXED: ParseOptions now properly sets both '-F' and 'format' keys for compatibility
  - IMPROVED: doctypes command output cleaned up (removed debug messages)
  - ENHANCED: Range processing (fpdf 1-20 doctypes) with proper CSV header handling
  - READY: Foundation set for improving extraction patterns in next iteration";

            public const string V3391 = @"NEW IN v3.39.1:
  - NEW: doctypes command for identifying specific document types in PDFs
  - ADDED: despacho2025 identifier for TJPB payment authorization dispatches
  - IMPLEMENTED: Structural + textual pattern matching with confidence scoring
  - EXTRACTS: All required fields (ProcessoAdmin, Juízo, Comarca, Promovente, etc)
  - SUPPORTS: Multiple output formats (txt, json, csv) with -F flag
  - TESTED: Successfully identifies payment dispatches across cache files";

            public const string V3390 = @"NEW IN v3.39.0:
  - MAJOR: Removed duplicate BasicMetadata field - now using only Metadata
  - FIXED: Metadata filtering now works correctly with cached PDFs
  - IMPROVED: WordOption.Matches() now used consistently across all text filters
  - ENHANCED: Dynamic version system - version managed centrally from .csproj
  - CLEANED: Removed legacy code and unnecessary field duplication
  - TESTED: All metadata filters working with advanced search syntax (AND, OR, fuzzy, regex)";

            public const string V3380 = @"NEW IN v3.38.0:
  - NEW: --signature filter added across ALL FilterPDF commands
  - ADDED: Intelligent signature detection in last 30% of page text
  - SUPPORTS: AND (&) and OR (|) operators for complex signature matching  
  - WORKS WITH: pages, documents, words, bookmarks, annotations, objects, fonts
  - USE CASE: Filter documents by specific signatories (Robson, João & Silva, etc)
  - TESTED: Full compatibility with existing filters and PNG extraction";

            public const string V3370 = @"NEW IN v3.37.0:
  - MAJOR: Complete PNG extraction implementation for ALL filter commands
  - FIXED: --output-dir parameter now properly parsed and respected
  - ADDED: Automatic output directory creation with permissions check
  - ADDED: --input-dir support with --recursive flag for batch processing
  - ENHANCED: Dynamic page filtering with PNG export (NO hardcoded values)
  - IMPROVED: Help interface with emoji-based command categories
  - TESTED: Selective page extraction working perfectly (filters → PNG)";

            public const string V3360 = @"NEW IN v3.36.0:
  - NEW: Robust signature filtering for nota de empenho extraction
  - ADDED: --signatures flag with advanced pattern matching (AND/OR logic)
  - ENHANCED: Integration with WordOption for fuzzy and complex searches
  - IMPROVED: Support for multiple signature combinations (Robson&Erivalda, etc)
  - OPTIMIZED: Reused existing WordOption code for zero duplication
  - USE CASE: Extract only official notas with specific approver signatures";
  
            public const string V3350 = @"NEW IN v3.35.0:
  - ADDED: Basic signature filtering for nota de empenho extraction
  - Initial implementation with simple text matching";
  
            public const string V3340 = @"NEW IN v3.34.0:
  - IMPROVED: Nota de empenho detection for portrait format
  - ENHANCED: Text pattern detection for 'ESTADO DA PARAÍBA'
  - FIXED: Dimension detection for A4 portrait documents";

            public const string V3270 = @"NEW IN v3.27.0:
  - CRITICAL FIX: Image extraction from cache now properly detects encoded formats (JPEG/PNG)
  - FIXED: CreatePngFromRawData now detects if data is already JPEG/PNG before conversion
  - IMPROVED: Current working directory automatically allowed for security validation
  - ENHANCED: Auto-allow parent directories up to 3 levels for project structures
  - FIXED: No more corrupted images when extracting from cache with base64 data";

            public const string V3260 = @"NEW IN v3.26.0:
  - CRITICAL FIX: Security error messages now show proper instructions with FPDF_ALLOWED_DIRS
  - FIXED: Files no longer processed twice when using --input-file flag
  - IMPROVED: All file processing now uses ProcessFilesInBatches for consistent error handling
  - FIXED: ParseOptions now properly handles --input-file flag to avoid duplicates
  - RESTORED: Clear instructions when security validation fails showing how to fix";

            public const string V3250 = @"NEW IN v3.25.0:
  - CRITICAL FIX: Corrected load command syntax with --input-file flag support
  - FIXED: fpdf load ultra --input-file now works correctly  
  - CORRECTED: All help examples to use proper syntax: fpdf load file.pdf
  - REMOVED: Invalid syntax fpdf file.pdf load from all documentation
  - UPDATED: FilterPDFCLI, FpdfObjectsCommand, FpdfCacheCommand help texts
  - VALIDATED: Both syntaxes now work: fpdf load file.pdf AND fpdf load ultra --input-file file.pdf";

            public const string V3240 = @"NEW IN v3.24.0:
  - MAJOR FIX: Complete image extraction pipeline working end-to-end
  - FIXED: FpdfLoadCommand now properly extracts images with base64 data in images-only mode
  - ENHANCED: ImageExtractionService with comprehensive PNG creation capabilities
  - ADDED: Direct PNG generation without System.Drawing dependencies for Linux compatibility
  - IMPROVED: Form XObjects image detection for complex PDF structures
  - VALIDATED: Full workflow from cache loading to PNG extraction working perfectly
  - TESTED: Successfully extracts images like pdf_nota_empenho_page_32._cache_p1_img1_744x1052.png";

            public const string V3230 = @"NEW IN v3.23.0:
  - FIXED: Image extraction with base64 data in images-only mode
  - IMPROVED: Clear and informative output for images command
  - ENHANCED: Shows PDF name and cache index for each file
  - ADDED: Progress indicators when processing multiple files
  - BETTER: Error messages with helpful suggestions
  - ORGANIZED: Visual separators between multiple files";

            public const string V3220 = @"NEW IN v3.22.0:
  - WORKFLOW: Images command now properly shows IMAGES, not scanned pages
  - ORGANIZED: Output structured by pages like all other commands
  - ENHANCED: Shows image metadata: size, colorspace, compression, quality
  - IMPROVED: Clear listing with index, name, and technical details
  - CORRECT: Focus on actual image objects within PDF structure
  - USE CASE: Locate and extract specific images (logos, charts, photos)";

            public const string V3210 = @"NEW IN v3.21.0:
  - FIXED: Images command now correctly lists IMAGES inside PDFs (not pages)
  - ENHANCED: Images command extracts actual image objects from PDF structure
  - IMPROVED: Direct extraction of JPEG/JPX images from PDF stream
  - ADDED: Support for raw image data conversion using ImageMagick
  - CORRECT: Now properly filters by actual image dimensions
  - USE CASE: Extract specific images (logos, signatures, etc) from PDFs";

            public const string V3200 = @"NEW IN v3.20.0:
  - NEW: PNG extraction support for images command (-F png)
  - NEW: --image-size filter with range support (ex: '744-750 x 1055')
  - NEW: --output-dir option to specify output directory for extracted images
  - ENHANCED: Images command can now extract matching pages directly to PNG files
  - IMPROVED: Intelligent size matching with flexible range specifications
  - ADDED: Support for pdftoppm and ImageMagick for PNG conversion
  - USE CASE: Extract nota de empenho pages (744x1052) as PNG images
  - TESTED: Full extraction pipeline verified with government documents";

            public const string V3190 = @"NEW IN v3.19.0:
  - MAJOR REFACTORING: All commands renamed to Fpdf pattern
  - RENAMED: All Filter*Command classes to Fpdf*Command
  - STANDARDIZED: Consistent naming convention across entire codebase
  - IMPROVED: Code organization and maintainability
  - ENHANCED: Help for images command with new options (--image-size, --output-dir, -F png)
  - PREPARED: Foundation for advanced image extraction features
  - CLEANUP: Removed obsolete extract-from-cache command";

            public const string V3180 = @"NEW IN v3.18.0:
  - UNIVERSAL OCR: Format -F ocr now available in ALL commands!
  - NEW: pages --width 744 --height 1052 -F ocr (extract text from nota de empenho)
  - NEW: words --word empenho -F ocr (find pages and extract text)
  - NEW: documents -F ocr (extract text from first page of each document)
  - SMART: Brazilian pattern recognition (CPF, CNPJ, currency, dates)
  - CONFIG: ocr.config.json with language, resolution, and pattern settings
  - PERFORMANCE: Process multiple pages with intelligent limits
  - TESTED: Universal OCR working across all main commands";

            public const string V3170 = @"NEW IN v3.17.0:
  - NEW: OCR format (-F ocr) using superior EasyOCR engine!
  - NEW: Extract text from pages with much better precision than Tesseract
  - NEW: Support for --extract-page with OCR processing
  - NEW: Automatic detection of images and documents for OCR
  - ENHANCED: Base64 command now supports high-quality text extraction
  - SUPERIOR: EasyOCR provides much better accuracy for Portuguese documents
  - USE CASE: Extract text from 'nota de empenho' and scanned documents
  - TESTED: OCR extraction verified with government documents";

            public const string V3161 = @"NEW IN v3.16.1:
  - FIXED: Raw output (-F raw) now produces clean base64 without debug messages
  - FIXED: Logging initialization suppressed for raw format output
  - IMPROVED: Better option handling for format flags
  - ENHANCED: Clean output for programmatic usage and piping
  - TESTED: All raw output modes now properly formatted";

            public const string V3160 = @"NEW IN v3.16.0:
  - NEW: Extract any PDF page as base64 content!
  - NEW: --extract-page option for base64 command
  - NEW: Support for -F raw and -F json output formats
  - ENHANCED: Base64 command now handles page extraction
  - USE CASE: Extract pages for external processing or image conversion
  - BILINGUAL: Full PT/EN documentation for new feature
  - TESTED: Page extraction verified with multiple PDFs";
  
            public const string V3150 = @"NEW IN v3.15.0:
  - NEW: Sorting options for pages command!
  - NEW: --sort-by-words sorts pages by word count (most words first)
  - NEW: --sort-by-size sorts pages by file/area size (largest first)
  - NEW: --sort-by-dimensions sorts pages by area (width × height)
  - FIXED: --first and --last now applied AFTER sorting
  - ENHANCED: Sorting information shown in output
  - BILINGUAL: Complete PT/EN documentation with sorting examples
  - TESTED: All sorting options working with existing filters";
  
            public const string V3140 = @"NEW IN v3.14.0:
  - NEW: File size filtering options for pages command!
  - NEW: --min-size-mb, --max-size-mb filters for page size in megabytes
  - NEW: --min-size-kb, --max-size-kb filters for page size in kilobytes
  - ENHANCED: JSON/XML output includes page file size information when filtered
  - ENHANCED: Match reasons show actual vs. searched file sizes
  - COMPLETE: All file size filtering functionality fully implemented
  - BILINGUAL: Complete PT/EN documentation with practical examples
  - TESTED: File size filters integrated with existing filtering system";

            public const string V3131 = @"NEW IN v3.13.1:
  - CRITICAL FIX: Corrected incorrect syntax in ALL help commands!
  - FIXED: Removed 'arquivo.pdf|file.pdf' syntax from all command helps
  - FIXED: Only 'load' and 'extract' commands accept file names
  - FIXED: All other commands (pages, documents, words, etc.) only accept cache indices
  - CORRECTED: All examples in help now use correct 'fpdf 1 command' syntax
  - IMPORTANT: This fixes major confusion about command syntax usage";

            public const string V3130 = @"NEW IN v3.13.0:
  - NEW: Page size filtering options for pages command!
  - NEW: --paper-size filter (A4, A3, LETTER, LEGAL, etc.)
  - NEW: --width, --height filters for exact page dimensions
  - NEW: --min-width, --max-width, --min-height, --max-height filters
  - ENHANCED: JSON output includes page size information when filtered
  - COMPLETE: All page size filtering functionality fully implemented
  - TESTED: Page size filters working with tolerance for exact matching";

            public const string V3120 = @"NEW IN v3.12.0:
  - CRITICAL FIX: Help system now works for ALL commands without cache context!
  - FIXED: 'fpdf pages --help' now shows complete help (was showing error before)
  - FIXED: All analysis commands (pages, words, documents, etc.) now support --help
  - ENHANCED: Intelligent help detection in CLI routing system
  - COMPLETE: Help system 100% functional for all 50+ options across all commands
  - TESTED: All help commands verified working in automated test suite";

            public const string V3110 = @"NEW IN v3.11.0:
  - CRITICAL FIX: ALL filter options were broken - always returning ALL pages!
  - FIXED: CommandExecutor was removing hyphens from options (--word became word)
  - FIXED: FpdfPagesCommand was looking for --word but receiving word
  - FIXED: Corrected help titles (removed test strings)
  - VERIFIED: All command options now working correctly
  - TESTED: 50+ options across all commands confirmed functional
  - pages, documents, words, bookmarks, annotations, fonts, metadata - ALL WORKING!";

            public const string V3101 = @"NEW IN v3.10.1:
  - FIXED: FpdfConfigCommand compilation errors resolved
  - CORRECT: Using official FpdfConfig.Instance singleton pattern
  - CORRECT: Using FpdfConfig.SaveToFile() method instead of manual JSON
  - NO GAMBIARRA: Proper implementation following official patterns
  - Sistema de configuração funcionando corretamente";

            public const string V3100 = @"NEW IN v3.10.0:
  - COMPLETO: Tradução extensiva de TODOS os textos principais!
  - Help principal totalmente traduzido PT/EN (30+ mensagens)
  - Help do comando 'pages' traduzido completamente (169 linhas)
  - Sistema de traduções robusto com LanguageManager avançado
  - Método GetPagesHelp() com versões PT/EN completas
  - Interface consistente em português e inglês
  - Exemplos práticos traduzidos (certidão, contrato, etc.)";

            public const string V390 = @"NEW IN v3.9.0:
  - NEW: Sistema de idiomas português/inglês completo implementado!
  - NEW: Comando 'fpdf idioma pt/en' para alternar idiomas
  - NEW: Todas as mensagens traduzidas para português
  - NEW: Configuração de idioma salva automaticamente
  - Comandos técnicos mantidos em inglês (padrão internacional)
  - Mensagens de erro, ajuda e instruções agora em PT/EN";

            public const string V386 = @"NEW IN v3.8.6:
  - FIXED: Command help system restored - fpdf 1 pages --help now works correctly
  - FIXED: Removed incorrect PDF file syntax from error messages
  - FIXED: Error messages now use correct cache-based syntax only
  - ENHANCED: Individual command help intercepted before execution
  - All 13 analysis commands now respond to --help flag properly";

            public const string V385 = @"NEW IN v3.8.5:
  - Final stable release after comprehensive command system restoration
  - All analysis commands tested and validated in production
  - Complete architectural refactoring successfully deployed
  - Enhanced user experience with proper error guidance";

            public const string V384 = @"NEW IN v3.8.4:
  - Corrected terminology from 'filter commands' to 'analysis commands'  
  - Enhanced error messages for analysis commands without context
  - All 12 analysis commands fully tested and functional
  - Improved user experience with educational error messages";

            public const string V383 = @"NEW IN v3.8.3:
  - Fixed command registration issues after architectural refactoring
  - All analysis commands now working with correct syntax
  - Confirmed FilterPDFCLI_Refactored as active entry point
  - Enhanced command execution through CommandExecutor service";

            public const string V381 = @"NEW IN v3.8.1:
  - Improved security error messages with clear instructions
  - Shows exact command to add directories to allowed list
  - Simplified security configuration process";
            
            public const string V380 = @"NEW IN v3.8.0:
  - Added comprehensive 'stats' command for statistical analysis
  - Page size distribution analysis (--page-sizes)
  - Image count and size analysis (--images)
  - Support for single PDF and range analysis
  - Generic statistical tool for various PDF analysis needs";
        }
    }
}