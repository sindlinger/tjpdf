using System;
using System.Collections.Generic;
using System.Linq;
using FilterPDF;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Data;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace FilterPDF.Strategies
{
    /// <summary>
    /// Coleta texto por linha usando iText7, com fonte/tamanho/estilo/render mode,
    /// espaçamentos e bounding box. Agrupa por Y aproximado.
    /// </summary>
    public class IText7LineCollector : IEventListener
    {
        private class LineBucket
        {
            public float Y;
            public List<TextRenderInfo> Infos = new List<TextRenderInfo>();
        }

        private readonly List<LineBucket> buckets = new List<LineBucket>();
        private readonly float yTolerance;

        public IText7LineCollector(float yTolerance = 1.5f)
        {
            this.yTolerance = yTolerance;
        }

        public void EventOccurred(IEventData data, EventType type)
        {
            if (type != EventType.RENDER_TEXT) return;
            var renderInfo = (TextRenderInfo)data;

            var baseline = renderInfo.GetBaseline();
            float y = baseline.GetStartPoint().Get(Vector.I2);

            foreach (var bucket in buckets)
            {
                if (Math.Abs(bucket.Y - y) <= yTolerance)
                {
                    bucket.Infos.Add(renderInfo);
                    return;
                }
            }

            buckets.Add(new LineBucket
            {
                Y = y,
                Infos = new List<TextRenderInfo> { renderInfo }
            });
        }

        public ICollection<EventType> GetSupportedEvents()
        {
            return new EventType[] { EventType.RENDER_TEXT };
        }

        public List<LineInfo> GetLines()
        {
            var result = new List<LineInfo>();

            foreach (var bucket in buckets)
            {
                if (bucket.Infos.Count == 0) continue;

                var ordered = bucket.Infos
                    .OrderBy(i => i.GetBaseline().GetStartPoint().Get(Vector.I1))
                    .ToList();

                string text = BuildLineText(ordered);

                float x0 = ordered.First().GetBaseline().GetStartPoint().Get(Vector.I1);
                float x1 = ordered.Last().GetAscentLine().GetEndPoint().Get(Vector.I1);
                float y0 = ordered.Min(i => i.GetDescentLine().GetStartPoint().Get(Vector.I2));
                float y1 = ordered.Max(i => i.GetAscentLine().GetEndPoint().Get(Vector.I2));

                var info = ordered.First();
                var font = info.GetFont();
                string fontName = font?.GetFontProgram()?.GetFontNames()?.GetFontName() ?? "";
                float size = Math.Abs(info.GetAscentLine().GetStartPoint().Get(Vector.I2) -
                                      info.GetDescentLine().GetStartPoint().Get(Vector.I2));

                int renderMode = info.GetTextRenderMode();
                bool underline = renderMode == 3 || renderMode == 2; // stroke modes (proxy)
                bool bold = fontName.ToLower().Contains("bold") || fontName.ToLower().Contains("black") ||
                            fontName.ToLower().Contains("heavy");
                bool italic = fontName.ToLower().Contains("italic") || fontName.ToLower().Contains("oblique") ||
                              fontName.ToLower().Contains("slant");

                // Métricas de espaçamento diretamente do TextRenderInfo (iText7)
                float charSpacing = 0;
                float wordSpacing = info.GetWordSpacing();
                float horizScaling = info.GetHorizontalScaling();
                float rise = info.GetRise();

                string norm = text.Trim().ToLowerInvariant();
                string hash;
                using (var sha1 = System.Security.Cryptography.SHA1.Create())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(norm);
                    hash = BitConverter.ToString(sha1.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                }

                result.Add(new LineInfo
                {
                    Text = text,
                    Font = fontName,
                    Size = size,
                    Bold = bold,
                    Italic = italic,
                    Underline = underline,
                    RenderMode = renderMode,
                    CharSpacing = charSpacing,
                    WordSpacing = wordSpacing,
                    HorizontalScaling = horizScaling,
                    Rise = rise,
                    LineHash = hash,
                    X0 = x0,
                    Y0 = y0,
                    X1 = x1,
                    Y1 = y1
                });
            }

            return result.OrderByDescending(l => l.Y0).ToList();
        }

        private string BuildLineText(List<TextRenderInfo> ordered)
        {
            if (ordered == null || ordered.Count == 0) return string.Empty;
            var sb = new System.Text.StringBuilder();
            TextRenderInfo? prev = null;

            float letterGapHint = GetMedianGap(ordered);
            foreach (var info in ordered)
            {
                var txt = info.GetText() ?? "";
                if (prev != null)
                {
                    if (!EndsWithSpace(sb) && !StartsWithSpace(txt))
                    {
                        float gap = GetGap(prev, info);
                        float threshold = letterGapHint > 0
                            ? (IsSingleToken(prev) && IsSingleToken(info) ? letterGapHint * 2.2f : letterGapHint * 1.15f)
                            : GetSpaceThreshold(prev);
                        if (gap > threshold) sb.Append(' ');
                    }
                }

                sb.Append(txt);
                prev = info;
            }

            return sb.ToString();
        }

        private bool StartsWithSpace(string text)
        {
            return !string.IsNullOrEmpty(text) && char.IsWhiteSpace(text[0]);
        }

        private bool EndsWithSpace(System.Text.StringBuilder sb)
        {
            if (sb == null || sb.Length == 0) return false;
            return char.IsWhiteSpace(sb[sb.Length - 1]);
        }

        private float GetGap(TextRenderInfo prev, TextRenderInfo curr)
        {
            float prevEnd = prev.GetAscentLine().GetEndPoint().Get(Vector.I1);
            float currStart = curr.GetBaseline().GetStartPoint().Get(Vector.I1);
            return currStart - prevEnd;
        }

        private float GetMedianGap(IList<TextRenderInfo> infos)
        {
            if (infos == null || infos.Count < 2) return 0;
            var gaps = new List<float>();
            for (int i = 1; i < infos.Count; i++)
            {
                var a = infos[i - 1];
                var b = infos[i];
                var ta = a.GetText();
                var tb = b.GetText();
                if (string.IsNullOrWhiteSpace(ta) || string.IsNullOrWhiteSpace(tb)) continue;
                float gap = GetGap(a, b);
                if (gap > 0) gaps.Add(gap);
            }
            if (gaps.Count == 0) return 0;
            gaps.Sort();
            int mid = gaps.Count / 2;
            return gaps.Count % 2 == 0 ? (gaps[mid - 1] + gaps[mid]) / 2f : gaps[mid];
        }

        private float GetSpaceThreshold(TextRenderInfo info)
        {
            float fontSize = GetFontSize(info);
            float space = 0;
            try { space = info.GetSingleSpaceWidth(); } catch { space = 0; }
            if (space <= 0) space = fontSize * 0.3f;
            return Math.Max(space * 0.9f, fontSize * 0.35f);
        }

        private float GetFontSize(TextRenderInfo info)
        {
            return Math.Abs(info.GetAscentLine().GetStartPoint().Get(Vector.I2) -
                            info.GetDescentLine().GetStartPoint().Get(Vector.I2));
        }

        private bool IsSingleToken(TextRenderInfo info)
        {
            if (info == null) return false;
            var t = info.GetText() ?? "";
            t = t.Trim();
            if (t.Length != 1) return false;
            return !char.IsWhiteSpace(t[0]);
        }
    }
}
