namespace Atalaya.Agents;

/// <summary>
/// <b>El techo de un bucle de herramientas que es NUESTRO</b> (PROV-3 §4).
/// <para>
/// <b>Por qué hace falta, habiendo ya un tope.</b> <c>Thresholds.MaxCallsPerPass</c> (12 por
/// defecto, D-866) existe y funciona, pero es del COORDINADOR: cuenta las llamadas por las
/// <c>UsageSample</c> que le llegan y, al pasarse, cancela el token de la unidad. Eso cubre una
/// auditoría conducida por <c>SessionCoordinator</c> — y solo eso. Medido sobre el código:
/// </para>
/// <list type="bullet">
/// <item>
/// <c>VerifyCoordinator</c> <b>no mira ningún techo de llamadas</b>: una verificación en bucle no
/// tenía nada que la parara.
/// </item>
/// <item>
/// El tope del coordinador es configurable y <b>0 lo desactiva</b>, que ahí es legítimo —queda el
/// techo de tokens como red— pero aquí dejaría un <c>while</c> sin salida.
/// </item>
/// <item>
/// Y solo cuenta cuando el proveedor <b>publica consumo</b>. En este dialecto <c>usage</c> es
/// opcional al hacer streaming, así que un endpoint que no lo mande dejaría el contador a cero
/// para siempre.
/// </item>
/// </list>
/// <para>
/// <b>Por qué vive aquí y no en el proveedor que lo necesita.</b> Las dos casas anteriores no
/// tienen bucle propio —lo lleva el CLI o el SDK—, así que el número no puede quedarse escondido
/// en el transporte del primero que sí lo tenga: un tope es una guarda, y las guardas de esta
/// aplicación viven donde cualquiera pueda leerlas y probarlas. La casa que abra un bucle mañana
/// hereda éste sin tener que acordarse de inventarse un número.
/// </para>
/// <para>
/// <b>No sustituye al del coordinador, lo respalda.</b> Quien pueda leer el ajuste se lo pasa a
/// <see cref="MaxTurns"/> y el bucle corta donde diga la aplicación; quien no —o quien lo tenga
/// desactivado— corta en <see cref="DefaultMaxTurns"/>. Lo que no puede pasar en ningún caso es
/// que un turno gire sin fin, porque aquí no hay proceso que matar: el bucle es el nuestro.
/// </para>
/// </summary>
public static class AuditLoopLimits
{
    /// <summary>
    /// Las vueltas que da un bucle de herramientas antes de cerrar por su cuenta. <b>Doce</b>, el
    /// mismo número y por la misma medida que <c>Thresholds.MaxCallsPerPass</c> (D-866): una
    /// pasada sana gasta dos, tres si además pide firmas. Deliberadamente igual — dos topes
    /// hermanos con dos números distintos solo sirven para que nadie sepa cuál saltó.
    /// </summary>
    public const int DefaultMaxTurns = 12;

    /// <summary>
    /// El techo efectivo: el que la aplicación tenga configurado, y <see cref="DefaultMaxTurns"/>
    /// cuando no hay ninguno o está desactivado. <b>Nunca devuelve cero</b>, que es toda la gracia.
    /// </summary>
    public static int MaxTurns(int? configured)
        => configured is int n && n > 0 ? n : DefaultMaxTurns;
}
