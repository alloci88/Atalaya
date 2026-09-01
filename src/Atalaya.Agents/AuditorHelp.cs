namespace Atalaya.Agents;

/// <summary>
/// Los textos de ayuda que NO dependen de la casa (F14).
/// <para>
/// Casi toda la ayuda es del proveedor —«ve a Cuenta y conecta con GitHub», «instala Claude Code e
/// inicia sesión»— y vive con él. Lo que baja aquí es lo que se dice IGUAL diga quien lo diga: que
/// el modelo elegido ya no sirve y que se cambia en Ajustes. Duplicar esa frase por proveedor solo
/// conseguiría que una de las copias envejeciera.
/// </para>
/// </summary>
public static class AuditorHelp
{
    /// <summary>
    /// El modelo configurado no sirve. Dice QUÉ pasó, POR QUÉ no es culpa de la red ni de la cuenta,
    /// y DÓNDE se arregla — las tres cosas que faltaban cuando el fallo era mudo (F5.15).
    /// </summary>
    public static string ModelUnavailable(string? modelId)
        => string.IsNullOrWhiteSpace(modelId)
            ? "No se pudo iniciar la sesión: el proveedor rechazó el modelo configurado. "
              + "Elige otro en Ajustes."
            : $"No se pudo iniciar: el modelo «{modelId}» no está disponible para tu cuenta. "
              + "Elige otro en Ajustes.";
}
