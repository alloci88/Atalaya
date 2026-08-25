using Atalaya.Domain;
using Atalaya.Domain.Ingestion;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Inventory.Tests;

public class ScannerTests
{
    private static AppConfig Config(TechStack stack = TechStack.Unknown, int largeLoc = 1500)
        => new()
        {
            Slug = "app",
            Name = "App",
            RepoUrl = "u",
            Stack = stack,
            Thresholds = new Thresholds { LargeUnitLoc = largeLoc },
        };

    [Fact]
    public void Detects_dotnet_stack_and_enumerates_cs_units()
    {
        using var repo = new SyntheticRepo();
        repo.File("src/App/App.csproj", "<Project/>")
            .File("src/App/Program.cs", "class P {}")
            .File("src/App/Service.cs", "class S {}")
            .File("src/App/obj/Generated.cs", "// build output")
            .File("src/App/Widget.Designer.cs", "// designer");

        ScanOutput output = new InventoryScanner().Scan(repo.Root, Config(), 1);

        output.Stack.Should().Be(TechStack.DotNet);
        output.Inventory.Units.Select(u => u.Path)
            .Should().BeEquivalentTo("src/App/Program.cs", "src/App/Service.cs");
        output.Inventory.Units.Should().OnlyContain(u => u.Module == "App");
    }

    [Fact]
    public void Excludes_tests_and_build_dirs()
    {
        using var repo = new SyntheticRepo();
        repo.File("pkg/package.json", "{}")
            .File("pkg/tsconfig.json", "{}")
            .File("pkg/index.ts", "export const x = 1;")
            .File("pkg/node_modules/dep/index.ts", "junk")
            .File("pkg/__tests__/index.test.ts", "test")
            .File("pkg/types.d.ts", "declare const y: number;");

        ScanOutput output = new InventoryScanner().Scan(repo.Root, Config(), 1);

        output.Stack.Should().Be(TechStack.TypeScript);
        output.Inventory.Units.Select(u => u.Path).Should().BeEquivalentTo("pkg/index.ts");
    }

    [Fact]
    public void Marks_large_units_and_emits_stable_refactor_finding()
    {
        using var repo = new SyntheticRepo();
        repo.File("src/App/App.csproj", "<Project/>")
            .Lines("src/App/Huge.cs", 2000);

        ScanOutput output = new InventoryScanner().Scan(repo.Root, Config(largeLoc: 1500), 1);

        InventoryUnit huge = output.Inventory.Units.Single();
        huge.State.Should().Be(UnitState.Grande);
        huge.Loc.Should().Be(2000);

        output.LargeUnitFindings.Should().ContainSingle();
        SubmittedFinding f = output.LargeUnitFindings[0];
        f.Pillar.Should().Be(Pillar.Mejoras);
        f.Tag.Should().Be(FindingTag.Checklist);
        f.Severity.Should().Be(Severity.Media);
    }

    /// <summary>
    /// El título del hallazgo "unidad grande" NO puede llevar el LOC: es lo que el auditor ve en
    /// la lista de existentes, y si cambiara cada vez que el fichero crece no podría reconocerlo
    /// como el mismo problema entre sesiones (F4). El LOC vive en la descripción.
    /// </summary>
    [Fact]
    public void Large_unit_title_is_stable_as_loc_grows()
    {
        var thresholds = new Thresholds { LargeUnitLoc = 1500 };
        SubmittedFinding a = InventoryScanner.BuildLargeUnitFinding("src/Huge.cs", 2000, thresholds);
        SubmittedFinding b = InventoryScanner.BuildLargeUnitFinding("src/Huge.cs", 5000, thresholds);

        b.Title.Should().Be(a.Title);
        b.RuleId.Should().Be(a.RuleId);
        b.PrimaryPath.Should().Be(a.PrimaryPath);
        b.Description.Should().NotBe(a.Description); // el LOC vive aquí
    }

    [Fact]
    public void Content_hash_is_computed_per_unit()
    {
        using var repo = new SyntheticRepo();
        repo.File("go.mod", "module x")
            .File("main.go", "package main")
            .File("util_test.go", "package main");

        ScanOutput output = new InventoryScanner().Scan(repo.Root, Config(), 1);

        output.Stack.Should().Be(TechStack.Go);
        InventoryUnit unit = output.Inventory.Units.Should().ContainSingle().Subject;
        unit.Path.Should().Be("main.go"); // _test.go excluded
        unit.ContentHash.Should().StartWith("sha256:");
    }
}
