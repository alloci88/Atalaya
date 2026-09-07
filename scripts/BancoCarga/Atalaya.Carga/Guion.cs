using System.IO;
using Atalaya.Agents;
using Atalaya.Copilot;

namespace Atalaya.Carga;

/// <summary>
/// El código que se audita y el guion del agente falso.
/// <para>
/// Aquí no se mide la calidad de la auditoría: se mide el hub con varias sesiones escribiendo a la
/// vez. El código es el mínimo que hace que una unidad exista y el guion el mínimo que hace que una
/// unidad produzca hallazgos y converja. Lo que importa de los dos es el <b>ritmo</b>: cada llamada
/// tarda un poco, porque una sesión que termina en un milisegundo no llega a coincidir con nadie y
/// no mediría nada.
/// </para>
/// </summary>
public static class Guion
{
    /// <summary>Cuánto tarda el agente falso en contestar a una unidad.</summary>
    public static TimeSpan Latencia { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Escribe un clon de mentira con <paramref name="cuantas"/> unidades y devuelve sus rutas.
    /// </summary>
    public static string[] EscribirCodigo(string clon, int cuantas)
    {
        var rutas = new List<string>();
        for (int i = 1; i <= cuantas; i++)
        {
            string modulo = i % 2 == 0 ? "Db" : "Web";
            string rel = $"src/{modulo}/Unidad{i:00}.cs";
            string abs = Path.Combine(clon, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
            File.WriteAllText(abs,
                $"namespace Banco;\n\npublic sealed class Unidad{i:00}\n{{\n"
                + $"    public int Suma(int a, int b) => a + b;\n"
                + $"    public string Nombre => \"unidad {i}\";\n}}\n");
            rutas.Add(rel);
        }

        return rutas.ToArray();
    }

    /// <summary>
    /// Una pasada productiva por unidad y por sesionista; las siguientes, secas. Es lo que hace
    /// que una sesión converja en vez de dar vueltas hasta que se acabe el tiempo.
    /// </summary>
    public sealed class Libreto
    {
        private readonly HashSet<string> _vistas = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _quien;

        public Libreto(string quien) => _quien = quien;

        public IEnumerable<SubmitFindingArgs> Auditar(AuditUnitRequest peticion)
        {
            Thread.Sleep(Latencia);

            lock (_vistas)
            {
                if (!_vistas.Add(peticion.UnitPath))
                {
                    return Array.Empty<SubmitFindingArgs>();
                }
            }

            return new[]
            {
                new SubmitFindingArgs(
                    RuleId: "criterio.correccion",
                    Pillar: "Errores",
                    Severity: "Media",
                    Title: $"Suma sin comprobar el desbordamiento en {Path.GetFileName(peticion.UnitPath)}",
                    Description: $"Encontrado por {_quien} durante la tanda de carga.",
                    Impact: "Un entero puede dar la vuelta sin que nadie se entere.",
                    Recommendation: "Usar aritmetica comprobada.",
                    Locations: new[] { new SubmitLocation(peticion.UnitPath, 5, null) },
                    Symbol: "Suma"),
            };
        }

        public IEnumerable<SubmitFindingArgs> Arreglar(AuditUnitRequest peticion)
            => Array.Empty<SubmitFindingArgs>();
    }
}
