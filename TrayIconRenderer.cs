using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Drawing = System.Drawing;

namespace MCHOSE;

/// <summary>Bar colour/state. Full = on power and fully charged (green bar + green bolt).</summary>
public enum BarKind { None, Good, Medium, Low, Charging, Full }

/// <param name="Text">The number (percent), or "?" when there is no reading.</param>
/// <param name="Level">Bar fill 0-100.</param>
/// <param name="Bar">Bar colour.</param>
/// <param name="Stale">Stale reading (mouse asleep / out of range) - icon is dimmed.</param>
/// <param name="Alert">Draw the number in a warning colour (low level).</param>
public sealed record TrayIconModel(string Text, int Level, BarKind Bar, bool Stale, bool Alert);

/// <summary>
/// Draws the tray icon: a number fitted to the full icon width plus a level bar at the bottom.
/// Rendered at the native icon size (depends on DPI scaling) in colours matching
/// a light or dark taskbar.
/// </summary>
public static class TrayIconRenderer
{
    private static readonly Typeface Font = new(
        new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

    /// <summary>Native tray icon size in pixels (depends on DPI scaling).</summary>
    public static int IconSize => Math.Max(16, GetSystemMetrics(SM_CXSMICON));

    public static Drawing.Icon Render(TrayIconModel m)
    {
        int size = IconSize;
        return ToIcon(RenderBitmap(m, size, IsTaskbarLight()), size);
    }

    /// <summary>
    /// Frames of the charging animation: the level bar fills smoothly from 0 to 100 % (one frame per
    /// pixel of bar width), stays full for a moment and then starts again from 0. Rendered once and
    /// cached by the caller, so the animation itself only swaps ready-made icons.
    /// </summary>
    public static Drawing.Icon[] RenderChargingFrames(TrayIconModel m)
    {
        int size = IconSize;
        bool light = IsTaskbarLight();
        return ChargingLevels(size)
            .Select(level => ToIcon(RenderBitmap(m with { Level = level }, size, light), size))
            .ToArray();
    }

    /// <summary>Bar levels (percent) of one animation cycle: 0 → 100 one pixel at a time, then a short hold.</summary>
    private static IEnumerable<int> ChargingLevels(int size)
    {
        int hold = Math.Max(2, size / 4);
        return Enumerable.Range(0, size + 1).Select(px => px * 100 / size)
            .Concat(Enumerable.Repeat(100, hold));
    }

    /// <summary>Saves sample icons as PNG (preview / documentation): --export-icons &lt;dir&gt;</summary>
    public static void ExportSamples(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var samples = new (string Name, TrayIconModel Model)[]
        {
            ("good", new("87", 87, BarKind.Good, false, false)),
            ("medium", new("42", 42, BarKind.Medium, false, false)),
            ("low", new("15", 15, BarKind.Low, false, true)),
            ("charging", new("63", 63, BarKind.Charging, false, false)),
            ("charging-100", new("99", 99, BarKind.Charging, false, false)),
            ("full", new("100", 100, BarKind.Full, false, false)),
            ("stale", new("58", 58, BarKind.Good, true, false)),
            ("unknown", new("?", 0, BarKind.None, true, false)),
        };
        foreach (var light in new[] { false, true })
        foreach (var size in new[] { 16, 24, 32 })
        foreach (var (name, model) in samples)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(RenderBitmap(model, size, light)));
            using var f = System.IO.File.Create(System.IO.Path.Combine(dir, $"{(light ? "light" : "dark")}-{size}-{name}.png"));
            enc.Save(f);
        }

        // Charging animation frames (24 px, dark taskbar) - same sequence as RenderChargingFrames.
        const int frameSize = 24;
        var charging = new TrayIconModel("63", 63, BarKind.Charging, false, false);
        var levels = ChargingLevels(frameSize).ToArray();
        for (int i = 0; i < levels.Length; i++)
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(RenderBitmap(charging with { Level = levels[i] }, frameSize, false)));
            using var f = System.IO.File.Create(System.IO.Path.Combine(dir, $"charge-{i:00}.png"));
            enc.Save(f);
        }
    }

    private static BitmapSource RenderBitmap(TrayIconModel m, int size, bool light)
    {
        var fg = light ? Color.FromRgb(0x1A, 0x1A, 0x1A) : Colors.White;
        var alert = light ? Color.FromRgb(0xC4, 0x2B, 0x1C) : Color.FromRgb(0xFF, 0x6B, 0x6B);
        var barColor = m.Bar switch
        {
            BarKind.Good or BarKind.Full => light ? Color.FromRgb(0x0F, 0x7B, 0x0F) : Color.FromRgb(0x6C, 0xCB, 0x5F),
            BarKind.Medium => light ? Color.FromRgb(0x9D, 0x5D, 0x00) : Color.FromRgb(0xFC, 0xE1, 0x00),
            BarKind.Low => alert,
            BarKind.Charging => light ? Color.FromRgb(0x00, 0x5F, 0xB8) : Color.FromRgb(0x4C, 0xC2, 0xFF),
            _ => Colors.Transparent,
        };

        double barH = Math.Max(2, Math.Round(size * 0.16));
        double gap = Math.Max(1, Math.Round(size * 0.08));
        double textH = size - barH - gap;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (m.Stale)
                dc.PushOpacity(0.45);

            // While charging, a lightning bolt sits at the right edge and overlaps the number a little;
            // a thin transparent outline is cut out of the number around it so both stay readable.
            bool charging = m.Bar is BarKind.Charging or BarKind.Full;
            Geometry? bolt = charging ? Bolt(size, textH * 0.85, textH * 0.075) : null;
            double textW = bolt is null ? size : size - bolt.Bounds.Width * 0.8; // bolt overlaps ~20 %

            // Number: fit to the tight glyph bounds to use every available pixel.
            var ft = new FormattedText(m.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                Font, 100, Brushes.Black, 1.0);
            Geometry geo = ft.BuildGeometry(new Point(0, 0));
            var b = geo.Bounds;
            if (!b.IsEmpty && b.Width > 0 && b.Height > 0)
            {
                double scale = Math.Min(textW / b.Width, textH / b.Height);
                double w = b.Width * scale, h = b.Height * scale;
                var t = new TransformGroup();
                t.Children.Add(new TranslateTransform(-b.X, -b.Y));
                t.Children.Add(new ScaleTransform(scale, scale));
                t.Children.Add(new TranslateTransform(Math.Round((textW - w) / 2), Math.Round((textH - h) / 2)));
                geo.Transform = t;
                if (bolt is not null)
                {
                    var outline = new Pen(Brushes.Black, Math.Max(1.2, size / 14.0)) { LineJoin = PenLineJoin.Round };
                    var cutout = Geometry.Combine(bolt, bolt.GetWidenedPathGeometry(outline), GeometryCombineMode.Union, null);
                    geo = Geometry.Combine(geo, cutout, GeometryCombineMode.Exclude, null);
                }
                dc.DrawGeometry(new SolidColorBrush(m.Alert ? alert : fg), null, geo);
            }

            if (bolt is not null)
                dc.DrawGeometry(new SolidColorBrush(barColor), null, bolt);

            // Level bar: track + fill.
            var track = new SolidColorBrush(Color.FromArgb(0x55, fg.R, fg.G, fg.B));
            dc.DrawRectangle(track, null, new Rect(0, size - barH, size, barH));
            if (m.Bar != BarKind.None && m.Level > 0)
            {
                double fill = Math.Max(1, Math.Round(size * Math.Clamp(m.Level, 0, 100) / 100.0));
                dc.DrawRectangle(new SolidColorBrush(barColor), null, new Rect(0, size - barH, fill, barH));
            }

            if (m.Stale)
                dc.Pop();
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
    }

    /// <summary>Material Design "bolt" icon outline (24×24 design grid, Apache 2.0).</summary>
    private static readonly Geometry BoltShape = Geometry.Parse(
        "M11,21h-1l1,-7H7.5c-0.58,0 -0.57,-0.32 -0.38,-0.66c0.19,-0.34 0.05,-0.08 0.07,-0.12" +
        "C8.48,10.94 10.42,7.54 13,3h1l-1,7h3.5c0.49,0 0.56,0.33 0.47,0.51l-0.07,0.15C12.96,17.55 11,21 11,21z");

    /// <summary>The bolt scaled to <paramref name="height"/> and aligned to the right edge (<paramref name="right"/>).</summary>
    private static Geometry Bolt(double right, double height, double top)
    {
        var shape = BoltShape.Clone();
        var b = shape.Bounds;
        double scale = height / b.Height;
        var t = new TransformGroup();
        t.Children.Add(new TranslateTransform(-b.X, -b.Y));
        t.Children.Add(new ScaleTransform(scale, scale));
        t.Children.Add(new TranslateTransform(right - b.Width * scale, top));
        shape.Transform = t;
        return shape.GetFlattenedPathGeometry(); // bake the transform in
    }

    private static Drawing.Icon ToIcon(BitmapSource src, int size)
    {
        var pixels = new byte[size * size * 4];
        src.CopyPixels(pixels, size * 4, 0);

        using var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Drawing.Rectangle(0, 0, size, size),
            Drawing.Imaging.ImageLockMode.WriteOnly, Drawing.Imaging.PixelFormat.Format32bppArgb);
        for (int y = 0; y < size; y++)
            Marshal.Copy(pixels, y * size * 4, data.Scan0 + y * data.Stride, size * 4);
        bmp.UnlockBits(data);

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            return (Drawing.Icon)Drawing.Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>Whether the taskbar uses the light theme (the "Windows mode" setting).</summary>
    public static bool IsTaskbarLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
    }

    private const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
