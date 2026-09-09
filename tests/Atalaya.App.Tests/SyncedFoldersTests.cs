using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// BUGFIX-SYNC — reconocer una carpeta sincronizada.
/// <para>
/// Lo que se prueba aquí es una detección que <b>no puede pasarse de lista</b>: un falso positivo
/// mete un aviso de OneDrive en el banner de alguien que no tiene OneDrive, y un aviso que no
/// viene a cuento se aprende a ignorar — con lo que el día que sí venga a cuento tampoco se leerá.
/// Por eso hay tantos casos de silencio como de aviso.
/// </para>
/// </summary>
public sealed class SyncedFoldersTests
{
    /// <summary>Un entorno de mentira: probar esto escribiendo en el del proceso contaminaría al resto.</summary>
    private static Func<string, string?> Env(params (string Name, string Value)[] variables)
        => name => variables.FirstOrDefault(v => v.Name == name).Value;

    private static readonly Func<string, string?> SinVariables = _ => null;

    // ------------------------------------------------------------------ cuando sí

    /// <summary>
    /// La detección fiable: la variable que planta el propio cliente. Funciona aunque la carpeta
    /// se llame de otra manera, que es lo que hace un despliegue con el tenant de una empresa.
    /// </summary>
    [Fact]
    public void Una_ruta_bajo_la_variable_de_OneDrive_se_reconoce()
    {
        Func<string, string?> env = Env(("OneDrive", @"C:\Users\ana\OneDrive - Acme"));

        SyncedFolders.Detect(@"C:\Users\ana\OneDrive - Acme\Escritorio\Atalaya", env)
            .Should().Be("OneDrive");
    }

    /// <summary>La variable de la cuenta de empresa es otra, y también cuenta.</summary>
    [Fact]
    public void Tambien_la_variable_de_la_cuenta_de_empresa()
        => SyncedFolders.Detect(
                @"D:\sync\Documentos\Atalaya",
                Env(("OneDriveCommercial", @"D:\sync")))
            .Should().Be("OneDrive");

    /// <summary>
    /// El caso que trajo este arreglo, y sin ninguna variable de entorno: el Escritorio
    /// redirigido, con el nombre que le pone el tenant.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\ana\OneDrive\Escritorio\Atalaya-v1.1.1-win-x64", "OneDrive")]
    [InlineData(@"C:\Users\ana\OneDrive - Acme\Escritorio\Atalaya-v1.1.1-win-x64", "OneDrive")]
    [InlineData(@"C:\Users\ana\Dropbox\Apps\Atalaya", "Dropbox")]
    [InlineData(@"C:\Users\ana\Dropbox (Acme)\Atalaya", "Dropbox")]
    [InlineData(@"C:\Users\ana\Google Drive\Atalaya", "Google Drive")]
    [InlineData(@"G:\Mi unidad\Atalaya", "Google Drive")]
    public void El_nombre_de_la_carpeta_raiz_basta(string path, string expected)
        => SyncedFolders.Detect(path, SinVariables).Should().Be(expected);

    // ------------------------------------------------------------------ cuando no

    [Theory]
    [InlineData(@"C:\Apps\Atalaya")]
    [InlineData(@"C:\Users\ana\Escritorio\Atalaya-v1.1.1-win-x64")]
    [InlineData(@"C:\Program Files\Atalaya")]
    [InlineData("")]
    [InlineData(null)]
    public void Una_ruta_normal_no_dice_nada(string? path)
    {
        SyncedFolders.Detect(path, SinVariables).Should().BeNull();
        SyncedFolders.Note(path, SinVariables).Should().BeEmpty();
        SyncedFolders.Advice(path, SinVariables).Should().BeEmpty();
    }

    /// <summary>
    /// «OneDriveAntiguo» no es OneDrive, y «Dropboxeo» tampoco es Dropbox. Se acepta el nombre
    /// exacto o el nombre seguido de un separador —«OneDrive - Acme», «Dropbox (Acme)»—, que son
    /// las formas que de verdad crean los clientes.
    /// </summary>
    [Theory]
    [InlineData(@"C:\copias\OneDriveAntiguo\Atalaya")]
    [InlineData(@"C:\Users\ana\Dropboxeo\Atalaya")]
    public void Un_nombre_que_solo_empieza_igual_no_cuenta(string path)
        => SyncedFolders.Detect(path, SinVariables).Should().BeNull();

    /// <summary>
    /// Una variable con basura dentro no puede tumbar la comprobación: lo peor que puede pasar es
    /// que no detecte nada, nunca que no se pueda actualizar.
    /// </summary>
    [Fact]
    public void Una_variable_con_una_ruta_invalida_no_rompe_nada()
        => SyncedFolders.Detect(@"C:\Apps\Atalaya", Env(("OneDrive", "|<>:")))
            .Should().BeNull();

    // ------------------------------------------------------------------ lo que se dice

    /// <summary>
    /// El aviso del banner nombra al culpable y da la salida rápida. No prohíbe nada: el texto
    /// dice «si falla», porque la mayoría de los días no falla.
    /// </summary>
    [Fact]
    public void El_aviso_nombra_al_culpable_y_no_prohibe_nada()
    {
        string note = SyncedFolders.Note(@"C:\Users\ana\OneDrive\Escritorio\Atalaya", SinVariables);

        note.Should().Contain("OneDrive");
        note.Should().Contain("pausa la sincronización");
        note.Should().Contain("si la actualización falla");
    }

    /// <summary>
    /// La receta del fallo da las DOS salidas: la de ahora —pausar— y la definitiva —mover la
    /// carpeta—. Un «acceso denegado» a secas no le dice a nadie qué hacer.
    /// </summary>
    [Fact]
    public void La_receta_da_las_dos_salidas()
    {
        string advice = SyncedFolders.Advice(@"C:\Users\ana\Dropbox\Atalaya", SinVariables);

        advice.Should().Contain("Dropbox");
        advice.Should().Contain("pausa la sincronización");
        advice.Should().Contain(@"C:\Apps\Atalaya");
    }
}
