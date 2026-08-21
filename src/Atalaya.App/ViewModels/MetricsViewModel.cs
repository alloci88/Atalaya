using System.Collections.ObjectModel;
using Atalaya.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.ViewModels;

/// <summary>A burndown bar (new vs resolved) with pixel heights precomputed for a pure-WPF chart.</summary>
public sealed record BurndownBar(string Label, double NewHeight, double ResolvedHeight, int New, int Resolved);

/// <summary>V6 Histórico y métricas (§8): computed metrics and a burndown chart.</summary>
public sealed partial class MetricsViewModel : ViewModelBase
{
    private const double MaxBarHeight = 90;
    private readonly MetricsQuery _metrics;

    public MetricsViewModel(MetricsQuery metrics) => _metrics = metrics;

    public override string Title => "Métricas";

    public ObservableCollection<BurndownBar> Burndown { get; } = new();
    public ObservableCollection<AppActive> PerApp { get; } = new();

    [ObservableProperty] private MetricsSummary? _summary;
    [ObservableProperty] private bool _isEmpty;

    public override async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            MetricsSummary summary = await Task.Run(() => _metrics.Build());
            Summary = summary;

            int max = Math.Max(1, summary.Burndown.Max(b => Math.Max(b.New, b.Resolved)));
            Burndown.Clear();
            foreach (WeekBucket b in summary.Burndown)
            {
                Burndown.Add(new BurndownBar(
                    b.Label, MaxBarHeight * b.New / max, MaxBarHeight * b.Resolved / max, b.New, b.Resolved));
            }

            PerApp.Clear();
            foreach (AppActive a in summary.PerApp)
            {
                PerApp.Add(a);
            }

            IsEmpty = summary.ActiveTotal == 0 && summary.Resolved == 0;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
