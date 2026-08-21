using System.Collections.ObjectModel;
using System.Text;
using Atalaya.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Atalaya.App.ViewModels;

/// <summary>Import wizard (§9): point at a v4 CodeAudit/ folder and migrate it into the hub.</summary>
public sealed partial class ImportViewModel : ViewModelBase
{
    private readonly ImportService _import;

    public ImportViewModel(ImportService import) => _import = import;

    public override string Title => "Importar v4";

    [ObservableProperty] private string _appName = string.Empty;
    [ObservableProperty] private string _repoUrl = string.Empty;
    [ObservableProperty] private string _codeAuditPath = string.Empty;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public ObservableCollection<string> Log { get; } = new();

    [RelayCommand]
    private async Task Import()
    {
        if (string.IsNullOrWhiteSpace(AppName) || !Directory.Exists(CodeAuditPath))
        {
            StatusMessage = "Indica el nombre de la app y una ruta CodeAudit/ válida.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Importando…";
        Log.Clear();
        try
        {
            string slug = Slugify(AppName);
            var log = await Task.Run(() => _import.Import(slug, AppName.Trim(), RepoUrl.Trim(), CodeAuditPath));
            foreach (string line in log)
            {
                Log.Add(line);
            }

            StatusMessage = "Importación completada.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Slugify(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        string slug = sb.ToString().Trim('-');
        return slug.Length == 0 ? "importado" : slug;
    }
}
