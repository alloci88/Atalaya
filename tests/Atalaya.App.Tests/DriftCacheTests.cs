using Atalaya.App.Services;
using Atalaya.Domain.Ids;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F12 §C — <b>la clave de la caché incluye TODAS las entradas del cálculo, o el evento
/// correspondiente invalida</b>.
/// <para>
/// El parte del banco de pruebas: verificar en verde un arreglo propio NO limpiaba la marca
/// «Arreglada — pendiente de verificar»; al reiniciar la aplicación, la marca había desaparecido.
/// Y corroborado en la dirección contraria: re-auditar sí la limpiaba al momento, porque re-auditar
/// cambia el commit de la unidad, que sí estaba en la clave.
/// </para>
/// <para>
/// La causa: F9.1 añadió la <b>cobertura</b> —qué arreglos ya están cerrados por el instrumento que
/// los detectó— como entrada del cálculo, y no la añadió a la clave. Estos tests pasan por
/// <see cref="DriftQuery.For"/> y no por <c>Compute</c>: con <c>Compute</c> el defecto era
/// invisible, que es exactamente por qué sobrevivió a F9.1.
/// </para>
/// </summary>
public sealed class DriftCacheTests
{
    /// <summary>Un clon auditado, arreglado por Atalaya y publicado por el usuario.</summary>
    private static Ulid Arreglada(DriftRepo r)
    {
        string c1 = r.Commit("inicial", ("src/A.cs", "roto"));
        r.Audit(c1, "src/A.cs");
        File.WriteAllText(Path.Combine(r.Clone, "src", "A.cs"), "arreglado");
        Ulid finding = r.RecordFix("src/A.cs");
        r.Commit("fix: el arreglo del agente");
        return finding;
    }

    [Fact]
    public void Verificar_en_verde_limpia_la_marca_sin_reiniciar_la_aplicacion()
    {
        using var r = new DriftRepo();
        Ulid finding = Arreglada(r);

        r.Of(r.Cached(), "src/A.cs").State.Should().Be(DriftState.ArregladaPendienteDeVerificar);

        r.VerifyGreen(finding);

        // Ni Invalidate(), ni re-auditar, ni reiniciar: la clave se entera sola.
        r.Of(r.Cached(), "src/A.cs").State.Should().Be(DriftState.SinCambios);
        r.Cached().FixedPendingVerify.Should().Be(0);
    }

    /// <summary>
    /// El caso hermano: resolver por MEDIDA (D-696) es la otra vía que cierra el ciclo de un
    /// arreglo, así que también mueve la cobertura y también tiene que enterarse la clave.
    /// </summary>
    [Fact]
    public void Resolver_por_medida_limpia_la_marca_igual_que_verificar()
    {
        using var r = new DriftRepo();
        Ulid finding = Arreglada(r);

        r.Of(r.Cached(), "src/A.cs").State.Should().Be(DriftState.ArregladaPendienteDeVerificar);

        r.ResolveByMeasure(finding);

        r.Of(r.Cached(), "src/A.cs").State.Should().Be(DriftState.SinCambios);
    }

    /// <summary>
    /// Y en la dirección contraria: reabrir lo que la verificación cerró devuelve la marca, también
    /// al momento. Una caché que solo acierta hacia un lado sigue siendo una caché que miente.
    /// </summary>
    [Fact]
    public void Reabrir_devuelve_la_marca_sin_reiniciar()
    {
        using var r = new DriftRepo();
        Ulid finding = Arreglada(r);
        r.VerifyGreen(finding);
        r.Of(r.Cached(), "src/A.cs").State.Should().Be(DriftState.SinCambios);

        r.Reopen(finding);

        r.Of(r.Cached(), "src/A.cs").State.Should()
            .Be(DriftState.ArregladaPendienteDeVerificar, "el arreglo vuelve a estar sin cerrar");
    }

    /// <summary>
    /// Una verificación en ROJO no cubre nada, así que no cambia la cobertura y no tiene por qué
    /// cambiar la respuesta. Es la mitad de la guarda que no puede aflojarse.
    /// </summary>
    [Fact]
    public void Una_verificacion_fallida_no_cambia_la_respuesta_cacheada()
    {
        using var r = new DriftRepo();
        Ulid finding = Arreglada(r);
        r.Cached();

        r.VerifyRed(finding);

        r.Of(r.Cached(), "src/A.cs").State.Should().Be(DriftState.ArregladaPendienteDeVerificar);
    }

    /// <summary>
    /// Y la caché sigue siendo una caché: sin que cambie ninguna entrada, la respuesta es el MISMO
    /// objeto. Sin esta comprobación, «arreglar» la invalidación desactivándola pasaría los demás
    /// tests y convertiría cada pintado del inventario en un recorrido del historial.
    /// </summary>
    [Fact]
    public void Sin_cambios_en_ninguna_entrada_la_cache_sigue_sirviendo()
    {
        using var r = new DriftRepo();
        Arreglada(r);

        AppDrift primera = r.Cached();
        AppDrift segunda = r.Cached();

        segunda.Should().BeSameAs(primera, "nada ha cambiado: no hay nada que recalcular");
    }
}
