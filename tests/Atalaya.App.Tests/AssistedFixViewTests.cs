using System.Text.RegularExpressions;
using Atalaya.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F6.9 §4 — LOS FRENOS DE LA VISTA DE ARREGLO, como invariantes.
/// <para>
/// Mismo criterio que <see cref="EmergencyBrakeTests"/> y por la misma razón. Con un agente
/// escribiendo en tu clon hay tres cosas que tienes que poder hacer siempre: <b>pararlo</b>,
/// <b>deshacerlo</b> y <b>decirle algo</b>. Los tres viven en la plantilla, y una plantilla se cae
/// en silencio: no la compila nadie.
/// </para>
/// <para>
/// Y hay una cuarta que no es un freno pero se rompe igual de callada: el recordatorio de que los
/// cambios están SIN COMMITEAR. Si esa frase desaparece de la pantalla de cierre, el usuario se
/// va convencido de que Atalaya ya ha publicado su arreglo.
/// </para>
/// </summary>
public sealed class AssistedFixViewTests
{
    [Fact]
    public void La_vista_ofrece_pausar_descartar_y_detener()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("TogglePauseCommand");
        xaml.Should().Contain("DiscardAllCommand");
        xaml.Should().Contain("StopCommand");
    }

    /// <summary>
    /// El campo de entrada está SIEMPRE, no solo cuando el agente pregunta: interrumpir y dirigir
    /// («no toques ese fichero») es la mitad del producto.
    /// </summary>
    [Fact]
    public void El_campo_de_entrada_del_usuario_esta_siempre_disponible()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("{Binding Draft, UpdateSourceTrigger=PropertyChanged}");
        xaml.Should().Contain("SendCommand");
        xaml.Should().Contain("OnDraftKeyDown", "Enter envía, como en cualquier conversación");
    }

    /// <summary>Las preguntas son tarjetas EN la conversación, no diálogos modales que la tapen.</summary>
    [Fact]
    public void Las_preguntas_se_pintan_dentro_de_la_conversacion()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("DataType=\"{x:Type services:FixQuestion}\"");
        xaml.Should().Contain("ChooseCommand");
        xaml.Should().Contain("AnswerFreeformCommand");
        xaml.Should().NotContain("ShowDialog", "una pregunta modal taparía lo que hay que leer para contestarla");
    }

    [Fact]
    public void El_panel_de_diff_tiene_pestana_por_fichero_y_marca_lo_que_esta_fuera_del_hallazgo()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("SelectFileCommand");
        xaml.Should().Contain("{Binding Lines}");
        xaml.Should().Contain("DiffKindToBrush");
        xaml.Should().Contain("{Binding InScope, Converter={StaticResource InverseBoolToVisibility}}");
    }

    /// <summary>El recordatorio que no puede desaparecer nunca.</summary>
    [Fact]
    public void La_pantalla_de_cierre_recuerda_que_los_cambios_estan_sin_commitear()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("sin commitear");
        xaml.Should().Contain("Arreglar no resuelve el hallazgo");
        AssistedFixViewModel.UncommittedReminder.Should().Contain("sin commitear");
    }

    /// <summary>
    /// La sugerencia de commit va en su tarjeta, EDITABLE, con su botón de copiar — y Atalaya no
    /// commitea: no hay ni un botón que lo insinúe.
    /// </summary>
    [Fact]
    public void La_sugerencia_de_commit_es_editable_y_se_copia_pero_no_se_commitea()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("{Binding Commit.Title, UpdateSourceTrigger=PropertyChanged}");
        xaml.Should().Contain("{Binding Commit.Description, UpdateSourceTrigger=PropertyChanged}");
        xaml.Should().Contain("CopyCommitCommand");
        xaml.Should().NotContain("CommitCommand\"", "la app no commitea; eso es del humano");
        xaml.Should().NotContain("PushCommand");
    }

    /// <summary>«Verificar ahora» se SUGIERE al cerrar; nunca se ejecuta solo.</summary>
    [Fact]
    public void El_cierre_sugiere_verificar_y_abrir_el_editor()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("VerifyNowCommand");
        xaml.Should().Contain("OpenInEditorCommand");
    }

    /// <summary>
    /// El item del rail y el indicador de la barra inferior: el camino de vuelta a un arreglo en
    /// curso desde cualquier página, con su punto latiendo.
    /// </summary>
    [Fact]
    public void La_carcasa_ofrece_el_camino_de_vuelta_al_arreglo()
    {
        string xaml = Markup(MainWindowXaml());

        xaml.Should().Contain("ShowFixCommand");
        xaml.Should().Contain("{Binding HasFix, Converter={StaticResource BoolToVisibility}}");
        xaml.Should().Contain("{Binding FixNavLabel}");
        xaml.Should().Contain("FixPulse", "el punto late mientras el agente escribe en el clon");
        xaml.Should().Contain("{Binding FixProgress}");
    }

    /// <summary>
    /// La ficha del hallazgo conserva el camino old school INTACTO, y el nuevo va al lado. Fundir
    /// los dos habría sido cambiar un flujo entregado y probado por uno que aún no lo estaba.
    /// </summary>
    [Fact]
    public void La_ficha_conserva_el_generador_de_prompt_y_anade_el_arreglo_con_agente()
    {
        string xaml = Markup(FindingDetailXaml());

        xaml.Should().Contain("GenerateFixPromptCommand", "el camino de siempre no se toca");
        xaml.Should().Contain("{Binding FixPromptActionLabel}");
        xaml.Should().Contain("StartAssistedFixCommand");
        xaml.Should().Contain("Arreglar con agente");
        xaml.Should().Contain("{Binding CanStartFix}", "sin precondiciones, el botón queda gris");
        xaml.Should().Contain("{Binding AssistedFixTooltip}");
    }

    private static string Markup(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string ViewXaml() => ReadView("AssistedFixView.xaml");

    private static string FindingDetailXaml() => ReadView("FindingDetailView.xaml");

    private static string MainWindowXaml()
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Atalaya.App", "MainWindow.xaml"));

    private static string ReadView(string name)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Atalaya.App", "Views", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("los tests corren dentro del repositorio");
        return dir!.FullName;
    }
}
