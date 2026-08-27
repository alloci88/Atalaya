namespace Atalaya.App.Services;

/// <summary>
/// Qué fichero de logo usar y si necesita placa detrás (F6.4 §2).
/// </summary>
/// <param name="Path">La ruta absoluta del PNG a pintar.</param>
/// <param name="NeedsPlate">
/// El logo va sobre una superficie clara. Es cierto solo cuando toca pintar el logotipo normal
/// —letras gris oscuro— sobre el fondo oscuro de la aplicación.
/// </param>
public sealed record BrandLogo(string Path, bool NeedsPlate);

/// <summary>
/// Dónde están los assets de marca y cuál toca en cada tema (F6.4 §2).
/// <para>
/// <b>Por qué se resuelven en DISCO y no como recurso compilado.</b> La versión en negativo del
/// logotipo —letras claras, para tema oscuro— hay que pedírsela a comunicación y todavía no
/// existe. Compilarla como recurso obligaría a que el fichero estuviera presente para poder
/// construir; buscándola en disco, el día que aparezca basta con dejarla en <c>assets/</c> junto
/// al ejecutable y la aplicación la usa sola. Y mientras no esté, el hueco no se rompe: el
/// logotipo normal se pinta sobre una placa clara.
/// </para>
/// <para>
/// <b>Y por qué nunca lanza.</b> Que falte un logo es una situación normal —un despliegue puede
/// no querer marca ninguna—, no un error. Sin asset, <see cref="Resolve"/> devuelve null y el
/// hueco se colapsa: ni marco vacío, ni interrogante, ni traza.
/// </para>
/// </summary>
public sealed class BrandAssets
{
    /// <summary>El logotipo tal y como lo entrega comunicación: letras gris oscuro.</summary>
    public const string LogoFile = "maxam-logo.png";

    /// <summary>La versión en negativo, para fondo oscuro. Opcional: puede no estar.</summary>
    public const string DarkLogoFile = "maxam-logo-dark.png";

    /// <summary>La carpeta que se despliega junto al ejecutable.</summary>
    public const string FolderName = "assets";

    private static BrandAssets? _forApp;

    public BrandAssets(string directory) => Directory = directory;

    /// <summary>Los assets que acompañan a ESTE ejecutable. Es lo que usan los controles.</summary>
    public static BrandAssets ForApp => _forApp ??=
        new BrandAssets(Path.Combine(AppContext.BaseDirectory, FolderName));

    public string Directory { get; }

    /// <summary>Hay algún logotipo que enseñar, en el tema que sea.</summary>
    public bool HasAny => Exists(LogoFile) || Exists(DarkLogoFile);

    /// <summary>
    /// Cuál toca. En tema oscuro manda la versión en negativo si existe —y entonces no hace falta
    /// placa, que es justo la mejora que se espera de ella—; si no existe se cae al logotipo
    /// normal CON placa. En tema claro el logotipo normal se lee tal cual.
    /// </summary>
    public BrandLogo? Resolve(bool dark)
    {
        if (dark && Exists(DarkLogoFile))
        {
            return new BrandLogo(Full(DarkLogoFile), NeedsPlate: false);
        }

        if (Exists(LogoFile))
        {
            return new BrandLogo(Full(LogoFile), NeedsPlate: dark);
        }

        // Solo está el negativo y estamos en claro: mejor enseñarlo sobre nada que no enseñar
        // marca ninguna. No lleva placa porque una placa clara bajo letras claras sería peor.
        return Exists(DarkLogoFile) ? new BrandLogo(Full(DarkLogoFile), NeedsPlate: false) : null;
    }

    private string Full(string file) => Path.Combine(Directory, file);

    private bool Exists(string file)
    {
        try
        {
            return File.Exists(Full(file));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
