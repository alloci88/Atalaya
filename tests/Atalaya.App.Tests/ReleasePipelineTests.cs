using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// F11 — que el ritual de publicación siga produciendo lo que la actualización necesita.
/// <para>
/// La app se niega a instalar un paquete sin checksum, y no aparece el botón en una instalación
/// sin relevo. Las dos cosas las produce el workflow, que <b>no se puede ejecutar desde aquí</b>
/// (Actions solo corre en GitHub): lo que sí se puede es afirmar que sus pasos siguen ahí, para
/// que quitarlos rompa un test en vez de romper la actualización de todo el equipo tres semanas
/// después.
/// </para>
/// </summary>
public sealed class ReleasePipelineTests
{
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

    private static string Workflow()
        => File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "release.yml"));

    [Fact]
    public void El_workflow_publica_el_checksum_junto_al_zip()
    {
        string yaml = Workflow();

        yaml.Should().Contain("Get-FileHash", "el checksum se calcula en el propio workflow");
        yaml.Should().Contain("SHA256");
        yaml.Should().Contain("$zip.sha256", "el nombre que la app busca es el del zip más .sha256");
        yaml.Should().Contain("$env:SUM", "y se adjunta a la Release, no solo se calcula");
    }

    /// <summary>
    /// El sufijo que la app busca y el que el workflow escribe tienen que ser el mismo. Es la
    /// clase de acuerdo que se rompe en silencio: la Release sale bien y el botón deja de
    /// funcionar sin que nada falle.
    /// </summary>
    [Fact]
    public void El_sufijo_del_checksum_es_el_mismo_en_los_dos_lados()
    {
        SelfUpdateService.ChecksumSuffix.Should().Be(".sha256");
        Workflow().Should().Contain($"$zip{SelfUpdateService.ChecksumSuffix}");
    }

    [Fact]
    public void El_workflow_empaqueta_el_relevo_de_actualizacion()
    {
        string yaml = Workflow();

        yaml.Should().Contain("Atalaya.Updater/Atalaya.Updater.csproj");
        yaml.Should().Contain("PublishSingleFile=true", "el relevo se copia SOLO y tiene que bastarse");
        yaml.Should().Contain("--self-contained true");
        yaml.Should().Contain($"dist/{SelfUpdateService.RunnerExe}", "y se comprueba que de verdad viaja");
    }

    /// <summary>
    /// El paquete que sale de una Release lleva el actualizador apuntando al MISMO repositorio
    /// que la publica.
    /// <para>
    /// Es de los acuerdos que se rompen en silencio: la Release sale perfecta y el zip que la
    /// gente se descarga busca sus versiones nuevas en otro sitio, cosa que no se ve hasta que
    /// alguien pulsa «Actualizar» semanas después. El workflow lo comprueba antes de comprimir;
    /// esto comprueba que lo sigue comprobando.
    /// </para>
    /// </summary>
    [Fact]
    public void El_workflow_exige_que_el_paquete_apunte_al_repositorio_que_lo_publica()
    {
        string yaml = Workflow();

        yaml.Should().Contain("$cfg.appRepoUrl", "se mira el appRepoUrl EMPAQUETADO");
        yaml.Should().Contain("https://github.com/${{ github.repository }}",
            "y se compara con el repositorio que está publicando");
        yaml.Should().Contain("throw \"El paquete dice", "y un desajuste tumba la publicación");
    }

    /// <summary>
    /// Un publish local tiene que producir la MISMA forma de carpeta que una Release. Si no, la
    /// única manera de probar la actualización sería publicando de verdad.
    /// </summary>
    [Fact]
    public void El_publish_local_tambien_deja_el_relevo()
    {
        string script = File.ReadAllText(Path.Combine(RepoRoot(), "scripts", "publish.ps1"));

        script.Should().Contain("Atalaya.Updater/Atalaya.Updater.csproj");
        script.Should().Contain("PublishSingleFile=true");
    }

    /// <summary>
    /// BUGFIX-RELEASE §3 — el log de tests sube <b>pase o falle</b>.
    /// <para>
    /// R1 vio caer un test intermitente en el runner y no pudo ponerle nombre: nadie guardaba el
    /// log del run. Se quedó en el backlog como «un test intermitente bajo carga» y volvió a costar
    /// cinco publicaciones. Es la clase de cosa que se rompe en silencio —quitar el paso no pone
    /// rojo nada, y el precio se paga meses después, el día que hace falta el log y no está—, así
    /// que se afirma aquí. Lo que importa es el `if: always()`: el run que hay que poder leer es
    /// justo el que ha fallado.
    /// </para>
    /// </summary>
    [Fact]
    public void El_log_de_tests_se_guarda_aunque_los_tests_fallen()
    {
        string yaml = Workflow();

        yaml.Should().Contain("--logger", "sin logger no hay .trx que subir");
        yaml.Should().Contain("trx", "el formato que trae el nombre del test y su pila");
        yaml.Should().Contain("artifacts/tests", "y un sitio conocido del que recogerlo");
        yaml.Should().Contain("if: always()",
            "el run que hay que poder leer es el que ha fallado, no el que ha ido bien");
    }


    // ================================ BUGFIX-RELEASE-2: y además, que sea PowerShell válido

    /// <summary>
    /// <b>Cada bloque <c>run:</c> del workflow es PowerShell que PARSEA</b> (BUGFIX-RELEASE-2).
    /// <para>
    /// Lo medido: el paso «Comprobar el despliegue empaquetado» llevaba
    /// <c>«…y lo publica $esperado: el actu…»</c> y PowerShell no lo aceptaba —<i>Variable
    /// reference is not valid. ':' was not followed by a valid variable name character</i>—,
    /// porque <c>$nombre:</c> es la forma de nombrar un ámbito o una unidad (<c>$env:RUTA</c>) y
    /// no una variable seguida de dos puntos. El paso <b>ni siquiera llegaba a ejecutarse</b>:
    /// reventaba al leerlo, con la publicación ya montada.
    /// </para>
    /// <para>
    /// <b>Y por qué el test que vigilaba ese paso no lo vio</b>: comprobaba su <b>texto</b>, y el
    /// texto seguía entero. Un guion puede decir exactamente lo que tiene que decir y no
    /// compilar. Así que aquí se parsea de verdad, con el parser de PowerShell y <b>sin ejecutar
    /// nada</b>: <c>Parser::ParseFile</c> devuelve el árbol y los errores de sintaxis, y ni un
    /// solo comando corre.
    /// </para>
    /// <para>
    /// Se miran <b>todos</b> los <c>run:</c> y no solo los que declaran <c>shell: pwsh</c>: el
    /// job corre en <c>windows-latest</c>, donde el intérprete por defecto de un <c>run:</c>
    /// también es PowerShell. Y las expresiones <c>${{ … }}</c> se sustituyen antes de parsear,
    /// que es lo que hace GitHub — el shell nunca las ve—: parsearlas dentro sería probar otro
    /// guion distinto del que se ejecuta.
    /// </para>
    /// </summary>
    [Fact]
    public void Cada_bloque_run_del_workflow_es_PowerShell_valido()
    {
        IReadOnlyList<WorkflowScript> blocks = PowerShellBlocks();

        blocks.Count.Should().BeGreaterThan(7,
            "si dejaran de encontrarse bloques, este test estaría en verde sin mirar nada");
        blocks.Select(b => b.Step).Should().Contain("Comprobar el despliegue empaquetado",
            "el paso que se rompió es el primero que hay que estar mirando");

        SyntaxErrors(blocks).Should().BeEmpty(
            "un run: que no parsea no llega a ejecutarse: el workflow muere en el runner");
    }

    /// <summary>
    /// <b>El cebo, escrito como test</b>: el defecto exacto que tumbó la publicación tiene que
    /// salir roto, y su versión corregida limpia. Sin esto, un extractor que no encontrara nada
    /// —o un parser que se tragara los errores— dejaría el test de arriba en verde para siempre.
    /// </summary>
    [Fact]
    public void El_defecto_que_tumbo_la_publicacion_lo_caza_el_parser()
    {
        var cebo = new WorkflowScript("cebo", "throw \"y lo publica $esperado: el resto\"");
        var sano = new WorkflowScript("sano", "throw \"y lo publica ${esperado}: el resto\"");

        SyntaxErrors(new[] { cebo }).Should().ContainSingle(
            "«$esperado:» no es una referencia de variable válida")
            .Which.Should().Contain("cebo");

        SyntaxErrors(new[] { sano }).Should().BeEmpty("y «${esperado}:» sí lo es");
    }

    // --------------------------------------------- de dónde salen los guiones, y quién los parsea

    /// <summary>Un <c>run:</c> del workflow: de qué paso es, y qué se le manda al shell.</summary>
    public sealed record WorkflowScript(string Step, string Script);

    /// <summary>
    /// Los <c>run:</c> del workflow, con el paso al que pertenecen. Se lee a mano y no con un
    /// parser de YAML —no hay ninguno en esta casa, y este fichero lo escribimos nosotros—: un
    /// paso empieza en <c>- name:</c>, y un bloque <c>run:</c> es lo que viene detrás sangrado por
    /// debajo de su clave, ya sea literal (<c>|</c>) o plegado (<c>&gt;</c>, que junta las líneas
    /// con un espacio, igual que hace YAML).
    /// </summary>
    private static IReadOnlyList<WorkflowScript> PowerShellBlocks()
    {
        string[] lines = File.ReadAllLines(WorkflowPath());
        var blocks = new List<WorkflowScript>();
        string step = "(sin nombre)";

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("- name:", StringComparison.Ordinal))
            {
                step = trimmed["- name:".Length..].Trim();
                continue;
            }

            if (!trimmed.StartsWith("run:", StringComparison.Ordinal))
            {
                continue;
            }

            int indent = line.Length - trimmed.Length;
            string rest = trimmed["run:".Length..].Trim();

            // `run: dotnet restore …` en una línea: el guion es esa línea y no hay más.
            if (rest.Length > 0 && rest != "|" && rest != ">")
            {
                blocks.Add(new WorkflowScript(step, Expand(rest)));
                continue;
            }

            var body = new List<string>();
            int j = i + 1;
            for (; j < lines.Length; j++)
            {
                if (lines[j].Trim().Length == 0)
                {
                    body.Add(string.Empty);
                    continue;
                }

                if (lines[j].Length - lines[j].TrimStart().Length <= indent)
                {
                    break;
                }

                body.Add(lines[j]);
            }

            i = j - 1;
            while (body.Count > 0 && body[^1].Length == 0)
            {
                body.RemoveAt(body.Count - 1);
            }

            string script = rest == ">"
                ? string.Join(" ", body.Select(b => b.Trim()))
                : Dedent(body);

            if (script.Trim().Length > 0)
            {
                blocks.Add(new WorkflowScript(step, Expand(script)));
            }
        }

        return blocks;
    }

    /// <summary>El sangrado del bloque literal se quita, que es lo que hace YAML al leerlo.</summary>
    private static string Dedent(IReadOnlyList<string> body)
    {
        int margin = body.Where(b => b.Length > 0)
            .Select(b => b.Length - b.TrimStart().Length)
            .DefaultIfEmpty(0)
            .Min();

        return string.Join("\n", body.Select(b => b.Length > margin ? b[margin..] : string.Empty));
    }

    /// <summary>
    /// Las expresiones de GitHub se sustituyen ANTES de que el shell vea nada, así que aquí
    /// también: se cambian por un valor cualquiera. Lo que se parsea es el guion que se ejecuta.
    /// </summary>
    private static string Expand(string script)
        => System.Text.RegularExpressions.Regex.Replace(script, @"\$\{\{[^}]*\}\}", "valor");

    /// <summary>
    /// Los errores de sintaxis de cada guion, en <b>una sola</b> llamada al intérprete: se
    /// escriben a disco con extensión <c>.ps1</c> y PowerShell los parsea todos de una pasada.
    /// <para>
    /// <b>Parsear no ejecuta.</b> <c>ParseFile</c> devuelve el árbol y la lista de errores; ni un
    /// comando del workflow llega a correr, que es lo que hace que este test se pueda tener.
    /// </para>
    /// <para>
    /// Se prefiere <c>pwsh</c> —el shell que declara el workflow y el que hay en el runner— y se
    /// cae a <c>powershell</c> donde no esté: el parser es el mismo para esta clase de error.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> SyntaxErrors(IReadOnlyList<WorkflowScript> blocks)
    {
        string dir = Path.Combine(Path.GetTempPath(), "atalaya-wf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                string name = i.ToString("00");
                byName[name] = blocks[i].Step;
                File.WriteAllText(
                    Path.Combine(dir, name + ".ps1"), blocks[i].Script,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }

            // Todo con comillas simples por dentro: lo que sale de aquí viaja como UN argumento.
            string command =
                "Get-ChildItem -LiteralPath '" + dir.Replace("'", "''") + "' -Filter *.ps1 | "
                + "Sort-Object Name | ForEach-Object { "
                + "$n = $_.BaseName; $err = $null; "
                + "$null = [System.Management.Automation.Language.Parser]::ParseFile("
                + "$_.FullName, [ref]$null, [ref]$err); "
                + "foreach ($e in $err) { $n + '|' + $e.Extent.StartLineNumber + '|' + $e.Message } }";

            return Parse(command).Replace("\r\n", "\n").Split('\n')
                .Where(l => l.Contains('|'))
                .Select(l =>
                {
                    string[] parts = l.Split('|', 3);
                    string paso = byName.TryGetValue(parts[0].Trim(), out string? name)
                        ? name : parts[0];
                    return $"«{paso}» línea {parts[1]}: {parts[2].Trim()}";
                })
                .ToList();
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Un temporal que no se deja borrar no invalida lo que ya se ha leído.
            }
        }
    }

    /// <summary>Lanza el intérprete a parsear y devuelve lo que escribió.</summary>
    private static string Parse(string command)
    {
        foreach (string shell in new[] { "pwsh", "powershell" })
        {
            var info = new System.Diagnostics.ProcessStartInfo(shell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("-NoProfile");
            info.ArgumentList.Add("-NonInteractive");
            info.ArgumentList.Add("-Command");
            info.ArgumentList.Add(command);

            System.Diagnostics.Process? proc;
            try
            {
                proc = System.Diagnostics.Process.Start(info);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                continue;   // Ese intérprete no está en esta máquina: se prueba el siguiente.
            }

            using (proc)
            {
                proc.Should().NotBeNull();
                string salida = proc!.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                proc.WaitForExit(60_000).Should().BeTrue("parsear no puede tardar un minuto");
                return salida;
            }
        }

        throw new InvalidOperationException(
            "No hay ni pwsh ni powershell en esta máquina: el workflow no se puede parsear.");
    }

    private static string WorkflowPath()
        => Path.Combine(RepoRoot(), ".github", "workflows", "release.yml");

    /// <summary>El botón vive en el aviso de versión, con su progreso y su explicación.</summary>
    [Fact]
    public void El_aviso_de_version_ofrece_el_boton_de_actualizar()
    {
        string xaml = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Atalaya.App", "MainWindow.xaml"));

        xaml.Should().Contain("InstallUpdateCommand");
        xaml.Should().Contain("InstallUpdateLabel");
        xaml.Should().Contain("CanInstallUpdate", "el botón se esconde cuando no se puede");
        xaml.Should().Contain("UpdateNotice", "y se dice por qué");
        xaml.Should().Contain("UpdateProgressText", "quien pulsó ve lo que pasa");
    }
}
