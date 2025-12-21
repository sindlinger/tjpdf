using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FilterPDF
{
    /// <summary>
    /// Detector de valores monetários brasileiros (R$)
    /// Identifica e extrai valores em reais de textos
    /// </summary>
    public static class BrazilianCurrencyDetector
    {
        // Regex para detectar valores monetários brasileiros
        // Formatos suportados:
        // R$ 1.234,56
        // R$1.234,56
        // R$ 1234,56
        // R$ 1.234.567,89
        // 1.234,56 (quando seguido de palavras como reais)
        private static readonly Regex CurrencyRegex = new Regex(
            @"(?:R\$\s*)?(\d{1,3}(?:\.\d{3})*(?:,\d{2})?)|(\d+(?:,\d{2})?)\s*(?:\breais\b|\breal\b|R\$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );
        
        // Regex mais específico para R$
        private static readonly Regex ExplicitCurrencyRegex = new Regex(
            @"R\$\s*(\d{1,3}(?:\.\d{3})*(?:,\d{2})?)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        );
        
        /// <summary>
        /// Verifica se o texto contém algum valor monetário brasileiro
        /// </summary>
        public static bool ContainsBrazilianCurrency(string text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
                
            return CurrencyRegex.IsMatch(text) || ExplicitCurrencyRegex.IsMatch(text);
        }
        
        /// <summary>
        /// Extrai todos os valores monetários encontrados no texto
        /// </summary>
        public static List<MonetaryValue> ExtractCurrencyValues(string text)
        {
            var values = new List<MonetaryValue>();
            
            if (string.IsNullOrEmpty(text))
                return values;
            
            // Primeiro, procurar por valores explícitos com R$
            var explicitMatches = ExplicitCurrencyRegex.Matches(text);
            foreach (Match match in explicitMatches)
            {
                var value = ParseMonetaryValue(match.Groups[1].Value);
                if (value != null)
                {
                    value.OriginalText = match.Value;
                    value.Position = match.Index;
                    values.Add(value);
                }
            }
            
            // Depois, procurar por outros padrões
            var generalMatches = CurrencyRegex.Matches(text);
            foreach (Match match in generalMatches)
            {
                // Evitar duplicatas
                if (values.Any(v => v.Position == match.Index))
                    continue;
                    
                string valueStr = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                var value = ParseMonetaryValue(valueStr);
                if (value != null)
                {
                    value.OriginalText = match.Value;
                    value.Position = match.Index;
                    values.Add(value);
                }
            }
            
            return values.OrderBy(v => v.Position).ToList();
        }
        
        /// <summary>
        /// Converte string de valor monetário brasileiro para decimal
        /// </summary>
        private static MonetaryValue? ParseMonetaryValue(string valueStr)
        {
            try
            {
                // Remover pontos de milhar e converter vírgula decimal
                string normalized = valueStr.Replace(".", "").Replace(",", ".");
                
                if (decimal.TryParse(normalized, 
                    System.Globalization.NumberStyles.Number, 
                    System.Globalization.CultureInfo.InvariantCulture, 
                    out decimal value))
                {
                    return new MonetaryValue
                    {
                        Value = value,
                        FormattedValue = FormatCurrency(value)
                    };
                }
            }
            catch
            {
                // Ignorar erros de parsing
            }
            
            return null;
        }
        
        /// <summary>
        /// Formata valor decimal como moeda brasileira
        /// </summary>
        public static string FormatCurrency(decimal value)
        {
            return value.ToString("C", new System.Globalization.CultureInfo("pt-BR"));
        }
    }
    
    /// <summary>
    /// Representa um valor monetário encontrado no texto
    /// </summary>
    public class MonetaryValue
    {
        public decimal Value { get; set; }
        public string FormattedValue { get; set; } = string.Empty;
        public string OriginalText { get; set; } = string.Empty;
        public int Position { get; set; }
        
        public override string ToString()
        {
            return FormattedValue;
        }
    }
}