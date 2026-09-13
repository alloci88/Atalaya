namespace Atalaya.OpenAI.Tests;

/// <summary>
/// El andamio de PROV-3: lo mínimo que tiene que ser verdad para que los cuatro frentes escriban
/// contra algo que existe. No prueba comportamiento del proveedor —eso llega con cada frente—,
/// prueba que el paso 0 dejó lo que dijo que dejaba.
/// </summary>
public sealed class AndamioTests
{
    [Fact]
    public async Task El_endpoint_falso_sirve_lo_grabado_y_apunta_lo_que_recibe()
    {
        var falso = new EndpointFalso();
        falso.RespondeJson("""{"data":[]}""");

        using var http = new HttpClient(falso);
        HttpResponseMessage r = await http.GetAsync("https://ejemplo/v1/models");

        r.IsSuccessStatusCode.Should().BeTrue();
        falso.Llamadas.Should().Be(1);
        falso.Recibidas[0].Url.Should().EndWith("/v1/models");
    }

    [Theory]
    [InlineData("https://api.ejemplo.com/v1", true)]
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://una-maquina-de-la-red/v1", false)]
    [InlineData("ftp://lo-que-sea/v1", false)]
    [InlineData("", false)]
    public void Solo_se_admite_http_en_local(string url, bool vale)
        => (OpenAiEndpoint.WhyNot(url) is null).Should().Be(
            vale,
            "un endpoint en claro fuera de esta máquina manda el prompt —con el código dentro— "
            + "y la clave sin cifrar");

    [Fact]
    public void La_configuracion_no_guarda_la_clave()
        => typeof(OpenAiEndpoint).GetProperties().Select(p => p.Name)
            .Should().NotContain(
                n => n.Contains("Key", StringComparison.OrdinalIgnoreCase)
                     || n.Contains("Clave", StringComparison.OrdinalIgnoreCase),
                "la clave vive en el almacén de secretos; si cupiera aquí acabaría en settings.json");
}
