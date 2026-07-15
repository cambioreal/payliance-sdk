using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CambioReal.Payliance;

/// <summary>Ambiente do gateway XML da Payliance.</summary>
public enum PaylianceEnvironment
{
    /// <summary>Staging — <c>https://staging.secure.tranfusionboc.com/api/transactions.aspx</c>.</summary>
    Staging = 0,

    /// <summary>Produção — <c>https://secure.tranfusionboc.com/api/transactions.aspx</c>.</summary>
    Production = 1,
}

/// <summary>Configuração do <see cref="PaylianceClient"/>.</summary>
public sealed class PaylianceOptions
{
    /// <summary>Nome da seção de configuração sugerida.</summary>
    public const string SectionName = "Payliance";

    /// <summary>Merchant ID. Origem: <c>pass cambio-real-v2/providers/payliance/staging-env</c>.</summary>
    public string MerchantId { get; set; } = string.Empty;

    /// <summary>Senha do gateway. Origem: idem.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Location ID. Origem: idem.</summary>
    public string LocationId { get; set; } = string.Empty;

    /// <summary>Atributo <c>test</c> do envelope gateway (o legado usa <c>"true"</c> fora de produção).</summary>
    public bool TestMode { get; set; } = true;

    /// <summary>Ambiente alvo. O padrão é <see cref="PaylianceEnvironment.Staging"/>, deliberadamente.</summary>
    public PaylianceEnvironment Environment { get; set; } = PaylianceEnvironment.Staging;

    /// <summary>Sobrescreve a URL do endpoint único.</summary>
    public Uri? EndpointUrl { get; set; }

    /// <summary>Timeout de cada requisição HTTP.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>URL efetiva do endpoint (a API é um endpoint XML único).</summary>
    public Uri ResolveEndpointUrl() => EndpointUrl ?? Environment switch
    {
        PaylianceEnvironment.Production => new Uri("https://secure.tranfusionboc.com/api/transactions.aspx"),
        _ => new Uri("https://staging.secure.tranfusionboc.com/api/transactions.aspx"),
    };

    /// <summary>Valida a configuração e lança se estiver inconsistente.</summary>
    /// <exception cref="InvalidOperationException">Alguma credencial obrigatória está ausente.</exception>
    public void Validate()
    {
        foreach (var (value, name) in new[]
        {
            (MerchantId, nameof(MerchantId)),
            (Password, nameof(Password)),
            (LocationId, nameof(LocationId)),
        })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{nameof(PaylianceOptions)}.{name} é obrigatório.");
            }
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{nameof(Timeout)} precisa ser positivo.");
        }
    }
}

/// <summary>Erro devolvido pelo gateway Payliance (<c>errorMsg</c>/<c>ValidationMessage</c> ou HTTP não-200).</summary>
public sealed class PaylianceApiException : Exception
{
    /// <summary>Cria uma exceção sem contexto.</summary>
    public PaylianceApiException()
    {
    }

    /// <summary>Cria uma exceção com mensagem.</summary>
    public PaylianceApiException(string message)
        : base(message)
    {
    }

    /// <summary>Cria uma exceção com mensagem e causa.</summary>
    public PaylianceApiException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Cria uma exceção com contexto de resposta.</summary>
    public PaylianceApiException(HttpStatusCode statusCode, string message, string? responseBody)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    /// <summary>Status HTTP da resposta (200 quando o erro veio no corpo XML).</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Corpo bruto da resposta, para diagnóstico.</summary>
    public string? ResponseBody { get; }
}

/// <summary>Pagamento eCheck (PPD) — confirmado no legado (<c>PayliancePayment::elementTransactions</c>). Amounts decimais USD.</summary>
public sealed record PaylianceEcheckPayment(
    string UniqueTranId,
    string Routing,
    string AccountNumber,
    string TransactionDate,
    decimal Amount,
    string FirstName,
    string LastName,
    string State)
{
    /// <summary>SEC code — sempre <c>PPD</c> no legado.</summary>
    public string Sec { get; init; } = "PPD";

    /// <summary>Sempre <c>"Personal Checking"</c> no legado.</summary>
    public string AccountType { get; init; } = "Personal Checking";

    /// <summary>Sempre <c>3</c> no legado.</summary>
    public int TranCode { get; init; } = 3;
}

/// <summary>Registro de settlement/return devolvido pelas queries — campos expostos crus (XML→dict).</summary>
public sealed record PaylianceTransactionRecord(IReadOnlyDictionary<string, string> Fields);

/// <summary>
/// Cliente do gateway XML da Payliance — endpoint único (<c>transactions.aspx</c>), autenticação
/// embutida no envelope (<c>&lt;authorization&gt;merchantID/password/locationID</c>), respostas
/// XML. Erros vêm com HTTP 200 + <c>errorMsg</c>/<c>ValidationMessage</c> no corpo.
/// </summary>
public sealed class PaylianceClient
{
    private readonly HttpClient httpClient;
    private readonly PaylianceOptions options;

    /// <summary>Cria o cliente sobre um <see cref="HttpClient"/> já configurado.</summary>
    public PaylianceClient(HttpClient httpClient, IOptions<PaylianceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);

        this.httpClient = httpClient;
        this.options = options.Value;
    }

    /// <summary>
    /// Consulta settlements por intervalo de datas (formato <c>MM/dd/yyyy</c> — padrão do legado).
    /// <c>query type="settle"</c> (ou <c>settlenoreturn</c>). Leitura — validado ao vivo
    /// (2026-07-15, staging 200 com auth aceita).
    /// </summary>
    public async Task<IReadOnlyList<PaylianceTransactionRecord>> GetSettlementsAsync(
        string startDate, string endDate, bool excludeReturns = false, CancellationToken cancellationToken = default)
    {
        var root = await SendAsync(BuildQuery(excludeReturns ? "settlenoreturn" : "settle", startDate, endDate), cancellationToken);
        return ParseRecords(root.Element("settlements"));
    }

    /// <summary>Consulta returns (devoluções ACH) por intervalo. <c>query type="return"</c>. Leitura.</summary>
    public async Task<IReadOnlyList<PaylianceTransactionRecord>> GetReturnsAsync(
        string startDate, string endDate, CancellationToken cancellationToken = default)
    {
        var root = await SendAsync(BuildQuery("return", startDate, endDate), cancellationToken);
        return ParseRecords(root.Element("returns") ?? root.Element("settlements"));
    }

    /// <summary>
    /// Exporta um pagamento eCheck. **FINANCIAL-WRITE** — movimenta ACH; não executar contra
    /// staging/produção sem autorização explícita (goal §0.5). Devolve o <c>AuthorizationID</c>.
    /// </summary>
    public async Task<string> ExportPaymentAsync(PaylianceEcheckPayment payment, CancellationToken cancellationToken = default)
    {
        var transactions = new XElement(
            "transactions",
            new XElement(
                "transaction",
                new XElement("uniqueTranID", payment.UniqueTranId),
                new XElement("routing", payment.Routing),
                new XElement("accountNumber", payment.AccountNumber),
                new XElement("sec", payment.Sec),
                new XElement("transactionDate", payment.TransactionDate),
                new XElement("amount", payment.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new XElement("accountType", payment.AccountType),
                new XElement("tranCode", payment.TranCode),
                new XElement("firstName", payment.FirstName),
                new XElement("lastName", payment.LastName),
                new XElement("state", payment.State)));

        var root = await SendAsync(transactions, cancellationToken);
        return ExtractAuthorizationId(root);
    }

    /// <summary>Refund de um pagamento pelo <c>AuthorizationID</c>. **FINANCIAL-WRITE.**</summary>
    public async Task<string> RefundPaymentAsync(string authorizationId, CancellationToken cancellationToken = default)
    {
        var root = await SendAsync(
            new XElement("transactions", new XElement("refund", new XElement("AuthorizationID", authorizationId))),
            cancellationToken);
        return ExtractAuthorizationId(root);
    }

    /// <summary>Void de um pagamento pelo <c>AuthorizationID</c>. **FINANCIAL-WRITE** (cancela antes de liquidar).</summary>
    public async Task<string> VoidPaymentAsync(string authorizationId, CancellationToken cancellationToken = default)
    {
        var root = await SendAsync(
            new XElement("transactions", new XElement("void", new XElement("AuthorizationID", authorizationId))),
            cancellationToken);
        return ExtractAuthorizationId(root);
    }

    private static XElement BuildQuery(string type, string startDate, string endDate) =>
        new(
            "transactions",
            new XElement(
                "query",
                new XAttribute("type", type),
                new XElement("startDate", startDate),
                new XElement("endDate", endDate)));

    private async Task<XElement> SendAsync(XElement transactions, CancellationToken cancellationToken)
    {
        var gateway = new XElement(
            "gateway",
            new XAttribute("test", options.TestMode ? "true" : "false"),
            new XElement(
                "authorization",
                new XElement("merchantID", options.MerchantId),
                new XElement("password", options.Password),
                new XElement("locationID", options.LocationId)),
            transactions);

        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null), gateway);

        using var content = new StringContent(document.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml");
        using var response = await httpClient.PostAsync(options.ResolveEndpointUrl(), content, cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new PaylianceApiException(
                response.StatusCode,
                $"A Payliance respondeu HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                body);
        }

        XElement root;
        try
        {
            root = XElement.Parse(body);
        }
        catch (System.Xml.XmlException exception)
        {
            throw new PaylianceApiException(
                response.StatusCode, "A Payliance devolveu um corpo que não é XML válido.", body)
            {
                // Sem InnerException via ctor de contexto — anexada abaixo.
            }.WithInner(exception);
        }

        // Erros vêm com HTTP 200 + errorMsg no corpo (confirmado no legado e ao vivo).
        var errorMessage = root.Element("errorMsg")?.Value;
        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            throw new PaylianceApiException(response.StatusCode, $"Erro Payliance: {errorMessage}.", body);
        }

        return root;
    }

    private static string ExtractAuthorizationId(XElement root)
    {
        var authorizationId = root.Element("AuthorizationID")?.Value ?? root.Descendants("AuthorizationID").FirstOrDefault()?.Value;

        if (string.IsNullOrWhiteSpace(authorizationId))
        {
            var validation = root.Element("ValidationMessage")?.Value
                ?? root.Descendants("ValidationMessage").FirstOrDefault()?.Value
                ?? "resposta sem AuthorizationID";
            throw new PaylianceApiException(HttpStatusCode.OK, $"Payliance rejeitou a operação: {validation}.", root.ToString());
        }

        return authorizationId;
    }

    private static IReadOnlyList<PaylianceTransactionRecord> ParseRecords(XElement? container)
    {
        if (container is null)
        {
            return [];
        }

        return [.. container.Elements().Select(record => new PaylianceTransactionRecord(
            record.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value)))];
    }
}

/// <summary>Helper interno para anexar InnerException mantendo o record de contexto.</summary>
internal static class PaylianceExceptionExtensions
{
    public static PaylianceApiException WithInner(this PaylianceApiException exception, Exception inner) =>
        new(exception.Message, inner);
}

/// <summary>Registro do cliente Payliance no container.</summary>
public static class PaylianceServiceCollectionExtensions
{
    /// <summary>Registra o cliente a partir de uma seção de configuração.</summary>
    public static IServiceCollection AddPaylianceClient(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services.AddPaylianceClient(configuration.Bind);
    }

    /// <summary>Registra <see cref="PaylianceClient"/> e o pipeline HTTP (auth vai no corpo XML).</summary>
    public static IServiceCollection AddPaylianceClient(this IServiceCollection services, Action<PaylianceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddOptions<PaylianceOptions>().Validate(
            options =>
            {
                options.Validate();
                return true;
            },
            "A configuração do PaylianceOptions é inválida.");

        services.AddHttpClient("payliance.api", (provider, client) =>
        {
            var options = provider.GetRequiredService<IOptions<PaylianceOptions>>().Value;
            options.Validate();
            client.Timeout = options.Timeout;
        });

        services.TryAddTransient(provider =>
        {
            var factory = provider.GetRequiredService<IHttpClientFactory>();
            return new PaylianceClient(
                factory.CreateClient("payliance.api"),
                provider.GetRequiredService<IOptions<PaylianceOptions>>());
        });

        return services;
    }
}
