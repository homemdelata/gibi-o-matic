// Generates src/GibiOMatic.App/Assets/gibiomatic.ico from the logo of the sidebar (Icon.Logo in
// Themes/Icons.axaml): the yellow rounded square with the dark stroke icon.
// Run from the repository root: dotnet run --project tools/IconGen
using SkiaSharp;

const string LogoPath = "M4,4 H9 A1,1 0 0 1 10,5 V19 A1,1 0 0 1 9,20 H4 A1,1 0 0 1 3,19 V5 A1,1 0 0 1 4,4 Z " +
                        "M13,4 H15 A1,1 0 0 1 16,5 V19 A1,1 0 0 1 15,20 H13 A1,1 0 0 1 12,19 V5 A1,1 0 0 1 13,4 Z M18,5 L21,19";
var accent = SKColor.Parse("#F5C542");
var onAccent = SKColor.Parse("#17181D");
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

var output = args.Length > 0 ? args[0] : Path.Combine("src", "GibiOMatic.App", "Assets", "gibiomatic.ico");
var pngs = sizes.Select(Render).ToList();

using (var file = File.Create(output))
using (var w = new BinaryWriter(file))
{
    // ICONDIR, then one ICONDIRENTRY per size; each image is stored as PNG (supported since Windows Vista).
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)sizes.Length);
    var offset = 6 + (16 * sizes.Length);
    for (var i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(pngs[i].Length);
        w.Write(offset);
        offset += pngs[i].Length;
    }

    foreach (var png in pngs)
    {
        w.Write(png);
    }
}

Console.WriteLine($"Wrote {output} ({string.Join(", ", sizes)} px)");

byte[] Render(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);

    // Same proportions as the sidebar logo: a 34 px tile with 9 px corners and a 20 px icon.
    var scale = size / 34f;
    using var tile = new SKPaint { Color = accent, IsAntialias = true };
    canvas.DrawRoundRect(new SKRect(0, 0, size, size), 9 * scale, 9 * scale, tile);

    // Small icons get a slightly larger, bolder glyph so they stay readable.
    var iconSize = size <= 24 ? 24 * scale : 20 * scale;
    var stroke = size <= 24 ? 2.6f : 2.2f;
    using var path = SKPath.ParseSvgPathData(LogoPath);
    canvas.Save();
    canvas.Translate((size - iconSize) / 2, (size - iconSize) / 2);
    canvas.Scale(iconSize / 24f);
    using var glyph = new SKPaint
    {
        Color = onAccent,
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = stroke,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };
    canvas.DrawPath(path, glyph);
    canvas.Restore();

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}
