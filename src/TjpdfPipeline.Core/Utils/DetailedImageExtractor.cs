using System;
using System.Collections.Generic;
using iText.Kernel.Pdf;

namespace FilterPDF
{
    /// <summary>
    /// Extrator que captura TODOS os detalhes das imagens: Width, Height, ColorSpace, Compression, etc. (iText7)
    /// </summary>
    public class DetailedImageExtractor
    {
        public static List<ImageInfo> ExtractCompleteImageDetails(PdfDocument doc, int pageNum)
        {
            var images = new List<ImageInfo>();
            
            try
            {
                var page = doc.GetPage(pageNum);
                var resources = page.GetResources();
                var xObjects = resources?.GetResource(PdfName.XObject) as PdfDictionary;

                if (xObjects != null)
                {
                    foreach (var key in xObjects.KeySet())
                    {
                        var stream = xObjects.GetAsStream(key);
                        if (stream == null) continue;

                        var subType = stream.GetAsName(PdfName.Subtype);
                        if (PdfName.Image.Equals(subType))
                        {
                            var image = ExtractSingleImageDetails(key.ToString(), stream);
                            if (image != null)
                            {
                                images.Add(image);
                            }
                        }
                        else if (PdfName.Form.Equals(subType))
                        {
                            // Buscar imagens dentro de Form XObject
                            images.AddRange(ExtractImagesFromForm(stream, key.ToString()));
                        }
                    }
                }
            }
            catch { }
            
            return images;
        }
        
        private static List<ImageInfo> ExtractImagesFromForm(PdfStream formStream, string formName)
        {
            var images = new List<ImageInfo>();

            try
            {
                var formResources = formStream.GetAsDictionary(PdfName.Resources);
                var formXObjects = formResources?.GetAsDictionary(PdfName.XObject);
                if (formXObjects != null)
                {
                    foreach (var key in formXObjects.KeySet())
                    {
                        var stream = formXObjects.GetAsStream(key);
                        if (stream == null) continue;

                        var subType = stream.GetAsName(PdfName.Subtype);
                        if (PdfName.Image.Equals(subType))
                        {
                            var image = ExtractSingleImageDetails($"{formName}/{key}", stream);
                            if (image != null) images.Add(image);
                        }
                        else if (PdfName.Form.Equals(subType))
                        {
                            images.AddRange(ExtractImagesFromForm(stream, $"{formName}/{key}"));
                        }
                    }
                }
            }
            catch { }

            return images;
        }
        
        private static ImageInfo? ExtractSingleImageDetails(string name, PdfStream stream)
        {
            try
            {
                var image = new ImageInfo
                {
                    Name = name
                };
                
                // Dimensões
                var width = stream.GetAsNumber(PdfName.Width);
                if (width != null) image.Width = width.IntValue();
                
                var height = stream.GetAsNumber(PdfName.Height);
                if (height != null) image.Height = height.IntValue();
                
                // Bits por componente
                var bpc = stream.GetAsNumber(PdfName.BitsPerComponent);
                if (bpc != null) image.BitsPerComponent = bpc.IntValue();
                
                // Espaço de cor
                var colorSpace = stream.Get(PdfName.ColorSpace);
                if (colorSpace != null) 
                {
                    image.ColorSpace = colorSpace.ToString();
                }
                
                // Tipo de compressão/filtro
                var filter = stream.Get(PdfName.Filter);
                if (filter != null) 
                {
                    if (filter is PdfArray filterArray && filterArray.Size() > 0)
                    {
                        image.CompressionType = filterArray.GetAsName(0).ToString();
                    }
                    else if (filter is PdfName filterName)
                    {
                        image.CompressionType = filterName.ToString();
                    }
                    else
                    {
                        image.CompressionType = filter.ToString();
                    }
                }
                
                // Extração dos dados base64 para cache
                try
                {
                    byte[] imageBytes;
                    var imageFilter = stream.Get(PdfName.Filter);

                    // Para JPEG/JPX, usar dados brutos; demais, usar decodificados
                    if (IsJpegFilter(imageFilter))
                    {
                        imageBytes = stream.GetBytes(false);
                    }
                    else
                    {
                        imageBytes = stream.GetBytes(true);
                    }

                    if (imageBytes != null && imageBytes.Length > 0)
                    {
                        image.Base64Data = Convert.ToBase64String(imageBytes);
                    }
                }
                catch
                {
                    // continuar sem base64
                }
                
                return image;
            }
            catch 
            {
                return null;
            }
        }
        
        /// <summary>
        /// Verifica se o filtro é de imagem JPEG/JPX
        /// </summary>
        private static bool IsJpegFilter(iText.Kernel.Pdf.PdfObject filter)
        {
            if (filter == null) return false;
            
            var filterStr = filter.ToString();
            return filterStr.Contains("DCTDecode") || filterStr.Contains("JPXDecode");
        }
    }
}
