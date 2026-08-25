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
    public async Task Fake_is_always_ready()
    {
        AgentReadiness readiness = await new FakeCopilotAgent().CheckAsync(CancellationToken.None);
        readiness.Ready.Should().BeTrue();
    }

    [Fact]
    public void Auth_help_text_names_the_copilot_login_step()
    {
        CopilotHelp.NotAuthenticated.Should().Contain("copilot");
        CopilotHelp.NotAuthenticated.Should().Contain("/login");
        new CopilotAuthenticationException(CopilotHelp.NotAuthenticated).Message.Should().Contain("autenticado");
    }

    [Fact]
    public async Task Fake_reports_scripted_findings_and_calls_unit_done()
    {
        var toolbox = new RecordingToolbox();
        var fake = new FakeCopilotAgent(auditScript: _ => new[]
        {
            new SubmitFindingArgs("errores.null.desreferencia", "errores", "alta",
                "NPE", "d", "i", "r", new[] { new SubmitLocation("a.cs", 5, null) }, null),
        });

        await fake.AuditUnitAsync(
            new AuditUnitRequest("a.cs", "code", "prompt", TechStack.DotNet, AuditMode.Lotes, Array.Empty<ExistingFinding>()),
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

        public SubmitFindingsResult SubmitFindings(SubmitFindingArgs[] findings)
        {
            var results = new List<SubmitFindingResult>(findings.Length);
            foreach (SubmitFindingArgs a in findings)
            {
                results.Add(SubmitFinding(a));
            }
            return new SubmitFindingsResult(results);
        }

        public List<VerdictArgs> Verdicts { get; } = new();

        public ReportVerdictsResult ReportVerdicts(VerdictArgs[] verdicts)
        {
            Verdicts.AddRange(verdicts);
            return new ReportVerdictsResult(verdicts.Select(_ => new ReportVerdictResult(true)).ToList());
        }

        public List<AddLocationsArgs> Extensions { get; } = new();

        public AddLocationsResult AddLocations(string findingId, SubmitLocation[] locations)
        {
            Extensions.Add(new AddLocationsArgs(findingId, locations));
            return new AddLocationsResult(true, locations.Length);
        }

        public void UnitDone(string unitPath, string summary) => UnitDoneCalled = true;

        public string ReadSignatures(string path) => "";
    }
}

/// <summary>
/// The F2 token/CLI pipeline: the runtime is always the bundled CLI, and the three not-ready
/// causes stay distinguishable so the UI can show a specific remedy.
/// </summary>
public class BundledCliTests
{
    [Fact]
    public void Bundled_cli_is_deployed_with_the_package()
    {
        string? path = CopilotCliLocator.ResolveBundled();

        path.Should().NotBeNull("el paquete del SDK despliega el CLI en runtimes/{rid}/native");
        File.Exists(path).Should().BeTrue();
        Path.GetFileName(path).Should().Be(CopilotCliLocator.BinaryName);
    }

    [Fact]
    public void Missing_bundle_is_null_rather_than_a_PATH_lookup()
    {
        string empty = Path.Combine(Path.GetTempPath(), "atalaya-nocli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            CopilotCliLocator.ResolveBundled(empty).Should().BeNull();
        }
        finally
        {
            Directory.Delete(empty, true);
        }
    }

    [Fact]
    public void Runtime_identifier_matches_the_sdk_output_layout()
        => CopilotCliLocator.RuntimeIdentifier().Should().MatchRegex("^(win|linux|osx)-(x64|arm64)$");

    [Fact]
    public void Readiness_defaults_to_no_problem_when_ready()
        => new AgentReadiness(true, "ok").Problem.Should().Be(AgentProblem.None);

    [Fact]
    public void Each_not_ready_cause_has_its_own_text()
    {
        CopilotHelp.NoAccount.Should().Contain("Conectar con GitHub");
        CopilotHelp.NoSeat.Should().Contain("asiento");
        CopilotHelp.TokenRejected.Should().Contain("vuelve a conectar");
        // The pre-F2 CLI fallback text is kept: existing machines still get their instructions.
        CopilotHelp.NotAuthenticated.Should().Contain("/login");
    }
}
