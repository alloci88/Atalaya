using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Svg;

namespace Atalaya.IconGen;

/// <summary>
/// Construye los assets de identidad de Atalaya (F6.4) a partir de sus fuentes.
/// <para>
/// <b>Qué produce.</b> <c>assets/atalaya.ico</c> multi-tamaño (16, 24, 32, 48, 64, 256) y
/// <c>assets/maxam-logo.png</c>. Es determinista: mismas fuentes, mismos bytes, así que
/// regenerar sin cambiar nada no ensucia el árbol de git.
/// </para>
/// <para>
/// <b>Por qué dos SVG.</b> Los tamaños grandes salen de <c>atalaya-icon.svg</c> y los dos
/// pequeños de <c>atalaya-icon-small.svg</c>. Reducir el grande a 16 px no da un icono
/// pequeño: da una mancha. El pequeño es el mismo icono con la silueta engordada y sin lo
/// que no sobrevive al remuestreo (halo, degradado, tronera).
/// </para>
/// </summary>
public static class Program
{
    /// <summary>Los tamaños del .ico, y de qué fuente sale cada uno.</summary>
    private static readonly (int Size, bool Small)[] Sizes =
    {
        (16, true),
        (24, true),
        (32, false),
        (48, false),
        (64, false),
        (256, false),
    };

    public static int Main(string[] args)
    {
        try
        {
            string root = args.Length > 0 ? args[0] : FindRepoRoot();
            string assets = Path.Combine(root, "assets");

            BuildIcon(assets);
            PrepareLogo(assets);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    // ---------- El icono ----------

    private static void BuildIcon(string assets)
    {
        string big = Require(Path.Combine(assets, "atalaya-icon.svg"));
        string small = Require(Path.Combine(assets, "atalaya-icon-small.svg"));

        var frames = new List<(int Size, byte[] Png, Bitmap Bitmap)>();
        foreach ((int size, bool useSmall) in Sizes)
        {
            Bitmap bitmap = Render(useSmall ? small : big, size);
            frames.Add((size, ToPng(bitmap), bitmap));
            Console.WriteLine($"  {size,3}px  <- {(useSmall ? "atalaya-icon-small.svg" : "atalaya-icon.svg")}");
        }

        string ico = Path.Combine(assets, "atalaya.ico");
        WriteIco(ico, frames);
        foreach ((_, _, Bitmap bitmap) in frames)
        {
            bitmap.Dispose();
        }

        Console.WriteLine($"OK  {ico}  ({new FileInfo(ico).Length} bytes, {frames.Count} tamaños)");
    }

    /// <summary>
    /// Rasteriza un SVG a un cuadrado de <paramref name="size"/> px con antialias y fondo
    /// transparente. El SVG declara su viewBox de 256; aquí solo se escala.
    /// </summary>
    private static Bitmap Render(string svgPath, int size)
    {
        SvgDocument doc = SvgDocument.Open(svgPath);
        doc.Width = size;
        doc.Height = size;

        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            doc.Draw(g);
        }

        return bitmap;
    }

    private static byte[] ToPng(Bitmap bitmap)
    {
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Png);
        return buffer.ToArray();
    }

    /// <summary>
    /// Escribe el contenedor ICO a mano. Son veintidós bytes de cabecera por entrada y el
    /// formato está publicado; usar <c>Icon.FromHandle</c> o el <c>System.Drawing.Icon</c> de
    /// serie habría dado un fichero de UN tamaño, que es justo lo que no se quiere.
    /// <para>
    /// Los tamaños hasta 64 px van como DIB de 32 bits —que es lo que espera todo el shell de
    /// Windows, incluidas las versiones viejas de los diálogos— y el de 256 va comprimido en
    /// PNG, que es como se ha hecho desde Vista y evita que el fichero pese 256 kB de más.
    /// </para>
    /// </summary>
    private static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Png, Bitmap Bitmap)> frames)
    {
        var payloads = frames
            .Select(f => f.Size >= 256 ? f.Png : ToDib(f.Bitmap))
            .ToList();

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(file);

        w.Write((ushort)0);                    // reservado
        w.Write((ushort)1);                    // tipo: 1 = icono
        w.Write((ushort)frames.Count);

        int offset = 6 + (16 * frames.Count);
        for (int i = 0; i < frames.Count; i++)
        {
            int size = frames[i].Size;
            w.Write((byte)(size >= 256 ? 0 : size));   // 0 significa 256
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);                  // colores de la paleta: 0 = sin paleta
            w.Write((byte)0);                  // reservado
            w.Write((ushort)1);                // planos
            w.Write((ushort)32);               // bits por píxel
            w.Write(payloads[i].Length);
            w.Write(offset);
            offset += payloads[i].Length;
        }

        foreach (byte[] payload in payloads)
        {
            w.Write(payload);
        }
    }

    /// <summary>
    /// Un BITMAPINFOHEADER + píxeles BGRA de abajo arriba + la máscara AND.
    /// <para>
    /// La máscara es obligatoria aunque el mapa lleve canal alfa: hay rutas del shell que la
    /// leen, y sin ella el icono sale con un rectángulo negro detrás en algunos diálogos. Y la
    /// altura declarada es el DOBLE de la real, porque el formato cuenta las dos imágenes —la
    /// de color y la máscara— como una sola.
    /// </para>
    /// </summary>
    private static byte[] ToDib(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int stride = width * 4;
        int maskStride = ((width + 31) / 32) * 4;   // filas de la máscara alineadas a 4 bytes

        using var buffer = new MemoryStream();
        using var w = new BinaryWriter(buffer);

        w.Write(40);                    // biSize
        w.Write(width);                 // biWidth
        w.Write(height * 2);            // biHeight: color + máscara
        w.Write((ushort)1);             // biPlanes
        w.Write((ushort)32);            // biBitCount
        w.Write(0);                     // biCompression = BI_RGB
        w.Write((stride * height) + (maskStride * height));
        w.Write(0);                     // biXPelsPerMeter
        w.Write(0);                     // biYPelsPerMeter
        w.Write(0);                     // biClrUsed
        w.Write(0);                     // biClrImportant

        // Píxeles: BGRA y de abajo arriba, que es como los guarda el formato.
        for (int y = height - 1; y >= 0; y--)
        {
            for (int x = 0; x < width; x++)
            {
                Color c = bitmap.GetPixel(x, y);
                w.Write(c.B);
                w.Write(c.G);
                w.Write(c.R);
                w.Write(c.A);
            }
        }

        // Máscara AND: 1 = píxel transparente. Con alfa de por medio es redundante, pero el
        // formato la exige y algunas rutas del shell la siguen mirando.
        for (int y = height - 1; y >= 0; y--)
        {
            var row = new byte[maskStride];
            for (int x = 0; x < width; x++)
            {
                if (bitmap.GetPixel(x, y).A == 0)
                {
                    row[x / 8] |= (byte)(0x80 >> (x % 8));
                }
            }

            w.Write(row);
        }

        return buffer.ToArray();
    }

    // ---------- El logo corporativo ----------

    /// <summary>
    /// Prepara <c>maxam-logo.png</c> desde su fuente. El único tratamiento permitido es
    /// técnico: dejar el fondo en transparencia. Recolorear, redibujar o retocar el logotipo
    /// no es decisión nuestra y esta herramienta no sabe hacerlo.
    /// <para>
    /// Si la fuente YA viene con transparencia se copia byte a byte, sin volver a codificarla:
    /// la forma más segura de no alterar una marca es no tocar sus píxeles.
    /// </para>
    /// </summary>
    private static void PrepareLogo(string assets)
    {
        string source = Path.Combine(assets, "maxam-logo-source.png");
        string target = Path.Combine(assets, "maxam-logo.png");

        if (!File.Exists(source))
        {
            Console.WriteLine("AVISO  no hay assets/maxam-logo-source.png: el logo se salta.");
            return;
        }

        using var image = new Bitmap(source);
        if (HasTransparentBackground(image))
        {
            File.Copy(source, target, overwrite: true);
            Console.WriteLine($"OK  {target}  (la fuente ya venía con transparencia: copia literal)");
            return;
        }

        using var cut = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Color c = image.GetPixel(x, y);
                cut.SetPixel(x, y, IsWhite(c) ? Color.Transparent : c);
            }
        }

        cut.Save(target, ImageFormat.Png);
        Console.WriteLine($"OK  {target}  (fondo blanco pasado a transparencia)");
    }

    /// <summary>Las cuatro esquinas transparentes: la fuente ya trae su canal alfa hecho.</summary>
    private static bool HasTransparentBackground(Bitmap image)
        => image.GetPixel(0, 0).A == 0
           && image.GetPixel(image.Width - 1, 0).A == 0
           && image.GetPixel(0, image.Height - 1).A == 0
           && image.GetPixel(image.Width - 1, image.Height - 1).A == 0;

    /// <summary>
    /// Blanco «de fondo», con holgura para el antialias del borde de las letras. El umbral es
    /// alto a propósito: el gris del logotipo es #51555A y el rojo #FE2413, así que ningún
    /// píxel de la marca se acerca a esto por accidente.
    /// </summary>
    private static bool IsWhite(Color c) => c.A > 0 && c.R >= 245 && c.G >= 245 && c.B >= 245;

    // ---------- Utilidades ----------

    private static string Require(string path)
        => File.Exists(path) ? path : throw new FileNotFoundException($"Falta el asset {path}");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new DirectoryNotFoundException("No encuentro la raíz del repositorio.");
    }
}
