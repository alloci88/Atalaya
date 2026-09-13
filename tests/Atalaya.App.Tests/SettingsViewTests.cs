using System.Text.RegularExpressions;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F5.7 §§1-3 — la limpieza de la vista Ajustes: un solo ritmo de espaciado, los dos controles
/// muertos fuera y una línea de ayuda por control.
/// <para>
/// Todo esto se comprueba sobre el XAML a propósito. Son reglas de <b>disposición</b>: no hay
/// estado que interrogar en un view-model, y lo que se quiere impedir es que el próximo control
/// que alguien añada nazca sin ayuda o con un margen puesto a ojo — que es exactamente cómo
/// llegaron los umbrales al desorden que esta tanda arregla.
/// </para>
/// </summary>
public sealed class SettingsViewTests
{
    // ---------- §2. Las retiradas ----------

    /// <summary>
    /// F6.9 — el interruptor de «arreglo asistido» VUELVE al UI, porque ahora hay algo detrás.
    /// <para>
    /// F5.7 §2 (D-275) lo retiró por ser un control conectado a nada, no por ser mala idea, y
    /// dejó el flag en la configuración exactamente para este día. La regla de aquel test no se
    /// relaja —un interruptor tiene que cambiar un comportamiento—: lo que cambia es que este ya
    /// lo cambia, y por eso el test se da la vuelta en vez de borrarse.
    /// </para>
    /// </summary>
    [Fact]
    public void El_toggle_de_arreglo_asistido_esta_en_el_UI_enlazado_al_flag_que_de_verdad_gobierna()
    {
        string xaml = Markup(SettingsXaml());

        xaml.Should().Contain("{Binding EnableAssistedFix}",
            "el control tiene que estar enlazado al ajuste, no ser decorativo");
        xaml.Should().Contain("Arreglo asistido");

        typeof(SettingsViewModel).GetProperty("EnableAssistedFix")
            .Should().NotBeNull("el view-model expone el interruptor");
        typeof(AppSettings).GetProperty("EnableAssistedFix")
            .Should().NotBeNull("y el flag sigue siendo el de siempre, no uno nuevo");

        // Encendido por defecto: es un flujo supervisado por construcción, y nacer apagado
        // escondería una capacidad segura tras un ajuste que nadie iba a encontrar.
        new AppSettings().EnableAssistedFix.Should().BeTrue();
    }

    /// <summary>
    /// Y guardarlo lo guarda de verdad. Es la mitad que D-275 echaba en falta: un interruptor que
    /// se mueve y no llega a la configuración es el mismo control muerto de antes, con otro nombre.
    /// </summary>
    [Fact]
    public void Guardar_persiste_el_interruptor_del_arreglo_asistido()
    {
        string root = Path.Combine(Path.GetTempPath(), "atalaya-settings", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppPaths(root);
            var settings = new SettingsService(paths);
            settings.Load();

            settings.Current.EnableAssistedFix.Should().BeTrue("por defecto viene encendido");

            settings.Current.EnableAssistedFix = false;
            settings.Save(settings.Current);

            var reloaded = new SettingsService(paths);
            reloaded.Load().EnableAssistedFix.Should().BeFalse();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // best effort
            }
        }
    }

    /// <summary>
    /// «Opciones avanzadas» (PAT de respaldo, TLS estricto, override de la URL del hub) sale del
    /// UI: la OAuth App es de la organización, así que el escenario «la org bloquea la app» ya no
    /// existe, y el override de <c>hubUrl</c> se edita en <c>appsettings.deploy.json</c>, que es
    /// justo el público de esa opción. El SOPORTE de PAT no se toca.
    /// </summary>
    [Fact]
    public void Las_opciones_avanzadas_salen_del_UI_pero_el_soporte_de_PAT_sigue_en_el_codigo()
    {
        string xaml = Markup(SettingsXaml());

        xaml.Should().NotContain("Opciones avanzadas");
        xaml.Should().NotContain("Binding Pat");
        xaml.Should().NotContain("ClearPatCommand");
        xaml.Should().NotContain("HubUrlOverride");
        xaml.Should().NotContain("RequireTlsRevocationCheck");

        typeof(SettingsService).GetMethod(nameof(SettingsService.GetPat))
            .Should().NotBeNull("el PAT sigue siendo el respaldo oculto: solo se va su interfaz");
        typeof(SettingsService).GetMethod(nameof(SettingsService.SetPat)).Should().NotBeNull();
        typeof(AppSettings).GetProperty(nameof(AppSettings.HubUrlOverride)).Should().NotBeNull();
    }

    // ---------- §1. Secciones y espaciado uniforme ----------

    /// <summary>
    /// <b>F26 §C: la página son CINCO SECCIONES navegables, y en el orden del trabajo</b> — con
    /// quién auditas · cómo audita · lo que cuesta · cómo se ve · lo demás.
    /// <para>
    /// La regla que este test protegía sigue viva y es la misma: Ajustes tiene una estructura
    /// declarada y la zona peligrosa va al final, no entre los ajustes normales. Lo que cambia es
    /// que las secciones son DATOS del view-model —se pueden contar y ordenar— en vez de rótulos
    /// buscados por su posición en el XAML.
    /// </para>
    /// </summary>
    [Fact]
    public void La_pagina_esta_dividida_en_las_cinco_secciones_y_en_ese_orden()
    {
        SettingsViewModel vm = Model();

        vm.Sections.Select(x => x.Key).Should().Equal(
            SettingsViewModel.ProviderSection,
            SettingsViewModel.AuditSection,
            SettingsViewModel.RatesSection,
            SettingsViewModel.AppearanceSection,
            SettingsViewModel.AdvancedSection);

        vm.Section.Should().Be(SettingsViewModel.ProviderSection, "se abre por la primera");
        vm.Sections[0].IsActive.Should().BeTrue("y la lista dice donde estas, como el rail");

        string xaml = Markup(SettingsXaml());
        int avanzado = xaml.IndexOf("\"Avanzado\"", StringComparison.Ordinal);
        int peligro = xaml.IndexOf("Zona peligrosa", StringComparison.Ordinal);
        avanzado.Should().BeGreaterThan(0);
        peligro.Should().BeGreaterThan(avanzado, "la zona peligrosa va al final, no entre los ajustes normales");
    }

    /// <summary>
    /// La sección es un DESTINO: Métricas enlaza hasta las tarifas cuando falta una, y llegar a la
    /// página entera para tener que buscar la tabla no es llegar. Se rompe en silencio —el enlace
    /// sigue navegando, solo que a la sección equivocada—, por eso se comprueba.
    /// </summary>
    [Fact]
    public void Elegir_una_seccion_la_ensena_y_apaga_las_demas()
    {
        SettingsViewModel vm = Model();

        vm.SelectSectionCommand.Execute(SettingsViewModel.RatesSection);

        vm.ShowRates.Should().BeTrue();
        vm.ShowProvider.Should().BeFalse();
        vm.Sections.Single(x => x.IsActive).Key.Should().Be(SettingsViewModel.RatesSection);
    }

    /// <summary>
    /// El espaciado no puede volver a ponerse a ojo: TODA fila etiqueta+control usa la misma
    /// rejilla y el mismo estilo, y ninguna declara su propio <c>Margin</c>.
    /// </summary>
    [Fact]
    public void Todas_las_filas_comparten_la_misma_rejilla_y_el_mismo_margen()
    {
        var rows = FieldRows();

        rows.Should().HaveCountGreaterThan(5, "una fila por control editable");
        foreach (string row in rows)
        {
            row.Should().Contain("{StaticResource Setting.Label}",
                $"todas las filas alinean su etiqueta en la misma columna → {Head(row)}");
            row.Should().Contain("{StaticResource Setting.Name}",
                $"y el nombre del ajuste usa el estilo común → {Head(row)}");

            // Y ningún elemento del ritmo compartido —etiqueta o ayuda— se pone su propio
            // margen: ahí es exactamente por donde el espaciado volvió a descuadrarse.
            foreach (Match block in Regex.Matches(row, "<TextBlock.*?/>", RegexOptions.Singleline))
            {
                if (!block.Value.Contains("Setting.Name", StringComparison.Ordinal)
                    && !block.Value.Contains("Setting.Help", StringComparison.Ordinal))
                {
                    continue;
                }

                block.Value.Should().NotContain("Margin=",
                    "el margen lo pone el estilo compartido, no cada elemento por su cuenta: "
                    + $"en la fila «{Head(row)}»");
            }
        }
    }

    // ---------- §3. Etiquetas que se explican solas ----------

    /// <summary>
    /// El criterio de la tanda: que un compañero que abre la aplicación por primera vez entienda
    /// cada control sin preguntar. Un control sin su línea de ayuda es el que deja adivinando.
    /// </summary>
    [Fact]
    public void Cada_control_lleva_su_linea_de_ayuda()
    {
        foreach (string row in FieldRows())
        {
            row.Should().Contain("{StaticResource Setting.Help}",
                $"este control no se explica solo → {Head(row)}");
        }
    }

    /// <summary>«Polling» no significaba nada para quien no lo escribió.</summary>
    [Fact]
    public void El_polling_se_llama_por_lo_que_hace()
    {
        string xaml = Markup(SettingsXaml());

        xaml.Should().NotContain("Polling (segundos)");
        xaml.Should().Contain("Sincronización del hub (segundos)");
        xaml.Should().Contain("Cada cuántos segundos se buscan cambios de tus compañeros en el hub");
    }

    [Fact]
    public void Las_ayudas_dicen_lo_que_la_tanda_pidio_que_dijeran()
    {
        string xaml = Markup(SettingsXaml());

        xaml.Should().Contain("Con qué editor se abre el código desde la ficha de un hallazgo");
        xaml.Should().Contain("A partir de cuántos días sin reconfirmarse un hallazgo");
        xaml.Should().Contain("Tiempo máximo de espera por una respuesta del modelo");
    }

    // ---------- §4. El guardado se ve, y cada control dice cuando aplica ----------

    /// <summary>
    /// BUGFIX-AJUSTES §3 — cada control dice CUÁNDO surte efecto. Descubrir a base de prueba y
    /// error que un ajuste necesitaba re-escanear (o reiniciar) es la mitad de lo que hizo tan
    /// caro el defecto del umbral: el usuario probó tres gestos sin saber cuál tocaba.
    /// </summary>
    /// <summary>La ayuda que llega por enlace: su texto vive en el view-model.</summary>
    private const string HelpFromViewModel = @"Setting[.]Help[}]""\s*Text=""[{]Binding";

    [Fact]
    public void Cada_ayuda_dice_cuando_surte_efecto()
    {
        foreach (string row in FieldRows())
        {
            // EXCEPCIÓN, declarada: la ayuda del modo exhaustivo sale del view-model —es la misma
            // frase que el tooltip de su icono, para que el precio medido en M2 exista en UN solo
            // sitio (R2 §1)— y desde la revisión de F26 §C dice qué hace el modo y qué cuesta, no
            // cuándo aplica. Es un cambio pedido y consciente: quien mira ese interruptor está
            // decidiendo si paga el triple, y el «cuándo» está en la tabla del MANUAL. El test lo
            // salta por la vía por la que llega el texto, no por su nombre, para que una fila
            // nueva con la ayuda escrita a mano siga teniendo que decirlo.
            if (Regex.IsMatch(row, HelpFromViewModel))
            {
                continue;
            }

            // R5 amplía las formas de decirlo, no la regla. Sin barra de guardar, «aplica al
            // guardar» dejó de ser cierto en ninguna fila: las que gobiernan lo que se lanza dicen
            // «Aplica a las sesiones que lances a partir de ahora» y las que valen ya —el tema, la
            // frescura, el sondeo— dicen «Se aplica al momento» o cuándo se nota. Lo que sigue sin
            // valer es no decirlo.
            row.Should().MatchRegex("[Aa]plica (al|a las|la próxima)",
                $"este control no dice cuándo surte efecto → {Head(row)}");
        }
    }

    /// <summary>
    /// F13: el umbral de unidad grande <b>no se edita aquí</b> — es política de cada aplicación y
    /// se gobierna en su Inventario. Dos sitios editables para el mismo valor son dos verdades
    /// esperando a discrepar, y eso es lo que este test protege.
    /// <para>
    /// <b>Lo que la revisión de F26 §C retira</b>: la fila que lo decía. Era un párrafo de cinco
    /// líneas fingiendo ser un ajuste, y la §C lo convirtió en un aviso con enlace; el usuario lo
    /// miró en el dist y pidió quitarlo entero. Ajustes es la lista de lo que SE PUEDE cambiar
    /// aquí, y un renglón dedicado a lo que no se puede cambiar aquí es la definición de ruido.
    /// Dónde vive el umbral lo dice el MANUAL, y la propia pantalla de Inventario.
    /// </para>
    /// <para>
    /// La mitad viva de la regla —que no quede ningún control que lo edite— se conserva entera.
    /// </para>
    /// </summary>
    [Fact]
    public void El_umbral_no_se_edita_en_Ajustes()
    {
        string xaml = Markup(SettingsXaml());

        xaml.Should().NotContain("{Binding LargeUnitLoc}", "no puede quedar un control que lo edite");
        xaml.Should().NotContain("Unidad grande", "ni una fila que hable de él");
        xaml.Should().NotContain("unidad grande");

        typeof(SettingsViewModel).GetProperty("LargeUnitLoc")
            .Should().BeNull("el view-model tampoco lo expone");
    }

    [Fact]
    public void Ajustes_no_tiene_ningun_texto_de_estado_incrustado()
    {
        Markup(SettingsXaml()).Should().NotContain("StatusMessage");
        typeof(SettingsViewModel).GetProperty("StatusMessage")
            .Should().BeNull("el aviso de guardado va por el toast global de F5.3");
    }

    // ---------- §5. La zona peligrosa ----------

    [Fact]
    public void La_zona_peligrosa_esta_marcada_en_rojo_y_lleva_el_reset()
    {
        string xaml = Markup(SettingsXaml());
        int zona = xaml.IndexOf("Zona peligrosa", StringComparison.Ordinal);
        zona.Should().BeGreaterThan(0);

        string bloque = xaml[Math.Max(0, zona - 400)..];

        // F26 §C: el rojo sale de la paleta y no de un hexadecimal escrito a mano — el de antes
        // (#18D13A3A sobre #55D13A3A) se eligió mirando el tema oscuro y en claro no se veía
        // (D-983 §8). La regla —la zona se ve como zona peligrosa y lleva el reset— no cambia.
        bloque.Should().Contain("{StaticResource Notice.Danger}",
            "el borde y el fondo de la zona son los del peligro del sistema, no un panel más");
        bloque.Should().Contain("FactoryResetCommand");
        // Y PERFILADO, NO MACIZO (UI-0039). Era `Button.DangerSolid` con el argumento de que
        // «destruir ES la acción de este bloque»; medido en la pantalla, el argumento no se
        // sostiene: el bloque no es la vista. El rojo macizo acababa siendo lo más saturado de
        // una pantalla cuyo primario está apagado en gris, así que el ojo iba a lo único que no
        // hay que pulsar. D-999 §4 reserva el macizo para cuando destruir es lo que se ha venido
        // a hacer, y eso es el diálogo que confirma — donde sí es macizo.
        bloque.Should().Contain("{StaticResource Button.Danger}",
            "la destructiva se pinta igual en todas partes: perfilada");
        bloque.Should().NotContain("{StaticResource Button.DangerSolid}",
            "el macizo se queda para el diálogo de confirmación");
    }

    // ---------- Utilidades ----------

    /// <summary>
    /// Un view-model de Ajustes sobre un directorio de usar y tirar. Solo lo mínimo: estos tests
    /// miran las secciones, que no dependen del hub ni del proveedor.
    /// </summary>
    private static SettingsViewModel Model()
    {
        var paths = new AppPaths(Path.Combine(
            Path.GetTempPath(), "atalaya-settings-sec", Guid.NewGuid().ToString("N")));
        var settings = new SettingsService(paths);
        settings.Load();
        HubContext hub = TestFactory.Hub(paths, settings);

        return new SettingsViewModel(
            settings,
            new Atalaya.Copilot.FakeCopilotAgent(),
            new ToastCenter(),
            new FactoryResetService(
                hub, paths, settings, TestFactory.Account(paths), new OpenSessionStore(paths),
                new ProviderSecretStore(paths)),
            new NoReset(),
            hub,
            new NavigationService(new NoServices()));
    }

    /// <summary>El reset de fábrica no se dispara sin querer desde estos tests.</summary>
    private sealed class NoReset : IFactoryResetConfirmer
    {
        public bool Confirm(FactoryResetConfirmation confirmation) => false;
    }

    private sealed class NoServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Cada fila etiqueta+control, desde su <c>Grid</c> hasta el cierre correspondiente.</summary>
    private static List<string> FieldRows()
    {
        string markup = Markup(SettingsXaml());
        var rows = new List<string>();
        const string open = "<DockPanel Style=\"{StaticResource Setting.Row}\"";
        int at = markup.IndexOf(open, StringComparison.Ordinal);
        while (at >= 0)
        {
            int end = markup.IndexOf("</DockPanel>", at, StringComparison.Ordinal);
            rows.Add(markup[at..(end < 0 ? markup.Length : end)]);
            at = markup.IndexOf(open, at + open.Length, StringComparison.Ordinal);
        }

        return rows;
    }

    /// <summary>La etiqueta de la fila, para que un fallo diga CUÁL falla.</summary>
    private static string Head(string row)
    {
        Match label = Regex.Match(row, "Text=\"([^\"{][^\"]*)\"");
        return label.Success ? label.Groups[1].Value : row[..Math.Min(120, row.Length)];
    }

    private static string Markup(string xaml)
        => Regex.Replace(xaml, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

    private static string SettingsXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Atalaya.sln")))
        {
            dir = dir.Parent;
        }

        return File.ReadAllText(
            Path.Combine(dir!.FullName, "src", "Atalaya.App", "Views", "SettingsView.xaml"));
    }
}
