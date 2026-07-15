using System.Collections.ObjectModel;
using System.Net;

namespace CambioReal.Payliance.Tests.Fakes;

/// <summary>Handler de teste: responde a partir de uma fila de respostas e grava o que recebeu.</summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responders = new();
    private readonly List<RecordedRequest> requests = [];

    public IReadOnlyList<RecordedRequest> Requests => new ReadOnlyCollection<RecordedRequest>(requests);

    public RecordingHttpMessageHandler RespondWith(HttpStatusCode statusCode, string json)
    {
        responders.Enqueue(_ => new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        });

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        requests.Add(RecordedRequest.Capture(request, body));

        if (responders.Count == 0)
        {
            throw new InvalidOperationException($"Requisição inesperada para {request.RequestUri}: a fila de respostas está vazia.");
        }

        return responders.Dequeue()(request);
    }
}

internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri? RequestUri,
    string? Authorization,
    string? ContentType,
    string? Body)
{
    public static RecordedRequest Capture(HttpRequestMessage request, string? body) => new(
        request.Method,
        request.RequestUri,
        request.Headers.Authorization?.ToString(),
        request.Content?.Headers.ContentType?.MediaType,
        body);
}

/// <summary>Fábrica de <see cref="HttpClient"/> que sempre devolve o mesmo handler gravado.</summary>
internal sealed class SingleHandlerHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler handler;
    private readonly Uri baseAddress;

    public SingleHandlerHttpClientFactory(HttpMessageHandler handler, Uri baseAddress)
    {
        this.handler = handler;
        this.baseAddress = baseAddress;
    }

    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = baseAddress };
}
