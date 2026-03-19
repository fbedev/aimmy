using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Aimmy.Mac
{
    public static class MathUtil
    {
        public static int CalculateNumDetections(int imageSize)
        {
            int stride8 = imageSize / 8;
            int stride16 = imageSize / 16;
            int stride32 = imageSize / 32;

            return (stride8 * stride8) + (stride16 * stride16) + (stride32 * stride32);
        }

        private static readonly float[] _byteToFloatLut = CreateByteToFloatLut();
        private static float[] CreateByteToFloatLut()
        {
            var lut = new float[256];
            for (int i = 0; i < 256; i++)
                lut[i] = i / 255f;
            return lut;
        }

        public static void ImageToFloatArray(Image<Rgb24> image, float[] result)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (result == null) throw new ArgumentNullException(nameof(result));

            int width = image.Width;
            int height = image.Height;
            int totalPixels = width * height;

            if (result.Length != 3 * totalPixels)
                throw new ArgumentException($"result must be length {3 * totalPixels}", nameof(result));

            int rOffset = 0;
            int gOffset = totalPixels;
            int bOffset = totalPixels * 2;

            image.ProcessPixelRows(accessor =>
            {
                // Removed Parallel.For to avoid closure over ref struct 'accessor'
                for (int y = 0; y < height; y++)
                {
                    var pixelRow = accessor.GetRowSpan(y);
                    int rowStart = y * width;
                    
                    for (int x = 0; x < width; x++)
                    {
                        ref Rgb24 pixel = ref pixelRow[x];
                        int idx = rowStart + x;

                        // Rgb24 layout is R, G, B
                        // Mapping to Planar RGB (default for most ONNX)
                        result[rOffset + idx] = _byteToFloatLut[pixel.R];
                        result[gOffset + idx] = _byteToFloatLut[pixel.G];
                        result[bOffset + idx] = _byteToFloatLut[pixel.B];
                    }
                }
            });
        }
    }
}
