using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace FilterPDF
{
    /// <summary>
    /// Conversor customizado para manter o formato JSON original
    /// </summary>
    public class CustomDateTimeConverter : DateTimeConverterBase
    {
        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
        {
            if (value is DateTime dateTime)
            {
                // Formato original: "/Date(1752169024600)/"
                var ticks = new DateTimeOffset(dateTime).ToUnixTimeMilliseconds();
                writer.WriteValue($"/Date({ticks})/");
            }
            else
            {
                writer.WriteNull();
            }
        }

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            if (reader.Value is string dateStr && dateStr.StartsWith("/Date(") && dateStr.EndsWith(")/"))
            {
                var ticksStr = dateStr.Substring(6, dateStr.Length - 8);
                if (long.TryParse(ticksStr, out long ticks))
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(ticks).DateTime;
                }
            }
            return null;
        }
    }

    /// <summary>
    /// Configurações customizadas para serialização JSON
    /// </summary>
    public static class JsonConfig
    {
        public static JsonSerializerSettings GetSettings()
        {
            return new JsonSerializerSettings
            {
                DateFormatHandling = DateFormatHandling.MicrosoftDateFormat,
                NullValueHandling = NullValueHandling.Include,
                Formatting = Formatting.Indented,
                Converters = new List<JsonConverter> { new CustomDateTimeConverter() }
            };
        }
    }

    /// <summary>
    /// Classe para corrigir nomes de fontes (remover /)
    /// </summary>
    public static class FontNameFixer
    {
        public static string Fix(string fontName)
        {
            if (string.IsNullOrEmpty(fontName))
                return fontName;
                
            // Remove / do início se existir
            if (fontName.StartsWith("/"))
                return fontName.Substring(1);
                
            return fontName;
        }
    }
}