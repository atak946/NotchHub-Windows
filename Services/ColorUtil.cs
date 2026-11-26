using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Notch.Services;

public static class ColorUtil
{
    /// <summary>Kapak resminden, siyah zemin uzerinde iyi gorunen canli bir vurgu rengi cikarir.</summary>
    public static Color? Vibrant(BitmapSource src)
    {
        try
        {
            var small = new TransformedBitmap(src, new ScaleTransform(24.0 / src.PixelWidth, 24.0 / src.PixelHeight));
            var conv = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            double sr = 0, sg = 0, sb = 0, sw = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                RgbToHsv(r, g, b, out _, out var s, out var v);
                double weight = s * s * v + 0.02; // doygun ve parlak pikseller agir basar
                sr += r * weight; sg += g * weight; sb += b * weight; sw += weight;
            }
            RgbToHsv(sr / sw, sg / sw, sb / sw, out var hh, out var ss, out var vv);
            ss = Math.Clamp(ss * 1.25, 0.45, 0.9);
            vv = Math.Max(vv, 0.9);
            HsvToRgb(hh, ss, vv, out var rr, out var gg, out var bb);
            return Color.FromRgb((byte)(rr * 255), (byte)(gg * 255), (byte)(bb * 255));
        }
        catch { return null; }
    }

    static void RgbToHsv(double r, double g, double b, out double h, out double s, out double v)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max == 0 ? 0 : d / max;
        if (d == 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    static void HsvToRgb(double h, double s, double v, out double r, out double g, out double b)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        r += m; g += m; b += m;
    }
}
