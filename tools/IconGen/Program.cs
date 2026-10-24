// Generates the app icon from docs/design/revamp/icone.svg (the source of truth of the drawing):
// src/GibiOMatic.App/Assets/gibiomatic.ico (16–256 px) and Assets/logo.png (256 px, sidebar logo).
// A glitch speech-balloon G over a navy tile with blue halftone; small sizes drop details.
// Run from the repository root: dotnet run --project tools/IconGen
using SkiaSharp;

// The G: an open ring ending in a bar, plus the balloon's tail (viewBox 64).
const string RingPath = "M45,20.1 A17,17 0 1 0 49,31 H34";
const string TailPath = "M17.3,39.5 L23.5,45.7 L11,53 Z";
var tileColor = SKColor.Parse("#0B1630");
var dotColor = SKColor.Parse("#1D7FC0");
var cyan = SKColor.Parse("#19E3FF");
var magenta = SKColor.Parse("#FF2D87");
var yellow = SKColor.Parse("#FFF27A");
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

var assets = Path.Combine("src", "GibiOMatic.App", "Assets");
var output = args.Length > 0 ? args[0] : Path.Combine(assets, "gibiomatic.ico");
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

var logo = args.Length > 1 ? args[1] : Path.Combine(assets, "logo.png");
File.WriteAllBytes(logo, pngs[^1]);

// Optional preview of every size, side by side, for checking the small ones.
if (args.Length > 2)
{
    var width = sizes.Sum() + (8 * (sizes.Length + 1));
    using var sheet = SKSurface.Create(new SKImageInfo(width, 256 + 16));
    sheet.Canvas.Clear(SKColor.Parse("#808080"));
    var x = 8;
    foreach (var (size, png) in sizes.Zip(pngs))
    {
        using var image = SKImage.FromEncodedData(png);
        sheet.Canvas.DrawImage(image, x, 8);
        x += size + 8;
    }

    using var snapshot = sheet.Snapshot();
    using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(args[2], data.ToArray());
}

Console.WriteLine($"Wrote {output} ({string.Join(", ", sizes)} px) and {logo}");

byte[] Render(int size)
{
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / 64f);

    // Details that do not survive small sizes (see the table in docs/design/revamp/REVAMP.md).
    var halftone = size >= 32;
    var slices = size >= 32;
    var bars = size >= 48;

    // 1. The tile.
    using var tilePath = new SKPath();
    tilePath.AddRoundRect(new SKRect(0, 0, 64, 64), 14, 14);
    using (var tile = new SKPaint { Color = tileColor, IsAntialias = true })
    {
        canvas.DrawPath(tilePath, tile);
    }

    // 2. Halftone: a 3-unit grid turned 30°, fading from the top-right corner to 65% of the diagonal.
    if (halftone)
    {
        canvas.Save();
        canvas.ClipPath(tilePath, antialias: true);
        canvas.SaveLayer();
        using (var dot = new SKPaint { Color = dotColor, IsAntialias = true })
        {
            var (sin, cos) = MathF.SinCos(MathF.PI / 6);
            for (var i = -30; i <= 30; i++)
            {
                for (var j = -30; j <= 30; j++)
                {
                    float px = 1.5f + (3 * i), py = 1.5f + (3 * j);
                    var (cx, cy) = ((px * cos) - (py * sin), (px * sin) + (py * cos));
                    if (cx is > -1 and < 65 && cy is > -1 and < 65)
                    {
                        canvas.DrawCircle(cx, cy, 0.85f, dot);
                    }
                }
            }
        }

        using (var fade = new SKPaint
        {
            BlendMode = SKBlendMode.DstIn,
            Shader = SKShader.CreateLinearGradient(new SKPoint(64, 0), new SKPoint(0, 64),
                [SKColors.White, SKColors.White.WithAlpha(0)], [0, 0.65f], SKShaderTileMode.Clamp),
        })
        {
            canvas.DrawRect(new SKRect(0, 0, 64, 64), fade);
        }

        canvas.Restore();
        canvas.Restore();
    }

    // 3. The ghosts, screen-blended; at least 1 px apart so they still show at 16 px.
    var ghost = MathF.Max(2.4f, 64f / size);
    DrawG(canvas, cyan, -ghost, SKBlendMode.Screen);
    DrawG(canvas, magenta, ghost, SKBlendMode.Screen);

    // 4. The yellow G, cut in four horizontal slices shifted like a misregistered print.
    if (slices)
    {
        (float Top, float Bottom, float Dx)[] cuts = [(0, 21, 0), (21, 28, -3.5f), (28, 37, 3), (37, 64, 0)];
        foreach (var (top, bottom, dx) in cuts)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(0, top, 64, bottom));
            DrawG(canvas, yellow, dx, SKBlendMode.SrcOver);
            canvas.Restore();
        }
    }
    else
    {
        DrawG(canvas, yellow, 0, SKBlendMode.SrcOver);
    }

    // 5. The registration bars.
    if (bars)
    {
        using var bar = new SKPaint { IsAntialias = true };
        bar.Color = magenta;
        canvas.DrawRect(SKRect.Create(41, 23.5f, 13, 2), bar);
        bar.Color = cyan;
        canvas.DrawRect(SKRect.Create(7, 34, 9, 1.6f), bar);
    }

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

void DrawG(SKCanvas canvas, SKColor color, float dx, SKBlendMode blend)
{
    canvas.Save();
    canvas.Translate(dx, 0);
    using var ring = SKPath.ParseSvgPathData(RingPath);
    using var tail = SKPath.ParseSvgPathData(TailPath);
    using var paint = new SKPaint
    {
        Color = color,
        IsAntialias = true,
        BlendMode = blend,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 6,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };

    // Ring and tail become one outline first, so overlapping strokes do not blend twice.
    using var ringOutline = paint.GetFillPath(ring);
    paint.StrokeWidth = 2;
    using var tailOutline = paint.GetFillPath(tail);
    using var shape = ringOutline.Op(tailOutline, SKPathOp.Union);
    using var filledTail = tail.Op(tailOutline, SKPathOp.Union);
    using var whole = shape.Op(filledTail, SKPathOp.Union);
    paint.Style = SKPaintStyle.Fill;
    canvas.DrawPath(whole, paint);
    canvas.Restore();
}
