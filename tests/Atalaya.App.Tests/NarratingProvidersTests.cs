using System.Reflection;
using Atalaya.Agents;
using Atalaya.ClaudeCode;
using Atalaya.Copilot;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F30 §2b — <b>LAS DOS CASAS SE ALCANZAN POR DONDE EL COORDINADOR PREGUNTA</b>.
/// <para>
/// <b>El fallo que lo trae, y por qué se coló.</b> La entrega 1 dio a Copilot su evento
/// <c>ToolStreamed</c>, lo llenó bien y lo probó bien — pero la clase nunca declaró
/// <see cref="INarratingAuditor"/>. El coordinador se suscribe con
/// <c>_agent as INarratingAuditor</c>, así que el <c>as</c> daba null y no escuchaba nadie: el
/// evento se disparaba contra el vacío. En el <c>dist</c> se veía como que el tramo largo seguía
/// sin contarse, exactamente igual que antes de la entrega.
/// </para>
/// <para>
/// <b>Y por qué los tests no lo vieron.</b> Los de Copilot se suscriben a
/// <c>agent.ToolStreamed</c> sobre el tipo CONCRETO, que compila y funciona sin la interfaz. Probaban
/// que el evento se emite bien, que era lo que se quería probar, y no que alguien pudiera llegar a
/// él. La costura entre el proveedor y quien lo escucha no estaba cubierta por ninguno de los dos
/// lados: cada mitad era correcta y no se tocaban.
/// </para>
/// <para>
/// Este test mira la costura, y por eso pregunta por lo mismo que pregunta el coordinador: no si el
/// tipo tiene un miembro que se llame así, sino si <b>se le puede asignar</b> la interfaz.
/// </para>
/// </summary>
public sealed class NarratingProvidersTests
{
    /// <summary>
    /// Las dos casas de verdad saben narrar, y se alcanzan por la interfaz. Los dobles de los tests
    /// no tienen por qué —son agentes mínimos que ejercitan un camino—, así que la lista es
    /// explícita: lo que se protege es que un proveedor REAL no se quede mudo por un olvido en su
    /// declaración.
    /// </summary>
    [Fact]
    public void Los_dos_proveedores_reales_se_alcanzan_como_narradores()
    {
        typeof(INarratingAuditor).IsAssignableFrom(typeof(RealCopilotAgent))
            .Should().BeTrue("el coordinador se suscribe con `_agent as INarratingAuditor`");
        typeof(INarratingAuditor).IsAssignableFrom(typeof(ClaudeCodeProvider))
            .Should().BeTrue("el coordinador se suscribe con `_agent as INarratingAuditor`");
    }

    /// <summary>
    /// <b>Y la comprobación general, que es la que sobrevive al proveedor siguiente</b>: cualquier
    /// tipo que declare un evento llamado <c>ToolStreamed</c> tiene que declarar además la interfaz
    /// por la que se escucha. Tener el evento y no la interfaz es exactamente el fallo de arriba —
    /// compila, se dispara, y no lo oye nadie.
    /// </summary>
    [Fact]
    public void Un_evento_de_narracion_sin_su_interfaz_no_lo_escucharia_nadie()
    {
        var offenders = new List<string>();

        foreach (Assembly assembly in new[]
                 {
                     typeof(RealCopilotAgent).Assembly,
                     typeof(ClaudeCodeProvider).Assembly,
                     typeof(IAuditorProvider).Assembly,
                 })
        {
            foreach (Type type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
            {
                if (type.GetEvent(nameof(INarratingAuditor.ToolStreamed)) is not null
                    && !typeof(INarratingAuditor).IsAssignableFrom(type))
                {
                    offenders.Add(type.FullName ?? type.Name);
                }
            }
        }

        offenders.Should().BeEmpty(
            "un evento que nadie puede escuchar es un evento que no existe:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
