using System.Windows;
using Atalaya.App.Services;
using Atalaya.App.ViewModels;
using Atalaya.App.Views;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using Atalaya.Inventory;

namespace Atalaya.Shots;

/// <summary>
/// Los DIALOGOS, sobre el mismo hub del banco.
///
/// No se pueden fotografiar desde el dist instalado sin mentirle al usuario: «Vincular clon local»
/// solo aparece cuando el clon esta roto, «Eliminar la aplicacion» borra de verdad y
/// «Restablecimiento de fabrica» vacia el hub del equipo. Aqui se construyen con su view-model de
/// verdad sobre datos de mentira, que es lo que hace el banco de las vistas densas.
///
/// Los cinco llevan ANCHO FIJO y ResizeMode="NoResize" (o alto fijo), asi que no cambian con el
/// tamano de la ventana: se fotografian una vez por TEMA, no cuatro veces. Eso se dice en el
/// informe para que nadie lea «solo dos combinaciones» como un descuido.
/// </summary>
public static class Dialogs
{
    public static IReadOnlyList<(string Name, Window Window)> Build(Fixture f)
    {
        return new List<(string, Window)>
        {
            ("d1-vincular-clon", LinkClone(f)),
            ("d2-patrones-silenciados", PatternSilences(f)),
            ("d3-eliminar-aplicacion", DeleteApp()),
            ("d4-restablecimiento-de-fabrica", FactoryReset()),
            ("d5-lanzar-auditoria", AuditLaunch()),
        };
    }

    /// <summary>El caso que el usuario ve de verdad: el clon SIN vincular, que es el que bloquea.</summary>
    private static Window LinkClone(Fixture f)
    {
        AppConfig app = f.Hub.Store.TryReadApp(f.Slug)!;
        var vm = new LinkCloneViewModel(
            app,
            CloneLink.Unknown(f.Slug),
            f.Links,
            new InventoryRescanService(f.Hub, new InventoryScanner()),
            new NoPicker());
        return new LinkCloneDialog(vm);
    }

    /// <summary>Con patrones dentro: la lista vacia ya tiene su estado y no es lo que hay que mirar.</summary>
    private static Window PatternSilences(Fixture f)
    {
        var now = DateTimeOffset.UtcNow;
        var seeds = new (string Short, string Exemplar, SilenceReason Reason, int? ExpiresDays, int Hits)[]
        {
            ("P-1", "bloques catch vacios que ocultan la excepcion en el arranque de la aplicacion",
                SilenceReason.DecisionArquitectonica, null, 34),
            ("P-2", "campos publicos mutables en los DTO de serializacion",
                SilenceReason.DeudaAceptada, 45, 12),
            ("P-3", "await sin ConfigureAwait(false) en codigo de interfaz",
                SilenceReason.FalsoPositivo, null, 108),
            ("P-4", "numeros magicos en las constantes de layout de las vistas",
                SilenceReason.DeudaAceptada, -3, 5),
        };

        int i = 0;
        foreach (var s in seeds)
        {
            f.Hub.Store.WritePatternSilence(f.Slug, new PatternSilence
            {
                Id = f.Ulids.NewUlid(),
                ShortId = s.Short,
                Exemplar = s.Exemplar,
                Reason = s.Reason,
                Notes = i == 0 ? "Acordado con el equipo en la revision de arquitectura." : null,
                By = "alciller88",
                Utc = now.AddDays(-30 + i),
                ExpiresUtc = s.ExpiresDays is { } d ? now.AddDays(d) : null,
                Suppressions = s.Hits,
                LastSuppressionUtc = now.AddDays(-2),
            });
            i++;
        }

        var vm = new PatternSilencesViewModel(
            f.Hub, new GovernanceService(f.Hub, f.Ulids), new ToastCenter());
        vm.Load(f.Slug);
        return new PatternSilencesDialog(vm);
    }

    /// <summary>La confirmacion de borrado, con el boton rojo TODAVIA deshabilitado: es su estado inicial.</summary>
    private static Window DeleteApp()
        => new DeleteAppDialog(new DeleteAppConfirmation(
            new AppDeletionImpact("atalayabanco", "atalayabanco-app-for-tests", 41, 6, 6, 4)));

    private static Window FactoryReset()
        => new FactoryResetDialog(new FactoryResetConfirmation(new FactoryResetImpact(3, 128, 19)));

    /// <summary>Y el lanzamiento, que es la otra confirmacion con consecuencias: gasta creditos.</summary>
    private static Window AuditLaunch()
        => new AuditLaunchDialog(new AuditLaunchConfirmation(
            "atalayabanco-app-for-tests",
            new CostEstimate(
                Units: 47, MaxPasses: 6, CostPerUnit: 18.4m, Total: 864.8m,
                CostUnit: "unidades SDK", SampleUnits: 22, SampleSessions: 4,
                ObservedMaxPasses: 5, PassFactor: 1.2m, Evidence: CostEvidence.Suficiente),
            providerName: "GitHub Copilot",
            modelName: "claude-opus-4.7"));

    /// <summary>El selector de carpeta no se abre en un banco: nadie va a pulsar «Examinar…».</summary>
    private sealed class NoPicker : IFolderPicker
    {
        public string? Pick(string title, string? initialDirectory = null) => null;
    }
}
