using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.5 §3 — el extractor de límites de método.
/// <para>
/// <b>Qué se está fijando.</b> Que lo que se enseña debajo de un hallazgo sea <b>el miembro
/// entero</b> y no un recorte de siete líneas que corta la firma. Y, sobre todo, que los casos en
/// los que contar llaves se equivoca —una llave dentro de una cadena, dentro de un comentario,
/// dentro de una cadena cruda— salgan bien: son la razón por la que esto usa un parser de verdad
/// y no una heurística, así que si alguien la cambiara por una, estos tests tienen que caerse.
/// </para>
/// </summary>
public sealed class MethodBoundaryTests
{
    private static string Lines(params string[] lines) => string.Join("\n", lines);

    /// <summary>Un fichero con el método <c>Suma</c> entre las líneas 5 y 9.</summary>
    private const string Simple = """
        using System;

        namespace Demo;

        public sealed class Calculadora
        {
            public int Suma(int a, int b)
            {
                int total = a + b;
                return total;
            }

            public int Resta(int a, int b) => a - b;
        }
        """;

    [Fact]
    public void El_recorte_es_el_metodo_ENTERO_no_una_ventana_alrededor()
    {
        // Línea 9: «int total = a + b;», en mitad de Suma.
        CodeSpanLines span = MethodBoundary.ForLine(Simple, 9, "Calculadora.cs");

        span.StartLine.Should().Be(7, "el recorte empieza en la firma del método");
        span.EndLine.Should().Be(11, "y termina en su llave de cierre");
        span.Member.Should().Be("Calculadora.Suma");
        span.IsMember.Should().BeTrue();
    }

    [Fact]
    public void La_firma_del_metodo_tambien_cae_dentro_de_su_propio_metodo()
    {
        CodeSpanLines span = MethodBoundary.ForLine(Simple, 7, "Calculadora.cs");

        span.StartLine.Should().Be(7);
        span.Member.Should().Be("Calculadora.Suma");
    }

    [Fact]
    public void Un_metodo_de_expresion_se_recorta_a_su_unica_linea()
    {
        CodeSpanLines span = MethodBoundary.ForLine(Simple, 13, "Calculadora.cs");

        span.StartLine.Should().Be(13);
        span.EndLine.Should().Be(13);
        span.Member.Should().Be("Calculadora.Resta");
    }

    /// <summary>
    /// El caso que tumba a cualquier heurística de contar llaves: las de dentro de la cadena, del
    /// comentario y de la cadena cruda no abren ni cierran nada.
    /// </summary>
    [Fact]
    public void Las_llaves_dentro_de_cadenas_y_comentarios_no_cuentan()
    {
        string source = Lines(
            "class Trampa",                                  // 1
            "{",                                             // 2
            "    public string Formatea(string nombre)",      // 3
            "    {",                                          // 4
            "        // esto abre una llave falsa {",         // 5
            "        string plantilla = \"{ \\\" } {{\";",    // 6
            "        char llave = '}';",                       // 7
            "        string crudo = \"\"\"",                   // 8
            "            { } } } {",                           // 9
            "            \"\"\";",                             // 10
            "        return plantilla + llave + crudo + nombre;", // 11
            "    }",                                           // 12
            "",                                                // 13
            "    public int Siguiente => 42;",                 // 14
            "}");                                              // 15

        CodeSpanLines span = MethodBoundary.ForLine(source, 11, "Trampa.cs");

        span.Member.Should().Be("Trampa.Formatea");
        span.StartLine.Should().Be(3);
        span.EndLine.Should().Be(12, "la llave de la línea 12 es la única que cierra de verdad");
    }

    [Fact]
    public void Una_propiedad_con_cuerpo_se_recorta_por_su_accesor()
    {
        string source = Lines(
            "class Config",                        // 1
            "{",                                   // 2
            "    private int _valor;",             // 3
            "",                                    // 4
            "    public int Valor",                // 5
            "    {",                               // 6
            "        get",                         // 7
            "        {",                           // 8
            "            return _valor;",          // 9
            "        }",                           // 10
            "        set => _valor = value;",      // 11
            "    }",                               // 12
            "}");                                  // 13

        CodeSpanLines span = MethodBoundary.ForLine(source, 9, "Config.cs");

        span.Member.Should().Be("Config.Valor.get");
        span.StartLine.Should().Be(7);
        span.EndLine.Should().Be(10);
    }

    [Fact]
    public void El_constructor_se_reconoce_como_miembro()
    {
        string source = Lines(
            "class Servicio",                        // 1
            "{",                                     // 2
            "    public Servicio(string nombre)",    // 3
            "    {",                                 // 4
            "        Nombre = nombre;",              // 5
            "    }",                                 // 6
            "}");                                    // 7

        CodeSpanLines span = MethodBoundary.ForLine(source, 5, "Servicio.cs");

        span.Member.Should().Be("Servicio.Servicio");
        span.StartLine.Should().Be(3);
        span.EndLine.Should().Be(6);
    }

    /// <summary>La función local gana al método que la hospeda: es el trozo que explica.</summary>
    [Fact]
    public void Una_funcion_local_gana_al_metodo_que_la_contiene()
    {
        string source = Lines(
            "class Host",                              // 1
            "{",                                       // 2
            "    public void Larga()",                 // 3
            "    {",                                   // 4
            "        int Interna(int x)",              // 5
            "        {",                               // 6
            "            return x * 2;",               // 7
            "        }",                               // 8
            "",                                        // 9
            "        Console.WriteLine(Interna(3));",  // 10
            "    }",                                   // 11
            "}");                                      // 12

        CodeSpanLines dentro = MethodBoundary.ForLine(source, 7, "Host.cs");
        dentro.Member.Should().Be("Host.Interna");
        dentro.StartLine.Should().Be(5);
        dentro.EndLine.Should().Be(8);

        CodeSpanLines fuera = MethodBoundary.ForLine(source, 10, "Host.cs");
        fuera.Member.Should().Be("Host.Larga", "fuera de la función local manda el método");
    }

    /// <summary>
    /// Un fichero a medio editar no compila y aun así tiene que dar un miembro: el parser trabaja
    /// con nodos de error, que es la mitad del motivo de usarlo.
    /// </summary>
    [Fact]
    public void Un_fichero_que_no_compila_sigue_dando_su_miembro()
    {
        string source = Lines(
            "class Roto",                     // 1
            "{",                              // 2
            "    public void Metodo()",       // 3
            "    {",                          // 4
            "        var x = ;",              // 5  <- error de sintaxis
            "        return",                 // 6  <- sin punto y coma
            "    }",                          // 7
            "}");                             // 8

        CodeSpanLines span = MethodBoundary.ForLine(source, 5, "Roto.cs");

        span.Member.Should().Be("Roto.Metodo");
        span.StartLine.Should().Be(3);
    }

    [Fact]
    public void Lo_que_no_es_Csharp_cae_en_la_ventana_de_quince_lineas()
    {
        string source = Lines(Enumerable.Range(1, 60).Select(i => $"linea {i}").ToArray());

        CodeSpanLines span = MethodBoundary.ForLine(source, 30, "script.py");

        span.IsMember.Should().BeFalse();
        span.Member.Should().BeNull();
        span.StartLine.Should().Be(30 - MethodBoundary.FallbackRadius);
        span.EndLine.Should().Be(30 + MethodBoundary.FallbackRadius);
    }

    /// <summary>
    /// Una línea que no vive dentro de ningún miembro —los <c>using</c> de la cabecera— tampoco
    /// tiene límites que extraer: la ventana fija es la respuesta correcta, no la clase entera.
    /// </summary>
    [Fact]
    public void Una_linea_fuera_de_todo_miembro_cae_en_la_ventana()
    {
        CodeSpanLines span = MethodBoundary.ForLine(Simple, 1, "Calculadora.cs");

        span.IsMember.Should().BeFalse();
        span.StartLine.Should().Be(1);
    }

    [Fact]
    public void La_ventana_se_recorta_al_fichero_por_los_dos_lados()
    {
        string corto = Lines("uno", "dos", "tres");

        CodeSpanLines span = MethodBoundary.ForLine(corto, 2, "notas.txt");

        span.StartLine.Should().Be(1);
        span.EndLine.Should().Be(3);
    }

    [Fact]
    public void Una_linea_fuera_del_fichero_se_ajusta_en_vez_de_reventar()
    {
        string corto = Lines("uno", "dos", "tres");

        CodeSpanLines span = MethodBoundary.ForLine(corto, 900, "notas.txt");

        span.StartLine.Should().Be(1);
        span.EndLine.Should().Be(3);
    }

    /// <summary>
    /// Un método monstruoso no se enseña entero: el panel acabaría siendo el fichero. Se recorta
    /// alrededor de la línea, pero conservando el nombre del miembro, que es lo que sitúa.
    /// </summary>
    [Fact]
    public void Un_metodo_gigantesco_se_recorta_pero_conserva_su_nombre()
    {
        var lines = new List<string> { "class Gigante", "{", "    public void Enorme()", "    {" };
        lines.AddRange(Enumerable.Range(1, 400).Select(i => $"        var v{i} = {i};"));
        lines.Add("    }");
        lines.Add("}");

        CodeSpanLines span = MethodBoundary.ForLine(string.Join("\n", lines), 200, "Gigante.cs");

        span.Member.Should().Be("Gigante.Enorme");
        span.LineCount.Should().BeLessThanOrEqualTo(MethodBoundary.MaxMemberLines + 1);
        span.StartLine.Should().BeLessThan(200);
        span.EndLine.Should().BeGreaterThan(200);
    }

    [Fact]
    public void Un_fichero_vacio_no_revienta()
    {
        CodeSpanLines span = MethodBoundary.ForLine(string.Empty, 1, "Vacio.cs");

        span.StartLine.Should().Be(1);
        span.EndLine.Should().Be(1);
    }
}
