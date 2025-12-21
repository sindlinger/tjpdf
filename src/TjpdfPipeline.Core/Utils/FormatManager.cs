using System;
using System.Collections.Generic;
using System.Linq;

namespace FilterPDF
{
    /// <summary>
    /// Gerenciador universal de formatos de saída (-F)
    /// </summary>
    public static class FormatManager
    {
        // Formatos universais suportados por TODOS os comandos
        private static readonly HashSet<string> UniversalFormats = new HashSet<string> 
        { 
            "txt", "raw", "json", "xml", "csv", "md", "count", "ocr", "png" 
        };
        
        // Formatos padrão por comando
        private static readonly Dictionary<string, string> DefaultFormats = new Dictionary<string, string>
        {
            ["extract"] = "txt",
            ["analyze"] = "json",
            ["load"] = "json"
        };
        
        /// <summary>
        /// Valida e normaliza o formato para um comando específico
        /// </summary>
        public static string ValidateFormat(string command, string format)
        {
            // Se não especificado, usar padrão
            if (string.IsNullOrEmpty(format))
            {
                return DefaultFormats.ContainsKey(command) ? DefaultFormats[command] : "txt";
            }
            
            // Normalizar formato
            format = format.ToLower();
            
            // Verificar se é formato universal válido
            if (!UniversalFormats.Contains(format))
            {
                var supported = string.Join(", ", UniversalFormats.OrderBy(f => f));
                var defaultFormat = DefaultFormats.ContainsKey(command) ? DefaultFormats[command] : "txt";
                
                Console.WriteLine($"Warning: Format '{format}' is not supported. Supported formats: {supported}");
                Console.WriteLine($"Using default format: {defaultFormat}");
                
                return defaultFormat;
            }
            
            return format;
        }
        
        /// <summary>
        /// Obtém descrição do formato
        /// </summary>
        public static string GetFormatDescription(string format)
        {
            switch (format)
            {
                case "txt":
                    return "Plain text with preserved layout";
                case "raw":
                    return "Raw data without formatting";
                case "json":
                    return "Structured JSON format";
                case "xml":
                    return "Structured XML format";
                case "csv":
                    return "Comma-separated values";
                case "md":
                    return "Markdown formatted text";
                case "count":
                    return "Count of results only";
                case "ocr":
                    return "OCR text extraction with Brazilian pattern recognition";
                case "png":
                    return "Extract filtered pages as PNG images";
                default:
                    return "Unknown format";
            }
        }
        
        /// <summary>
        /// Obtém extensão de arquivo apropriada para o formato
        /// </summary>
        public static string GetFileExtension(string format)
        {
            switch (format)
            {
                case "txt":
                case "raw":
                    return ".txt";
                case "json":
                    return ".json";
                case "xml":
                    return ".xml";
                case "csv":
                    return ".csv";
                case "md":
                    return ".md";
                case "count":
                    return ".txt";
                case "ocr":
                    return ".txt";
                case "png":
                    return ".png";
                default:
                    return ".txt";
            }
        }
        
        /// <summary>
        /// Obtém lista de formatos suportados (universal para todos os comandos)
        /// </summary>
        public static string GetSupportedFormatsHelp(string command)
        {
            var defaultFormat = DefaultFormats.ContainsKey(command) ? DefaultFormats[command] : "txt";
            
            var lines = new List<string>
            {
                "SUPPORTED FORMATS (-F):"
            };
            
            foreach (var format in UniversalFormats.OrderBy(f => f))
            {
                var desc = GetFormatDescription(format);
                var isDefault = format == defaultFormat ? " (default)" : "";
                lines.Add($"    {format,-8} {desc}{isDefault}");
            }
            
            return string.Join("\n", lines);
        }
        
        /// <summary>
        /// Extrai formato das opções de linha de comando
        /// </summary>
        public static string ExtractFormat(Dictionary<string, string> options, string command)
        {
            string? format = null;
            
            // Procurar por -F (maiúsculo)
            if (options.ContainsKey("-F"))
            {
                format = options["-F"];
            }
            // Manter compatibilidade com antigo -f/--format por enquanto
            else if (options.ContainsKey("-f") || options.ContainsKey("--format"))
            {
                format = options.ContainsKey("-f") ? options["-f"] : options["--format"];
            }
            // Check for "format" key (argument parsing may convert --format to format)
            else if (options.ContainsKey("format"))
            {
                format = options["format"];
            }
            
            return ValidateFormat(command, format ?? "");
        }
    }
}