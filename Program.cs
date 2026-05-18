using System.ComponentModel;
using BitMiracle.LibTiff.Classic;
using SkiaSharp;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Tsv2Tiff;

public sealed class ConvertSettings : CommandSettings
{
    [CommandArgument(0, "[TSV]")]
    [Description("Path to the TSV file (use - for stdin)")]
    public string? TsvPath { get; init; }

    [CommandOption("-f|--font")]
    [Description("Path to font file (TTF/TTC). Uses embedded NotoSansCJK if omitted")]
    public string? FontPath { get; init; }

    [CommandOption("-o|--output")]
    [Description("Output directory (default: ./output)")]
    public string? OutputDir { get; init; }

    [CommandOption("-s|--scale")]
    [Description("Scale factor for output image (default: 1.0)")]
    [DefaultValue(1.0)]
    public double Scale { get; init; } = 1.0;

    [CommandOption("--crop")]
    [Description("Crop to tight bounding box around text")]
    public bool Crop { get; init; }

    [CommandOption("--combined")]
    [Description("Output a single combined image containing all pages")]
    public bool Combined { get; init; }

    [CommandOption("--stdin")]
    [Description("Read TSV data from standard input")]
    public bool Stdin { get; init; }

    public bool UseStdin => Stdin || TsvPath == "-";
}

public sealed class ConvertCommand : Command<ConvertSettings>
{
    protected override int Execute(CommandContext context, ConvertSettings settings, CancellationToken cancellationToken)
    {
        if (settings.UseStdin && Console.IsInputRedirected == false)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No piped input. Use: cat file.tsv | tsv2tiff");
            return 1;
        }

        var outputDir = settings.OutputDir ?? "output";
        Directory.CreateDirectory(outputDir);

        List<TsvEntry> entries;
        if (settings.UseStdin)
        {
            using var reader = Console.In;
            entries = ParseTsv(reader);
        }
        else
        {
            if (!File.Exists(settings.TsvPath))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] TSV file not found: {settings.TsvPath}");
                return 1;
            }
            entries = ParseTsv(File.OpenText(settings.TsvPath));
        }
        var pages = entries.GroupBy(e => e.Page).OrderBy(g => g.Key).ToList();

        SKTypeface? typeface = null;
        if (!string.IsNullOrEmpty(settings.FontPath))
        {
            if (!File.Exists(settings.FontPath))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Font file not found: {settings.FontPath}");
                return 1;
            }
            typeface = SKTypeface.FromFile(settings.FontPath);
        }
        typeface ??= LoadEmbeddedFont();

        AnsiConsole.MarkupLine($"[bold]Processing {pages.Count} page(s)...[/]");

        var pageBitmaps = new List<(int PageNum, SKBitmap Bitmap)>();

        var progress = AnsiConsole.Progress();
        progress.Start(ctx =>
        {
            var progressTask = ctx.AddTask("Rendering", maxValue: pages.Count);

            foreach (var pageGroup in pages)
            {
                int pageNum = pageGroup.Key;
                var items = pageGroup.Where(e => e.Text != "\\n").ToList();

                if (items.Count == 0)
                {
                    progressTask.Increment(1);
                    continue;
                }

                double minX = items.Min(e => e.BBox.X1);
                double minY = items.Min(e => e.BBox.Y1);
                double maxX = items.Max(e => e.BBox.X2);
                double maxY = items.Max(e => e.BBox.Y2);

                double contentWidth = maxX - minX;
                double contentHeight = maxY - minY;

                double targetWidth = settings.Crop ? contentWidth + 40 : maxX + 50;
                double targetHeight = settings.Crop ? contentHeight + 40 : maxY + 50;

                int imgWidth = (int)Math.Ceiling(targetWidth * settings.Scale);
                int imgHeight = (int)Math.Ceiling(targetHeight * settings.Scale);

                double offsetX = settings.Crop ? -minX + 20 : 0;
                double offsetY = settings.Crop ? -minY + 20 : 0;

                var bitmap = RenderPageBitmap(items, typeface, imgWidth, imgHeight, offsetX, offsetY, settings.Scale);
                pageBitmaps.Add((pageNum, bitmap));

                progressTask.Increment(1);
            }
        });

        if (settings.Combined)
        {
            int combinedWidth = pageBitmaps.Max(b => b.Bitmap.Width);
            int combinedHeight = pageBitmaps.Sum(b => b.Bitmap.Height);

            using var combined = new SKBitmap(combinedWidth, combinedHeight);
            using var combinedCanvas = new SKCanvas(combined);
            combinedCanvas.Clear(SKColors.White);

            int yOffset = 0;
            foreach (var (_, bitmap) in pageBitmaps)
            {
                combinedCanvas.DrawBitmap(bitmap, 0, yOffset);
                yOffset += bitmap.Height;
                bitmap.Dispose();
            }

            string combinedPath = Path.Combine(outputDir, "combined.tiff");
            SaveAsTiffG4(combined, combinedPath);
            AnsiConsole.MarkupLine($"[green]Done.[/] Combined image: {Path.GetFullPath(combinedPath)}");
        }
        else
        {
            foreach (var (pageNum, bitmap) in pageBitmaps)
            {
                string outputPath = Path.Combine(outputDir, $"page_{pageNum + 1}.tiff");
                SaveAsTiffG4(bitmap, outputPath);
                bitmap.Dispose();
            }
            AnsiConsole.MarkupLine($"[green]Done.[/] Saved to: {Path.GetFullPath(outputDir)}");
        }
        return 0;
    }

    static SKBitmap RenderPageBitmap(List<TsvEntry> items, SKTypeface typeface,
        int imgWidth, int imgHeight, double offsetX, double offsetY, double scale)
    {
        var bitmap = new SKBitmap(imgWidth, imgHeight);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        canvas.Scale((float)scale);

        foreach (var item in items)
        {
            float bboxHeight = (float)(item.BBox.Y2 - item.BBox.Y1);
            float fontSize = bboxHeight * 0.85f;

            using var font = new SKFont(typeface, fontSize) { Edging = SKFontEdging.Antialias };
            using var paint = new SKPaint { Color = SKColors.Black };

            var fontMetrics = font.Metrics;
            float baselineY = (float)(item.BBox.Y1 + offsetY) - fontMetrics.Ascent;

            canvas.DrawText(item.Text,
                (float)(item.BBox.X1 + offsetX),
                baselineY,
                SKTextAlign.Left, font, paint);
        }

        return bitmap;
    }

    static SKTypeface LoadEmbeddedFont()
    {
        var assembly = typeof(Program).Assembly;
        var resourceName = "Tsv2Tiff.NotoSansCJK-Regular.ttc";

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return SKTypeface.Default;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return SKTypeface.FromStream(new MemoryStream(ms.ToArray()));
    }

    static List<TsvEntry> ParseTsv(TextReader reader)
    {
        var entries = new List<TsvEntry>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;

            int page = int.Parse(parts[0]);
            string text = parts[1];
            var coords = parts[2].Split(',');
            var bbox = new BBox(
                double.Parse(coords[0]),
                double.Parse(coords[1]),
                double.Parse(coords[2]),
                double.Parse(coords[3])
            );

            entries.Add(new TsvEntry(page, text, bbox));
        }
        return entries;
    }

    static void SaveAsTiffG4(SKBitmap bitmap, string outputPath)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;

        byte[] raster = ConvertTo1BitRaster(bitmap, width, height);

        using var tiff = Tiff.Open(outputPath, "w");
        if (tiff == null) throw new Exception($"Cannot open {outputPath} for writing");

        tiff.SetField(TiffTag.IMAGEWIDTH, width);
        tiff.SetField(TiffTag.IMAGELENGTH, height);
        tiff.SetField(TiffTag.COMPRESSION, Compression.CCITTFAX4);
        tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISWHITE);
        tiff.SetField(TiffTag.BITSPERSAMPLE, 1);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
        tiff.SetField(TiffTag.ROWSPERSTRIP, height);
        tiff.SetField(TiffTag.FILLORDER, FillOrder.MSB2LSB);

        int stride = (width + 7) / 8;
        for (int i = 0; i < height; i++)
        {
            tiff.WriteScanline(raster, i * stride, i, 0);
        }
    }

    static byte[] ConvertTo1BitRaster(SKBitmap bitmap, int width, int height)
    {
        int stride = (width + 7) / 8;
        byte[] raster = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                float brightness = (pixel.Red + pixel.Green + pixel.Blue) / (3f * 255f);
                bool isBlack = brightness < 0.5f;

                int byteIndex = y * stride + x / 8;
                int bitIndex = 7 - (x % 8);

                if (isBlack)
                {
                    raster[byteIndex] |= (byte)(1 << bitIndex);
                }
            }
        }

        return raster;
    }
}

record TsvEntry(int Page, string Text, BBox BBox);
record BBox(double X1, double Y1, double X2, double Y2);

public static class Program
{
    public static int Main(string[] args)
    {
        var app = new CommandApp<ConvertCommand>();
        app.Configure(config =>
        {
            config.SetApplicationName("tsv2tiff");
            config.AddExample(new[] { "input.tsv", "-f", "NotoSansCJK-Regular.ttc" });
            config.AddExample(new[] { "--stdin", "-f", "font.ttf", "--combined" });
        });
        return app.Run(args);
    }
}
