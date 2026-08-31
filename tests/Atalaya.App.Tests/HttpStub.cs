using System.Net;
using System.Net.Http;

namespace Atalaya.App.Tests;

/// <summary>
/// A scripted <see cref="HttpMessageHandler"/>: each queued response is returned in order, so a
/// whole OAuth device-flow conversation (pending → slow_down → success) is exercised offline.
/// </summary>
internal sealed class HttpStub : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<HttpRequestMessage> Requests { get; } = new();

    public List<string> Bodies { get; } = new();

    public HttpStub Json(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });
        return this;
    }

    public HttpStub Status(HttpStatusCode status, string body = "{}", params (string Name, string Value)[] headers)
    {
        _responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            foreach ((string name, string value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });
        return this;
    }

    /// <summary>Un adjunto de una Release: bytes crudos, como los entrega la API (F11).</summary>
    public HttpStub Bytes(byte[] payload)
    {
        _responses.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        });
        return this;
    }

    /// <summary>Texto plano — el fichero <c>.sha256</c> que acompaña al zip.</summary>
    public HttpStub Text(string body)
        => Bytes(System.Text.Encoding.UTF8.GetBytes(body));

    public HttpStub Throws(Exception ex)
    {
        _responses.Enqueue(_ => throw ex);
        return this;
    }

    public HttpClient Client() => new(this) { Timeout = TimeSpan.FromSeconds(5) };

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("HttpStub ran out of scripted responses.");
        }

        return _responses.Dequeue()(request);
    }
}
