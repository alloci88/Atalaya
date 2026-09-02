using Atalaya.Domain;

namespace Atalaya.App.Services;

/// <summary>
/// Quién reconcilia a quién (F17 §3), en una sola pregunta para que el toolbox y el coordinador
/// no puedan discrepar: un ciclo <b>General</b> reconcilia TODOS los hallazgos de la unidad —es la
/// mirada completa, y con ella nada cambia respecto a antes de F17—; un ciclo temático reconcilia
/// SOLO los de su temática. Los demás no se tocan: ni se confirman, ni se resuelven, ni se
/// disputan. Envejecen, y eso es la verdad: nadie los está mirando.
/// </summary>
public static class ThemeScope
{
    public static bool Reconciles(AuditTheme cycle, AuditTheme finding)
        => cycle == AuditTheme.General || cycle == finding;
}
