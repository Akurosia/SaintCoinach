using System;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using DrawingImage = System.Drawing.Image;

namespace SaintCoinach.Cmd {
    internal static class ImageExportHelper {
        public const string WebpExtension = ".webp";

        private const int WebpMaxDimension = 16383;
        private const int WebpQuality = 78;

        public static void SaveAsDropperWebp(DrawingImage image, string path) {
            using var encodedInput = new MemoryStream();
            image.Save(encodedInput, ImageFormat.Png);
            encodedInput.Position = 0;

            if (TrySaveWithImageMagick(encodedInput, path))
                return;

            encodedInput.Position = 0;
            using var rgbaImage = SixLabors.ImageSharp.Image.Load<Rgba32>(encodedInput);
            ResizeForWebpIfNeeded(rgbaImage);

            var encoder = new WebpEncoder {
                FileFormat = WebpFileFormatType.Lossy,
                Method = WebpEncodingMethod.Level4,
                Quality = WebpQuality
            };

            if (HasTransparency(rgbaImage)) {
                rgbaImage.SaveAsWebp(path, encoder);
            }
            else {
                using var rgbImage = rgbaImage.CloneAs<Rgb24>();
                rgbImage.SaveAsWebp(path, encoder);
            }
        }

        private static bool TrySaveWithImageMagick(Stream pngInput, string path) {
            var tempPng = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.png");

            try {
                using (var output = File.Create(tempPng)) {
                    pngInput.CopyTo(output);
                }

                var psi = new ProcessStartInfo {
                    FileName = "magick",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add(tempPng);
                psi.ArgumentList.Add("-quality");
                psi.ArgumentList.Add(WebpQuality.ToString());
                psi.ArgumentList.Add("-define");
                psi.ArgumentList.Add("webp:method=4");
                psi.ArgumentList.Add("-define");
                psi.ArgumentList.Add("webp:alpha-quality=80");
                psi.ArgumentList.Add(path);

                using var process = Process.Start(psi);
                if (process == null)
                    return false;

                if (!process.WaitForExit(TimeSpan.FromMinutes(1))) {
                    process.Kill();
                    return false;
                }

                return process.ExitCode == 0 && File.Exists(path);
            }
            catch {
                return false;
            }
            finally {
                try {
                    File.Delete(tempPng);
                }
                catch {
                }
            }
        }

        private static bool HasTransparency(SixLabors.ImageSharp.Image<Rgba32> image) {
            var hasTransparency = false;
            image.ProcessPixelRows(accessor => {
                for (var y = 0; y < accessor.Height && !hasTransparency; y++) {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++) {
                        if (row[x].A < byte.MaxValue) {
                            hasTransparency = true;
                            return;
                        }
                    }
                }
            });

            return hasTransparency;
        }

        private static void ResizeForWebpIfNeeded(SixLabors.ImageSharp.Image<Rgba32> image) {
            if (image.Width <= WebpMaxDimension && image.Height <= WebpMaxDimension)
                return;

            var scale = Math.Min(WebpMaxDimension / (double)image.Width, WebpMaxDimension / (double)image.Height);
            var width = Math.Max(1, (int)Math.Floor(image.Width * scale));
            var height = Math.Max(1, (int)Math.Floor(image.Height * scale));

            image.Mutate(c => c.Resize(width, height, KnownResamplers.Lanczos3));
        }
    }
}
