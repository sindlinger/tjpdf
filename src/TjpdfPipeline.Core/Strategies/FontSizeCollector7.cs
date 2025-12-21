using System.Collections.Generic;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace FilterPDF.Strategies
{
    /// <summary>
    /// Collects font sizes per font name on a page using iText7 parser events.
    /// </summary>
    public class FontSizeCollector7 : IEventListener
    {
        private readonly Dictionary<string, HashSet<float>> _sizes;

        public FontSizeCollector7(Dictionary<string, HashSet<float>> sizes)
        {
            _sizes = sizes;
        }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT) return;
            var info = (TextRenderInfo)data;
            var font = info.GetFont();
            if (font == null) return;

            string fontName = font.GetFontProgram()?.GetFontNames()?.GetFontName() ?? font.GetFontProgram()?.ToString() ?? "Unknown";
            if (!_sizes.TryGetValue(fontName, out var set))
            {
                set = new HashSet<float>();
                _sizes[fontName] = set;
            }
            set.Add(info.GetFontSize());
        }

        public ICollection<EventType> GetSupportedEvents() => new[] { EventType.RENDER_TEXT };
    }
}
