using Atalaya.Domain.Hashing;
using Atalaya.Domain.Model;
using Atalaya.Inventory;
using Atalaya.Storage.Sync;
using LibGit2Sharp;

namespace Atalaya.App.Services;

/// <summary>
/// Si esta máquina puede auditar una app del portafolio (F5.8 §1). Son TRES estados, no dos:
/// entre «lo tengo» y «no lo tengo» hay un caso real y frecuente —la carpeta se movió, se borró,
/// o se reutilizó para otro repo— que se arregla de otra manera y por eso se dice de otra manera.
/// </summary>
public enum CloneLinkState
{
    /// <summary>🟢 Hay clon, la ruta existe y su remoto es el de la app. Se puede auditar.</summary>
    Vinculada,

    /// <summary>🔴 No hay clon registrado en esta máquina. Solo lectura.</summary>
    SinVincular,

    /// <summary>🟡 Hay ruta registrada, pero lo que hay al final de ella ya no sirve.</summary>
    Problema,
}

/// <summary>
/// El estado de vinculación local de UNA app, con lo que hay que enseñar de él.
/// <para>
/// Todo el texto vive aquí y no en el XAML por la razón de siempre: el piloto aparece en la
/// tarjeta del portafolio y el mismo estado gobierna el modo solo-lectura del inventario. Dos
/// sitios, un texto.
/// </para>
/// </summary>
/// <param name="Slug">La app.</param>
/// <param name="State">Verde, rojo o ámbar.</param>
/// <param name="Path">La ruta registrada en <c>machines.json</c>, si la hay.</param>
/// <param name="Problem">Qué falla, cuando el estado es <see cref="CloneLinkState.Problema"/>.</param>
public sealed record CloneLink(
    string Slug,
    CloneLinkState State,
    string? Path,
    string? Problem)
{
    /// <summary>Lo que se sabe de una app cuya vinculación aún no se ha mirado.</summary>
    public static CloneLink Unknown(string slug) => new(slug, CloneLinkState.SinVincular, null, null);

    /// <summary>Auditar necesita el código delante. Es la única puerta.</summary>
    public bool CanAudit => State == CloneLinkState.Vinculada;

    /// <summary>Hay algo que vincular o que reparar: la tarjeta enseña el botón.</summary>
    public bool NeedsAction => State != CloneLinkState.Vinculada;

    /// <summary>
    /// El nombre del estado. Va SIEMPRE junto al piloto: el color es lo que se ve de lejos, pero
    /// quien no distinga verde de rojo tiene que poder leerlo — y «vinculada con problema» no se
    /// deduce de un ámbar.
    /// </summary>
    public string Label => State switch
    {
        CloneLinkState.Vinculada => "Vinculada",
        CloneLinkState.Problema => "Vinculada con problema",
        _ => "Sin vincular",
    };

    /// <summary>El botón que apaga el piloto. Reparar y vincular NO son el mismo gesto.</summary>
    public string ActionLabel => State == CloneLinkState.Problema
        ? "Reparar vínculo…"
        : "Vincular clon local…";

    /// <summary>
    /// Qué pasa y qué hacer, en los TRES casos. Un piloto verde sin explicación tampoco es obvio
    /// la primera vez que se ve.
    /// </summary>
    public string Tooltip => State switch
    {
        CloneLinkState.Vinculada =>
            $"Tu clon local está en {Path}. Puedes auditar esta aplicación desde esta máquina.",
        // El salto de línea: el diagnóstico del caso ámbar puede terminar en las dos URLs, una por
        // renglón, y sin él la frase siguiente se lee como parte de la última.
        CloneLinkState.Problema =>
            $"{Problem}\nUsa «Reparar vínculo…» para apuntar a la carpeta correcta o volver a clonarla. "
            + "Mientras tanto puedes ver hallazgos, métricas e informes, pero no auditar.",
        _ =>
            "No tienes un clon de esta aplicación en esta máquina. Puedes ver hallazgos, métricas e "
            + "informes, pero para auditar hace falta el código: usa «Vincular clon local…».",
    };

    /// <summary>Lo que se lee donde una acción de auditar está gris (F5.8 §3).</summary>
    public string DisabledActionTooltip => State == CloneLinkState.Problema
        ? "Repara el vínculo con tu clon local para auditar."
        : "Vincula tu clon local para auditar.";
}

/// <summary>Por qué NO se puede vincular una carpeta, con las dos URLs delante (N-2).</summary>
/// <param name="Ok">True si la carpeta sirve.</param>
/// <param name="Error">El motivo exacto, cuando no sirve.</param>
public sealed record CloneValidation(bool Ok, string? Error)
{
    public static CloneValidation Valid { get; } = new(true, null);

    public static CloneValidation Invalid(string error) => new(false, error);
}

/// <summary>
/// Cuánto se ha movido el clon respecto del inventario vigente (F5.8 §2). Se mide por
/// <c>contentHash</c>, que es lo que el inventario ya guarda de cada unidad, y no por el commit:
/// dos commits distintos con el mismo contenido de fuente no cambian nada de lo auditado.
/// </summary>
/// <param name="Compared">Unidades del inventario que se pudieron comparar.</param>
/// <param name="Differing">Cuántas no coinciden con lo que hay en el clon (las que faltan incluidas).</param>
public sealed record InventoryDrift(int Compared, int Differing)
{
    public static InventoryDrift None { get; } = new(0, 0);

    public bool HasDrift => Differing > 0;

    public string Message =>
        $"Tu clon está en un commit distinto al del último inventario: {Differing} de {Compared} "
        + "unidades no coinciden. Re-escanear pone el inventario al día con lo que hay en tu clon.";
}

/// <summary>
/// Quién sabe si esta máquina tiene el clon de una app, y quién lo vincula (F5.8).
/// <para>
/// <b>Por qué es un servicio y no una comprobación suelta en la tarjeta.</b> «Estar vinculada»
/// se pregunta desde el portafolio (el piloto), desde el inventario (el modo solo-lectura), desde
/// la ficha de un hallazgo (verificar) y desde el asistente de alta (¿esta app ya existe?). Con la
/// comprobación repartida, cada sitio acabaría teniendo su propia idea de qué cuenta como
/// vinculada — y el caso ámbar, que es el que menos se ve venir, sería el primero en perderse.
/// </para>
/// </summary>
public sealed class CloneLinkService
{
    private readonly HubContext _hub;
    private readonly MachineConfigStore _machines;

    public CloneLinkService(HubContext hub, MachineConfigStore machines)
    {
        _hub = hub;
        _machines = machines;
    }

    /// <summary>El estado de una app por su slug. Nunca lanza: es una lectura de pantalla.</summary>
    public CloneLink For(string slug)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        return app is null ? CloneLink.Unknown(slug) : For(app);
    }

    /// <summary>
    /// El estado, con la app ya leída. El orden de las comprobaciones es el orden en que fallan
    /// de verdad: primero si hay ruta, luego si sigue ahí, luego si es un repo, y por último si
    /// es el repo CORRECTO — que es la que nadie mira y la que dejaría auditar el proyecto de al
    /// lado publicando los hallazgos bajo el nombre de éste.
    /// </summary>
    public CloneLink For(AppConfig app)
    {
        string slug = app.Slug;
        string? path = _machines.Load().ClonePathFor(slug);

        if (string.IsNullOrWhiteSpace(path))
        {
            return new CloneLink(slug, CloneLinkState.SinVincular, null, null);
        }

        if (!Directory.Exists(path))
        {
            return new CloneLink(slug, CloneLinkState.Problema, path,
                $"La carpeta registrada ya no existe: {path}.");
        }

        if (!GitInfo.IsRepo(path))
        {
            return new CloneLink(slug, CloneLinkState.Problema, path,
                $"{path} ya no es un repositorio git.");
        }

        // Una app declarada sin URL en el hub no se puede contrastar contra nada. No se inventa
        // un problema donde no hay evidencia de uno.
        if (string.IsNullOrWhiteSpace(app.RepoUrl))
        {
            return new CloneLink(slug, CloneLinkState.Vinculada, path, null);
        }

        string? origin = GitInfo.OriginUrl(path);
        if (origin is null)
        {
            return new CloneLink(slug, CloneLinkState.Problema, path,
                $"{path} no tiene remoto «origin», así que no se puede comprobar que sea el repo de esta aplicación.");
        }

        return RemoteUrl.Same(origin, app.RepoUrl)
            ? new CloneLink(slug, CloneLinkState.Vinculada, path, null)
            : new CloneLink(slug, CloneLinkState.Problema, path, Mismatch(origin, app.RepoUrl));
    }

    /// <summary>
    /// ¿Sirve esta carpeta como clon de esta app? Se comprueba SIEMPRE antes de escribir nada en
    /// <c>machines.json</c>: vincular una carpeta equivocada en silencio es el fallo caro — se
    /// auditaría otro proyecto y sus hallazgos se publicarían bajo el nombre de éste.
    /// </summary>
    public CloneValidation Validate(string slug, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return CloneValidation.Invalid("Elige la carpeta de tu clon local.");
        }

        if (!Directory.Exists(folder))
        {
            return CloneValidation.Invalid($"La carpeta no existe: {folder}");
        }

        if (!GitInfo.IsRepo(folder))
        {
            return CloneValidation.Invalid(
                $"{folder} no es un repositorio git. Elige la carpeta raíz del clon (la que contiene «.git»).");
        }

        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null)
        {
            return CloneValidation.Invalid($"La aplicación «{slug}» ya no está en el hub.");
        }

        if (string.IsNullOrWhiteSpace(app.RepoUrl))
        {
            // Nada contra lo que contrastar: se acepta, pero no se finge que se ha verificado.
            return CloneValidation.Valid;
        }

        string? origin = GitInfo.OriginUrl(folder);
        if (origin is null)
        {
            return CloneValidation.Invalid(
                $"{folder} no tiene remoto «origin», así que no se puede comprobar que sea el repo de «{app.Name}».");
        }

        return RemoteUrl.Same(origin, app.RepoUrl)
            ? CloneValidation.Valid
            : CloneValidation.Invalid(Mismatch(origin, app.RepoUrl));
    }

    /// <summary>Las DOS URLs, siempre. Un «no coincide» a secas no se puede ni discutir ni arreglar.</summary>
    private static string Mismatch(string origin, string expected)
        => "El remoto de esa carpeta no es el de esta aplicación."
           + $"\n· Carpeta elegida → {origin}"
           + $"\n· Repo de la aplicación → {expected}";

    /// <summary>
    /// Registra la ruta en <c>machines.json</c> tras validarla. Devuelve el resultado de la
    /// validación para que quien llama no tenga que volver a preguntarlo.
    /// </summary>
    public CloneValidation Link(string slug, string folder)
    {
        CloneValidation validation = Validate(slug, folder);
        if (!validation.Ok)
        {
            return validation;
        }

        _machines.SetClonePath(slug, Path.GetFullPath(folder));
        return CloneValidation.Valid;
    }

    /// <summary>
    /// Clona el repo de la app dentro de <paramref name="parentDirectory"/> y lo vincula. Usa la
    /// credencial de la cuenta —la misma que el hub (D3)—, así que un repo privado de la
    /// organización se clona sin pedir nada.
    /// </summary>
    /// <returns>La ruta del clon recién creado.</returns>
    public string CloneAndLink(string slug, string? parentDirectory, IProgress<string>? progress = null)
    {
        AppConfig app = _hub.Store.TryReadApp(slug)
                        ?? throw new InvalidOperationException($"La aplicación «{slug}» ya no está en el hub.");

        if (string.IsNullOrWhiteSpace(app.RepoUrl))
        {
            throw new InvalidOperationException(
                $"«{app.Name}» no tiene URL de repositorio en el hub, así que no se puede clonar. "
                + "Usa «Ya tengo el repo clonado».");
        }

        if (string.IsNullOrWhiteSpace(parentDirectory))
        {
            throw new InvalidOperationException("Elige una carpeta destino.");
        }

        string target = Path.Combine(Path.GetFullPath(parentDirectory), FolderName(app));
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            throw new InvalidOperationException(
                $"{target} ya existe y no está vacía. Elige otra carpeta destino, o usa "
                + "«Ya tengo el repo clonado» si ésa es ya tu copia.");
        }

        progress?.Report($"Clonando {app.RepoUrl}…");
        Directory.CreateDirectory(target);

        var options = new CloneOptions();
        if (_hub.BuildCredentials() is { } credentials)
        {
            options.FetchOptions.CredentialsProvider = credentials;
        }

        options.FetchOptions.OnTransferProgress = p =>
        {
            progress?.Report(p.TotalObjects <= 0
                ? "Descargando…"
                : $"Descargando… {PercentText.Of(p.ReceivedObjects, p.TotalObjects)}");
            return true;
        };
        options.OnCheckoutProgress = (_, done, total) =>
            progress?.Report(total <= 0 ? "Extrayendo…" : $"Extrayendo… {PercentText.Of(done, total)}");

        Repository.Clone(app.RepoUrl, target, options);
        progress?.Report("Clon terminado. Vinculando…");

        CloneValidation validation = Link(slug, target);
        if (!validation.Ok)
        {
            // No debería pasar —se acaba de clonar de esa misma URL—, pero si pasa, se dice.
            throw new InvalidOperationException(validation.Error);
        }

        return target;
    }

    /// <summary>El nombre de carpeta que un <c>git clone</c> habría elegido.</summary>
    internal static string FolderName(AppConfig app)
    {
        string normalized = RemoteUrl.Normalize(app.RepoUrl);
        int slash = normalized.LastIndexOf('/');
        string last = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        return last.Length == 0 ? app.Slug : last;
    }

    /// <summary>
    /// Compara el <c>contentHash</c> del inventario vigente contra los ficheros del clon recién
    /// vinculado (F5.8 §2). Las unidades sin hash guardado no se cuentan: no coincidir con nada
    /// no es una discrepancia.
    /// </summary>
    public InventoryDrift Drift(string slug, string? clonePath)
    {
        AppConfig? app = _hub.Store.TryReadApp(slug);
        if (app is null || string.IsNullOrWhiteSpace(clonePath) || !Directory.Exists(clonePath))
        {
            return InventoryDrift.None;
        }

        InventoryCycle? inventory = _hub.Store.TryReadInventory(slug, app.CurrentCycle);
        if (inventory is null)
        {
            return InventoryDrift.None;
        }

        int compared = 0;
        int differing = 0;
        foreach (InventoryUnit unit in inventory.Units)
        {
            if (string.IsNullOrWhiteSpace(unit.ContentHash))
            {
                continue;
            }

            compared++;
            string abs = Path.Combine(clonePath, unit.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs))
            {
                differing++;
                continue;
            }

            try
            {
                string actual = HashUtil.Sha256Hex(File.ReadAllBytes(abs));
                if (!string.Equals(actual, unit.ContentHash, StringComparison.OrdinalIgnoreCase))
                {
                    differing++;
                }
            }
            catch (IOException)
            {
                differing++;
            }
        }

        return new InventoryDrift(compared, differing);
    }

    /// <summary>
    /// ¿Ya existe en el hub una app con ESTE repo? Es la pregunta que hace que «Nueva aplicación»
    /// no cree un duplicado (F5.8 §2).
    /// </summary>
    public AppConfig? FindByRepoUrl(string? repoUrl)
    {
        if (string.IsNullOrWhiteSpace(repoUrl))
        {
            return null;
        }

        foreach (string slug in _hub.Store.ListAppSlugs())
        {
            AppConfig? app = _hub.Store.TryReadApp(slug);
            if (app is not null && RemoteUrl.Same(app.RepoUrl, repoUrl))
            {
                return app;
            }
        }

        return null;
    }
}
