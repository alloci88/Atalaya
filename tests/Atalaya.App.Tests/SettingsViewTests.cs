using System.Text.RegularExpressions;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
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

    [Fact]
    public void La_pagina_esta_dividida_en_las_cuatro_secciones_y_en_ese_orden()
    {
        string xaml = Markup(SettingsXaml());

        int general = xaml.IndexOf("\"General\"", StringComparison.Ordinal);
        int auditoria = xaml.IndexOf("\"Auditoría\"", StringComparison.Ordinal);
        int sync = xaml.IndexOf("\"Sincronización\"", StringComparison.Ordinal);
        int peligro = xaml.IndexOf("\"Zona peligrosa\"", StringComparison.Ordinal);

        general.Should().BeGreaterThan(0);
        auditoria.Should().BeGreaterThan(general);
        sync.Should().BeGreaterThan(auditoria);
        peligro.Should().BeGreaterThan(sync, "la zona peligrosa va al final, no entre los ajustes normales");
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
            row.Should().Contain("<ColumnDefinition Width=\"220\" />",
                $"todas las filas alinean su etiqueta en la misma columna → {Head(row)}");
            row.Should().Contain("{StaticResource FieldLabel}",
                $"la etiqueta usa el estilo común → {Head(row)}");

            // Y ningún elemento del ritmo compartido —etiqueta o ayuda— se pone su propio
            // margen: ahí es exactamente por donde el espaciado volvió a descuadrarse.
            foreach (Match block in Regex.Matches(row, "<TextBlock.*?/>", RegexOptions.Singleline))
            {
                if (!block.Value.Contains("FieldLabel", StringComparison.Ordinal)
                    && !block.Value.Contains("FieldHelp", StringComparison.Ordinal))
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
            row.Should().Contain("{StaticResource FieldHelp}",
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

    // ---------- §4. El guardado, por toast ----------

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
        bloque.Should().Contain("D13A3A", "el borde y el fondo de la zona son rojos, no un panel más");
        bloque.Should().Contain("FactoryResetCommand");
        bloque.Should().Contain("Appearance=\"Danger\"", "el botón final es rojo");
    }

    // ---------- Utilidades ----------

    /// <summary>Cada fila etiqueta+control, desde su <c>Grid</c> hasta el cierre correspondiente.</summary>
    private static List<string> FieldRows()
    {
        string markup = Markup(SettingsXaml());
        var rows = new List<string>();
        const string open = "<Grid Style=\"{StaticResource FieldRow}\">";
        int at = markup.IndexOf(open, StringComparison.Ordinal);
        while (at >= 0)
        {
            int end = markup.IndexOf("</Grid>", at, StringComparison.Ordinal);
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
