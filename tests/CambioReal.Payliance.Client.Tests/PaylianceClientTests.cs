using System.Net;
using CambioReal.Payliance.Tests.Fakes;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace CambioReal.Payliance.Tests;

public sealed class PaylianceClientTests
{
    private static PaylianceOptions NewOptions() => new()
    {
        MerchantId = "m-1",
        Password = "p-1",
        LocationId = "l-1",
        TestMode = true,
        Environment = PaylianceEnvironment.Staging,
    };

    private static (PaylianceClient Client, RecordingHttpMessageHandler Transport) NewClient(
        params (HttpStatusCode Status, string Xml)[] responses)
    {
        var transport = new RecordingHttpMessageHandler();
        foreach (var (status, xml) in responses)
        {
            transport.RespondWith(status, xml);
        }

        return (new PaylianceClient(new HttpClient(transport), Options.Create(NewOptions())), transport);
    }

    [Fact]
    public void ValidOptionsPassValidation()
        => Should.NotThrow(() => NewOptions().Validate());

    [Fact]
    public void EnvironmentsResolveToOfficialEndpoints()
    {
        NewOptions().ResolveEndpointUrl().ToString().ShouldBe("https://staging.secure.tranfusionboc.com/api/transactions.aspx");
        var prod = NewOptions();
        prod.Environment = PaylianceEnvironment.Production;
        prod.ResolveEndpointUrl().ToString().ShouldBe("https://secure.tranfusionboc.com/api/transactions.aspx");
    }

    [Fact]
    public async Task QuerySettlementsBuildsGatewayEnvelopeWithEmbeddedAuth()
    {
        var (client, transport) = NewClient((HttpStatusCode.OK,
            """<?xml version="1.0" encoding="UTF-8" ?><gateway><errorMsg></errorMsg><settlements><settlement><id>1</id><amount>10.00</amount></settlement></settlements></gateway>"""));

        var records = await client.GetSettlementsAsync("07/14/2026", "07/15/2026");

        records.Count.ShouldBe(1);
        records[0].Fields["amount"].ShouldBe("10.00");

        var request = transport.Requests.Single();
        request.ContentType.ShouldBe("text/xml");
        request.Body!.ShouldContain("<gateway test=\"true\">");
        request.Body!.ShouldContain("<merchantID>m-1</merchantID>");
        request.Body!.ShouldContain("<query type=\"settle\">");
        request.Body!.ShouldContain("<startDate>07/14/2026</startDate>");
    }

    /// <summary>Erros vêm com HTTP 200 + errorMsg no corpo — confirmado no legado.</summary>
    [Fact]
    public async Task ErrorMsgInBodyThrows()
    {
        var (client, _) = NewClient((HttpStatusCode.OK,
            """<?xml version="1.0"?><gateway><errorMsg>Invalid credentials</errorMsg></gateway>"""));

        var error = await Should.ThrowAsync<PaylianceApiException>(
            async () => await client.GetSettlementsAsync("07/14/2026", "07/15/2026"));

        error.Message.ShouldContain("Invalid credentials");
    }

    [Fact]
    public async Task ExportPaymentSerializesEcheckShapeAndParsesAuthorizationId()
    {
        var (client, transport) = NewClient((HttpStatusCode.OK,
            """<?xml version="1.0"?><gateway><AuthorizationID>AUTH-1</AuthorizationID></gateway>"""));

        var id = await client.ExportPaymentAsync(new PaylianceEcheckPayment(
            UniqueTranId: "TX1",
            Routing: "021000021",
            AccountNumber: "12345678",
            TransactionDate: "07/15/2026",
            Amount: 100.50m,
            FirstName: "Jane",
            LastName: "Doe",
            State: "DE"));

        id.ShouldBe("AUTH-1");

        var body = transport.Requests.Single().Body!;
        body.ShouldContain("<sec>PPD</sec>");
        body.ShouldContain("<tranCode>3</tranCode>");
        body.ShouldContain("<accountType>Personal Checking</accountType>");
        body.ShouldContain("<amount>100.50</amount>");
    }

    [Fact]
    public async Task RejectedPaymentSurfacesValidationMessage()
    {
        var (client, _) = NewClient((HttpStatusCode.OK,
            """<?xml version="1.0"?><gateway><ValidationMessage>Invalid routing number</ValidationMessage></gateway>"""));

        var error = await Should.ThrowAsync<PaylianceApiException>(
            async () => await client.RefundPaymentAsync("AUTH-X"));

        error.Message.ShouldContain("Invalid routing number");
    }

    [Fact]
    public async Task NonXmlBodyThrows()
    {
        var (client, _) = NewClient((HttpStatusCode.OK, "<html>waf page"));

        await Should.ThrowAsync<PaylianceApiException>(
            async () => await client.GetSettlementsAsync("07/14/2026", "07/15/2026"));
    }
}
