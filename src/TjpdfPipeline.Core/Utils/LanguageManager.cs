using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace FilterPDF.Utils
{
    /// <summary>
    /// Manages application language and localization
    /// </summary>
    public static class LanguageManager
    {
        private static string _currentLanguage = "en"; // Default to English
        private static readonly string _settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".fpdf", "language.json");
        
        // Language dictionaries
        private static readonly Dictionary<string, Dictionary<string, string>> _messages = new()
        {
            ["en"] = new()
            {
                // Error messages
                ["error_command_needs_cache"] = "Error: Command '{0}' requires a PDF loaded in cache.",
                ["error_unknown_command"] = "Error: Unknown command '{0}'",
                ["error_invalid_command_format"] = "Error: Invalid command format.",
                ["error_cache_not_found"] = "Error: Cache index '{0}' not found.",
                ["error_load_cache_failed"] = "Error: Could not load cache file '{0}'.",
                
                // Instructions
                ["how_to_use"] = "HOW TO USE:",
                ["step_1_load"] = "1. First load your PDF:",
                ["step_2_use_cache"] = "2. Then use the cache index:",
                ["examples"] = "EXAMPLES:",
                ["view_loaded_pdfs"] = "To view loaded PDFs: fpdf cache list",
                
                // General messages
                ["no_help_available"] = "No help available for command '{0}'.",
                ["help_not_yet_available"] = "Help for '{0}' command is not yet available.",
                ["services_initialized"] = "[INFO] FilterPDF services initialized successfully",
                ["application_starting"] = "[INFO] FilterPDF application starting...",
                
                // Command descriptions
                ["cmd_desc_filter_words"] = "FILTER WORDS - Search for specific words in PDFs",
                ["cmd_desc_filter_objects"] = "FILTER OBJECTS - Search PDF internal objects",
                ["cmd_desc_filter_fonts"] = "FILTER FONTS - List fonts used in PDFs",
                ["cmd_desc_filter_metadata"] = "FILTER METADATA - Show PDF metadata information",
                ["cmd_desc_filter_base64"] = "FILTER BASE64 - Extract base64 encoded data from PDFs",
                
                // Language commands
                ["language_set_to"] = "Language set to: {0}",
                ["available_languages"] = "Available languages:",
                ["current_language"] = "Current language: {0}",
                ["language_help"] = "LANGUAGE - Set application language\n\nUSAGE:\n    fpdf idioma [language]\n    fpdf language [language]\n\nAVAILABLE LANGUAGES:\n    en - English\n    pt - Português\n\nEXAMPLES:\n    fpdf idioma pt      # Set to Portuguese\n    fpdf language en    # Set to English\n    fpdf idioma         # Show current language",
                
                // Main help
                ["main_help_title"] = "FilterPDF (fpdf) {0} - Advanced PDF Filter & Analysis Tool",
                ["main_help_author"] = "Author: {0}",
                ["main_help_usage"] = "USAGE:",
                ["main_help_usage_cache"] = "    fpdf <cache-index> <command> [options]       # For cached PDFs",
                ["main_help_usage_direct"] = "    fpdf <command> [options]                     # Direct commands",
                ["main_help_divider"] = "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━",
                ["main_help_direct_commands"] = "🔵 DIRECT COMMANDS (work with PDF files directly):",
                ["main_help_extract"] = "    extract     Extract text from PDF file",
                ["main_help_load"] = "    load        Load PDF into cache for analysis",
                ["main_help_stats"] = "    stats       Statistical analysis of PDF file",
                ["main_help_cache_required"] = "🟡 CACHE-BASED COMMANDS (require 'load' first):",
                ["main_help_cache_note"] = "    ⚠️  These commands need cache index. First run: fpdf load file.pdf",
                ["main_help_pages"] = "    pages           Filter pages by criteria",
                ["main_help_bookmarks"] = "    bookmarks       Filter bookmarks",
                ["main_help_words"] = "    words           Search for words in PDF",
                ["main_help_annotations"] = "    annotations     Filter annotations",
                ["main_help_objects"] = "    objects         Filter PDF objects (needs --ultra)",
                ["main_help_fonts"] = "    fonts           List fonts used in PDF",
                ["main_help_metadata"] = "    metadata        Show PDF metadata",
                ["main_help_structure"] = "    structure       Show PDF structure",
                ["main_help_modifications"] = "    modifications   Detect modifications",
                ["main_help_documents"] = "    documents       Filter documents",
                ["main_help_images"] = "    images          Extract images from PDF",
                ["main_help_base64"] = "    base64          Extract base64 encoded data",
                ["main_help_doctypes"] = "    doctypes        Identify document types in PDF",
                ["main_help_management"] = "🟢 MANAGEMENT COMMANDS:",
                ["main_help_cache"] = "    cache       Manage cache files (list, clean, info)",
                ["main_help_config"] = "    config      Manage configuration",
                ["main_help_language"] = "    idioma      Set language / Definir idioma",
                ["main_help_workflow"] = "📋 TYPICAL WORKFLOW:",
                ["main_help_workflow1"] = "    1. fpdf load document.pdf        # Load PDF (returns index, e.g., [1])",
                ["main_help_workflow2"] = "    2. fpdf cache list               # View all cached PDFs with indices",
                ["main_help_workflow3"] = "    3. fpdf 1 pages --word 'text'    # Use index for analysis",
                ["main_help_examples"] = "EXAMPLES:",
                ["main_help_ex1"] = "    fpdf extract document.pdf              # Direct: extract text",
                ["main_help_ex2"] = "    fpdf load document.pdf --ultra         # Load with full analysis",
                ["main_help_ex3"] = "    fpdf 1 pages --word 'contract' -F png  # Use cache index 1",
                ["main_help_ex4"] = "    fpdf 1-5 words --word 'process'        # Process multiple cached files",
                
                // Security messages
                ["security_files_blocked"] = "❌ {0} files blocked by security restrictions",
                ["security_to_fix"] = "📋 TO FIX: Add directory to allowed list:",
                ["security_config_json"] = "   Create/edit: ~/.fpdf/config.json",
                ["security_config_content"] = "   Content: {{\"Security\": {{\"AllowedDirectories\": [\"{0}\"]}}}}",
                ["security_or_env"] = "   OR set environment: export FPDF_ALLOWED_DIRS='{0}'",
                ["security_help_config"] = "   For more info: fpdf --help",
                
                // Words command messages
                ["words_help_title"] = "FILTER WORDS - Search for specific words in PDFs",
                ["words_help_usage"] = "USAGE:",
                ["words_help_usage_line"] = "    fpdf <cache-index> words [options]",
                ["words_help_search_options"] = "SEARCH OPTIONS:",
                ["words_help_word"] = "    -w, --word <text>        Search for specific word",
                ["words_help_regex"] = "    -r, --regex <pattern>    Search using regular expression",
                ["words_help_page"] = "    -p, --page <number>      Search on specific page",
                ["words_help_page_range"] = "    --page-range <start-end> Search in page range",
                ["words_help_case"] = "    -c, --case-sensitive     Case sensitive search",
                ["words_help_output_options"] = "OUTPUT OPTIONS:",
                ["words_help_format"] = "    -F, --format <format>    Output format: txt, json, xml, csv, md, count, ocr",
                ["words_help_output"] = "    -o, --output <file>      Save results to file",
                ["words_help_examples"] = "EXAMPLES:",
                ["words_help_ex1"] = "    fpdf 1 words -w 'contract'",
                ["words_help_ex2"] = "    fpdf 1 words -r '\\\\b\\\\d{4}\\\\b' # Find 4-digit numbers",
                ["words_help_ex3"] = "    fpdf 1 words -w 'important' -F json",
                
                // Fonts command messages
                ["fonts_help_title"] = "FILTER FONTS - List fonts used in PDFs",
                ["fonts_help_usage"] = "USAGE:",
                ["fonts_help_usage_line"] = "    fpdf <cache-index> fonts [options]",
                ["fonts_help_filter_options"] = "FILTER OPTIONS:",
                ["fonts_help_name"] = "    -n, --name <name>        Filter by font name",
                ["fonts_help_type"] = "    -t, --type <type>        Filter by font type (TrueType, Type1, etc.)",
                ["fonts_help_embedded"] = "    --embedded <true/false>  Filter embedded/non-embedded fonts",
                ["fonts_help_subset"] = "    --subset <true/false>    Filter subset/complete fonts",
                ["fonts_help_page"] = "    -p, --page <number>      Fonts used on specific page",
                ["fonts_help_output_options"] = "OUTPUT OPTIONS:",
                ["fonts_help_format"] = "    -F, --format <format>    Output format: txt, json, xml, csv, md, count",
                ["fonts_help_output"] = "    -o, --output <file>      Save results to file",
                ["fonts_help_examples"] = "EXAMPLES:",
                ["fonts_help_ex1"] = "    fpdf 1 fonts",
                ["fonts_help_ex2"] = "    fpdf 1 fonts -n 'Arial*'",
                ["fonts_help_ex3"] = "    fpdf 1 fonts --embedded false -F json",
                
                // Metadata command messages
                ["metadata_help_title"] = "FILTER METADATA - Show PDF metadata information",
                ["metadata_help_usage"] = "USAGE:",
                ["metadata_help_usage_line"] = "    fpdf <cache-index> metadata [options]",
                ["metadata_help_filter_options"] = "FILTER OPTIONS:",
                ["metadata_help_title_filter"] = "    --title <text>           Filter by document title",
                ["metadata_help_author"] = "    --author <text>          Filter by author",
                ["metadata_help_subject"] = "    --subject <text>         Filter by subject",
                ["metadata_help_keywords"] = "    --keywords <text>        Filter by keywords",
                ["metadata_help_created"] = "    --created-after <date>   Created after date",
                ["metadata_help_modified"] = "    --modified-before <date> Modified before date",
                ["metadata_help_output_options"] = "OUTPUT OPTIONS:",
                ["metadata_help_format"] = "    -F, --format <format>    Output format: txt, json, xml, csv, md",
                ["metadata_help_output"] = "    -o, --output <file>      Save results to file",
                ["metadata_help_examples"] = "EXAMPLES:",
                ["metadata_help_ex1"] = "    fpdf 1 metadata",
                ["metadata_help_ex2"] = "    fpdf 1 metadata --author 'John*'",
                ["metadata_help_ex3"] = "    fpdf 1 metadata --created-after 2024-01-01",
                
                // Objects command messages
                ["objects_help_title"] = "FILTER OBJECTS - Search PDF internal objects",
                ["objects_help_usage"] = "USAGE:",
                ["objects_help_usage_line"] = "    fpdf <cache-index> objects [options]",
                ["objects_help_filter_options"] = "FILTER OPTIONS:",
                ["objects_help_type"] = "    -t, --type <type>        Object type (Stream, Font, Image, etc.)",
                ["objects_help_content"] = "    -c, --content <text>     Search object content",
                ["objects_help_size"] = "    --min-size <bytes>       Minimum object size",
                ["objects_help_compressed"] = "    --compressed <bool>      Filter compressed objects",
                ["objects_help_page"] = "    -p, --page <number>      Objects on specific page",
                ["objects_help_output_options"] = "OUTPUT OPTIONS:",
                ["objects_help_format"] = "    -F, --format <format>    Output format: txt, json, xml, csv, md, count",
                ["objects_help_output"] = "    -o, --output <file>      Save results to file",
                ["objects_help_examples"] = "EXAMPLES:",
                ["objects_help_ex1"] = "    fpdf 1 objects -t Stream",
                ["objects_help_ex2"] = "    fpdf 1 objects --min-size 1024",
                ["objects_help_ex3"] = "    fpdf 1 objects -c 'JavaScript' -F json",
                
                // Base64 command messages
                ["base64_help_title"] = "FILTER BASE64 - Extract base64 encoded data from PDFs",
                ["base64_help_usage"] = "USAGE:",
                ["base64_help_usage_line"] = "    fpdf <cache-index> base64 [options]",
                ["base64_help_filter_options"] = "FILTER OPTIONS:",
                ["base64_help_min_length"] = "    --min-length <n>         Minimum base64 string length",
                ["base64_help_decode"] = "    --decode                 Attempt to decode base64 data",
                ["base64_help_type"] = "    --type <type>            Filter by decoded data type",
                ["base64_help_page"] = "    -p, --page <number>      Search on specific page",
                ["base64_help_extract_page"] = "    --extract-page <number>  Extract a specific page as base64",
                ["base64_help_output_options"] = "OUTPUT OPTIONS:",
                ["base64_help_format"] = "    -F, --format <format>    Output format: txt, json, xml, csv, md, count, ocr, raw",
                ["base64_help_output"] = "    -o, --output <file>      Save results to file",
                ["base64_help_save_decoded"] = "    --save-decoded <dir>     Save decoded files to directory",
                ["base64_help_examples"] = "EXAMPLES:",
                ["base64_help_ex1"] = "    fpdf 1 base64",
                ["base64_help_ex2"] = "    fpdf 1 base64 --decode --min-length 100",
                ["base64_help_ex3"] = "    fpdf 1 base64 --save-decoded ./decoded/",
                ["base64_help_ex4"] = "    fpdf 1 base64 --extract-page 34 -F raw",
                ["base64_help_ex5"] = "    fpdf 1 base64 --extract-page 34 -F ocr",

                // Documents command messages (EN)
                ["documents_help_title"] = "FILTER DOCUMENTS - Identify document boundaries in multi-document PDFs",
                ["documents_help_usage"] = "USAGE:",
                ["documents_help_usage_line"] = "    fpdf <cache-index> documents [options]",
                ["documents_help_description"] = "DESCRIPTION:",
                ["documents_help_desc_line1"] = "    Automatically detects where one document ends and another begins.",
                ["documents_help_desc_line2"] = "    Uses multiple strategies: text patterns, signatures, density changes,",
                ["documents_help_desc_line3"] = "    font changes, page size changes, and image signatures.",
                ["documents_help_options"] = "OPTIONS:",
                ["documents_help_word"] = "    -w, --word <text>      Find documents containing word (supports & and | operators)",
                ["documents_help_not_words"] = "    --not-words <text>     Exclude documents containing specific words (supports & and | operators)",
                ["documents_help_font"] = "    -f, --font <name>      Find documents using font (supports & and | operators)",
                ["documents_help_regex"] = "    -r, --regex <pattern>  Find documents matching regex pattern",
                ["documents_help_value"] = "    -v, --value            Find documents containing Brazilian currency values (R$)",
                ["documents_help_signature"] = "    -s, --signature <name> Find documents with signatures (names or patterns)",
                ["documents_help_min_pages"] = "    --min-pages <n>        Minimum pages for a document (default: 1)",
                ["documents_help_min_confidence"] = "    --min-confidence <n>   Minimum confidence score 0-1 (default: 0.5)",
                ["documents_help_verbose"] = "    --verbose              Show detailed detection information",
                ["documents_help_format"] = "    -F, --format <fmt>     Output format: txt (default), raw, json, xml, csv, md, count, detailed, ocr",
                ["documents_help_output"] = "    -o, --output, --output-file <file>  Save results to file",
                ["documents_help_output_dir"] = "    --output-dir <dir>     Save results to directory with auto-generated filename",
                ["documents_help_important"] = "IMPORTANT - FILTER OPTIONS IN JSON OUTPUT:",
                ["documents_help_important_line1"] = "    When any filter option is used, it appears in BOTH:",
                ["documents_help_important_line2"] = "    • Search criteria (what you searched for)",
                ["documents_help_important_line3"] = "    • Individual results (the value for each document found)",
                ["documents_help_examples_title"] = "    Examples of filter options that appear in JSON output:",
                ["documents_help_examples_word"] = "    -w, --word               → 'searchedWords' + actual words found",
                ["documents_help_examples_font"] = "    -f, --font               → 'searchedFont' + 'fontsFound' array",
                ["documents_help_examples_regex"] = "    -r, --regex              → 'regexPattern' used",
                ["documents_help_examples_value"] = "    -v, --value              → 'monetaryValues': true",
                ["documents_help_examples_signature"] = "    -s, --signature          → 'searchedSignature' + 'hasSignaturePatterns' true",
                ["documents_help_examples_not_words"] = "    --not-words              → 'excludedWords'",
                ["documents_help_strategies"] = "DETECTION STRATEGIES:",
                ["documents_help_strategy1"] = "    • Text patterns (PODER JUDICIÁRIO, Processo nº, etc.)",
                ["documents_help_strategy2"] = "    • Digital signatures (assinado eletronicamente)",
                ["documents_help_strategy3"] = "    • Density changes (full page → sparse page)",
                ["documents_help_strategy4"] = "    • Font changes (different font sets)",
                ["documents_help_strategy5"] = "    • Page size changes (A4 → Letter)",
                ["documents_help_strategy6"] = "    • Image signatures (small images at page bottom)",
                ["documents_help_strategy7"] = "    • Structure reset (page numbering restart)",
                ["documents_help_examples"] = "EXAMPLES:",
                ["documents_help_ex1_comment"] = "    # Show all documents found",
                ["documents_help_ex1"] = "    fpdf 1 documents",
                ["documents_help_ex2_comment"] = "    # Find documents containing specific word",
                ["documents_help_ex2"] = "    fpdf 1 documents -w \"honorários\"",
                ["documents_help_ex3_comment"] = "    # Find documents with multiple words (AND)",
                ["documents_help_ex3"] = "    fpdf 1 documents -w \"processo&judicial\"",
                ["documents_help_ex4_comment"] = "    # Find documents using specific font",
                ["documents_help_ex4"] = "    fpdf 1 documents -f \"Times*\"",
                ["documents_help_ex5_comment"] = "    # Find documents containing 'processo' but not 'arquivado'",
                ["documents_help_ex5"] = "    fpdf 1 documents -w \"processo\" --not-words \"arquivado\"",
                ["documents_help_ex6_comment"] = "    # Verbose mode to see why documents were split",
                ["documents_help_ex6"] = "    fpdf 1 documents -v",
                ["documents_help_ex7_comment"] = "    # Only show documents with 5+ pages and high confidence",
                ["documents_help_ex7"] = "    fpdf 1 documents --min-pages 5 --min-confidence 0.7",
                ["documents_help_ex8_comment"] = "    # Search all cached PDFs for documents with specific word",
                ["documents_help_ex8"] = "    fpdf 0 documents -w \"perito\"",
                ["documents_help_ex9_comment"] = "    # Complex pattern with wildcards and & operator (USE SINGLE QUOTES!)",
                ["documents_help_ex9"] = "    fpdf 1-10 documents -w '~diesp~&~despacho~&Perit*'",
                ["documents_help_note"] = "    # Note: Do NOT use \\& inside quotes - this searches for literal backslash",
                
                // Bookmarks command messages (EN)
                ["bookmarks_help_title"] = "FILTER BOOKMARKS - Find bookmarks that match specific criteria",
                ["bookmarks_help_usage"] = "USAGE:",
                ["bookmarks_help_usage_line"] = "    fpdf <cache-index> bookmarks [options]",
                ["bookmarks_help_filter_options"] = "FILTER OPTIONS:",
                ["bookmarks_help_word"] = "    -w, --word <text>        Find bookmarks containing specific text",
                ["bookmarks_help_regex"] = "    -r, --regex <pattern>    Find bookmarks matching regex pattern",
                ["bookmarks_help_value"] = "    -v, --value              Find bookmarks containing Brazilian currency values (R$)",
                ["bookmarks_help_page"] = "    -p, --page <number>      Find bookmarks pointing to specific page",
                ["bookmarks_help_page_range"] = "    --page-range <start-end> Find bookmarks in page range",
                ["bookmarks_help_level"] = "    --level <n>              Find bookmarks at specific level",
                ["bookmarks_help_min_level"] = "    --min-level <n>          Find bookmarks at minimum level",
                ["bookmarks_help_max_level"] = "    --max-level <n>          Find bookmarks at maximum level",
                ["bookmarks_help_has_children"] = "    --has-children <bool>    Find bookmarks with/without children",
                ["bookmarks_help_orientation"] = "    -or, --orientation <type> Find bookmarks on pages with specific orientation",
                ["bookmarks_help_header"] = "    -hd, --header <text>     Find bookmarks whose pages have text in header",
                ["bookmarks_help_footer"] = "    -ft, --footer <text>     Find bookmarks whose pages have text in footer",
                ["bookmarks_help_output_options"] = "OUTPUT OPTIONS:",
                ["bookmarks_help_format"] = "    -F, --format <format>    Output format: txt, json, xml, csv, md, raw, count",
                ["bookmarks_help_output"] = "    -o, --output, --output-file <file>  Save results to file",
                ["bookmarks_help_output_dir"] = "    --output-dir <dir>       Save results to directory with auto-generated filename",
                ["bookmarks_help_important"] = "IMPORTANT - FILTER OPTIONS IN JSON OUTPUT:",
                ["bookmarks_help_important_line1"] = "    When any filter option is used, it appears in BOTH:",
                ["bookmarks_help_important_line2"] = "    • Search criteria (what you searched for)",
                ["bookmarks_help_important_line3"] = "    • Individual results (the value for each bookmark found)",
                ["bookmarks_help_examples_title"] = "    Examples of filter options that appear in JSON output:",
                ["bookmarks_help_examples_word"] = "    -w, --word               → 'searchedWords' + actual words found",
                ["bookmarks_help_examples_regex"] = "    -r, --regex              → 'regexPattern' used",
                ["bookmarks_help_examples_page"] = "    -p, --page               → 'searchedPage' + page number",
                ["bookmarks_help_examples_page_range"] = "    --page-range             → 'pageRange' searched",
                ["bookmarks_help_examples_level"] = "    --level                  → 'searchedLevel' + actual level",
                ["bookmarks_help_examples_min_level"] = "    --min-level              → 'minLevel' + actual level",
                ["bookmarks_help_examples_max_level"] = "    --max-level              → 'maxLevel' + actual level",
                ["bookmarks_help_examples_has_children"] = "    --has-children           → 'searchedHasChildren' + boolean",
                ["bookmarks_help_examples_orientation"] = "    -or, --orientation       → 'orientation' for bookmark's page",
                ["bookmarks_help_examples_value"] = "    -v, --value              → 'monetaryValues': true",
                ["bookmarks_help_examples"] = "EXAMPLES:",
                ["bookmarks_help_ex1_comment"] = "    # Find all bookmarks",
                ["bookmarks_help_ex1"] = "    fpdf 1 bookmarks",
                ["bookmarks_help_ex2_comment"] = "    # Find bookmarks containing 'Chapter'",
                ["bookmarks_help_ex2"] = "    fpdf 1 bookmarks -w 'Chapter'",
                ["bookmarks_help_ex3_comment"] = "    # Find bookmarks whose pages have 'CONFIDENTIAL' in header",
                ["bookmarks_help_ex3"] = "    fpdf 1 bookmarks -hd 'CONFIDENTIAL'",
                ["bookmarks_help_ex4_comment"] = "    # Find bookmarks at level 1 with children",
                ["bookmarks_help_ex4"] = "    fpdf 1 bookmarks --level 1 --has-children true",
                
                // Annotations command messages (EN)
                ["annotations_help_title"] = "COMMAND: annotations - Find and analyze PDF annotations with various criteria",
                ["annotations_help_usage"] = "USAGE:",
                ["annotations_help_usage_line"] = "    fpdf <cache-index> annotations [options]",
                ["annotations_help_description"] = "DESCRIPTION:",
                ["annotations_help_desc_line1"] = "    Searches for annotations (comments, highlights, notes, etc.) in PDF files.",
                ["annotations_help_desc_line2"] = "    Supports filtering by content, type, author, dates, and page location.",
                ["annotations_help_desc_line3"] = "    Can search using text patterns, regex, and boolean operators.",
                ["annotations_help_filter_options"] = "FILTER OPTIONS:",
                ["annotations_help_word"] = "    -w, --word <text>           Find annotations containing specific text",
                ["annotations_help_word_desc"] = "                                Supports OR (|) and AND (&) operators",
                ["annotations_help_regex"] = "    -r, --regex <pattern>       Find annotations matching regex pattern",
                ["annotations_help_value"] = "    -v, --value                 Find annotations containing Brazilian currency values (R$)",
                ["annotations_help_page"] = "    -p, --page <number>         Find annotations on specific page",
                ["annotations_help_page_range"] = "    --page-range <start-end>    Find annotations in page range (e.g., 5-10)",
                ["annotations_help_page_ranges"] = "    --page-ranges <ranges>      QPdf-style ranges (e.g., \"1-3,5,7-9,r1\")",
                ["annotations_help_type"] = "    --type <type>               Find annotations of specific type",
                ["annotations_help_type_desc"] = "                                (e.g., Highlight, Note, Ink, FreeText)",
                ["annotations_help_author"] = "    --author <name>             Find annotations by author",
                ["annotations_help_has_reply"] = "    --has-reply [true/false]    Find annotations with/without replies",
                ["annotations_help_date_filters"] = "DATE FILTERS:",
                ["annotations_help_created_before"] = "    --created-before <date>     Document created before date",
                ["annotations_help_created_after"] = "    --created-after <date>      Document created after date",
                ["annotations_help_modified_before"] = "    --modified-before <date>    Document modified before date",
                ["annotations_help_modified_after"] = "    --modified-after <date>     Document modified after date",
                ["annotations_help_output_options"] = "OUTPUT OPTIONS:",
                ["annotations_help_format"] = "    -F <format>                 Output format: txt, json, xml, csv, md, raw, count",
                ["annotations_help_format_default"] = "                                Default: txt",
                ["annotations_help_output"] = "    -o, --output, --output-file <file>  Save results to file",
                ["annotations_help_output_dir"] = "    --output-dir <dir>          Save results to directory with auto-generated filename",
                ["annotations_help_objects"] = "    --objects                   Include related PDF objects in output",
                ["annotations_help_text_search"] = "TEXT SEARCH OPERATORS:",
                ["annotations_help_or"] = "    |  OR operator              \"todo|fixme\" finds todo OR fixme",
                ["annotations_help_and"] = "    &  AND operator             \"review&approved\" finds both words",
                ["annotations_help_examples"] = "EXAMPLES:",
                ["annotations_help_ex1_comment"] = "    # Find all TODO annotations",
                ["annotations_help_ex1"] = "    fpdf 1 annotations -w \"todo\"",
                ["annotations_help_ex2_comment"] = "    # Find annotations on page 10",
                ["annotations_help_ex2"] = "    fpdf 1 annotations -p 10",
                ["annotations_help_ex3_comment"] = "    # Find highlight annotations by author",
                ["annotations_help_ex3"] = "    fpdf 1 annotations --type \"Highlight\" --author \"John\"",
                ["annotations_help_ex4_comment"] = "    # Find annotations in page range with JSON output",
                ["annotations_help_ex4"] = "    fpdf 1 annotations --page-range 5-15 -F json",
                ["annotations_help_ex5_comment"] = "    # Find annotations with multiple keywords",
                ["annotations_help_ex5"] = "    fpdf 1 annotations -w \"urgent|important|critical\"",
                ["annotations_help_ex6_comment"] = "    # Using cache index",
                ["annotations_help_ex6"] = "    fpdf 1 annotations --type \"Note\" -F csv",
                ["annotations_help_types"] = "ANNOTATION TYPES:",
                ["annotations_help_types_desc1"] = "    Common types include: Text, FreeText, Line, Square, Circle,",
                ["annotations_help_types_desc2"] = "    Polygon, PolyLine, Highlight, Underline, Squiggly, StrikeOut,",
                ["annotations_help_types_desc3"] = "    Stamp, Caret, Ink, Popup, FileAttachment, Sound, Movie, Widget,",
                ["annotations_help_types_desc4"] = "    Screen, PrinterMark, TrapNet, Watermark, 3D, Redact",

                // Config command messages
                ["config_help_title"] = "CONFIG - Manage fpdf configuration",
                ["config_help_usage"] = "USAGE:",
                ["config_help_usage_line"] = "    fpdf config [subcommand] [options]",
                ["config_help_subcommands"] = "SUBCOMMANDS:",
                ["config_help_add_dir"] = "    add-dir <directory>     Add directory to allowed list",
                ["config_help_remove_dir"] = "    remove-dir <directory>  Remove directory from allowed list",
                ["config_help_list_dirs"] = "    list-dirs               List allowed directories",
                ["config_help_show"] = "    show                    Show current configuration",
                ["config_help_reset"] = "    reset                   Reset configuration to defaults",
                ["config_help_examples"] = "EXAMPLES:",
                ["config_help_ex1"] = "    fpdf config add-dir /mnt/c/Users/user/PDFs",
                ["config_help_ex2"] = "    fpdf config list-dirs",
                ["config_help_ex3"] = "    fpdf config show",
                ["config_error_specify_dir_add"] = "Error: Specify directory to add",
                ["config_error_specify_dir_remove"] = "Error: Specify directory to remove",
                ["config_error_unknown_subcommand"] = "Error: Unknown subcommand '{0}'",
                ["config_error_dir_not_exists"] = "Error: Directory '{0}' does not exist",
                ["config_dir_already_allowed"] = "Directory '{0}' is already in allowed list",
                ["config_dir_added"] = "✅ Directory added: {0}",
                ["config_restart_required"] = "Restart fpdf to apply changes",
                ["config_error_adding_dir"] = "Error adding directory: {0}",
                ["config_dir_removed"] = "✅ Directory removed: {0}",
                ["config_dir_not_in_list"] = "Directory '{0}' is not in allowed list",
                ["config_error_removing_dir"] = "Error removing directory: {0}",
                ["config_allowed_directories"] = "Allowed directories:",
                ["config_none_configured"] = "  (none configured)",
                ["config_current_config"] = "Current configuration:",
                ["config_directories_count"] = "directories",
                ["config_reset_success"] = "✅ Configuration reset to defaults",
                ["config_no_config_file"] = "No configuration file found"
            },
            
            ["pt"] = new()
            {
                // Error messages
                ["error_command_needs_cache"] = "Erro: O comando '{0}' precisa de um PDF carregado no cache.",
                ["error_unknown_command"] = "Erro: Comando '{0}' desconhecido",
                ["error_invalid_command_format"] = "Erro: Formato de comando inválido.",
                ["error_cache_not_found"] = "Erro: Índice de cache '{0}' não encontrado.",
                ["error_load_cache_failed"] = "Erro: Não foi possível carregar o arquivo de cache '{0}'.",
                
                // Instructions
                ["how_to_use"] = "COMO USAR:",
                ["step_1_load"] = "1. Primeiro carregue seu PDF:",
                ["step_2_use_cache"] = "2. Depois use o índice do cache:",
                ["examples"] = "EXEMPLOS:",
                ["view_loaded_pdfs"] = "Para ver PDFs carregados: fpdf cache list",
                
                // General messages
                ["no_help_available"] = "Nenhuma ajuda disponível para o comando '{0}'.",
                ["help_not_yet_available"] = "Ajuda para o comando '{0}' ainda não está disponível.",
                ["services_initialized"] = "[INFO] Serviços do FilterPDF inicializados com sucesso",
                ["application_starting"] = "[INFO] Aplicação FilterPDF iniciando...",
                
                // Command descriptions
                ["cmd_desc_filter_words"] = "FILTRAR PALAVRAS - Buscar palavras específicas em PDFs",
                ["cmd_desc_filter_objects"] = "FILTRAR OBJETOS - Buscar objetos internos do PDF",
                ["cmd_desc_filter_fonts"] = "FILTRAR FONTES - Listar fontes usadas em PDFs",
                ["cmd_desc_filter_metadata"] = "FILTRAR METADADOS - Mostrar informações de metadados do PDF",
                ["cmd_desc_filter_base64"] = "FILTRAR BASE64 - Extrair dados codificados em base64 de PDFs",
                
                // Language commands
                ["language_set_to"] = "Idioma definido para: {0}",
                ["available_languages"] = "Idiomas disponíveis:",
                ["current_language"] = "Idioma atual: {0}",
                ["language_help"] = "IDIOMA - Definir idioma da aplicação\n\nUSO:\n    fpdf idioma [idioma]\n    fpdf language [idioma]\n\nIDIOMAS DISPONÍVEIS:\n    en - English\n    pt - Português\n\nEXEMPLOS:\n    fpdf idioma pt      # Definir para Português\n    fpdf language en    # Definir para English\n    fpdf idioma         # Mostrar idioma atual",
                
                // Main help  
                ["main_help_title"] = "FilterPDF (fpdf) {0} - Ferramenta Avançada de Filtro e Análise de PDF",
                ["main_help_author"] = "Autor: {0}",
                ["main_help_usage"] = "USO:",
                ["main_help_usage_cache"] = "    fpdf <índice-cache> <comando> [opções]        # Para PDFs em cache",
                ["main_help_usage_direct"] = "    fpdf <comando> [opções]                       # Comandos diretos",
                ["main_help_divider"] = "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━",
                ["main_help_direct_commands"] = "🔵 COMANDOS DIRETOS (funcionam direto com arquivos PDF):",
                ["main_help_extract"] = "    extract     Extrair texto do arquivo PDF",
                ["main_help_load"] = "    load        Carregar PDF no cache para análise",
                ["main_help_stats"] = "    stats       Análise estatística do arquivo PDF",
                ["main_help_cache_required"] = "🟡 COMANDOS COM CACHE (requerem 'load' primeiro):",
                ["main_help_cache_note"] = "    ⚠️  Estes comandos precisam de índice. Primeiro execute: fpdf load arquivo.pdf",
                ["main_help_pages"] = "    pages           Filtrar páginas por critérios",
                ["main_help_bookmarks"] = "    bookmarks       Filtrar marcadores",
                ["main_help_words"] = "    words           Buscar palavras no PDF",
                ["main_help_annotations"] = "    annotations     Filtrar anotações",
                ["main_help_objects"] = "    objects         Filtrar objetos do PDF (precisa --ultra)",
                ["main_help_fonts"] = "    fonts           Listar fontes usadas no PDF",
                ["main_help_metadata"] = "    metadata        Mostrar metadados do PDF",
                ["main_help_structure"] = "    structure       Mostrar estrutura do PDF",
                ["main_help_modifications"] = "    modifications   Detectar modificações",
                ["main_help_documents"] = "    documents       Filtrar documentos",
                ["main_help_images"] = "    images          Extrair imagens do PDF",
                ["main_help_base64"] = "    base64          Extrair dados codificados em base64",
                ["main_help_management"] = "🟢 COMANDOS DE GERENCIAMENTO:",
                ["main_help_cache"] = "    cache       Gerenciar arquivos de cache (list, clean, info)",
                ["main_help_config"] = "    config      Gerenciar configuração",
                ["main_help_language"] = "    idioma      Definir idioma / Set language",
                ["main_help_workflow"] = "📋 FLUXO DE TRABALHO TÍPICO:",
                ["main_help_workflow1"] = "    1. fpdf load documento.pdf        # Carrega PDF (retorna índice, ex: [1])",
                ["main_help_workflow2"] = "    2. fpdf cache list                # Ver todos os PDFs em cache com índices",
                ["main_help_workflow3"] = "    3. fpdf 1 pages --word 'texto'    # Usar índice para análise",
                ["main_help_examples"] = "EXEMPLOS:",
                ["main_help_ex1"] = "    fpdf extract documento.pdf              # Direto: extrair texto",
                ["main_help_ex2"] = "    fpdf load documento.pdf --ultra         # Carregar com análise completa",
                ["main_help_ex3"] = "    fpdf 1 pages --word 'contrato' -F png   # Usar índice 1 do cache",
                ["main_help_ex4"] = "    fpdf 1-5 words --word 'processo'        # Processar múltiplos arquivos",
                
                // Security messages
                ["security_files_blocked"] = "❌ {0} arquivos bloqueados por restrições de segurança",
                ["security_to_fix"] = "📋 SOLUÇÃO: Adicionar diretório à lista permitida:",
                ["security_config_json"] = "   Criar/editar: ~/.fpdf/config.json",
                ["security_config_content"] = "   Conteúdo: {{\"Security\": {{\"AllowedDirectories\": [\"{0}\"]}}}}",
                ["security_or_env"] = "   OU definir variável: export FPDF_ALLOWED_DIRS='{0}'",
                ["security_help_config"] = "   Mais informações: fpdf --help",
                
                // Config command messages
                ["config_help_title"] = "CONFIG - Gerenciar configuração do fpdf",
                ["config_help_usage"] = "USO:",
                ["config_help_usage_line"] = "    fpdf config [subcomando] [opções]",
                ["config_help_subcommands"] = "SUBCOMANDOS:",
                ["config_help_add_dir"] = "    add-dir <diretório>     Adicionar diretório à lista permitida",
                ["config_help_remove_dir"] = "    remove-dir <diretório>  Remover diretório da lista permitida",
                ["config_help_list_dirs"] = "    list-dirs               Listar diretórios permitidos",
                ["config_help_show"] = "    show                    Mostrar configuração atual",
                ["config_help_reset"] = "    reset                   Resetar configuração para padrões",
                ["config_help_examples"] = "EXEMPLOS:",
                ["config_help_ex1"] = "    fpdf config add-dir /mnt/c/Users/usuario/PDFs",
                ["config_help_ex2"] = "    fpdf config list-dirs",
                ["config_help_ex3"] = "    fpdf config show",
                ["config_error_specify_dir_add"] = "Erro: Especifique o diretório para adicionar",
                ["config_error_specify_dir_remove"] = "Erro: Especifique o diretório para remover",
                ["config_error_unknown_subcommand"] = "Erro: Subcomando '{0}' desconhecido",
                ["config_error_dir_not_exists"] = "Erro: Diretório '{0}' não existe",
                ["config_dir_already_allowed"] = "Diretório '{0}' já está na lista permitida",
                ["config_dir_added"] = "✅ Diretório adicionado: {0}",
                ["config_restart_required"] = "Reinicie o fpdf para aplicar as mudanças",
                ["config_error_adding_dir"] = "Erro ao adicionar diretório: {0}",
                ["config_dir_removed"] = "✅ Diretório removido: {0}",
                ["config_dir_not_in_list"] = "Diretório '{0}' não está na lista permitida",
                ["config_error_removing_dir"] = "Erro ao remover diretório: {0}",
                ["config_allowed_directories"] = "Diretórios permitidos:",
                ["config_none_configured"] = "  (nenhum configurado)",
                ["config_current_config"] = "Configuração atual:",
                ["config_directories_count"] = "diretórios",
                ["config_reset_success"] = "✅ Configuração resetada para padrões",
                ["config_no_config_file"] = "Nenhum arquivo de configuração encontrado",
                
                // Words command messages (PT)
                ["words_help_title"] = "FILTRAR PALAVRAS - Buscar palavras específicas em PDFs",
                ["words_help_usage"] = "USO:",
                ["words_help_usage_line"] = "    fpdf <índice-cache> words [opções]",
                ["words_help_search_options"] = "OPÇÕES DE BUSCA:",
                ["words_help_word"] = "    -w, --word <texto>       Buscar palavra específica",
                ["words_help_regex"] = "    -r, --regex <padrão>     Buscar usando expressão regular",
                ["words_help_page"] = "    -p, --page <número>      Buscar em página específica",
                ["words_help_page_range"] = "    --page-range <início-fim> Buscar em intervalo de páginas",
                ["words_help_case"] = "    -c, --case-sensitive     Busca sensível a maiúsculas",
                ["words_help_output_options"] = "OPÇÕES DE SAÍDA:",
                ["words_help_format"] = "    -F, --format <formato>   Formato de saída: txt, json, xml, csv, md, count, ocr",
                ["words_help_output"] = "    -o, --output <arquivo>   Salvar resultados em arquivo",
                ["words_help_examples"] = "EXEMPLOS:",
                ["words_help_ex1"] = "    fpdf 1 words -w 'contrato'",
                ["words_help_ex2"] = "    fpdf 1 words -r '\\\\b\\\\d{4}\\\\b' # Encontrar números de 4 dígitos",
                ["words_help_ex3"] = "    fpdf 1 words -w 'importante' -F json",
                
                // Fonts command messages (PT)
                ["fonts_help_title"] = "FILTRAR FONTES - Listar fontes usadas em PDFs",
                ["fonts_help_usage"] = "USO:",
                ["fonts_help_usage_line"] = "    fpdf <índice-cache> fonts [opções]",
                ["fonts_help_filter_options"] = "OPÇÕES DE FILTRO:",
                ["fonts_help_name"] = "    -n, --name <nome>        Filtrar por nome da fonte",
                ["fonts_help_type"] = "    -t, --type <tipo>        Filtrar por tipo de fonte (TrueType, Type1, etc.)",
                ["fonts_help_embedded"] = "    --embedded <true/false>  Filtrar fontes incorporadas/não-incorporadas",
                ["fonts_help_subset"] = "    --subset <true/false>    Filtrar fontes subset/completas",
                ["fonts_help_page"] = "    -p, --page <número>      Fontes usadas em página específica",
                ["fonts_help_output_options"] = "OPÇÕES DE SAÍDA:",
                ["fonts_help_format"] = "    -F, --format <formato>   Formato de saída: txt, json, xml, csv, md, count",
                ["fonts_help_output"] = "    -o, --output <arquivo>   Salvar resultados em arquivo",
                ["fonts_help_examples"] = "EXEMPLOS:",
                ["fonts_help_ex1"] = "    fpdf 1 fonts",
                ["fonts_help_ex2"] = "    fpdf 1 fonts -n 'Arial*'",
                ["fonts_help_ex3"] = "    fpdf 1 fonts --embedded false -F json",
                
                // Metadata command messages (PT)
                ["metadata_help_title"] = "FILTRAR METADADOS - Mostrar informações de metadados do PDF",
                ["metadata_help_usage"] = "USO:",
                ["metadata_help_usage_line"] = "    fpdf <índice-cache> metadata [opções]",
                ["metadata_help_filter_options"] = "OPÇÕES DE FILTRO:",
                ["metadata_help_title_filter"] = "    --title <texto>          Filtrar por título do documento",
                ["metadata_help_author"] = "    --author <texto>         Filtrar por autor",
                ["metadata_help_subject"] = "    --subject <texto>        Filtrar por assunto",
                ["metadata_help_keywords"] = "    --keywords <texto>       Filtrar por palavras-chave",
                ["metadata_help_created"] = "    --created-after <data>   Criado após data",
                ["metadata_help_modified"] = "    --modified-before <data> Modificado antes da data",
                ["metadata_help_output_options"] = "OPÇÕES DE SAÍDA:",
                ["metadata_help_format"] = "    -F, --format <formato>   Formato de saída: txt, json, xml, csv, md",
                ["metadata_help_output"] = "    -o, --output <arquivo>   Salvar resultados em arquivo",
                ["metadata_help_examples"] = "EXEMPLOS:",
                ["metadata_help_ex1"] = "    fpdf 1 metadata",
                ["metadata_help_ex2"] = "    fpdf 1 metadata --author 'João*'",
                ["metadata_help_ex3"] = "    fpdf 1 metadata --created-after 2024-01-01",
                
                // Objects command messages (PT)
                ["objects_help_title"] = "FILTRAR OBJETOS - Buscar objetos internos do PDF",
                ["objects_help_usage"] = "USO:",
                ["objects_help_usage_line"] = "    fpdf <índice-cache> objects [opções]",
                ["objects_help_filter_options"] = "OPÇÕES DE FILTRO:",
                ["objects_help_type"] = "    -t, --type <tipo>        Tipo de objeto (Stream, Font, Image, etc.)",
                ["objects_help_content"] = "    -c, --content <texto>    Buscar conteúdo do objeto",
                ["objects_help_size"] = "    --min-size <bytes>       Tamanho mínimo do objeto",
                ["objects_help_compressed"] = "    --compressed <bool>      Filtrar objetos comprimidos",
                ["objects_help_page"] = "    -p, --page <número>      Objetos em página específica",
                ["objects_help_output_options"] = "OPÇÕES DE SAÍDA:",
                ["objects_help_format"] = "    -F, --format <formato>   Formato de saída: txt, json, xml, csv, md, count",
                ["objects_help_output"] = "    -o, --output <arquivo>   Salvar resultados em arquivo",
                ["objects_help_examples"] = "EXEMPLOS:",
                ["objects_help_ex1"] = "    fpdf 1 objects -t Stream",
                ["objects_help_ex2"] = "    fpdf 1 objects --min-size 1024",
                ["objects_help_ex3"] = "    fpdf 1 objects -c 'JavaScript' -F json",
                
                // Base64 command messages (PT)
                ["base64_help_title"] = "FILTRAR BASE64 - Extrair dados codificados em base64 de PDFs",
                ["base64_help_usage"] = "USO:",
                ["base64_help_usage_line"] = "    fpdf <índice-cache> base64 [opções]",
                ["base64_help_filter_options"] = "OPÇÕES DE FILTRO:",
                ["base64_help_min_length"] = "    --min-length <n>         Comprimento mínimo da string base64",
                ["base64_help_decode"] = "    --decode                 Tentar decodificar dados base64",
                ["base64_help_type"] = "    --type <tipo>            Filtrar por tipo de dados decodificados",
                ["base64_help_page"] = "    -p, --page <número>      Buscar em página específica",
                ["base64_help_extract_page"] = "    --extract-page <número>  Extrair uma página específica como base64",
                ["base64_help_output_options"] = "OPÇÕES DE SAÍDA:",
                ["base64_help_format"] = "    -F, --format <formato>   Formato de saída: txt, json, xml, csv, md, count, ocr, raw",
                ["base64_help_output"] = "    -o, --output <arquivo>   Salvar resultados em arquivo",
                ["base64_help_save_decoded"] = "    --save-decoded <dir>     Salvar arquivos decodificados no diretório",
                ["base64_help_examples"] = "EXEMPLOS:",
                ["base64_help_ex1"] = "    fpdf 1 base64",
                ["base64_help_ex2"] = "    fpdf 1 base64 --decode --min-length 100",
                ["base64_help_ex3"] = "    fpdf 1 base64 --save-decoded ./decodificados/",
                ["base64_help_ex4"] = "    fpdf 1 base64 --extract-page 34 -F raw",
                ["base64_help_ex5"] = "    fpdf 1 base64 --extract-page 34 -F ocr",
                
                // Documents command messages (PT)
                ["documents_help_title"] = "FILTRAR DOCUMENTOS - Identificar limites de documentos em PDFs multi-documento",
                ["documents_help_usage"] = "USO:",
                ["documents_help_usage_line"] = "    fpdf <índice-cache> documents [opções]",
                ["documents_help_description"] = "DESCRIÇÃO:",
                ["documents_help_desc_line1"] = "    Detecta automaticamente onde um documento termina e outro começa.",
                ["documents_help_desc_line2"] = "    Usa múltiplas estratégias: padrões de texto, assinaturas, mudanças de densidade,",
                ["documents_help_desc_line3"] = "    mudanças de fonte, mudanças de tamanho de página e assinaturas de imagem.",
                ["documents_help_options"] = "OPÇÕES:",
                ["documents_help_word"] = "    -w, --word <texto>      Encontrar documentos contendo palavra (suporta operadores & e |)",
                ["documents_help_not_words"] = "    --not-words <texto>     Excluir documentos contendo palavras específicas (suporta & e |)",
                ["documents_help_font"] = "    -f, --font <nome>       Encontrar documentos usando fonte (suporta operadores & e |)",
                ["documents_help_regex"] = "    -r, --regex <padrão>    Encontrar documentos correspondendo ao padrão regex",
                ["documents_help_value"] = "    -v, --value             Encontrar documentos contendo valores monetários brasileiros (R$)",
                ["documents_help_signature"] = "    -s, --signature <nome>  Encontrar documentos com assinaturas (nomes ou padrões)",
                ["documents_help_min_pages"] = "    --min-pages <n>         Páginas mínimas para um documento (padrão: 1)",
                ["documents_help_min_confidence"] = "    --min-confidence <n>    Pontuação mínima de confiança 0-1 (padrão: 0.5)",
                ["documents_help_verbose"] = "    --verbose               Mostrar informações detalhadas de detecção",
                ["documents_help_format"] = "    -F, --format <fmt>      Formato de saída: txt (padrão), raw, json, xml, csv, md, count, detailed, ocr",
                ["documents_help_output"] = "    -o, --output, --output-file <arquivo>  Salvar resultados em arquivo",
                ["documents_help_output_dir"] = "    --output-dir <dir>      Salvar resultados em diretório com nome auto-gerado",
                ["documents_help_important"] = "IMPORTANTE - OPÇÕES DE FILTRO NA SAÍDA JSON:",
                ["documents_help_important_line1"] = "    Quando qualquer opção de filtro é usada, ela aparece em AMBOS:",
                ["documents_help_important_line2"] = "    • Critérios de busca (o que você buscou)",
                ["documents_help_important_line3"] = "    • Resultados individuais (o valor para cada documento encontrado)",
                ["documents_help_examples_title"] = "    Exemplos de opções de filtro que aparecem na saída JSON:",
                ["documents_help_examples_word"] = "    -w, --word               → 'searchedWords' + palavras reais encontradas",
                ["documents_help_examples_font"] = "    -f, --font               → 'searchedFont' + array 'fontsFound'",
                ["documents_help_examples_regex"] = "    -r, --regex              → 'regexPattern' usado",
                ["documents_help_examples_value"] = "    -v, --value              → 'monetaryValues': true",
                ["documents_help_examples_signature"] = "    -s, --signature          → 'searchedSignature' + 'hasSignaturePatterns' true",
                ["documents_help_examples_not_words"] = "    --not-words              → 'excludedWords'",
                ["documents_help_strategies"] = "ESTRATÉGIAS DE DETECÇÃO:",
                ["documents_help_strategy1"] = "    • Padrões de texto (PODER JUDICIÁRIO, Processo nº, etc.)",
                ["documents_help_strategy2"] = "    • Assinaturas digitais (assinado eletronicamente)",
                ["documents_help_strategy3"] = "    • Mudanças de densidade (página cheia → página esparsa)",
                ["documents_help_strategy4"] = "    • Mudanças de fonte (conjuntos de fontes diferentes)",
                ["documents_help_strategy5"] = "    • Mudanças de tamanho de página (A4 → Letter)",
                ["documents_help_strategy6"] = "    • Assinaturas de imagem (imagens pequenas no rodapé da página)",
                ["documents_help_strategy7"] = "    • Reset de estrutura (reinício da numeração de páginas)",
                ["documents_help_examples"] = "EXEMPLOS:",
                ["documents_help_ex1_comment"] = "    # Mostrar todos os documentos encontrados",
                ["documents_help_ex1"] = "    fpdf 1 documents",
                ["documents_help_ex2_comment"] = "    # Encontrar documentos contendo palavra específica",
                ["documents_help_ex2"] = "    fpdf 1 documents -w \"honorários\"",
                ["documents_help_ex3_comment"] = "    # Encontrar documentos com múltiplas palavras (E)",
                ["documents_help_ex3"] = "    fpdf 1 documents -w \"processo&judicial\"",
                ["documents_help_ex4_comment"] = "    # Encontrar documentos usando fonte específica",
                ["documents_help_ex4"] = "    fpdf 1 documents -f \"Times*\"",
                ["documents_help_ex5_comment"] = "    # Encontrar documentos contendo 'processo' mas não 'arquivado'",
                ["documents_help_ex5"] = "    fpdf 1 documents -w \"processo\" --not-words \"arquivado\"",
                ["documents_help_ex6_comment"] = "    # Modo verboso para ver por que documentos foram separados",
                ["documents_help_ex6"] = "    fpdf 1 documents -v",
                ["documents_help_ex7_comment"] = "    # Mostrar apenas documentos com 5+ páginas e alta confiança",
                ["documents_help_ex7"] = "    fpdf 1 documents --min-pages 5 --min-confidence 0.7",
                ["documents_help_ex8_comment"] = "    # Buscar em todos os PDFs em cache por documentos com palavra específica",
                ["documents_help_ex8"] = "    fpdf 0 documents -w \"perito\"",
                ["documents_help_ex9_comment"] = "    # Padrão complexo com curingas e operador & (USE ASPAS SIMPLES!)",
                ["documents_help_ex9"] = "    fpdf 1-10 documents -w '~diesp~&~despacho~&Perit*'",
                ["documents_help_note"] = "    # Nota: NÃO use \\& dentro de aspas - isso busca por barra invertida literal",
                
                // Bookmarks command messages (PT)
                ["bookmarks_help_title"] = "FILTRAR MARCADORES - Encontrar marcadores que correspondem a critérios específicos",
                ["bookmarks_help_usage"] = "USO:",
                ["bookmarks_help_usage_line"] = "    fpdf <índice-cache> bookmarks [opções]",
                ["bookmarks_help_filter_options"] = "OPÇÕES DE FILTRO:",
                ["bookmarks_help_word"] = "    -w, --word <texto>        Encontrar marcadores contendo texto específico",
                ["bookmarks_help_regex"] = "    -r, --regex <padrão>      Encontrar marcadores correspondendo ao padrão regex",
                ["bookmarks_help_value"] = "    -v, --value               Encontrar marcadores contendo valores monetários brasileiros (R$)",
                ["bookmarks_help_page"] = "    -p, --page <número>       Encontrar marcadores apontando para página específica",
                ["bookmarks_help_page_range"] = "    --page-range <início-fim> Encontrar marcadores no intervalo de páginas",
                ["bookmarks_help_level"] = "    --level <n>               Encontrar marcadores no nível específico",
                ["bookmarks_help_min_level"] = "    --min-level <n>           Encontrar marcadores no nível mínimo",
                ["bookmarks_help_max_level"] = "    --max-level <n>           Encontrar marcadores no nível máximo",
                ["bookmarks_help_has_children"] = "    --has-children <bool>     Encontrar marcadores com/sem filhos",
                ["bookmarks_help_orientation"] = "    -or, --orientation <tipo> Encontrar marcadores em páginas com orientação específica",
                ["bookmarks_help_header"] = "    -hd, --header <texto>     Encontrar marcadores cujas páginas têm texto no cabeçalho",
                ["bookmarks_help_footer"] = "    -ft, --footer <texto>     Encontrar marcadores cujas páginas têm texto no rodapé",
                ["bookmarks_help_output_options"] = "OPÇÕES DE SAÍDA:",
                ["bookmarks_help_format"] = "    -F, --format <formato>    Formato de saída: txt, json, xml, csv, md, raw, count",
                ["bookmarks_help_output"] = "    -o, --output, --output-file <arquivo>  Salvar resultados em arquivo",
                ["bookmarks_help_output_dir"] = "    --output-dir <dir>        Salvar resultados em diretório com nome auto-gerado",
                ["bookmarks_help_important"] = "IMPORTANTE - OPÇÕES DE FILTRO NA SAÍDA JSON:",
                ["bookmarks_help_important_line1"] = "    Quando qualquer opção de filtro é usada, ela aparece em AMBOS:",
                ["bookmarks_help_important_line2"] = "    • Critérios de busca (o que você buscou)",
                ["bookmarks_help_important_line3"] = "    • Resultados individuais (o valor para cada marcador encontrado)",
                ["bookmarks_help_examples_title"] = "    Exemplos de opções de filtro que aparecem na saída JSON:",
                ["bookmarks_help_examples_word"] = "    -w, --word               → 'searchedWords' + palavras reais encontradas",
                ["bookmarks_help_examples_regex"] = "    -r, --regex              → 'regexPattern' usado",
                ["bookmarks_help_examples_page"] = "    -p, --page               → 'searchedPage' + número da página",
                ["bookmarks_help_examples_page_range"] = "    --page-range             → 'pageRange' buscado",
                ["bookmarks_help_examples_level"] = "    --level                  → 'searchedLevel' + nível real",
                ["bookmarks_help_examples_min_level"] = "    --min-level              → 'minLevel' + nível real",
                ["bookmarks_help_examples_max_level"] = "    --max-level              → 'maxLevel' + nível real",
                ["bookmarks_help_examples_has_children"] = "    --has-children           → 'searchedHasChildren' + booleano",
                ["bookmarks_help_examples_orientation"] = "    -or, --orientation       → 'orientation' para página do marcador",
                ["bookmarks_help_examples_value"] = "    -v, --value              → 'monetaryValues': true",
                ["bookmarks_help_examples"] = "EXEMPLOS:",
                ["bookmarks_help_ex1_comment"] = "    # Encontrar todos os marcadores",
                ["bookmarks_help_ex1"] = "    fpdf 1 bookmarks",
                ["bookmarks_help_ex2_comment"] = "    # Encontrar marcadores contendo 'Capítulo'",
                ["bookmarks_help_ex2"] = "    fpdf 1 bookmarks -w 'Capítulo'",
                ["bookmarks_help_ex3_comment"] = "    # Encontrar marcadores cujas páginas têm 'CONFIDENCIAL' no cabeçalho",
                ["bookmarks_help_ex3"] = "    fpdf 1 bookmarks -hd 'CONFIDENCIAL'",
                ["bookmarks_help_ex4_comment"] = "    # Encontrar marcadores no nível 1 com filhos",
                ["bookmarks_help_ex4"] = "    fpdf 1 bookmarks --level 1 --has-children true",
                
                // Annotations command messages (PT)
                ["annotations_help_title"] = "COMANDO: annotations - Encontrar e analisar anotações PDF com vários critérios",
                ["annotations_help_usage"] = "USO:",
                ["annotations_help_usage_line"] = "    fpdf <índice-cache> annotations [opções]",
                ["annotations_help_description"] = "DESCRIÇÃO:",
                ["annotations_help_desc_line1"] = "    Busca por anotações (comentários, destaques, notas, etc.) em arquivos PDF.",
                ["annotations_help_desc_line2"] = "    Suporta filtragem por conteúdo, tipo, autor, datas e localização da página.",
                ["annotations_help_desc_line3"] = "    Pode buscar usando padrões de texto, regex e operadores booleanos.",
                ["annotations_help_filter_options"] = "OPÇÕES DE FILTRO:",
                ["annotations_help_word"] = "    -w, --word <texto>          Encontrar anotações contendo texto específico",
                ["annotations_help_word_desc"] = "                                Suporta operadores OR (|) e AND (&)",
                ["annotations_help_regex"] = "    -r, --regex <padrão>        Encontrar anotações correspondendo ao padrão regex",
                ["annotations_help_value"] = "    -v, --value                 Encontrar anotações contendo valores monetários brasileiros (R$)",
                ["annotations_help_page"] = "    -p, --page <número>         Encontrar anotações na página específica",
                ["annotations_help_page_range"] = "    --page-range <início-fim>   Encontrar anotações no intervalo de páginas (ex: 5-10)",
                ["annotations_help_page_ranges"] = "    --page-ranges <intervalos>  Intervalos estilo QPdf (ex: \"1-3,5,7-9,r1\")",
                ["annotations_help_type"] = "    --type <tipo>               Encontrar anotações de tipo específico",
                ["annotations_help_type_desc"] = "                                (ex: Highlight, Note, Ink, FreeText)",
                ["annotations_help_author"] = "    --author <nome>             Encontrar anotações por autor",
                ["annotations_help_has_reply"] = "    --has-reply [true/false]    Encontrar anotações com/sem respostas",
                ["annotations_help_date_filters"] = "FILTROS DE DATA:",
                ["annotations_help_created_before"] = "    --created-before <data>     Documento criado antes da data",
                ["annotations_help_created_after"] = "    --created-after <data>      Documento criado após a data",
                ["annotations_help_modified_before"] = "    --modified-before <data>    Documento modificado antes da data",
                ["annotations_help_modified_after"] = "    --modified-after <data>     Documento modificado após a data",
                ["annotations_help_output_options"] = "OPÇÕES DE SAÍDA:",
                ["annotations_help_format"] = "    -F <formato>                Formato de saída: txt, json, xml, csv, md, raw, count",
                ["annotations_help_format_default"] = "                                Padrão: txt",
                ["annotations_help_output"] = "    -o, --output, --output-file <arquivo>  Salvar resultados em arquivo",
                ["annotations_help_output_dir"] = "    --output-dir <dir>          Salvar resultados em diretório com nome auto-gerado",
                ["annotations_help_objects"] = "    --objects                   Incluir objetos PDF relacionados na saída",
                ["annotations_help_text_search"] = "OPERADORES DE BUSCA DE TEXTO:",
                ["annotations_help_or"] = "    |  Operador OR              \"todo|fixme\" encontra todo OU fixme",
                ["annotations_help_and"] = "    &  Operador AND             \"review&approved\" encontra ambas as palavras",
                ["annotations_help_examples"] = "EXEMPLOS:",
                ["annotations_help_ex1_comment"] = "    # Encontrar todas as anotações TODO",
                ["annotations_help_ex1"] = "    fpdf 1 annotations -w \"todo\"",
                ["annotations_help_ex2_comment"] = "    # Encontrar anotações na página 10",
                ["annotations_help_ex2"] = "    fpdf 1 annotations -p 10",
                ["annotations_help_ex3_comment"] = "    # Encontrar anotações de destaque por autor",
                ["annotations_help_ex3"] = "    fpdf 1 annotations --type \"Highlight\" --author \"João\"",
                ["annotations_help_ex4_comment"] = "    # Encontrar anotações no intervalo de páginas com saída JSON",
                ["annotations_help_ex4"] = "    fpdf 1 annotations --page-range 5-15 -F json",
                ["annotations_help_ex5_comment"] = "    # Encontrar anotações com múltiplas palavras-chave",
                ["annotations_help_ex5"] = "    fpdf 1 annotations -w \"urgente|importante|crítico\"",
                ["annotations_help_ex6_comment"] = "    # Usando índice do cache",
                ["annotations_help_ex6"] = "    fpdf 1 annotations --type \"Note\" -F csv",
                ["annotations_help_types"] = "TIPOS DE ANOTAÇÃO:",
                ["annotations_help_types_desc1"] = "    Tipos comuns incluem: Text, FreeText, Line, Square, Circle,",
                ["annotations_help_types_desc2"] = "    Polygon, PolyLine, Highlight, Underline, Squiggly, StrikeOut,",
                ["annotations_help_types_desc3"] = "    Stamp, Caret, Ink, Popup, FileAttachment, Sound, Movie, Widget,",
                ["annotations_help_types_desc4"] = "    Screen, PrinterMark, TrapNet, Watermark, 3D, Redact",
                
            }
        };
        
        public static string CurrentLanguage 
        { 
            get => _currentLanguage;
            private set => _currentLanguage = value;
        }
        
        static LanguageManager()
        {
            LoadLanguageSettings();
        }
        
        public static string GetMessage(string key, params object[] args)
        {
            if (_messages.ContainsKey(_currentLanguage) && _messages[_currentLanguage].ContainsKey(key))
            {
                var message = _messages[_currentLanguage][key];
                return args.Length > 0 ? string.Format(message, args) : message;
            }
            
            // Fallback to English if key not found in current language
            if (_currentLanguage != "en" && _messages["en"].ContainsKey(key))
            {
                var message = _messages["en"][key];
                return args.Length > 0 ? string.Format(message, args) : message;
            }
            
            // Ultimate fallback
            return key;
        }
        
        public static bool SetLanguage(string language)
        {
            if (_messages.ContainsKey(language))
            {
                _currentLanguage = language;
                SaveLanguageSettings();
                return true;
            }
            return false;
        }
        
        public static string[] GetAvailableLanguages()
        {
            return new[] { "en", "pt" };
        }
        
        public static string GetLanguageName(string code)
        {
            return code switch
            {
                "en" => "English",
                "pt" => "Português",
                _ => code
            };
        }
        
        /// <summary>
        /// Get complete help text for Words command
        /// </summary>
        public static string GetWordsHelp()
        {
            var title = GetMessage("words_help_title");
            var usage = GetMessage("words_help_usage");
            var usageLine = GetMessage("words_help_usage_line");
            var searchOptions = GetMessage("words_help_search_options");
            var word = GetMessage("words_help_word");
            var regex = GetMessage("words_help_regex");
            var page = GetMessage("words_help_page");
            var pageRange = GetMessage("words_help_page_range");
            var caseSensitive = GetMessage("words_help_case");
            var outputOptions = GetMessage("words_help_output_options");
            var format = GetMessage("words_help_format");
            var output = GetMessage("words_help_output");
            var examples = GetMessage("words_help_examples");
            var ex1 = GetMessage("words_help_ex1");
            var ex2 = GetMessage("words_help_ex2");
            var ex3 = GetMessage("words_help_ex3");
            
            return $@"{title}

{usage}
{usageLine}

{searchOptions}
{word}
{regex}
{page}
{pageRange}
{caseSensitive}

{outputOptions}
{format}
{output}

{examples}
{ex1}
{ex2}
{ex3}";
        }

        /// <summary>
        /// Get complete help text for Fonts command
        /// </summary>
        public static string GetFontsHelp()
        {
            var title = GetMessage("fonts_help_title");
            var usage = GetMessage("fonts_help_usage");
            var usageLine = GetMessage("fonts_help_usage_line");
            var filterOptions = GetMessage("fonts_help_filter_options");
            var name = GetMessage("fonts_help_name");
            var type = GetMessage("fonts_help_type");
            var embedded = GetMessage("fonts_help_embedded");
            var subset = GetMessage("fonts_help_subset");
            var page = GetMessage("fonts_help_page");
            var outputOptions = GetMessage("fonts_help_output_options");
            var format = GetMessage("fonts_help_format");
            var output = GetMessage("fonts_help_output");
            var examples = GetMessage("fonts_help_examples");
            var ex1 = GetMessage("fonts_help_ex1");
            var ex2 = GetMessage("fonts_help_ex2");
            var ex3 = GetMessage("fonts_help_ex3");
            
            return $@"{title}

{usage}
{usageLine}

{filterOptions}
{name}
{type}
{embedded}
{subset}
{page}

{outputOptions}
{format}
{output}

{examples}
{ex1}
{ex2}
{ex3}";
        }

        /// <summary>
        /// Get complete help text for Metadata command
        /// </summary>
        public static string GetMetadataHelp()
        {
            var title = GetMessage("metadata_help_title");
            var usage = GetMessage("metadata_help_usage");
            var usageLine = GetMessage("metadata_help_usage_line");
            var filterOptions = GetMessage("metadata_help_filter_options");
            var titleFilter = GetMessage("metadata_help_title_filter");
            var author = GetMessage("metadata_help_author");
            var subject = GetMessage("metadata_help_subject");
            var keywords = GetMessage("metadata_help_keywords");
            var created = GetMessage("metadata_help_created");
            var modified = GetMessage("metadata_help_modified");
            var outputOptions = GetMessage("metadata_help_output_options");
            var format = GetMessage("metadata_help_format");
            var output = GetMessage("metadata_help_output");
            var examples = GetMessage("metadata_help_examples");
            var ex1 = GetMessage("metadata_help_ex1");
            var ex2 = GetMessage("metadata_help_ex2");
            var ex3 = GetMessage("metadata_help_ex3");
            
            return $@"{title}

{usage}
{usageLine}

{filterOptions}
{titleFilter}
{author}
{subject}
{keywords}
{created}
{modified}

{outputOptions}
{format}
{output}

{examples}
{ex1}
{ex2}
{ex3}";
        }

        /// <summary>
        /// Get complete help text for Objects command
        /// </summary>
        public static string GetObjectsHelp()
        {
            var title = GetMessage("objects_help_title");
            var usage = GetMessage("objects_help_usage");
            var usageLine = GetMessage("objects_help_usage_line");
            var filterOptions = GetMessage("objects_help_filter_options");
            var type = GetMessage("objects_help_type");
            var content = GetMessage("objects_help_content");
            var size = GetMessage("objects_help_size");
            var compressed = GetMessage("objects_help_compressed");
            var page = GetMessage("objects_help_page");
            var outputOptions = GetMessage("objects_help_output_options");
            var format = GetMessage("objects_help_format");
            var output = GetMessage("objects_help_output");
            var examples = GetMessage("objects_help_examples");
            var ex1 = GetMessage("objects_help_ex1");
            var ex2 = GetMessage("objects_help_ex2");
            var ex3 = GetMessage("objects_help_ex3");
            
            return $@"{title}

{usage}
{usageLine}

{filterOptions}
{type}
{content}
{size}
{compressed}
{page}

{outputOptions}
{format}
{output}

{examples}
{ex1}
{ex2}
{ex3}";
        }

        /// <summary>
        /// Get complete help text for Base64 command
        /// </summary>
        public static string GetBase64Help()
        {
            var title = GetMessage("base64_help_title");
            var usage = GetMessage("base64_help_usage");
            var usageLine = GetMessage("base64_help_usage_line");
            var filterOptions = GetMessage("base64_help_filter_options");
            var minLength = GetMessage("base64_help_min_length");
            var decode = GetMessage("base64_help_decode");
            var type = GetMessage("base64_help_type");
            var page = GetMessage("base64_help_page");
            var extractPage = GetMessage("base64_help_extract_page");
            var outputOptions = GetMessage("base64_help_output_options");
            var format = GetMessage("base64_help_format");
            var output = GetMessage("base64_help_output");
            var saveDecoded = GetMessage("base64_help_save_decoded");
            var examples = GetMessage("base64_help_examples");
            var ex1 = GetMessage("base64_help_ex1");
            var ex2 = GetMessage("base64_help_ex2");
            var ex3 = GetMessage("base64_help_ex3");
            var ex4 = GetMessage("base64_help_ex4");
            
            return $@"{title}

{usage}
{usageLine}

{filterOptions}
{minLength}
{decode}
{type}
{page}
{extractPage}

{outputOptions}
{format}
{output}
{saveDecoded}

{examples}
{ex1}
{ex2}
{ex3}
{ex4}";
        }

        /// <summary>
        /// Get complete help text for Bookmarks command
        /// </summary>
        public static string GetBookmarksHelp()
        {
            var title = GetMessage("bookmarks_help_title");
            var usage = GetMessage("bookmarks_help_usage");
            var usageLine = GetMessage("bookmarks_help_usage_line");
            var filterOptions = GetMessage("bookmarks_help_filter_options");
            var word = GetMessage("bookmarks_help_word");
            var regex = GetMessage("bookmarks_help_regex");
            var value = GetMessage("bookmarks_help_value");
            var page = GetMessage("bookmarks_help_page");
            var pageRange = GetMessage("bookmarks_help_page_range");
            var level = GetMessage("bookmarks_help_level");
            var minLevel = GetMessage("bookmarks_help_min_level");
            var maxLevel = GetMessage("bookmarks_help_max_level");
            var hasChildren = GetMessage("bookmarks_help_has_children");
            var orientation = GetMessage("bookmarks_help_orientation");
            var header = GetMessage("bookmarks_help_header");
            var footer = GetMessage("bookmarks_help_footer");
            var outputOptions = GetMessage("bookmarks_help_output_options");
            var format = GetMessage("bookmarks_help_format");
            var output = GetMessage("bookmarks_help_output");
            var outputDir = GetMessage("bookmarks_help_output_dir");
            var important = GetMessage("bookmarks_help_important");
            var importantLine1 = GetMessage("bookmarks_help_important_line1");
            var importantLine2 = GetMessage("bookmarks_help_important_line2");
            var importantLine3 = GetMessage("bookmarks_help_important_line3");
            var examplesTitle = GetMessage("bookmarks_help_examples_title");
            var examplesWord = GetMessage("bookmarks_help_examples_word");
            var examplesRegex = GetMessage("bookmarks_help_examples_regex");
            var examplesPage = GetMessage("bookmarks_help_examples_page");
            var examplesPageRange = GetMessage("bookmarks_help_examples_page_range");
            var examplesLevel = GetMessage("bookmarks_help_examples_level");
            var examplesMinLevel = GetMessage("bookmarks_help_examples_min_level");
            var examplesMaxLevel = GetMessage("bookmarks_help_examples_max_level");
            var examplesHasChildren = GetMessage("bookmarks_help_examples_has_children");
            var examplesOrientation = GetMessage("bookmarks_help_examples_orientation");
            var examplesValue = GetMessage("bookmarks_help_examples_value");
            var examples = GetMessage("bookmarks_help_examples");
            var ex1Comment = GetMessage("bookmarks_help_ex1_comment");
            var ex1 = GetMessage("bookmarks_help_ex1");
            var ex2Comment = GetMessage("bookmarks_help_ex2_comment");
            var ex2 = GetMessage("bookmarks_help_ex2");
            var ex3Comment = GetMessage("bookmarks_help_ex3_comment");
            var ex3 = GetMessage("bookmarks_help_ex3");
            var ex4Comment = GetMessage("bookmarks_help_ex4_comment");
            var ex4 = GetMessage("bookmarks_help_ex4");
            
            return $@"{title}

{usage}
{usageLine}

{filterOptions}
{word}
{regex}
{value}
{page}
{pageRange}
{level}
{minLevel}
{maxLevel}
{hasChildren}
{orientation}
{header}
{footer}

{outputOptions}
{format}
{output}
{outputDir}

{important}
{importantLine1}
{importantLine2}
{importantLine3}

{examplesTitle}
{examplesWord}
{examplesRegex}
{examplesPage}
{examplesPageRange}
{examplesLevel}
{examplesMinLevel}
{examplesMaxLevel}
{examplesHasChildren}
{examplesOrientation}
{examplesValue}

{examples}
{ex1Comment}
{ex1}

{ex2Comment}
{ex2}

{ex3Comment}
{ex3}

{ex4Comment}
{ex4}";
        }
        
        /// <summary>
        /// Get complete help text for Annotations command
        /// </summary>
        public static string GetAnnotationsHelp()
        {
            var title = GetMessage("annotations_help_title");
            var usage = GetMessage("annotations_help_usage");
            var usageLine = GetMessage("annotations_help_usage_line");
            var description = GetMessage("annotations_help_description");
            var descLine1 = GetMessage("annotations_help_desc_line1");
            var descLine2 = GetMessage("annotations_help_desc_line2");
            var descLine3 = GetMessage("annotations_help_desc_line3");
            var filterOptions = GetMessage("annotations_help_filter_options");
            var word = GetMessage("annotations_help_word");
            var wordDesc = GetMessage("annotations_help_word_desc");
            var regex = GetMessage("annotations_help_regex");
            var value = GetMessage("annotations_help_value");
            var page = GetMessage("annotations_help_page");
            var pageRange = GetMessage("annotations_help_page_range");
            var pageRanges = GetMessage("annotations_help_page_ranges");
            var type = GetMessage("annotations_help_type");
            var typeDesc = GetMessage("annotations_help_type_desc");
            var author = GetMessage("annotations_help_author");
            var hasReply = GetMessage("annotations_help_has_reply");
            var dateFilters = GetMessage("annotations_help_date_filters");
            var createdBefore = GetMessage("annotations_help_created_before");
            var createdAfter = GetMessage("annotations_help_created_after");
            var modifiedBefore = GetMessage("annotations_help_modified_before");
            var modifiedAfter = GetMessage("annotations_help_modified_after");
            var outputOptions = GetMessage("annotations_help_output_options");
            var format = GetMessage("annotations_help_format");
            var formatDefault = GetMessage("annotations_help_format_default");
            var output = GetMessage("annotations_help_output");
            var outputDir = GetMessage("annotations_help_output_dir");
            var objects = GetMessage("annotations_help_objects");
            var textSearch = GetMessage("annotations_help_text_search");
            var or = GetMessage("annotations_help_or");
            var and = GetMessage("annotations_help_and");
            var examples = GetMessage("annotations_help_examples");
            var ex1Comment = GetMessage("annotations_help_ex1_comment");
            var ex1 = GetMessage("annotations_help_ex1");
            var ex2Comment = GetMessage("annotations_help_ex2_comment");
            var ex2 = GetMessage("annotations_help_ex2");
            var ex3Comment = GetMessage("annotations_help_ex3_comment");
            var ex3 = GetMessage("annotations_help_ex3");
            var ex4Comment = GetMessage("annotations_help_ex4_comment");
            var ex4 = GetMessage("annotations_help_ex4");
            var ex5Comment = GetMessage("annotations_help_ex5_comment");
            var ex5 = GetMessage("annotations_help_ex5");
            var ex6Comment = GetMessage("annotations_help_ex6_comment");
            var ex6 = GetMessage("annotations_help_ex6");
            var types = GetMessage("annotations_help_types");
            var typesDesc1 = GetMessage("annotations_help_types_desc1");
            var typesDesc2 = GetMessage("annotations_help_types_desc2");
            var typesDesc3 = GetMessage("annotations_help_types_desc3");
            var typesDesc4 = GetMessage("annotations_help_types_desc4");
            
            return $@"{title}

{usage}
{usageLine}

{description}
{descLine1}
{descLine2}
{descLine3}

{filterOptions}
{word}
{wordDesc}
{regex}
{value}
{page}
{pageRange}
{pageRanges}
{type}
{typeDesc}
{author}
{hasReply}

{dateFilters}
{createdBefore}
{createdAfter}
{modifiedBefore}
{modifiedAfter}

{outputOptions}
{format}
{formatDefault}
{output}
{outputDir}
{objects}

{textSearch}
{or}
{and}

{examples}
{ex1Comment}
{ex1}

{ex2Comment}
{ex2}

{ex3Comment}
{ex3}

{ex4Comment}
{ex4}

{ex5Comment}
{ex5}

{ex6Comment}
{ex6}

{types}
{typesDesc1}
{typesDesc2}
{typesDesc3}
{typesDesc4}";
        }

        /// <summary>
        /// Get complete help text for Documents command
        /// </summary>
        public static string GetDocumentsHelp()
        {
            var title = GetMessage("documents_help_title");
            var usage = GetMessage("documents_help_usage");
            var usageLine = GetMessage("documents_help_usage_line");
            var description = GetMessage("documents_help_description");
            var descLine1 = GetMessage("documents_help_desc_line1");
            var descLine2 = GetMessage("documents_help_desc_line2");
            var descLine3 = GetMessage("documents_help_desc_line3");
            var options = GetMessage("documents_help_options");
            var word = GetMessage("documents_help_word");
            var notWords = GetMessage("documents_help_not_words");
            var font = GetMessage("documents_help_font");
            var regex = GetMessage("documents_help_regex");
            var value = GetMessage("documents_help_value");
            var signature = GetMessage("documents_help_signature");
            var minPages = GetMessage("documents_help_min_pages");
            var minConfidence = GetMessage("documents_help_min_confidence");
            var verbose = GetMessage("documents_help_verbose");
            var format = GetMessage("documents_help_format");
            var output = GetMessage("documents_help_output");
            var outputDir = GetMessage("documents_help_output_dir");
            var important = GetMessage("documents_help_important");
            var importantLine1 = GetMessage("documents_help_important_line1");
            var importantLine2 = GetMessage("documents_help_important_line2");
            var importantLine3 = GetMessage("documents_help_important_line3");
            var examplesTitle = GetMessage("documents_help_examples_title");
            var examplesWord = GetMessage("documents_help_examples_word");
            var examplesFont = GetMessage("documents_help_examples_font");
            var examplesRegex = GetMessage("documents_help_examples_regex");
            var examplesValue = GetMessage("documents_help_examples_value");
            var examplesSignature = GetMessage("documents_help_examples_signature");
            var examplesNotWords = GetMessage("documents_help_examples_not_words");
            var strategies = GetMessage("documents_help_strategies");
            var strategy1 = GetMessage("documents_help_strategy1");
            var strategy2 = GetMessage("documents_help_strategy2");
            var strategy3 = GetMessage("documents_help_strategy3");
            var strategy4 = GetMessage("documents_help_strategy4");
            var strategy5 = GetMessage("documents_help_strategy5");
            var strategy6 = GetMessage("documents_help_strategy6");
            var strategy7 = GetMessage("documents_help_strategy7");
            var examples = GetMessage("documents_help_examples");
            var ex1Comment = GetMessage("documents_help_ex1_comment");
            var ex1 = GetMessage("documents_help_ex1");
            var ex2Comment = GetMessage("documents_help_ex2_comment");
            var ex2 = GetMessage("documents_help_ex2");
            var ex3Comment = GetMessage("documents_help_ex3_comment");
            var ex3 = GetMessage("documents_help_ex3");
            var ex4Comment = GetMessage("documents_help_ex4_comment");
            var ex4 = GetMessage("documents_help_ex4");
            var ex5Comment = GetMessage("documents_help_ex5_comment");
            var ex5 = GetMessage("documents_help_ex5");
            var ex6Comment = GetMessage("documents_help_ex6_comment");
            var ex6 = GetMessage("documents_help_ex6");
            var ex7Comment = GetMessage("documents_help_ex7_comment");
            var ex7 = GetMessage("documents_help_ex7");
            var ex8Comment = GetMessage("documents_help_ex8_comment");
            var ex8 = GetMessage("documents_help_ex8");
            var ex9Comment = GetMessage("documents_help_ex9_comment");
            var ex9 = GetMessage("documents_help_ex9");
            var note = GetMessage("documents_help_note");
            
            return $@"{title}

{usage}
{usageLine}

{description}
{descLine1}
{descLine2}
{descLine3}

{options}
{word}
{notWords}
{font}
{regex}
{value}
{signature}
{minPages}
{minConfidence}
{verbose}
{format}
{output}
{outputDir}

{important}
{importantLine1}
{importantLine2}
{importantLine3}

{examplesTitle}
{examplesWord}
{examplesFont}
{examplesRegex}
{examplesValue}
{examplesSignature}
{examplesNotWords}

{strategies}
{strategy1}
{strategy2}
{strategy3}
{strategy4}
{strategy5}
{strategy6}
{strategy7}

{examples}
{ex1Comment}
{ex1}

{ex2Comment}
{ex2}

{ex3Comment}
{ex3}

{ex4Comment}
{ex4}

{ex5Comment}
{ex5}

{ex6Comment}
{ex6}

{ex7Comment}
{ex7}

{ex8Comment}
{ex8}

{ex9Comment}
{ex9}
{note}";
        }

        /// <summary>
        /// Get complete help text for FpdfPagesCommand
        /// </summary>
        public static string GetPagesHelp()
        {
            if (_currentLanguage == "pt")
            {
                return @"FILTRAR PÁGINAS - Encontrar páginas que correspondem a critérios específicos

USO:
    fpdf <índice-cache> pages [opções]

OPÇÕES DE BUSCA DE TEXTO:
    -p, --page <texto>        Buscar texto nas páginas (suporta operadores & e |)
    -w, --word <texto>        Buscar palavra específica nas páginas
    --not-words <texto>       Excluir páginas contendo palavras específicas (suporta & e |)
    -r, --regex <padrão>      Buscar usando expressão regular
    -v, --value               Encontrar páginas contendo valores em reais (R$)
    -s, --signature <nome>    Encontrar páginas com assinaturas (nomes ou padrões)

OPÇÕES DE SELEÇÃO DE PÁGINAS:
    --page-ranges, --pr <intervalos>  Selecionar páginas por número (sintaxe qpdf)
    --page-range <início-fim>         Selecionar intervalo de páginas (ex: 5-10)
    --first <n>                       Selecionar primeiras n páginas
    --last <n>                        Selecionar últimas n páginas

FILTROS DE CONTEÚDO:
    -f, --font <nome>        Páginas usando fonte específica
    --font-bold <true/false> Páginas com/sem fontes negrito
    --font-italic <true/false> Páginas com/sem fontes itálico
    --font-mono <true/false> Páginas com/sem fontes monoespaçadas
    --font-serif <true/false> Páginas com/sem fontes serif
    --font-sans <true/false> Páginas com/sem fontes sans-serif
    -i, --image <true/false> Páginas com/sem imagens
    -a, --annotations <bool> Páginas com/sem anotações
    --min-words <n>          Páginas com pelo menos n palavras
    --max-words <n>          Páginas com no máximo n palavras
    -or, --orientation <tipo> Páginas com orientação retrato/paisagem
    --blank                  Encontrar páginas em branco
    --tables <true/false>    Páginas com/sem tabelas
    --columns <true/false>   Páginas com/sem colunas
    -hd <texto>              Buscar texto em cabeçalhos (10% superior da página)
    -hd                      Mostrar conteúdo de cabeçalhos para todas as páginas

FILTROS DE TAMANHO DE PÁGINA:
    --paper-size <tamanho>   Filtrar por tamanho de papel (A4, A3, LETTER, LEGAL, etc.)
    --width <pontos>         Filtrar por largura exata em pontos
    --height <pontos>        Filtrar por altura exata em pontos
    --min-width <pontos>     Largura mínima em pontos
    --max-width <pontos>     Largura máxima em pontos
    --min-height <pontos>    Altura mínima em pontos
    --max-height <pontos>    Altura máxima em pontos

FILTROS DE TAMANHO DE ARQUIVO:
    --min-size-mb <número>   Tamanho mínimo da página em megabytes (MB)
    --max-size-mb <número>   Tamanho máximo da página em megabytes (MB)
    --min-size-kb <número>   Tamanho mínimo da página em kilobytes (KB)
    --max-size-kb <número>   Tamanho máximo da página em kilobytes (KB)

OPÇÕES DE ORDENAÇÃO:
    --sort-by-size          Ordenar páginas por tamanho (maior primeiro)
    --sort-by-dimensions    Ordenar por área (largura × altura)
    --sort-by-words         Ordenar por quantidade de palavras

OPÇÕES DE SAÍDA:
    -F, --format <formato>   Formato de saída: txt, json, xml, csv, md, raw, count, ocr
    -u, --unique             Mostrar apenas características únicas de cada página
    -o, --output, --output-file <arquivo>  Salvar resultados em arquivo
    --output-dir <diretório> Salvar resultados em diretório com nome auto-gerado

OPERADORES DE BUSCA DE TEXTO:

  OPERADORES BÁSICOS:
    |  Operador OU:  'palavra1|palavra2' encontra páginas com qualquer palavra
    &  Operador E:   'palavra1&palavra2' encontra páginas com ambas palavras
    *  Curinga:      'doc*' corresponde a 'documento', 'docs', etc.
    ?  Caractere único: 'doc?' corresponde a 'docs' mas não 'documento'

  BUSCA NORMALIZADA (ignora acentos e maiúsculas):
    ~palavra~  Envolver palavra em til para busca normalizada

  EXEMPLOS:
    Busca básica (correspondência exata):
      -w ""CERTIDÃO""             → Encontra apenas 'CERTIDÃO' (maiúsculas e acentos exatos)
      -w ""certidão""             → Encontra apenas 'certidão' (maiúsculas e acentos exatos)

    Busca normalizada:
      -w ""~certidao~""           → Encontra: CERTIDÃO, Certidão, certidao, CERTIDAO
      -w ""~justica~""            → Encontra: JUSTIÇA, Justiça, justiça, JUSTICA

    Combinado com E (&):
      -w ""~certidao~&Robson""    → Páginas com certidão (qualquer forma) E Robson (exato)
      -w ""~justica~&~tribunal~"" → Páginas com justiça E tribunal (ambos normalizados)
      -w ""BANCO&~certidao~""     → Páginas com BANCO (exato) E certidão (qualquer forma)

SINTAXE DE INTERVALOS DE PÁGINAS (--page-ranges):
    1,3,5         Páginas específicas
    1-10          Intervalo de páginas
    r1            Última página (r2 = penúltima, etc.)
    1-10,x3-4     Páginas 1-10 exceto 3 e 4
    1-10:even     Páginas pares no intervalo
    1-10:odd      Páginas ímpares no intervalo

OPERADORES DE BUSCA DE ASSINATURA:
    nome1|nome2   Encontrar páginas com assinatura nome1 OU nome2
    nome1&nome2   Encontrar páginas com assinaturas nome1 E nome2
    'Assinado por' Encontrar páginas com padrões de assinatura

EXEMPLOS:
    # Encontrar páginas contendo 'contrato' ou 'acordo'
    fpdf 1 pages -p 'contrato|acordo'

    # Encontrar páginas com 'fatura' e '2024'
    fpdf 1 pages --page 'fatura&2024'

    # Selecionar páginas 1-5 e última página
    fpdf 1 pages --page-ranges '1-5,r1'

    # Encontrar páginas com imagens contendo 'logo'
    fpdf 1 pages -i true -w 'logo'

    # Encontrar páginas contendo 'contrato' mas não 'cancelado'
    fpdf 1 pages -w 'contrato' --not-words 'cancelado'

    # Encontrar páginas em formato A4
    fpdf 1 pages --paper-size A4

    # Encontrar páginas com largura maior que 600 pontos
    fpdf 1 pages --min-width 600

    # Encontrar páginas A3 com logo
    fpdf 1 pages --paper-size A3 -w 'logo'
    
    # Encontrar páginas maiores que 2 MB
    fpdf 1 pages --min-size-mb 2.0
    
    # Encontrar páginas entre 500 KB e 1.5 MB
    fpdf 1 pages --min-size-kb 500 --max-size-mb 1.5
    
    # Encontrar páginas pequenas (menos de 100 KB) com imagens
    fpdf 1 pages --max-size-kb 100 -i true
    
    # Encontrar páginas com assinatura específica
    fpdf 1 pages --signature 'João Silva'
    
    # Encontrar páginas com qualquer assinatura (João OU Maria)
    fpdf 1 pages --signature 'João|Maria'
    
    # Encontrar páginas com ambas assinaturas (João E Maria)
    fpdf 1 pages --signature 'João&Maria'
    
    # Encontrar páginas com padrões de assinatura
    fpdf 1 pages --signature 'Assinado por'

NOTA: A busca de texto trata automaticamente texto espaçado (correspondência difusa)
      'anexo' encontrará 'a n e x o' em PDFs com espaçamento de caracteres
      
NOTA SOBRE ASSINATURAS: A busca por assinaturas foca nos últimos 30% da página
      onde assinaturas geralmente aparecem, mas também procura nomes no documento
      inteiro se encontrar indicadores de assinatura como 'Assinado por', linhas '_____'";
            }
            else
            {
                return @"FILTER PAGES - Find pages that match specific criteria

USAGE:
    fpdf <cache-index> pages [options]

TEXT SEARCH OPTIONS:
    -p, --page <text>        Search for text in pages (supports & and | operators)
    -w, --word <text>        Search for specific word in pages
    --not-words <text>       Exclude pages containing specific words (supports & and | operators)
    -r, --regex <pattern>    Search using regular expression
    -v, --value              Find pages containing Brazilian currency values (R$)
    -s, --signature <name>   Find pages with signatures (names or patterns)

PAGE SELECTION OPTIONS:
    --page-ranges, --pr <ranges>  Select pages by number (qpdf-style syntax)
    --page-range <start-end>      Select page range (e.g., 5-10)
    --first <n>                   Select first n pages
    --last <n>                    Select last n pages

CONTENT FILTERS:
    -f, --font <name>        Pages using specific font
    --font-bold <true/false> Pages with/without bold fonts
    --font-italic <true/false> Pages with/without italic fonts
    --font-mono <true/false> Pages with/without monospace fonts
    --font-serif <true/false> Pages with/without serif fonts
    --font-sans <true/false> Pages with/without sans-serif fonts
    -i, --image <true/false> Pages with/without images
    -a, --annotations <bool> Pages with/without annotations
    --min-words <n>          Pages with at least n words
    --max-words <n>          Pages with at most n words
    -or, --orientation <type> Pages with portrait/landscape orientation
    --blank                  Find blank pages
    --tables <true/false>    Pages with/without tables
    --columns <true/false>   Pages with/without columns
    -hd <text>               Search text in headers (top 10% of page)
    -hd                      Show headers content for all pages

PAGE SIZE FILTERS:
    --paper-size <size>      Filter by paper size (A4, A3, LETTER, LEGAL, etc.)
    --width <points>         Filter by exact width in points
    --height <points>        Filter by exact height in points
    --min-width <points>     Minimum width in points
    --max-width <points>     Maximum width in points
    --min-height <points>    Minimum height in points
    --max-height <points>    Maximum height in points

FILE SIZE FILTERS:
    --min-size-mb <number>   Minimum page size in megabytes (MB)
    --max-size-mb <number>   Maximum page size in megabytes (MB)
    --min-size-kb <number>   Minimum page size in kilobytes (KB)
    --max-size-kb <number>   Maximum page size in kilobytes (KB)

SORTING OPTIONS:
    --sort-by-size          Sort pages by size (largest first)
    --sort-by-dimensions    Sort by area (width × height)
    --sort-by-words         Sort by word count

OUTPUT OPTIONS:
    -F, --format <format>    Output format: txt, json, xml, csv, md, raw, count, ocr
    -u, --unique             Show only unique characteristics of each page
    -o, --output, --output-file <file>  Save results to file
    --output-dir <dir>       Save results to directory with auto-generated filename

TEXT SEARCH OPERATORS:

  BASIC OPERATORS:
    |  OR operator:  'word1|word2' finds pages with either word
    &  AND operator: 'word1&word2' finds pages with both words
    *  Wildcard:     'doc*' matches 'document', 'docs', etc.
    ?  Single char:  'doc?' matches 'docs' but not 'document'

  NORMALIZED SEARCH (ignores accents and case):
    ~word~  Wrap word in tildes for normalized search

  EXAMPLES:
    Basic search (exact match):
      -w ""CERTIDÃO""             → Finds only 'CERTIDÃO' (exact case and accent)
      -w ""certidão""             → Finds only 'certidão' (exact case and accent)

    Normalized search:
      -w ""~certidao~""           → Finds: CERTIDÃO, Certidão, certidao, CERTIDAO
      -w ""~justica~""            → Finds: JUSTIÇA, Justiça, justiça, JUSTICA

    Combined with AND (&):
      -w ""~certidao~&Robson""    → Pages with certidão (any form) AND Robson (exact)
      -w ""~justica~&~tribunal~"" → Pages with justiça AND tribunal (both normalized)
      -w ""BANCO&~certidao~""     → Pages with BANCO (exact) AND certidão (any form)

PAGE RANGES SYNTAX (--page-ranges):
    1,3,5         Specific pages
    1-10          Range of pages
    r1            Last page (r2 = second to last, etc.)
    1-10,x3-4     Pages 1-10 except 3 and 4
    1-10:even     Even-positioned pages in range
    1-10:odd      Odd-positioned pages in range

SIGNATURE SEARCH OPERATORS:
    name1|name2   Find pages with name1 OR name2 signature
    name1&name2   Find pages with name1 AND name2 signatures
    'Signed by'   Find pages with signature patterns

EXAMPLES:
    # Find pages containing 'contract' or 'agreement'
    fpdf 1 pages -p 'contract|agreement'

    # Find pages with both 'invoice' and '2024'
    fpdf 1 pages --page 'invoice&2024'

    # Select pages 1-5 and last page
    fpdf 1 pages --page-ranges '1-5,r1'

    # Find pages with images containing 'logo'
    fpdf 1 pages -i true -w 'logo'

    # Find pages containing 'contract' but not 'cancelled'
    fpdf 1 pages -w 'contract' --not-words 'cancelled'

    # Find pages in A4 format
    fpdf 1 pages --paper-size A4

    # Find pages wider than 600 points
    fpdf 1 pages --min-width 600

    # Find A3 pages with logo
    fpdf 1 pages --paper-size A3 -w 'logo'
    
    # Find pages larger than 2 MB
    fpdf 1 pages --min-size-mb 2.0
    
    # Find pages between 500 KB and 1.5 MB
    fpdf 1 pages --min-size-kb 500 --max-size-mb 1.5
    
    # Find small pages (less than 100 KB) with images
    fpdf 1 pages --max-size-kb 100 -i true
    
    # Find pages with specific signature
    fpdf 1 pages --signature 'John Smith'
    
    # Find pages with any signature (John OR Mary)
    fpdf 1 pages --signature 'John|Mary'
    
    # Find pages with both signatures (John AND Mary)
    fpdf 1 pages --signature 'John&Mary'
    
    # Find pages with signature patterns
    fpdf 1 pages --signature 'Signed by'

NOTE: Text search automatically handles spaced text (fuzzy matching)
      'anexo' will find 'a n e x o' in PDFs with character spacing
      
NOTE ABOUT SIGNATURES: Signature search focuses on the last 30% of the page
      where signatures typically appear, but also searches the entire document
      for names if signature indicators like 'Signed by', lines '_____' are found";
            }
        }
        
        private static void LoadLanguageSettings()
        {
            try
            {
                if (File.Exists(_settingsFile))
                {
                    var json = File.ReadAllText(_settingsFile);
                    var settings = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                    if (settings?.ContainsKey("language") == true)
                    {
                        var lang = settings["language"];
                        if (_messages.ContainsKey(lang))
                        {
                            _currentLanguage = lang;
                        }
                    }
                }
            }
            catch
            {
                // Ignore errors, use default language
            }
        }
        
        private static void SaveLanguageSettings()
        {
            try
            {
                var dir = Path.GetDirectoryName(_settingsFile);
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                
                var settings = new Dictionary<string, string>
                {
                    ["language"] = _currentLanguage
                };
                
                var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
                File.WriteAllText(_settingsFile, json);
            }
            catch
            {
                // Ignore errors
            }
        }
    }
}