using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AudioSwitch.Services;

namespace AudioSwitch.Ui;

/// <summary>背景图片的加载、模糊与配色取样。</summary>
internal static class Backgrounds
{
    private static readonly Dictionary<string, BitmapSource> BlurCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    /// <summary>加载图片，失败时返回 null 并记录日志。</summary>
    public static BitmapSource? Load(string? path, int decodeWidth = 1600)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            string fullPath = System.IO.Path.GetFullPath(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(fullPath, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = decodeWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex)
        {
            Log.Warn($"加载背景图片失败（{path}）：{ex.Message}");
            return null;
        }
    }

    public static ImageBrush? CreateBrush(BitmapSource? image, double opacity)
    {
        if (image is null)
        {
            return null;
        }

        var brush = new ImageBrush(image)
        {
            Stretch = Stretch.UniformToFill,
            AlignmentX = AlignmentX.Center,
            AlignmentY = AlignmentY.Center,
            Opacity = Math.Clamp(opacity, 0.0, 1.0),
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>缩到 96px 再两遍盒式模糊；结果按路径缓存，避免每次改设置都重算。</summary>
    public static BitmapSource? CreateBlurred(string? path, BitmapSource? source)
    {
        if (string.IsNullOrWhiteSpace(path) || source is null)
        {
            return source;
        }

        lock (CacheLock)
        {
            if (BlurCache.TryGetValue(path, out BitmapSource? cached))
            {
                return cached;
            }
        }

        BitmapSource blurred;
        try
        {
            blurred = Blur(source);
        }
        catch (Exception ex)
        {
            Log.Warn($"模糊背景图片失败：{ex.Message}");
            return source;
        }

        lock (CacheLock)
        {
            BlurCache[path] = blurred;
        }

        return blurred;
    }

    private static BitmapSource Blur(BitmapSource source)
    {
        const int target = 96;
        double scale = Math.Min(1.0, target / (double)Math.Max(source.PixelWidth, source.PixelHeight));
        int width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        int height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));

        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        converted.DestinationFormat = PixelFormats.Bgra32;
        converted.EndInit();

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        BoxBlur(pixels, width, height);
        BoxBlur(pixels, width, height);

        BitmapSource result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static void BoxBlur(byte[] pixels, int width, int height)
    {
        byte[] copy = (byte[])pixels.Clone();
        int stride = width * 4;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (int dy = -1; dy <= 1; dy++)
                {
                    int ny = y + dy;
                    if (ny < 0 || ny >= height)
                    {
                        continue;
                    }

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx;
                        if (nx < 0 || nx >= width)
                        {
                            continue;
                        }

                        int index = ny * stride + nx * 4;
                        b += copy[index];
                        g += copy[index + 1];
                        r += copy[index + 2];
                        a += copy[index + 3];
                        n++;
                    }
                }

                int offset = y * stride + x * 4;
                pixels[offset] = (byte)(b / n);
                pixels[offset + 1] = (byte)(g / n);
                pixels[offset + 2] = (byte)(r / n);
                pixels[offset + 3] = (byte)(a / n);
            }
        }
    }

    /// <summary>
    /// 从图片里挑一个“主色”（偏饱和、偏亮），同时给出整图平均色。
    /// 找不到合适的主色时返回 false。
    /// </summary>
    public static bool TrySampleAccent(BitmapSource image, out Color accent, out Color average, out double luminance)
    {
        const int target = 64;
        accent = Color.FromRgb(0x5B, 0x9D, 0xFF);
        average = accent;
        luminance = 0.5;

        double scale = Math.Min(1.0, target / (double)Math.Max(image.PixelWidth, image.PixelHeight));
        int width = Math.Max(1, (int)Math.Round(image.PixelWidth * scale));
        int height = Math.Max(1, (int)Math.Round(image.PixelHeight * scale));

        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        converted.DestinationFormat = PixelFormats.Bgra32;
        converted.EndInit();

        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        const int bins = 32 * 32 * 32;
        var weight = new double[bins];
        var count = new int[bins];
        var sumR = new double[bins];
        var sumG = new double[bins];
        var sumB = new double[bins];
        double totalR = 0, totalG = 0, totalB = 0;

        for (int i = 0; i < pixels.Length; i += 4)
        {
            byte b = pixels[i];
            byte g = pixels[i + 1];
            byte r = pixels[i + 2];
            byte a = pixels[i + 3];
            if (a < 16)
            {
                continue;
            }

            totalR += r;
            totalG += g;
            totalB += b;

            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            if (max == 0)
            {
                continue;
            }

            double saturation = (max - min) / (double)max;
            double value = max / 255.0;
            if (saturation < 0.18 || value < 0.12)
            {
                continue;
            }

            int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
            double w = saturation * saturation * (0.2 + value);
            weight[key] += w;
            count[key]++;
            sumR[key] += r;
            sumG[key] += g;
            sumB[key] += b;
        }

        int pixelsCount = pixels.Length / 4;
        if (pixelsCount == 0)
        {
            return false;
        }

        average = Color.FromRgb(
            (byte)Math.Clamp(totalR / pixelsCount, 0, 255),
            (byte)Math.Clamp(totalG / pixelsCount, 0, 255),
            (byte)Math.Clamp(totalB / pixelsCount, 0, 255));
        luminance = Luminance(average);

        int best = -1;
        double bestWeight = 0;
        for (int i = 0; i < bins; i++)
        {
            if (weight[i] > bestWeight)
            {
                bestWeight = weight[i];
                best = i;
            }
        }

        if (best < 0 || count[best] == 0)
        {
            return false;
        }

        accent = Color.FromRgb(
            (byte)Math.Clamp(sumR[best] / count[best], 0, 255),
            (byte)Math.Clamp(sumG[best] / count[best], 0, 255),
            (byte)Math.Clamp(sumB[best] / count[best], 0, 255));
        return true;
    }

    public static double Luminance(Color color) =>
        (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;
}
