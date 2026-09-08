using LibGit2Sharp;

namespace Atalaya.Tests;

/// <summary>
/// <b>El git de los tests: todo repositorio que nace en un test nace con su identidad puesta</b>
/// (OMPT-BUGFIX-CI).
/// <para>
/// <b>El defecto que cierra.</b> Los repositorios temporales de los tests no configuraban
/// <c>user.name</c> ni <c>user.email</c>, así que quien commiteaba en ellos acababa cayendo a la
/// identidad GLOBAL de la máquina. En el puesto de quien desarrolla esa global existe y lo tapaba;
/// en el runner de Actions no existe, y nueve tests de <c>AssistedFixTests</c> y uno de
/// <c>VerifyAfterRestructureTests</c> se caían con «Este clon no tiene identidad de git
/// configurada» — un fallo que no era del producto, sino del arnés.
/// </para>
/// <para>
/// <b>La regla, en una frase: un test no depende de la configuración de git de la máquina.</b> La
/// identidad se pone en la config <b>LOCAL</b> del repositorio en el momento de crearlo, aquí y no
/// test a test, para que sea la misma en todas partes. Y al revés: los tests que prueban «sin
/// identidad → fallo con su motivo» la <see cref="ClearIdentity">quitan explícitamente</see>, para
/// seguir siendo verdes en una máquina que sí la tiene.
/// </para>
/// <para>
/// <b>Por qué LOCAL y no una variable de entorno.</b> Está medido: libgit2 <b>no</b> mira
/// <c>GIT_CONFIG_GLOBAL</c> ni <c>GIT_CONFIG_SYSTEM</c> —solo el CLI de git lo hace—, así que
/// ninguna variable puede a la vez cegar a los dos motores que esta casa usa. La config local del
/// repositorio sí la leen ambos, y gana sobre la global en los dos.
/// </para>
/// <para>
/// Este fichero está <b>enlazado</b> desde los dos proyectos de test que crean repositorios
/// (<c>Atalaya.App.Tests</c> y <c>Atalaya.Storage.Tests</c>): una sola fábrica, una sola regla.
/// </para>
/// </summary>
internal static class TestGit
{
    /// <summary>El autor de todo lo que commitea un test. No es el de nadie: es el del arnés.</summary>
    public const string Name = "Atalaya Tests";

    /// <summary>El correo del arnés. <c>.local</c> es reservado: no puede ser el de nadie.</summary>
    public const string Email = "tests@atalaya.local";

    /// <summary>
    /// Crea el repositorio y le pone su identidad. Sustituye a <c>Repository.Init</c> en los tests:
    /// llamar al de LibGit2Sharp directamente deja el repositorio a merced de la máquina.
    /// </summary>
    /// <param name="path">Dónde. Se crea si no existe.</param>
    /// <param name="isBare">Un remoto <c>--bare</c> de los de N-1.</param>
    /// <returns>La misma ruta, para poder encadenar.</returns>
    public static string Init(string path, bool isBare = false)
    {
        Repository.Init(path, isBare);
        SetIdentity(path);
        return path;
    }

    /// <summary>
    /// Pone la identidad del arnés en la config LOCAL de un repositorio que ya existe. Es
    /// idempotente, así que vale también para una carpeta que ya era un repo.
    /// </summary>
    public static void SetIdentity(string path)
    {
        using var repo = new Repository(path);
        repo.Config.Set("user.name", Name, ConfigurationLevel.Local);
        repo.Config.Set("user.email", Email, ConfigurationLevel.Local);
    }

    /// <summary>
    /// <b>Deja el repositorio SIN identidad</b>, pase lo que pase en la máquina: la borra de la
    /// config local y además la escribe vacía, que es lo único que gana a una global existente
    /// —desde F32 el producto trata la cadena vacía como ausencia, igual que git—.
    /// <para>
    /// La necesitan los tests que prueban el rechazo con motivo de <c>FixCommitter</c>: sin esto
    /// serían verdes en el runner por casualidad y rojos en el puesto de quien desarrolla.
    /// </para>
    /// </summary>
    public static void ClearIdentity(string path)
    {
        using var repo = new Repository(path);
        repo.Config.Unset("user.name", ConfigurationLevel.Local);
        repo.Config.Unset("user.email", ConfigurationLevel.Local);
        repo.Config.Set("user.name", string.Empty, ConfigurationLevel.Local);
        repo.Config.Set("user.email", string.Empty, ConfigurationLevel.Local);
    }
}
