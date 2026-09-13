using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// PROV-2 §1 — <b>LA APLICACIÓN Y EL DOMINIO NO CONOCEN A NINGUNA CASA</b>.
/// <para>
/// <b>De dónde sale.</b> PROV-1 midió el acoplamiento: el vocabulario común de Atalaya —el
/// compositor del prompt, el catálogo de reglas, el de temáticas, la rúbrica, el presupuesto de
/// directivas— vivía dentro del proyecto de un proveedor, así que <c>Atalaya.App</c> tenía que
/// referenciarlo y la aplicación entera arrastraba su SDK. Con eso puesto, añadir una casa tercera
/// era tocar media aplicación. PROV-2 lo saca: el vocabulario baja a <c>Atalaya.Agents</c>, los
/// proveedores se montan en <c>Atalaya.Providers</c>, y la aplicación los conoce solo por el
/// registro.
/// </para>
/// <para>
/// <b>Qué se rompería EN SILENCIO sin este test.</b> Nada, el primer día. El desacople no se
/// deshace de golpe: se deshace en el primer arreglo rápido. Alguien necesita el nombre para
/// mostrar de una casa en una pantalla, escribe <c>using</c> del proyecto del proveedor porque ahí
/// está la constante, y el compilador no se queja —la referencia sigue llegando de rebote por la
/// composición—. Al mes siguiente otro compara <c>provider == "…"</c> con el identificador escrito
/// a mano porque el <c>if</c> es de una línea. Ninguna de las dos cosas falla ningún test, ninguna
/// se ve en pantalla, y cuando aparezca la tercera casa el trabajo de PROV-2 estará deshecho sin
/// que nadie recuerde haberlo deshecho. Esto es lo único que lo nota.
/// </para>
/// <para>
/// <b>Los nombres van partidos</b> —<c>"cop" + "…"</c>— porque un barrido que lleva escrita entera
/// la cadena que busca se encuentra a sí mismo el día que alguien le pase un directorio de más.
/// </para>
/// </summary>
public sealed class ProviderDecouplingTests
{
    // ============================================================ los nombres, partidos a mano

    /// <summary>El identificador del proveedor de fábrica, sin escribirlo entero.</summary>
    private const string HouseId = "cop" + "ilot";

    /// <summary>El de la otra casa, igual.</summary>
    private const string OptionalHouseId = "claude" + "-code";

    /// <summary>El proyecto de cada casa: lo único que puede nombrarse a sí mismo.</summary>
    private const string HouseProject = "Atalaya." + "Cop" + "ilot";

    private const string OptionalHouseProject = "Atalaya." + "Claude" + "Code";

    /// <summary>La casa por API de PROV-3, y su proyecto. Partidos como los otros.</summary>
    private const string ApiHouseId = "openai" + "-compatible";

    private const string ApiHouseProject = "Atalaya." + "Open" + "AI";

    /// <summary>La raíz del espacio de nombres del SDK de esa casa.</summary>
    private const string SdkNamespace = "GitHub." + "Cop" + "ilot";

    /// <summary>Y su paquete.</summary>
    private const string SdkPackage = SdkNamespace + ".SDK";

    /// <summary>El proyecto de COMPOSICIÓN, que existe justamente para conocerlas a todas.</summary>
    private const string CompositionProject = "Atalaya.Providers";

    // ============================================================================== las excepciones

    /// <summary>
    /// <b>Exenciones permanentes</b>, cada una con su porqué. Cada línea de aquí es deuda: si un
    /// día sobra una, se quita.
    /// <list type="bullet">
    /// <item>
    /// El proyecto de cada casa, para las dos reglas: la implementación de un proveedor es el
    /// único sitio donde su identificador y su SDK son suyos.
    /// </item>
    /// <item>
    /// El proyecto de composición, <b>solo para la regla del <c>using</c></b>: es el único sitio
    /// del árbol que conoce a las dos casas a la vez, y existe para eso. Sigue sujeto a la regla
    /// de los literales — pide todo con la constante <c>Id</c> de cada proveedor.
    /// </item>
    /// <item>
    /// <c>DirectiveCatalog.cs</c>, solo para la regla de los literales: lo que nombra ahí no es un
    /// proveedor de auditoría, sino una <b>familia de ficheros de directivas</b> que se busca en
    /// el repositorio auditado (<c>.github/copilot-instructions.md</c> y compañía). Coincide en
    /// texto con un identificador de proveedor y no tiene nada que ver con él: el inventario
    /// escanea ficheros, no elige con quién auditar.
    /// </item>
    /// </list>
    /// </summary>
    private static bool IsHouseOfItsOwn(string relative)
        => relative.StartsWith("src/" + HouseProject + "/", StringComparison.Ordinal)
           || relative.StartsWith("src/" + OptionalHouseProject + "/", StringComparison.Ordinal)
           || relative.StartsWith("src/" + ApiHouseProject + "/", StringComparison.Ordinal);

    /// <summary>
    /// <b>La lista vacía es el final de PROV-2.</b> Aquí vivió, durante el reparto, lo que
    /// todavía nombraba a una casa y estaba en manos de otro agente —los ajustes de modelo, el
    /// mapa de nombres del histórico y el cálculo del coste—: se listaba en vez de arreglarse
    /// desde fuera para que dos agentes no editaran el mismo fichero (N-10). El integrador la
    /// vació al mergear, y vacía es como tiene que quedarse: cada nombre que vuelva aquí es una
    /// casa que alguien ha vuelto a escribir a mano.
    /// </summary>
    private static readonly string[] StillNamesAHouse = Array.Empty<string>();

    // ====================================================================== la puerta del código

    /// <summary>
    /// <b>Ni la aplicación ni el dominio importan el proyecto del proveedor ni su SDK.</b> Es la
    /// puerta ancha: mientras la referencia siga llegando de rebote por la composición, un
    /// <c>using</c> de más compila sin que nadie se entere.
    /// </summary>
    [Fact]
    public void Fuera_de_su_casa_nadie_importa_al_proveedor_ni_su_SDK()
    {
        var offenders = new List<string>();

        foreach (string file in SourceFiles("*.cs"))
        {
            string relative = Relative(file);
            if (IsHouseOfItsOwn(relative)
                || relative.StartsWith("src/" + CompositionProject + "/", StringComparison.Ordinal)
                || StillNamesAHouse.Contains(relative))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            if (text.Contains("using " + HouseProject + ";", StringComparison.Ordinal))
            {
                offenders.Add($"{relative} → using del proyecto del proveedor");
            }

            if (text.Contains("using " + SdkNamespace, StringComparison.Ordinal))
            {
                offenders.Add($"{relative} → using del SDK del proveedor");
            }
        }

        offenders.Should().BeEmpty(
            "la aplicación y el dominio conocen a los proveedores por el registro, no por su "
            + "ensamblado (PROV-2 §1):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// <b>Ningún identificador de proveedor escrito a mano fuera de la casa que lo declara.</b>
    /// Se busca el literal EXACTO —el identificador entre comillas—, no la subcadena suelta:
    /// el nombre de un ajuste que empiece igual es otra cosa y no puede hacer saltar esto.
    /// <para>
    /// La comparación a mano es la forma en que el acoplamiento vuelve sin referencia: un
    /// <c>if</c> de una línea que decide como si solo hubiera dos casas, y que el día que haya una
    /// tercera decide mal en silencio.
    /// </para>
    /// </summary>
    [Fact]
    public void Ningun_identificador_de_proveedor_se_escribe_a_mano_fuera_de_su_casa()
    {
        string[] ids = { "\"" + HouseId + "\"", "\"" + OptionalHouseId + "\"" };
        var offenders = new List<string>();

        foreach (string file in SourceFiles("*.cs"))
        {
            string relative = Relative(file);
            if (IsHouseOfItsOwn(relative)
                // Familias de ficheros de directivas, no proveedores de auditoría: ver arriba.
                || relative.EndsWith("/DirectiveCatalog.cs", StringComparison.Ordinal)
                || StillNamesAHouse.Contains(relative))
            {
                continue;
            }

            string text = File.ReadAllText(file);
            foreach (string id in ids)
            {
                if (text.Contains(id, StringComparison.Ordinal))
                {
                    offenders.Add($"{relative} → {id}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "un identificador de proveedor sale de la constante que lo declara, y de ningún otro "
            + "sitio (PROV-2 §1):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // =================================================================== y la puerta de los .csproj

    /// <summary>
    /// <b>Y los ficheros de proyecto.</b> Dos cosas, que son la misma: el paquete del SDK solo lo
    /// declara el proyecto de su casa, y ni la aplicación ni el dominio referencian el proyecto de
    /// ninguna casa — la aplicación referencia la COMPOSICIÓN, que es lo que hace que un tercer
    /// proveedor no le cueste una línea.
    /// </summary>
    [Fact]
    public void Solo_el_proyecto_de_la_casa_declara_su_SDK_y_la_aplicacion_no_referencia_a_nadie()
    {
        var offenders = new List<string>();

        foreach (string file in ProjectFiles())
        {
            string relative = Relative(file);
            if (relative.EndsWith("/" + HouseProject + ".csproj", StringComparison.Ordinal))
            {
                continue;
            }

            if (File.ReadAllText(file).Contains(SdkPackage, StringComparison.Ordinal))
            {
                offenders.Add($"{relative} → PackageReference al SDK del proveedor");
            }
        }

        foreach (string project in new[] { "src/Atalaya.App/Atalaya.App.csproj", "src/Atalaya.Domain/Atalaya.Domain.csproj" })
        {
            string text = File.ReadAllText(Path.Combine(RepoRoot(), project.Replace('/', Path.DirectorySeparatorChar)));
            foreach (string house in new[] { HouseProject, OptionalHouseProject })
            {
                if (text.Contains(house + ".csproj", StringComparison.Ordinal))
                {
                    offenders.Add($"{project} → ProjectReference a {house}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "el SDK de una casa es de su proyecto, y la aplicación solo referencia la composición "
            + "(PROV-2 §1):" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    // ================================================================================== andamiaje

    /// <summary>El código del producto: <c>src/</c>, sin artefactos de compilación.</summary>
    private static IEnumerable<string> SourceFiles(string pattern)
        => Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "src"), pattern, SearchOption.AllDirectories)
            .Where(NotAnArtefact);

    /// <summary>Todos los ficheros de proyecto del repositorio: también los bancos y los tests.</summary>
    private static IEnumerable<string> ProjectFiles()
        => new[] { "src", "tests", "scripts" }
            .Select(d => Path.Combine(RepoRoot(), d))
            .Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.csproj", SearchOption.AllDirectories))
            .Where(NotAnArtefact);

    private static bool NotAnArtefact(string path)
    {
        string relative = Relative(path);
        return !relative.Contains("/bin/", StringComparison.Ordinal)
               && !relative.Contains("/obj/", StringComparison.Ordinal);
    }

    /// <summary>La ruta desde la raíz del repositorio, siempre con barras hacia delante.</summary>
    private static string Relative(string path)
        => Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return dir!.FullName;
    }
}
