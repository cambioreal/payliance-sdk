using CambioReal.Payliance;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace CambioReal.Payliance.SandboxTests;

/// <summary>
/// Integração staging real, opt-in. Variáveis a partir do
/// <c>pass cambio-real-v2/providers/payliance/staging-env</c>
/// (MERCHANT_ID/LOCATION_ID/PASSWORD → prefixo <c>PAYLIANCE_SANDBOX_</c>). Leitura apenas —
/// payment/refund/void são financial-write e NUNCA existem aqui (goal §0.5).
/// </summary>
public sealed class PaylianceSandboxTests
{
    private readonly ITestOutputHelper output;

    public PaylianceSandboxTests(ITestOutputHelper output) => this.output = output;

    [Fact]
    [Trait("Category", "Sandbox")]
    public async Task SettlementQueryAuthenticatesAndReadsLive()
    {
        using var provider = BuildServiceProvider();
        var client = provider.GetRequiredService<PaylianceClient>();

        // errorMsg vazio = auth aceita (credencial inválida vem como errorMsg preenchido).
        var records = await client.GetSettlementsAsync("07/14/2026", "07/15/2026");

        records.ShouldNotBeNull();
        output.WriteLine($"query settle: 200, errorMsg vazio (auth aceita), {records.Count} settlement(s) no intervalo.");
    }

    [Fact]
    [Trait("Category", "Sandbox")]
    public async Task ReturnsQueryReadsLive()
    {
        using var provider = BuildServiceProvider();
        var client = provider.GetRequiredService<PaylianceClient>();

        var records = await client.GetReturnsAsync("07/14/2026", "07/15/2026");

        records.ShouldNotBeNull();
        output.WriteLine($"query return: 200, {records.Count} return(s) no intervalo.");
    }

    private static ServiceProvider BuildServiceProvider()
    {
        string Req(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
                ? value
                : throw new InvalidOperationException($"{name} ausente — carregue do pass.");

        var services = new ServiceCollection();
        services.AddPaylianceClient(options =>
        {
            options.Environment = PaylianceEnvironment.Staging;
            options.MerchantId = Req("PAYLIANCE_SANDBOX_MERCHANT_ID");
            options.LocationId = Req("PAYLIANCE_SANDBOX_LOCATION_ID");
            options.Password = Req("PAYLIANCE_SANDBOX_PASSWORD");
        });

        return services.BuildServiceProvider();
    }
}
