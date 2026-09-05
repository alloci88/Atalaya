using System.IO;
namespace Atalaya.Shots;

/// <summary>
/// El clon de mentira: seis ficheros con código creíble y un repo git de verdad detrás —el
/// anclaje de hallazgos necesita un commit—. Los nombres son los de la sesión real que el usuario
/// capturó, para que las dos fotos se puedan comparar.
/// </summary>
public static class Code
{
    public static string[] Write(string clone)
    {
        Directory.CreateDirectory(clone);

        var files = new Dictionary<string, string>
        {
            ["src/AtalayaBanco.Core/Legacy/MotorCalculoLegacy.cs"] = Legacy(),
            ["src/AtalayaBanco.Core/Servicios/ClienteRemoto.cs"] = Cliente(),
            ["src/AtalayaBanco.Core/Servicios/ConversorHex.cs"] = Conversor(),
            ["src/AtalayaBanco.Core/Servicios/FormateadorInforme.cs"] = Formateador(),
            ["src/AtalayaBanco.Core/Servicios/RepositorioVoladuras.cs"] = Repositorio(),
            ["src/AtalayaBanco.Core/Utilidades/Conversiones.cs"] = Conversiones(),
        };

        foreach (var (path, body) in files)
        {
            string full = Path.Combine(clone, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
        }

        if (!LibGit2Sharp.Repository.IsValid(clone))
        {
            LibGit2Sharp.Repository.Init(clone);
        }

        using (var repo = new LibGit2Sharp.Repository(clone))
        {
            if (repo.Network.Remotes["origin"] is null)
            {
                repo.Network.Remotes.Add("origin", "https://example.invalid/org/atalayabanco.git");
            }

            LibGit2Sharp.Commands.Stage(repo, "*");
            var who = new LibGit2Sharp.Signature("banco", "banco@example.invalid", DateTimeOffset.Now);
            repo.Commit("inicial", who, who, new LibGit2Sharp.CommitOptions());
        }

        return files.Keys.ToArray();
    }

    private static string Cliente() => """
        using System.Net.Http;

        namespace AtalayaBanco.Core.Servicios;

        public sealed class ClienteRemoto
        {
            private const string UsuarioServicio = "svc_partes";
            private const string ClaveServicio = "P4rt3s!2024";
            private static readonly HttpClient Http = new();

            private readonly string _urlBase;

            public ClienteRemoto(string urlBase) => _urlBase = urlBase;

            public string DescargarParte(int numero)
            {
                Http.DefaultRequestHeaders.Add("Authorization", Credencial());
                var respuesta = Http.GetAsync($"{_urlBase}/partes/{numero}").Result;
                var cuerpo = respuesta.Content.ReadAsStringAsync().Result;
                return cuerpo;
            }

            private static string Credencial()
                => Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes($"{UsuarioServicio}:{ClaveServicio}"));
        }
        """;

    private static string Legacy() => """
        namespace AtalayaBanco.Core.Legacy;

        public static class MotorCalculoLegacy
        {
            public static double CargaTotalKg(double[] barrenos)
            {
                double total = 0;
                for (int i = 0; i <= barrenos.Length; i++)
                {
                    total += barrenos[i];
                }

                return total;
            }

            public static double FactorRoca(string tipo)
                => tipo == "granito" ? 1.35 : tipo == "caliza" ? 1.1 : 1.0;
        }
        """;

    private static string Conversor() => """
        namespace AtalayaBanco.Core.Servicios;

        public static class ConversorHex
        {
            public static string ADecimal(string hex)
            {
                return Convert.ToInt64(hex, 16).ToString();
            }

            public static string AHex(long valor) => valor.ToString("X");
        }
        """;

    private static string Formateador() => """
        namespace AtalayaBanco.Core.Servicios;

        public sealed class FormateadorInforme
        {
            public string Encabezado(string aplicacion, DateTime fecha)
                => aplicacion + " - " + fecha.ToString("dd/MM/yyyy");

            public string Linea(string clave, object valor) => clave + ": " + valor;
        }
        """;

    private static string Repositorio() => """
        namespace AtalayaBanco.Core.Servicios;

        public sealed class RepositorioVoladuras
        {
            private readonly List<string> _cache = new();

            public void Guardar(string voladura) => _cache.Add(voladura);

            public string Buscar(int indice) => _cache[indice];
        }
        """;

    private static string Conversiones() => """
        namespace AtalayaBanco.Core.Utilidades;

        public static class Conversiones
        {
            public static double MetrosAPies(double metros) => metros * 3.28084;

            public static double KilosALibras(double kilos) => kilos * 2.20462;

            public static double GradosARadianes(double grados) => grados * Math.PI / 180;
        }
        """;
}
