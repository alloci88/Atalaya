namespace Atalaya.Carga;

/// <summary>
/// Lo que se cuenta de cada persona durante la tanda. Todo con candado: los sesionistas corren de
/// verdad a la vez y un contador que se pisa mide su propia carrera en vez de la del hub.
/// </summary>
public sealed class Medidas
{
    private readonly object _puerta = new();
    private readonly List<string> _conflictos = new();
    private readonly List<string> _roturas = new();

    public Medidas(string quien, Ritmo ritmo)
    {
        Quien = quien;
        Ritmo = ritmo;
    }

    public string Quien { get; }

    public Ritmo Ritmo { get; }

    public int SesionesTerminadas { get; private set; }

    public int HallazgosNuevos { get; private set; }

    public int Reintentos { get; private set; }

    public bool SeCayoAMitad { get; private set; }

    /// <summary>
    /// <b>La cifra por la que existe la fase</b>: lo más que llegó a tardar una publicación. El día
    /// 6 esto no tenía tope y por eso una sesión no arrancaba.
    /// </summary>
    public TimeSpan PublicacionMasLarga { get; private set; }

    public TimeSpan SesionMasLarga { get; private set; }

    public IReadOnlyList<string> ConflictosResueltos
    {
        get { lock (_puerta) { return _conflictos.ToList(); } }
    }

    public IReadOnlyList<string> Roturas
    {
        get { lock (_puerta) { return _roturas.ToList(); } }
    }

    public void SesionTerminada(int hallazgos, TimeSpan duracion)
    {
        lock (_puerta)
        {
            SesionesTerminadas++;
            HallazgosNuevos += hallazgos;
            if (duracion > SesionMasLarga)
            {
                SesionMasLarga = duracion;
            }
        }
    }

    public void SesionRota(Exception ex)
    {
        lock (_puerta)
        {
            _roturas.Add(ex.GetType().Name + ": " + ex.Message);
        }
    }

    public void Reintento()
    {
        lock (_puerta)
        {
            Reintentos++;
        }
    }

    public void Publicacion(TimeSpan cuanto)
    {
        lock (_puerta)
        {
            if (cuanto > PublicacionMasLarga)
            {
                PublicacionMasLarga = cuanto;
            }
        }
    }

    public void Conflictos(IReadOnlyList<string> avisos)
    {
        if (avisos is null || avisos.Count == 0)
        {
            return;
        }

        lock (_puerta)
        {
            _conflictos.AddRange(avisos);
        }
    }

    public void SeCayo()
    {
        lock (_puerta)
        {
            SeCayoAMitad = true;
        }
    }
}
