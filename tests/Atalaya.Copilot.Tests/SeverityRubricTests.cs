using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Copilot.Tests;

/// <summary>
/// F12 §D — <b>la escala de severidad, con criterios explícitos y ejemplos, en un solo sitio</b>.
/// <para>
/// La prueba objetiva que lo motiva: el banco de pruebas de F12 tenía UNA crítica sembrada
/// —credenciales escritas en el código— y la auditoría devolvió <b>siete</b>. Los off-by-one y las
/// desreferencias nulas salieron críticas, y justo la crítica de verdad salió <b>alta</b>. La escala
/// no estaba solo inflada: estaba invertida en el peor sitio.
/// </para>
/// <para>
/// Lo que estos tests fijan no es el juicio del modelo —eso lo verifica el usuario re-auditando el
/// banco contra su clave— sino que <b>el criterio existe, dice lo que tiene que decir y llega al
/// prompt</b>. Un criterio que no viaja no calibra nada.
/// </para>
/// </summary>
public class SeverityRubricTests
{
    [Fact]
    public void La_rubrica_nombra_los_tres_danos_que_hacen_una_critica()
    {
        string r = SeverityRubric.Text;

        r.Should().Contain("secretos o credenciales", "es la que salió alta en el banco");
        r.Should().Contain("pérdida o corrupción de datos");
        r.Should().Contain("vulnerabilidad explotable");
    }

    /// <summary>
    /// Lo que la rúbrica anterior NO decía, y por lo que un off-by-one podía leerse como crítica:
    /// su renglón de crítica terminaba en «error de cálculo de negocio». Ahora el crash y el
    /// resultado incorrecto del camino normal tienen su escalón, que es «alta».
    /// </summary>
    [Fact]
    public void Un_off_by_one_y_una_desreferencia_nula_estan_puestos_en_alta()
    {
        string r = SeverityRubric.Text;

        r.Should().Contain("off-by-one");
        r.Should().Contain("desreferencia nula");
        r.Should().Contain("Un off-by-one es alta; una desreferencia nula es alta.");
        r.Should().NotContain("error de cálculo de negocio",
            "era la puerta por la que entraba todo off-by-one como crítica");
    }

    [Fact]
    public void Cada_escalon_trae_su_criterio_y_al_menos_un_ejemplo()
    {
        string r = SeverityRubric.Text;

        foreach (string escalon in new[] { "- critica —", "- alta —", "- media —", "- baja —" })
        {
            r.Should().Contain(escalon);
        }

        r.Should().Contain("Ejemplos:", "un criterio sin ejemplo se interpreta como a cada uno le parece");
        r.Should().Contain("camino de ERROR", "es lo que distingue media de alta");
    }

    /// <summary>
    /// Las reglas de desempate son la mitad del arreglo: sin ellas, «importante» se lee «crítica» y
    /// la escala se vuelve a inflar sola.
    /// </summary>
    [Fact]
    public void La_rubrica_dice_como_desempatar_y_hacia_donde()
    {
        string r = SeverityRubric.Text;

        r.Should().Contain("«critica» NO significa «importante»");
        r.Should().Contain("elige el MENOR");
        r.Should().Contain("si todo es critico, no hay orden que seguir");
        r.Should().Contain("El tamaño del defecto no es su daño.");
    }

    [Fact]
    public void El_brief_del_auditor_la_lleva_entera_en_cualquier_stack()
    {
        foreach (TechStack stack in Enum.GetValues<TechStack>())
        {
            PillarBrief.For(stack).Should().Contain(SeverityRubric.Text);
        }
    }

    /// <summary>
    /// Y llega hasta el prompt de la unidad, que es lo que el auditor lee de verdad: el brief viaja
    /// dentro de él.
    /// </summary>
    [Fact]
    public void El_prompt_de_la_unidad_lleva_la_rubrica_y_manda_aplicarla()
    {
        string prompt = PromptComposer.ComposeUnitPrompt(
            "src/A.cs", "class A {}", PillarBrief.For(TechStack.DotNet), AuditMode.Lotes);

        prompt.Should().Contain("RÚBRICA DE SEVERIDAD");
        prompt.Should().Contain("aplicando la RÚBRICA DE",
            "declarar los valores válidos no es lo mismo que decir con qué criterio se eligen");
    }

    /// <summary>
    /// Un solo sitio, y se comprueba: la rúbrica NO se copia dentro de otro prompt. El día que se
    /// copie habrá dos escalas, y la segunda envejecerá sin que nadie se entere.
    /// </summary>
    [Fact]
    public void La_rubrica_no_esta_escrita_dos_veces()
    {
        string prompt = PromptComposer.ComposeUnitPrompt(
            "src/A.cs", "class A {}", PillarBrief.For(TechStack.DotNet), AuditMode.Lotes);

        CountOf(prompt, "RÚBRICA DE SEVERIDAD").Should().Be(1);
    }

    /// <summary>
    /// El verificador NO clasifica: su contrato es <c>submit_verdict(findingUlid, verdict,
    /// evidence)</c> y no lleva severidad. Enseñarle un criterio que no puede aplicar sería gastar
    /// tokens en ruido, así que su prompt no la lleva — y este test lo deja escrito, para que el
    /// día que el verificador clasifique se cite <see cref="SeverityRubric.Text"/> en vez de
    /// escribir una segunda escala.
    /// </summary>
    [Fact]
    public void El_verificador_no_clasifica_asi_que_no_recibe_la_rubrica()
    {
        var target = new VerifyTarget(
            "01J0000000000000000000000A", "src/A.cs", 4, "class A {}", "titulo", "descripcion");

        string prompt = PromptComposer.ComposeVerifyPrompt(new[] { target });

        prompt.Should().NotContain("RÚBRICA DE SEVERIDAD");
        prompt.Should().Contain("verdict ∈ {confirmado, resuelto, no-verificable, no-es-defecto}",
            "lo único que se le pide es un veredicto");
        prompt.Should().NotContain("severity");
    }

    private static int CountOf(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }

        return n;
    }
}
