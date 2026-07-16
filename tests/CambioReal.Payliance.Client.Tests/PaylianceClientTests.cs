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

    /// <summary>
    /// GetTransactionStatusAsync é derivado: sem Retrieve nativo no legado, ele combina
    /// query type="return" + query type="settlenoreturn" (nessa ordem) e filtra pelo ID.
    /// </summary>
    [Fact]
    public async Task TransactionStatusReturnsReturnedKindWithNachaReturnCode()
    {
        var (client, transport) = NewClient((HttpStatusCode.OK,
            """
            <?xml version="1.0"?><gateway><errorMsg></errorMsg><returns>
                <return>
                    <AuthorizationID>5438</AuthorizationID>
                    <uniqueTranID>TX1</uniqueTranID>
                    <dateReturned>07/10/2026</dateReturned>
                    <amount>12.00</amount>
                    <returnAmount>12.00</returnAmount>
                    <returnReason>R01</returnReason>
                    <addenda>Insufficient Funds</addenda>
                </return>
            </returns></gateway>
            """));

        var status = await client.GetTransactionStatusAsync("07/01/2026", "07/15/2026", authorizationId: "5438");

        status.Kind.ShouldBe(PaylianceTransactionStatusKind.Returned);
        status.AuthorizationId.ShouldBe("5438");
        status.UniqueTranId.ShouldBe("TX1");
        status.ReturnCode.ShouldBe("R01");
        status.ReturnDescription.ShouldBe("Insufficient Funds");
        status.DateReturned.ShouldBe("07/10/2026");

        // Encontrada em returns — não deve nem consultar settlenoreturn.
        transport.Requests.Count.ShouldBe(1);
        transport.Requests.Single().Body!.ShouldContain("<query type=\"return\">");
    }

    [Fact]
    public async Task TransactionStatusFallsBackToSettlenoreturnWhenNotReturned()
    {
        var (client, transport) = NewClient(
            (HttpStatusCode.OK, """<?xml version="1.0"?><gateway><errorMsg></errorMsg><returns></returns></gateway>"""),
            (HttpStatusCode.OK,
                """
                <?xml version="1.0"?><gateway><errorMsg></errorMsg><settlements>
                    <settlement>
                        <AuthorizationID>5438</AuthorizationID>
                        <uniqueTranID>TX1</uniqueTranID>
                        <settleDate>07/12/2026</settleDate>
                        <amount>12.00</amount>
                    </settlement>
                </settlements></gateway>
                """));

        var status = await client.GetTransactionStatusAsync("07/01/2026", "07/15/2026", uniqueTranId: "TX1");

        status.Kind.ShouldBe(PaylianceTransactionStatusKind.Settled);
        status.AuthorizationId.ShouldBe("5438");
        status.SettleDate.ShouldBe("07/12/2026");
        status.ReturnCode.ShouldBeNull();

        transport.Requests.Count.ShouldBe(2);
        transport.Requests[0].Body!.ShouldContain("<query type=\"return\">");
        transport.Requests[1].Body!.ShouldContain("<query type=\"settlenoreturn\">");
    }

    [Fact]
    public async Task TransactionStatusReturnsNotFoundWhenAbsentFromBothQueries()
    {
        var (client, _) = NewClient(
            (HttpStatusCode.OK, """<?xml version="1.0"?><gateway><errorMsg></errorMsg><returns></returns></gateway>"""),
            (HttpStatusCode.OK, """<?xml version="1.0"?><gateway><errorMsg></errorMsg><settlements></settlements></gateway>"""));

        var status = await client.GetTransactionStatusAsync("07/01/2026", "07/15/2026", authorizationId: "does-not-exist");

        status.Kind.ShouldBe(PaylianceTransactionStatusKind.NotFound);
        status.AuthorizationId.ShouldBe("does-not-exist");
        status.Fields.ShouldBeNull();
    }

    [Fact]
    public async Task TransactionStatusRequiresAtLeastOneIdentifier()
    {
        var (client, _) = NewClient();

        await Should.ThrowAsync<ArgumentException>(
            async () => await client.GetTransactionStatusAsync("07/01/2026", "07/15/2026"));
    }
}
