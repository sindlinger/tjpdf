using System;
using System.Collections.Generic;
using System.IO;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas.Parser;
using iText.Kernel.Pdf.Canvas.Parser.Listener;

namespace FilterPDF
{
    /// <summary>
    /// Extrator que captura dados binários das imagens e converte para base64
    /// </summary>
    public class ImageDataExtractor
    {
        // Thread-safe warning management for multi-worker scenarios
        private static readonly object RecursionWarningLock = new object();
        private static readonly HashSet<string> WarnedForms = new HashSet<string>();
        public static bool SuppressRecursionWarnings { get; set; } = false;
        public class ImageWithData
        {
            public string Name { get; set; } = "";
            public int Width { get; set; }
            public int Height { get; set; }
            public int BitsPerComponent { get; set; }
            public string ColorSpace { get; set; } = "";
            public string CompressionType { get; set; } = "";
            public long EstimatedSize { get; set; }
            public string Base64Data { get; set; } = "";
            public string MimeType { get; set; } = "";
            public bool IsFullPage { get; set; } = false;
        }

        public static List<ImageWithData> ExtractImagesWithData(PdfDocument doc, int pageNum, bool includeBase64 = true)
        {
            var images = new List<ImageWithData>();
            
            try
            {
                var page = doc.GetPage(pageNum);
                var resources = page.GetResources();
                var xObjects = resources?.GetResource(iText.Kernel.Pdf.PdfName.XObject) as PdfDictionary;
                var pageSize = page.GetPageSize();
                var pageWidth = pageSize.GetWidth();
                var pageHeight = pageSize.GetHeight();

                if (xObjects != null)
                {
                    foreach (var key in xObjects.KeySet())
                    {
                        var stream = xObjects.GetAsStream(key);
                        if (stream == null) continue;

                        var subType = stream.GetAsName(iText.Kernel.Pdf.PdfName.Subtype);

                        if (iText.Kernel.Pdf.PdfName.Image.Equals(subType))
                        {
                            var image = ExtractSingleImageWithData(key.ToString(), stream, includeBase64);
                            if (image != null)
                            {
                                if (Math.Abs(image.Width - pageWidth) < pageWidth * 0.1 &&
                                    Math.Abs(image.Height - pageHeight) < pageHeight * 0.1)
                                {
                                    image.IsFullPage = true;
                                }
                                images.Add(image);
                            }
                        }
                        else if (iText.Kernel.Pdf.PdfName.Form.Equals(subType))
                        {
                            var formImages = ExtractImagesFromForm(stream, key.ToString(), includeBase64, pageWidth, pageHeight);
                            images.AddRange(formImages);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not extract images from page {pageNum}: {ex.Message}");
            }
            
            return images;
        }
        
        /// <summary>
        /// Extract images from Form XObjects (recursive with protection against infinite loops)
        /// </summary>
        private static List<ImageWithData> ExtractImagesFromForm(PdfStream formStream, string formName, bool includeBase64, float pageWidth, float pageHeight, HashSet<string> visitedForms = null, int recursionDepth = 0)
        {
            var images = new List<ImageWithData>();
            
            // Protection against infinite recursion
            const int MAX_RECURSION_DEPTH = 20;
            if (recursionDepth > MAX_RECURSION_DEPTH)
            {
                // Suppress warning in multi-threaded scenarios or only show once per form
                if (!SuppressRecursionWarnings)
                {
                    lock (RecursionWarningLock)
                    {
                        if (!WarnedForms.Contains(formName))
                        {
                            Console.WriteLine($"Warning: Maximum recursion depth ({MAX_RECURSION_DEPTH}) reached for Form XObject {formName}");
                            WarnedForms.Add(formName);
                        }
                    }
                }
                return images;
            }
            
            // Initialize visited forms set on first call
            if (visitedForms == null)
            {
                visitedForms = new HashSet<string>();
            }
            
            // Check for circular reference using form name
            if (visitedForms.Contains(formName))
            {
                // Suppress warning in multi-threaded scenarios or only show once per form
                if (!SuppressRecursionWarnings)
                {
                    lock (RecursionWarningLock)
                    {
                        var circularKey = $"circular_{formName}";
                        if (!WarnedForms.Contains(circularKey))
                        {
                            Console.WriteLine($"Warning: Circular reference detected in Form XObject {formName}");
                            WarnedForms.Add(circularKey);
                        }
                    }
                }
                return images;
            }
            visitedForms.Add(formName);
            
            try
            {
                // Get the Form XObject's Resources
                var formResources = formStream.GetAsDictionary(iText.Kernel.Pdf.PdfName.Resources);
                if (formResources != null)
                {
                    var formXObjects = formResources.GetAsDictionary(iText.Kernel.Pdf.PdfName.XObject);
                    if (formXObjects != null)
                    {
                        foreach (var key in formXObjects.KeySet())
                        {
                            var stream = formXObjects.GetAsStream(key);
                            if (stream != null)
                            {
                                var subType = stream.GetAsName(iText.Kernel.Pdf.PdfName.Subtype);
                                
                                if (iText.Kernel.Pdf.PdfName.Image.Equals(subType))
                                {
                                    var image = ExtractSingleImageWithData($"{formName}/{key}", stream, includeBase64);
                                    if (image != null)
                                    {
                                        if (Math.Abs(image.Width - pageWidth) < pageWidth * 0.1 && 
                                            Math.Abs(image.Height - pageHeight) < pageHeight * 0.1)
                                        {
                                            image.IsFullPage = true;
                                        }
                                        images.Add(image);
                                    }
                                }
                                else if (iText.Kernel.Pdf.PdfName.Form.Equals(subType))
                                {
                                    string nestedFormName = $"{formName}/{key}";
                                    var nestedImages = ExtractImagesFromForm(stream, nestedFormName, includeBase64, pageWidth, pageHeight, visitedForms, recursionDepth + 1);
                                    images.AddRange(nestedImages);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not extract images from Form XObject {formName}: {ex.Message}");
            }
            finally
            {
                // Remove current form from visited set when backtracking
                visitedForms.Remove(formName);
            }
            
            return images;
        }
        
        private static ImageWithData? ExtractSingleImageWithData(string name, PdfStream stream, bool includeBase64)
        {
            try
            {
                var image = new ImageWithData
                {
                    Name = name
                };
                
                // Extract dimensions
                var width = stream.GetAsNumber(iText.Kernel.Pdf.PdfName.Width);
                if (width != null) image.Width = width.IntValue();
                
                var height = stream.GetAsNumber(iText.Kernel.Pdf.PdfName.Height);
                if (height != null) image.Height = height.IntValue();
                
                // Bits per component
                var bpc = stream.GetAsNumber(iText.Kernel.Pdf.PdfName.BitsPerComponent);
                if (bpc != null) image.BitsPerComponent = bpc.IntValue();
                
                // Color space
                var colorSpace = stream.Get(iText.Kernel.Pdf.PdfName.ColorSpace);
                if (colorSpace != null) 
                {
                    image.ColorSpace = colorSpace.ToString();
                }
                
                // Compression type
                var filter = stream.Get(iText.Kernel.Pdf.PdfName.Filter);
                if (filter != null) 
                {
                    if (filter is PdfArray filterArray && filterArray.Size() > 0)
                    {
                        var filterName = filterArray.GetAsName(0).ToString();
                        image.CompressionType = filterName;
                        image.MimeType = GetMimeTypeFromFilter(filterName);
                    }
                    else if (filter is PdfName filterName)
                    {
                        image.CompressionType = filterName.ToString();
                        image.MimeType = GetMimeTypeFromFilter(filterName.ToString());
                    }
                    else
                    {
                        image.CompressionType = filter.ToString();
                        image.MimeType = "image/unknown";
                    }
                }
                
                // Extract binary data if requested
                if (includeBase64)
                {
                    try
                    {
                        var streamBytes = stream.GetBytes(false); // raw
                        
                        if (image.CompressionType == "/DCTDecode" || image.CompressionType == "/JPXDecode")
                        {
                            image.Base64Data = Convert.ToBase64String(streamBytes);
                        }
                        else
                        {
                            var decodedBytes = stream.GetBytes(true);
                            image.Base64Data = Convert.ToBase64String(decodedBytes);
                            
                            if (string.IsNullOrEmpty(image.MimeType))
                            {
                                image.MimeType = "application/octet-stream";
                            }
                            image.EstimatedSize = decodedBytes.Length;
                        }
                        
                        if (image.EstimatedSize == 0) image.EstimatedSize = streamBytes.Length;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Warning: Could not extract image data for {name}: {ex.Message}");
                        // Still return image metadata even if data extraction fails
                    }
                }
                else
                {
                    // Calculate estimated size
                    image.EstimatedSize = (image.Width * image.Height * image.BitsPerComponent) / 8;
                }
                
                return image;
            }
            catch 
            {
                return null;
            }
        }
        
        private static string GetMimeTypeFromFilter(string filter)
        {
            switch (filter)
            {
                case "/DCTDecode":
                    return "image/jpeg";
                case "/JPXDecode":
                    return "image/jp2";
                case "/FlateDecode":
                case "/LZWDecode":
                    return "image/png"; // May need conversion
                case "/CCITTFaxDecode":
                    return "image/tiff";
                default:
                    return "image/unknown";
            }
        }
        
        /// <summary>
        /// Check if a page is a scanned image (full-page image with no extractable text)
        /// </summary>
        public static bool IsScannedPage(PdfDocument doc, int pageNum)
        {
            try
            {
                // Check if page has extractable text
                var text = PdfTextExtractor.GetTextFromPage(doc.GetPage(pageNum), new SimpleTextExtractionStrategy());
                var hasText = !string.IsNullOrWhiteSpace(text) && text.Length > 50; // More than 50 chars
                
                if (hasText)
                {
                    return false; // Has text, not a scanned page
                }
                
                // Check if page has a full-page image
                var images = ExtractImagesWithData(doc, pageNum, false);
                
                foreach (var img in images)
                {
                    if (img.IsFullPage)
                    {
                        return true; // Has full-page image and no text = scanned page
                    }
                }
                
                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Converts Base64 encoded image data to PNG format using iTextSharp
        /// </summary>
        /// <param name="base64Data">Base64 encoded image data</param>
        /// <param name="outputPath">Path where the PNG file will be saved</param>
        /// <param name="imageInfo">Optional image info with width, height, etc</param>
        /// <returns>True if conversion was successful, false otherwise</returns>
        public static bool CreatePngFromBase64(string base64Data, string outputPath, ImageWithData imageInfo = null)
        {
            try
            {
                // Remove data URI prefix if present (e.g., "data:image/jpeg;base64,")
                var base64 = base64Data;
                if (base64.Contains(","))
                {
                    base64 = base64.Substring(base64.IndexOf(",") + 1);
                }

                // Convert Base64 to byte array
                byte[] imageBytes = Convert.FromBase64String(base64);
                Console.WriteLine($"      🔍 Decoded {imageBytes.Length} bytes from Base64");

                // Detect format
                string actualExtension = GetImageExtension(imageBytes);
                Console.WriteLine($"      🔍 Detected format: {actualExtension}");

                // Check if it's already a valid image format
                if (IsValidImageFormat(imageBytes))
                {
                    // It's already a valid image (JPEG, PNG, etc)
                    if (actualExtension == ".png")
                    {
                        // Already PNG, just save it
                        File.WriteAllBytes(outputPath, imageBytes);
                        Console.WriteLine($"      ✅ PNG saved directly");
                        return true;
                    }
                    else if (actualExtension == ".jpg")
                    {
                        // JPEG - try to convert using ImageMagick
                        return ConvertJpegToPngWithImageMagick(imageBytes, outputPath);
                    }
                    else
                    {
                        // Other format, save as-is
                        string finalPath = Path.ChangeExtension(outputPath, actualExtension);
                        File.WriteAllBytes(finalPath, imageBytes);
                        Console.WriteLine($"      ℹ️ Saved as {actualExtension}: {finalPath}");
                        return true;
                    }
                }
                else if (imageInfo != null && imageInfo.Width > 0 && imageInfo.Height > 0)
                {
                    // RAW data - use ImageMagick with exact dimensions
                    Console.WriteLine($"      🎨 Processing RAW data: {imageInfo.Width}x{imageInfo.Height}, {imageBytes.Length} bytes");
                    return ConvertRawToPngWithImageMagick(imageBytes, outputPath, imageInfo.Width, imageInfo.Height);
                }

                // Last resort: save raw bytes
                File.WriteAllBytes(outputPath, imageBytes);
                Console.WriteLine($"      ⚠️ Saved raw bytes to {outputPath} (may not be valid image)");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error converting Base64 to PNG: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Converts JPEG to PNG using ImageMagick
        /// </summary>
        private static bool ConvertJpegToPngWithImageMagick(byte[] jpegBytes, string outputPath, int? expectedWidth = null, int? expectedHeight = null)
        {
            try
            {
                string tempJpeg = Path.GetTempFileName() + ".jpg";
                File.WriteAllBytes(tempJpeg, jpegBytes);
                
                // First, get actual dimensions of the JPEG
                bool needsResize = false;
                
                var identifyProcess = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "identify",
                        Arguments = $"-format \"%w %h\" \"{tempJpeg}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                
                identifyProcess.Start();
                string dimensions = identifyProcess.StandardOutput.ReadToEnd().Trim();
                identifyProcess.WaitForExit(1000);
                
                if (!string.IsNullOrEmpty(dimensions))
                {
                    var parts = dimensions.Split(' ');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int actualWidth) && int.TryParse(parts[1], out int actualHeight))
                    {
                        Console.WriteLine($"      📏 Actual JPEG dimensions: {actualWidth}x{actualHeight}");
                        
                        // Check if we need to resize
                        needsResize = expectedWidth.HasValue && expectedHeight.HasValue &&
                                    (actualWidth != expectedWidth.Value || actualHeight != expectedHeight.Value);
                        
                        if (needsResize)
                        {
                            Console.WriteLine($"      ⚠️ Dimensions mismatch! Expected: {expectedWidth}x{expectedHeight}, Got: {actualWidth}x{actualHeight}");
                            Console.WriteLine($"      🔧 Resizing to match expected dimensions...");
                        }
                    }
                }
                
                // Build convert command with resize if needed and strip metadata
                string convertArgs;
                if (needsResize && expectedWidth.HasValue && expectedHeight.HasValue)
                {
                    // Force resize to exact dimensions and strip metadata
                    convertArgs = $"\"{tempJpeg}\" -resize {expectedWidth}x{expectedHeight}! -strip \"{outputPath}\"";
                    Console.WriteLine($"      🔧 Applying resize: {expectedWidth}x{expectedHeight} and removing metadata");
                }
                else
                {
                    // Simple conversion without resize but strip metadata
                    convertArgs = $"\"{tempJpeg}\" -strip \"{outputPath}\"";
                    Console.WriteLine($"      🧹 Removing metadata from image");
                }
                
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "convert",
                        Arguments = convertArgs,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                
                process.Start();
                process.WaitForExit(5000); // 5 second timeout
                
                File.Delete(tempJpeg);
                
                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    Console.WriteLine($"      ✅ JPEG converted to PNG using ImageMagick");
                    return true;
                }
                else
                {
                    string error = process.StandardError.ReadToEnd();
                    Console.WriteLine($"      ⚠️ ImageMagick conversion failed: {error}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      ⚠️ ImageMagick not available: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Converts RAW pixel data to PNG using ImageMagick with exact dimensions
        /// </summary>
        private static bool ConvertRawToPngWithImageMagick(byte[] rawBytes, string outputPath, int width, int height)
        {
            try
            {
                string tempRaw = Path.GetTempFileName() + ".raw";
                File.WriteAllBytes(tempRaw, rawBytes);
                
                // Calculate pixel format based on data size
                int pixelCount = width * height;
                string format = "rgb"; // default
                
                if (rawBytes.Length == pixelCount * 4)
                    format = "rgba";
                else if (rawBytes.Length == pixelCount * 3)
                    format = "rgb";
                else if (rawBytes.Length == pixelCount)
                    format = "gray";
                else
                {
                    Console.WriteLine($"      ⚠️ Unexpected data size: {rawBytes.Length} bytes for {width}x{height} pixels");
                    // Try RGB anyway
                }
                
                // Use ImageMagick convert command with exact dimensions
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "convert",
                        Arguments = $"-size {width}x{height} -depth 8 {format}:\"{tempRaw}\" -strip \"{outputPath}\"",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                
                Console.WriteLine($"      🔧 ImageMagick: convert -size {width}x{height} -depth 8 {format}:raw → png");
                
                process.Start();
                process.WaitForExit(5000); // 5 second timeout
                
                File.Delete(tempRaw);
                
                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    Console.WriteLine($"      ✅ RAW data converted to PNG using ImageMagick");
                    return true;
                }
                else
                {
                    string error = process.StandardError.ReadToEnd();
                    Console.WriteLine($"      ⚠️ ImageMagick RAW conversion failed: {error}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      ⚠️ ImageMagick not available: {ex.Message}");
                return false;
            }
        }
        
        /// <summary>
        /// Gets the file extension based on image format
        /// </summary>
        private static string GetImageExtension(byte[] bytes)
        {
            if (bytes.Length < 4) return ".bin";
            
            // PNG signature: 89 50 4E 47
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return ".png";
            
            // JPEG signature: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return ".jpg";
            
            // GIF signature: 47 49 46
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
                return ".gif";
            
            // BMP signature: 42 4D
            if (bytes[0] == 0x42 && bytes[1] == 0x4D)
                return ".bmp";
            
            // TIFF signatures
            if ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00) ||
                (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A))
                return ".tiff";
            
            return ".bin";
        }
        
        /// <summary>
        /// Checks if byte array represents a valid image format
        /// </summary>
        private static bool IsValidImageFormat(byte[] bytes)
        {
            if (bytes.Length < 4) return false;
            
            // PNG signature: 89 50 4E 47
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return true;
            
            // JPEG signature: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return true;
            
            // GIF signature: 47 49 46
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
                return true;
            
            // BMP signature: 42 4D
            if (bytes[0] == 0x42 && bytes[1] == 0x4D)
                return true;
            
            // TIFF signatures
            if ((bytes[0] == 0x49 && bytes[1] == 0x49 && bytes[2] == 0x2A && bytes[3] == 0x00) ||
                (bytes[0] == 0x4D && bytes[1] == 0x4D && bytes[2] == 0x00 && bytes[3] == 0x2A))
                return true;
            
            return false;
        }

        /// <summary>
        /// Converts Base64 encoded image data to PNG and returns as byte array
        /// </summary>
        /// <param name="base64Data">Base64 encoded image data</param>
        /// <returns>PNG image as byte array, or null if conversion failed</returns>
        public static byte[]? CreatePngFromBase64(string base64Data)
        {
            try
            {
                // Remove data URI prefix if present
                var base64 = base64Data;
                if (base64.Contains(","))
                {
                    base64 = base64.Substring(base64.IndexOf(",") + 1);
                }

                // Convert Base64 to byte array
                byte[] imageBytes = Convert.FromBase64String(base64);

                // Check if it's already a PNG
                if (IsValidImageFormat(imageBytes) && GetImageExtension(imageBytes) == ".png")
                {
                    return imageBytes;
                }

                // For other formats, we would need System.Drawing or ImageMagick
                // For now, return the raw bytes
                return imageBytes;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error converting Base64 to PNG bytes: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Batch converts multiple Base64 images to PNG files
        /// </summary>
        /// <param name="base64Images">Dictionary of filename (without extension) to Base64 data</param>
        /// <param name="outputDirectory">Directory where PNG files will be saved</param>
        /// <returns>Number of successfully converted images</returns>
        public static int BatchCreatePngFromBase64(Dictionary<string, string> base64Images, string outputDirectory)
        {
            int successCount = 0;
            
            // Create output directory if it doesn't exist
            if (!Directory.Exists(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            foreach (var kvp in base64Images)
            {
                var outputPath = Path.Combine(outputDirectory, $"{kvp.Key}.png");
                if (CreatePngFromBase64(kvp.Value, outputPath))
                {
                    successCount++;
                    Console.WriteLine($"✓ Created: {outputPath}");
                }
                else
                {
                    Console.WriteLine($"✗ Failed: {kvp.Key}");
                }
            }

            return successCount;
        }
    }
}
