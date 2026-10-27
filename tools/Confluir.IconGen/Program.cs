using System.Buffers.Binary;
using SkiaSharp;
using Svg.Skia;

// Gera os ícones do app a partir do SVG fonte:
//   <saída>/png/confluir-<N>.png  (Linux e uso geral)
//   <saída>/confluir.icns         (macOS, entradas PNG)
// Uso: dotnet run --project tools/Confluir.IconGen -- <svg> <pasta-de-saída>
if (args.Length < 2)
{
    Console.Error.WriteLine("Uso: Confluir.IconGen <arquivo.svg> <pasta-de-saída>");
    return 1;
}

var svgPath = args[0];
var outDir = args[1];
var pngDir = Path.Combine(outDir, "png");
Directory.CreateDirectory(pngDir);

using var svg = new SKSvg();
var picture = svg.Load(svgPath) ?? throw new InvalidOperationException($"SVG inválido: {svgPath}");

int[] sizes = [16, 24, 32, 48, 64, 128, 256, 512, 1024];
var pngs = new Dictionary<int, byte[]>();
foreach (var size in sizes)
{
    pngs[size] = Render(picture, size);
    var path = Path.Combine(pngDir, $"confluir-{size}.png");
    File.WriteAllBytes(path, pngs[size]);
    Console.WriteLine(path);
}

// Tipos ICNS que aceitam PNG: icp4=16, icp5=32, icp6=64, ic07=128, ic08=256, ic09=512, ic10=1024 (512@2x).
(string Type, int Size)[] icnsEntries =
    [("icp4", 16), ("icp5", 32), ("icp6", 64), ("ic07", 128), ("ic08", 256), ("ic09", 512), ("ic10", 1024)];
var icnsPath = Path.Combine(outDir, "confluir.icns");
File.WriteAllBytes(icnsPath, BuildIcns(icnsEntries.Select(e => (e.Type, pngs[e.Size]))));
Console.WriteLine(icnsPath);
return 0;

static byte[] Render(SKPicture picture, int size)
{
    var bounds = picture.CullRect;
    using var surface = SKSurface.Create(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / bounds.Width, size / bounds.Height);
    canvas.Translate(-bounds.Left, -bounds.Top);
    canvas.DrawPicture(picture);
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

static byte[] BuildIcns(IEnumerable<(string Type, byte[] Png)> entries)
{
    // Formato: "icns" + tamanho total (big-endian), seguido de blocos tipo(4) + tamanho(4) + dados.
    using var body = new MemoryStream();
    Span<byte> header = stackalloc byte[8];
    foreach (var (type, png) in entries)
    {
        System.Text.Encoding.ASCII.GetBytes(type, header[..4]);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], png.Length + 8);
        body.Write(header);
        body.Write(png);
    }

    using var file = new MemoryStream();
    System.Text.Encoding.ASCII.GetBytes("icns", header[..4]);
    BinaryPrimitives.WriteInt32BigEndian(header[4..], (int)body.Length + 8);
    file.Write(header);
    body.WriteTo(file);
    return file.ToArray();
}
