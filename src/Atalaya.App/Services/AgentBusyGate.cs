namespace Atalaya.App.Services;

/// <summary>Qué está ocupando el agente ahora mismo.</summary>
public enum AgentWork
{
    Ninguno,

    /// <summary>Una sesión de auditoría o de verificación.</summary>
    Auditoria,

    /// <summary>Una sesión de arreglo asistido (F6.9).</summary>
    Arreglo,
}

/// <summary>
/// Una sola sesión de Copilot a la vez, sea del tipo que sea (F6.9 §1).
/// <para>
/// <b>Por qué un cerrojo compartido y no dos banderas.</b> Auditar y arreglar comparten el mismo
/// runtime, el mismo asiento y el mismo presupuesto, y el arreglo además ESCRIBE en el clon que la
/// auditoría está leyendo: dejarlos convivir es leer código a medio cambiar y publicar hallazgos
/// sobre un estado que no existió nunca. Con dos banderas independientes cada servicio miraría la
/// del otro y el cerrojo se echaría dos veces —o ninguna—; con una sola pieza hay UN sitio donde
/// se decide y UN sitio que probar.
/// </para>
/// </summary>
public sealed class AgentBusyGate
{
    private readonly object _gate = new();

    /// <summary>Qué hay corriendo. <see cref="AgentWork.Ninguno"/> significa libre.</summary>
    public AgentWork Current { get; private set; }

    public bool IsBusy => Current != AgentWork.Ninguno;

    /// <summary>
    /// Echa el cerrojo si está libre. El bloqueo se toma ANTES de cualquier <c>await</c> del
    /// llamante, que es lo que impide que dos disparos casi simultáneos arranquen dos sesiones
    /// (misma lección que D-085).
    /// </summary>
    public bool TryEnter(AgentWork work)
    {
        lock (_gate)
        {
            if (Current != AgentWork.Ninguno)
            {
                return false;
            }

            Current = work;
            return true;
        }
    }

    /// <summary>Suelta el cerrojo. Solo lo suelta quien lo tiene: un <c>finally</c> ajeno no puede
    /// abrirle la puerta a la sesión del otro.</summary>
    public void Exit(AgentWork work)
    {
        lock (_gate)
        {
            if (Current == work)
            {
                Current = AgentWork.Ninguno;
            }
        }
    }

    /// <summary>Lo que se le dice al usuario cuando la puerta está cerrada.</summary>
    public string BusyMessage => Current switch
    {
        AgentWork.Auditoria =>
            "Hay una auditoría en curso. Espera a que termine —o deténla desde «Sesión en vivo»— "
            + "antes de lanzar un arreglo: las dos usan el mismo agente y el mismo clon.",
        AgentWork.Arreglo =>
            "Hay un arreglo asistido en curso. Termínalo o deténlo desde «Arreglo asistido» antes "
            + "de lanzar otra sesión.",
        _ => string.Empty,
    };
}
