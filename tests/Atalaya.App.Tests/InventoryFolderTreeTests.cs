using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F37 §1.1-§1.2 y §1.8 — <b>el árbol de carpetas, como función pura</b>.
/// <para>
/// Se prueba aquí y no a través de la vista porque aquí ES donde está la regla: de una lista de
/// rutas canónicas salen unas carpetas y no otras, plegadas de una manera y no de otra. Lo que se
/// rompería en silencio es exactamente eso — una carpeta de más, una de menos, o una cadena sin
/// plegar—, y no se vería hasta abrir el inventario de una aplicación grande.
/// </para>
/// </summary>
public sealed class InventoryFolderTreeTests
{
    /// <summary>Los nombres de las filas de carpeta, nivel a nivel: «a > b, c».</summary>
    private static string Shape(UnitFolder folder)
    {
        string mine = string.Join(", ", folder.Folders.Select(f => f.Name));
        var below = folder.Folders
            .Where(f => f.Folders.Count > 0)
            .Select(f => $"{f.Name} > {Shape(f)}");
        return string.Join(" | ", new[] { mine }.Where(x => x.Length > 0).Concat(below));
    }

    /// <summary>
    /// <b>Solo existe la carpeta que contiene alguna unidad.</b> No hay una lista de directorios
    /// que recorrer: el árbol se construye DESDE las unidades, así que una carpeta sin ninguna no
    /// llega a nacer. Es lo que impide que el inventario enseñe `bin`, `.vs` o cualquier carpeta
    /// que el escaneo ya descartó.
    /// </summary>
    [Fact]
    public void Solo_aparecen_las_carpetas_que_llevan_alguna_unidad()
    {
        UnitFolder tree = UnitFolderTree.Build("Proyecto", new[]
        {
            "src/Proyecto/Raiz.cs",
            "src/Proyecto/Forms/Uno.cs",
            "src/Proyecto/Class/Objects3D/Dos.cs",
        });

        Shape(tree).Should().Be("Class/Objects3D, Forms");
        tree.Units.Should().Equal("src/Proyecto/Raiz.cs");
    }

    /// <summary>
    /// <b>La cadena de una sola subcarpeta es UNA fila</b> (§1.2). Dos filas para decir un solo
    /// salto es un escalón que no separa nada: cuesta un clic y no contesta ninguna pregunta.
    /// </summary>
    [Fact]
    public void Una_cadena_de_una_sola_subcarpeta_se_pliega_en_una_fila()
    {
        UnitFolder tree = UnitFolderTree.Build("P", new[] { "P/Class/Objects3D/Uno.cs" });

        UnitFolder fila = tree.Folders.Single();
        fila.Name.Should().Be("Class/Objects3D", "una cadena sin ramas es un solo salto");
        fila.RelativePath.Should().Be("Class/Objects3D", "la identidad es la carpeta de abajo");
        fila.Units.Should().Equal("P/Class/Objects3D/Uno.cs");
    }

    /// <summary>
    /// <b>Y no se pliega en cuanto la carpeta separa algo</b>: unidades propias o dos subcarpetas.
    /// Es el caso real de X-BLAST —`XBLASTCore/Class` tiene 139 unidades suyas y cinco
    /// subcarpetas—, y plegarlo escondería esas 139 dentro del nombre de otra carpeta.
    /// </summary>
    [Fact]
    public void Una_carpeta_con_unidades_propias_no_se_pliega_con_su_unica_subcarpeta()
    {
        UnitFolder tree = UnitFolderTree.Build("P", new[]
        {
            "P/Class/Suya.cs",
            "P/Class/Objects3D/Uno.cs",
        });

        UnitFolder clase = tree.Folders.Single();
        clase.Name.Should().Be("Class");
        clase.Units.Should().Equal("P/Class/Suya.cs");
        clase.Folders.Single().Name.Should().Be("Objects3D");
    }

    [Fact]
    public void Una_carpeta_con_dos_subcarpetas_no_se_pliega_con_ninguna()
    {
        UnitFolder tree = UnitFolderTree.Build("P", new[]
        {
            "P/Class/Objects2D/Uno.cs",
            "P/Class/Objects3D/Dos.cs",
        });

        tree.Folders.Single().Name.Should().Be("Class");
        Shape(tree).Should().Be("Class | Class > Objects2D, Objects3D");
    }

    /// <summary>
    /// §1.8 — carpetas primero, unidades después, y dentro de cada grupo el orden canónico. El
    /// orden es lo que hace que dos aperturas de la misma lista se lean iguales.
    /// </summary>
    [Fact]
    public void Carpetas_primero_y_luego_unidades_en_orden_canonico()
    {
        UnitFolder tree = UnitFolderTree.Build("P", new[]
        {
            "P/z.cs",
            "P/Zeta/Uno.cs",
            "P/a.cs",
            "P/Alfa/Dos.cs",
        });

        tree.Folders.Select(f => f.Name).Should().Equal("Alfa", "Zeta");
        tree.Units.Should().Equal("P/a.cs", "P/z.cs");
    }

    /// <summary>
    /// <b>La carpeta del proyecto no se traga la subcarpeta donde vive todo</b>. El inventario no
    /// guarda dónde está el proyecto, así que se deduce del prefijo común de sus unidades — y con
    /// eso solo, un proyecto cuyas unidades están TODAS en `Class` se leería como si el proyecto
    /// fuera `P/Class`, y la carpeta desaparecería. El corte por el nombre del módulo es lo que lo
    /// evita. Es el caso real de `XBLASTMatLab` y `XBLASTUtils`.
    /// </summary>
    [Fact]
    public void El_proyecto_termina_en_su_nombre_aunque_todo_cuelgue_de_una_subcarpeta()
    {
        UnitFolderTree.ProjectRoot("XBLASTMatLab", new[]
        {
            "XBLASTMatLab/Class/Uno.cs",
            "XBLASTMatLab/Class/Dos.cs",
        }).Should().Be("XBLASTMatLab");

        UnitFolder tree = UnitFolderTree.Build("XBLASTMatLab", new[]
        {
            "XBLASTMatLab/Class/Uno.cs",
            "XBLASTMatLab/Class/Dos.cs",
        });

        tree.Folders.Single().Name.Should().Be("Class");
        tree.Units.Should().BeEmpty("ninguna cuelga del proyecto a pelo");
    }

    /// <summary>
    /// Y sin el nombre del módulo por ninguna parte —un `.csproj` que no se llama como su
    /// carpeta— se queda en el prefijo común, que es la respuesta honesta con lo que se sabe
    /// (N-2): agrupa igual de bien y como mucho deja de nombrar un tramo que ninguna unidad
    /// distingue.
    /// </summary>
    [Fact]
    public void Sin_el_nombre_del_modulo_en_la_ruta_el_proyecto_es_el_prefijo_comun()
    {
        UnitFolderTree.ProjectRoot("OtroNombre", new[]
        {
            "libs/motor/Forms/Uno.cs",
            "libs/motor/Class/Dos.cs",
        }).Should().Be("libs/motor");
    }

    // =============================================================== §0 sobre X-BLAST real

    private static IReadOnlyList<(string Module, string Path, string State)> Xblast()
        => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "xblast-units.tsv"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split('\t'))
            .Select(p => (p[0], p[1], p[2]))
            .ToList();

    private static int Depth(UnitFolder folder)
        => folder.Folders.Count == 0 ? 0 : 1 + folder.Folders.Max(Depth);

    /// <summary>
    /// <b>Las cifras del §0, sobre el inventario real</b> — 923 unidades en 22 proyectos. Se fijan
    /// aquí para que un cambio en la construcción del árbol tenga que declararse: si mañana el
    /// plegado se lleva una carpeta por delante o aparecen veinte filas nuevas, este test lo dice
    /// con un número, que es la única forma de discutirlo.
    /// </summary>
    [Fact]
    public void El_arbol_de_X_BLAST_anade_62_filas_y_no_pasa_de_dos_niveles()
    {
        var porProyecto = Xblast()
            .GroupBy(u => u.Module)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(u => u.Path).ToList());

        var arboles = porProyecto
            .Select(kv => UnitFolderTree.Build(kv.Key, kv.Value))
            .ToList();

        int carpetas = arboles.Sum(ContarCarpetas);
        int unidades = porProyecto.Sum(kv => kv.Value.Count);

        porProyecto.Should().HaveCount(22);
        unidades.Should().Be(923);

        carpetas.Should().Be(62, "las filas de carpeta que el árbol añade");
        (22 + carpetas + unidades).Should().Be(1007, "filas con árbol");
        (22 + unidades).Should().Be(945, "filas sin árbol: 62 más, un 6,6 %");

        arboles.Max(Depth).Should().Be(2, "`Class/Objects2D` es lo más hondo que hay");
    }

    private static int ContarCarpetas(UnitFolder folder)
        => folder.Folders.Count + folder.Folders.Sum(ContarCarpetas);
}
