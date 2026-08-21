using System.Collections.ObjectModel;
using Atalaya.Domain;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Atalaya.App.ViewModels;

/// <summary>A selectable unit row in the V2 tree.</summary>
public sealed partial class UnitNode : ObservableObject
{
    public required string Path { get; init; }
    public required string Module { get; init; }
    public int Loc { get; init; }
    public UnitState State { get; init; }
    public string? ClaimedBy { get; init; }

    [ObservableProperty]
    private bool _isSelected;

    public string FileName => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;

    public string StateLabel => State switch
    {
        UnitState.Auditada => "Auditada",
        UnitState.Grande => "Grande",
        _ => "Pendiente",
    };
}

/// <summary>A module group in the V2 tree.</summary>
public sealed class ModuleNode
{
    public required string Name { get; init; }

    public ObservableCollection<UnitNode> Units { get; } = new();

    public int Total => Units.Count;

    public int Audited => Units.Count(u => u.State == UnitState.Auditada);

    public string Header => $"{Name}  ({Audited}/{Total})";
}
