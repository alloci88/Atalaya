using System.Text.RegularExpressions;
using Atalaya.App;
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
        string xaml = Markup(ConversationXaml());

        xaml.Should().Contain("DataType=\"{x:Type services:FixQuestion}\"");
        xaml.Should().Contain("ChooseCommand");
        xaml.Should().Contain("AnswerFreeformCommand");
        xaml.Should().NotContain("ShowDialog", "una pregunta modal taparía lo que hay que leer para contestarla");
        Markup(ViewXaml()).Should().NotContain("ShowDialog");
    }

    [Fact]
    public void El_panel_de_diff_tiene_pestana_por_fichero_y_marca_lo_que_esta_fuera_del_hallazgo()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("SelectFileCommand");
        xaml.Should().Contain("{Binding Lines}");
        // La regla es que el diff DISTINGA lo añadido de lo quitado por color. Desde D-980 ese
        // color lo pone un estilo con DataTrigger y no un converter, porque un converter devuelve
        // un pincel ya resuelto y congela el tema; la regla no cambia, cambia quién la aplica.
        xaml.Should().Contain("Diff.Text");
        xaml.Should().Contain("DiffKindToFill");
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
    /// El camino de vuelta a un arreglo en curso, desde cualquier página: la entrada del raíl y el
    /// indicador del pie. Desde F26 §A el raíl son datos (<c>NavGroups</c>), así que la regla se
    /// mide ahí; el pie sigue leyéndose del XAML porque ahí sí es plantilla.
    /// </summary>
    [Fact]
    public void La_carcasa_ofrece_el_camino_de_vuelta_al_arreglo()
    {
        string xaml = Markup(MainWindowXaml());

        xaml.Should().Contain("ShowFixCommand", "el pie lleva al arreglo en curso");
        xaml.Should().Contain("{Binding FixProgress}", "y dice en qué va");
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

    // ==================================================== D-564: lo que no se puede leer, no se elige

    /// <summary>
    /// <b>El mensaje del agente se lee entero.</b> Un <c>StackPanel</c> horizontal mide a sus hijos
    /// con ancho INFINITO: dentro de uno, <c>TextWrapping="Wrap"</c> no envuelve nada y el texto
    /// largo se sale del globo. Por eso el globo es una rejilla, y por eso esto es un test: el
    /// síntoma solo aparece con un mensaje largo, y los mensajes de prueba son cortos.
    /// <para>
    /// F30 §3 — la regla no cambia; cambia dónde se cumple. Con UN componente para las tres vistas,
    /// el envoltorio lo ponen los estilos de texto —una vez, para todas las burbujas— y lo que hay
    /// que exigir es que <b>ninguna</b> burbuja lo pierda y que ninguna monte su contenido en un
    /// <c>StackPanel</c> horizontal.
    /// </para>
    /// </summary>
    [Fact]
    public void El_globo_de_la_conversacion_envuelve_el_texto_en_vez_de_cortarlo()
    {
        string component = Markup(ConversationXaml());

        foreach (string style in new[]
                 {
                     "Conversation.Text", "Conversation.Prose.Text", "Conversation.Reasoning.Text",
                 })
        {
            string declared = Between(component, "x:Key=\"" + style + "\"", "</Style>");
            (declared.Contains("TextWrapping\" Value=\"Wrap\"")
                || declared.Contains("BasedOn=\"{StaticResource Conversation.Text}\""))
                .Should().BeTrue($"«{style}» tiene que envolver: sin eso el texto largo se sale del globo");
        }

        component.Should().NotContain("StackPanel Orientation=\"Horizontal\"",
            "un StackPanel horizontal da ancho infinito y deja el Wrap sin efecto");
    }

    /// <summary>
    /// <b>Las opciones se leen ENTERAS.</b> El usuario no elige «Sí/No»: elige consecuencias
    /// —«(A) lanzar excepción y adaptar los 7 llamadores»—. Un botón que recorta esa frase le hace
    /// elegir a ciegas. Ni elipsis, ni <c>Content</c> plano de una sola línea.
    /// </summary>
    [Fact]
    public void Las_opciones_de_elicitacion_se_muestran_enteras()
    {
        string pregunta = Between(Markup(ConversationXaml()), "x:Type services:FixQuestion", "</ResourceDictionary>");

        pregunta.Should().Contain("<TextBlock Text=\"{Binding Label}\" TextWrapping=\"Wrap\" />",
            "la etiqueta de la opción va en un TextBlock que envuelve, no como Content de una línea");
        pregunta.Should().NotContain("TextTrimming",
            "una consecuencia con elipsis es una consecuencia que no se ha leído");
        pregunta.Should().NotContain("Width=\"420\"", "un ancho fijo no cabe en una pantalla de 1366");
    }

    /// <summary>
    /// La conversación tiene scroll propio y el autoscroll de V5: se queda al final mientras nadie
    /// la toque, se PAUSA en cuanto alguien sube a leer, y ofrece la vuelta al final.
    /// </summary>
    [Fact]
    public void La_conversacion_tiene_scroll_propio_con_autoscroll_que_se_pausa()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("ScrollChanged=\"OnConversationScrollChanged\"");
        xaml.Should().Contain("BackToBottomCommand");
        xaml.Should().Contain("{Binding AutoScroll, Converter={StaticResource InverseBoolToVisibility}}");
    }

    /// <summary>
    /// Rutas largas: elipsis EN MEDIO —el nombre del fichero es lo que identifica la pestaña— con
    /// la ruta entera en el tooltip. Y lo que tiene tope de altura tiene que poder alcanzarse: un
    /// <c>MaxHeight</c> sin scroll recorta en silencio.
    /// </summary>
    [Fact]
    public void Las_rutas_largas_se_recortan_por_el_medio_y_lo_alto_tiene_scroll()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("Converter={StaticResource MiddleEllipsis}");
        xaml.Should().Contain("ToolTip=\"{Binding RelativePath}\"");

        // El `Style` del sistema va delante desde UI-AUDIT-1: todo contenedor con desplazamiento
        // lleva su aire, y ese aire se declara una vez (`Pad.Scroll`, UI-0028/UI-0037).
        xaml.Should().Contain(
            "<ScrollViewer Style=\"{StaticResource Scroll}\" MaxHeight=\"220\"");

        // La regla no es «hay un solo tope de altura», que envejece en cuanto la vista crece: es que
        // TODO tope de altura vaya en un ScrollViewer. Un MaxHeight sin scroll recorta en silencio.
        foreach (Match m in Regex.Matches(xaml, @"<(\w+)[^>]*?MaxHeight=""[0-9]+"""))
        {
            m.Groups[1].Value.Should().Be("ScrollViewer",
                $"el tope de altura de <{m.Groups[1].Value}> no se puede alcanzar sin scroll");
        }
    }

    /// <summary>La elipsis en medio conserva el nombre del fichero, que es lo que se lee.</summary>
    [Theory]
    [InlineData("Common/CommonStatics.cs", 44, "Common/CommonStatics.cs")]
    [InlineData("src/Modules/Telemetry/Internal/Buffers/RingBufferWriter.cs", 30, "src/Modul…/RingBufferWriter.cs")]
    [InlineData(@"src\Modules\Telemetry\RingBufferWriter.cs", 30, @"src\Modul…\RingBufferWriter.cs")]
    public void La_elipsis_en_medio_conserva_el_nombre_del_fichero(string path, int max, string expected)
        => MiddleEllipsisConverter.Shorten(path, max).Should().Be(expected);

    /// <summary>
    /// Un nombre que no cabe ni él solo se recorta por delante: el final —la extensión— es lo
    /// último que se pierde.
    /// </summary>
    [Fact]
    public void Un_nombre_mas_largo_que_el_hueco_conserva_el_final()
    {
        string shortened = MiddleEllipsisConverter.Shorten("dir/UnNombreDeFicheroLarguisimo.cs", 20);

        shortened.Should().HaveLength(20);
        shortened.Should().StartWith("…");
        shortened.Should().EndWith("Larguisimo.cs");
    }

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        from.Should().BeGreaterThan(-1, "la plantilla existe");
        int to = text.IndexOf(end, from, StringComparison.Ordinal);
        to.Should().BeGreaterThan(-1, "la plantilla se cierra");
        return text[from..to];
    }

    // ==================================================== H9.1 §1: los cuatro caminos de vuelta

    /// <summary>
    /// La vista del arreglo ofrece la vuelta al hallazgo, y no solo en la pantalla de cierre: el
    /// enlace vive en la CABECERA, así que también está cuando se vuelve al «último arreglo» desde
    /// el rail o cuando la sesión falló.
    /// </summary>
    [Fact]
    public void La_vista_del_arreglo_ofrece_volver_al_hallazgo()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("BackToFindingCommand");
        xaml.Should().Contain("{Binding BackToFindingLabel}", "el identificador va en el rótulo");
        xaml.Should().Contain("CanGoBackToFinding");
    }

    /// <summary>Del informe de un arreglo se vuelve a SU hallazgo, no solo a la lista de la app.</summary>
    [Fact]
    public void El_visor_de_informes_lleva_al_hallazgo_del_arreglo()
    {
        string xaml = Markup(ReportsXaml());

        xaml.Should().Contain("OpenFindingCommand");
        xaml.Should().Contain("{Binding OpenFindingLabel}");
        xaml.Should().Contain("{Binding CanOpenFinding, Converter={StaticResource BoolToVisibility}}");
        xaml.Should().Contain("OpenFindingsCommand", "el camino de siempre a la lista no se toca");
    }

    /// <summary>Y del historial de la ficha, al informe del arreglo que lo escribió.</summary>
    [Fact]
    public void El_historial_del_hallazgo_lleva_al_informe_del_arreglo()
    {
        string xaml = Markup(FindingDetailXaml());

        xaml.Should().Contain("OpenSessionReportCommand");
        xaml.Should().Contain("{Binding HasSession, Converter={StaticResource BoolToVisibility}}");
    }

    /// <summary>
    /// H9.1 §2 — el ámbito de la compilación es del usuario. Tiene que estar en la vista, porque
    /// el agente no puede pedirlo: no es suya la decisión.
    /// </summary>
    [Fact]
    public void El_ambito_de_compilacion_es_un_interruptor_del_usuario()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("{Binding BuildFullSolution}");
        xaml.Should().Contain("Compilar solución completa");
        xaml.Should().Contain("{Binding BuildScopeText}", "un veredicto sin ámbito no se interpreta");
    }

    private static string ReportsXaml() => ReadView("ReportsView.xaml");

    // ==================================================== H9.1 §4: al cerrar, se aterriza

    /// <summary>
    /// Terminada la sesión, la pantalla de cierre tiene que quedar delante sin que el usuario pelee
    /// con la rueda. El disparo va por el aviso de visibilidad del panel —que solo se levanta cuando
    /// la visibilidad CAMBIA— y no por PropertyChanged, que se dispara en cada repintado y movería
    /// el scroll bajo los dedos del usuario.
    /// </summary>
    [Fact]
    public void La_pantalla_de_cierre_aterriza_sola_al_llegar_fix_done()
    {
        string xaml = Markup(ViewXaml());

        xaml.Should().Contain("x:Name=\"ClosingPanel\"");
        xaml.Should().Contain("IsVisibleChanged=\"OnClosingShown\"");
        xaml.Should().Contain("x:Name=\"ClosingScroll\"");

        string code = ViewCode();
        code.Should().Contain("ClosingScroll.ScrollToTop()");
        code.Should().Contain("ConversationScroll.ScrollToEnd()",
            "el cierre ignora la pausa del autoscroll a propósito: la sesión ha terminado");
    }

    /// <summary>
    /// <b>Un solo scroll manda en cada panel.</b> Los contenedores internos de la pantalla de
    /// cierre —la salida del build y la descripción del commit— ceden la rueda en su tope con el
    /// patrón de la casa (<c>SnippetScroll</c>, F5.6). Sin esto, bajar hasta la tarjeta de commit
    /// era una lotería según por dónde pasara el cursor.
    /// </summary>
    [Fact]
    public void Los_scrolls_internos_ceden_la_rueda_en_su_tope()
    {
        string xaml = Markup(ViewXaml());

        Regex.Matches(xaml, "PreviewMouseWheel=\"OnInnerScroll\"").Count
            .Should().Be(2, "la salida del build y la descripción del commit");

        string code = ViewCode();
        code.Should().Contain("SnippetScroll.ShouldBubble", "el patrón de la casa, no uno nuevo");
    }

    /// <summary>
    /// La conversación sigue teniendo UN solo ScrollViewer: el del flujo. Meter otro dentro —para
    /// una tarjeta, para un mensaje largo— es volver a la pelea por la rueda.
    /// </summary>
    [Fact]
    public void La_conversacion_tiene_un_unico_scroll()
    {
        // El panel de la conversación, desde su borde hasta el separador: Markup() se come los
        // comentarios, así que el ancla es el propio marcado.
        //
        // El ancla ERA el color de fondo escrito a mano de ese Border. Desde D-983 ningún XAML
        // convertido escribe colores —ese `#0C000000` era el mismo gris en los dos temas— así que
        // se ancla en la columna, que es lo que de verdad identifica al panel y no cambia al
        // repintarlo.
        string conversation = Between(
            Markup(ViewXaml()), "<Border Grid.Column=\"0\"", "<GridSplitter");

        Regex.Matches(conversation, "<ScrollViewer").Count
            .Should().Be(1, "el del flujo de la conversación, y ninguno anidado dentro");
    }

    private static string ViewCode()
        => File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Atalaya.App", "Views", "AssistedFixView.xaml.cs"));

    private static string Markup(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string ViewXaml() => ReadView("AssistedFixView.xaml");

    private static string FindingDetailXaml() => ReadView("FindingDetailView.xaml");

    private static string MainWindowXaml()
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Atalaya.App", "MainWindow.xaml"));

    private static string ReadView(string name)
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Atalaya.App", "Views", name));

    /// <summary>
    /// El componente de conversación (F30 §3). Las burbujas y la tarjeta de pregunta ya no se
    /// declaran en esta vista: viven en el sistema y las usan las tres. Lo que estos tests protegen
    /// no ha cambiado —que el globo envuelva, que las opciones se lean enteras—, así que se leen
    /// donde ahora está lo que protegen.
    /// </summary>
    private static string ConversationXaml()
        => File.ReadAllText(Path.Combine(RepoRoot(), "src", "Atalaya.App", "Themes", "Conversation.xaml"));

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
