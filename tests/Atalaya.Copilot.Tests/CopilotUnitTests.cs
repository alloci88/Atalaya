using Atalaya.Copilot;
using Atalaya.Domain;
using Atalaya.Domain.Model;
using FluentAssertions;
using Xunit;

namespace Atalaya.Copilot.Tests;

public class RuleCatalogTests
{
    [Fact]
    public void Catalog_rules_are_valid()
    {
        RuleCatalog.IsValid("errores.recursos.no-liberado").Should().BeTrue();
        RuleCatalog.IsValid("mejoras.mantenibilidad.unidad-grande").Should().BeTrue();
    }

    [Fact]
    public void Any_criterio_area_is_valid()
    {
        RuleCatalog.IsValid("criterio.arquitectura").Should().BeTrue();
        RuleCatalog.IsValid("criterio.lo-que-sea").Should().BeTrue();
    }

    [Fact]
    public void Unknown_rule_is_invalid()
    {
        RuleCatalog.IsValid("errores.inventado.xyz").Should().BeFalse();
    }
}

public class PromptComposerTests
{
    [Fact]
    public void Unit_prompt_includes_content_and_rules()
    {
        string brief = PillarBrief.For(TechStack.DotNet);
        string prompt = PromptComposer.ComposeUnitPrompt("src/A.cs", "class A {}", brief, AuditMode.Lotes);

        prompt.Should().Contain("submit_finding");
        prompt.Should().Contain("class A {}");
        prompt.Should().Contain("errores.recursos.no-liberado"); // catalog embedded in brief
    }

    [Fact]
    public void Brief_contains_severity_rubric()
    {
        PillarBrief.For(TechStack.Go).Should().Contain("RÚBRICA DE SEVERIDAD");
    }
}

public class FakeAgentTests
{
    [Fact]
    public async Task Fake_reports_scripted_findings_and_calls_unit_done()
    {
        var toolbox = new RecordingToolbox();
        var fake = new FakeCopilotAgent(auditScript: _ => new[]
        {
            new SubmitFindingArgs("errores.null.desreferencia", "errores", "checklist", "alta",
                "NPE", "d", "i", "r", new[] { new SubmitLocation("a.cs", 5, null) }, null),
        });

        await fake.AuditUnitAsync(new AuditUnitRequest("a.cs", "code", "prompt", TechStack.DotNet, AuditMode.Lotes),
            toolbox, CancellationToken.None);

        toolbox.Submitted.Should().ContainSingle();
        toolbox.UnitDoneCalled.Should().BeTrue();
    }

    private sealed class RecordingToolbox : IAuditToolbox
    {
        public List<SubmitFindingArgs> Submitted { get; } = new();
        public bool UnitDoneCalled { get; private set; }

        public SubmitFindingResult SubmitFinding(SubmitFindingArgs args)
        {
            Submitted.Add(args);
            return new SubmitFindingResult(true);
        }

        public void UnitDone(string unitPath, string summary) => UnitDoneCalled = true;

        public string ReadSignatures(string path) => "";
    }
}
