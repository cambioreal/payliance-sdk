# payliance-sdk

Cliente .NET tipado (`CambioReal.Payliance.Client`) para o **gateway XML da Payliance**
(eCheck/ACH US): payment PPD, refund, void, queries de settlement/return e status de transação
(derivado). Particularidades encapsuladas: endpoint XML único, autenticação embutida no envelope
e **erros com HTTP 200** (`errorMsg`/`ValidationMessage` no corpo).

`GetTransactionStatusAsync` (0.2.0) **não é um `Retrieve` nativo** — o protocolo legado
`transactions.aspx` não tem consulta pontual por transação (confirmado contra o legado `cerebro`,
2026-07-16). O método combina `query type="return"` + `query type="settlenoreturn"` (as duas
queries reais) e filtra por `AuthorizationId`/`uniqueTranId`. `QueryInstitution` (elegibilidade ACH
de routing number) **não foi implementado** — sem qualquer referência no legado nem confirmação de
que `transactions.aspx` oferece essa capacidade; documentado como gap best-effort, não inventado.

Validação viva (2026-07-15, staging): queries settle/return 200 com auth aceita. 11 unit + 3
sandbox verdes. Payment/refund/void = **financial-write**, nunca executados (goal §0.5).

Secrets: `pass cambio-real-v2/providers/payliance/staging-env`. Discovery:
`docs/providers/payliance/discovery.md`.

## Instalação e uso

Pacote no GitHub Packages da org `cambioreal` (feed configurado no `NuGet.config` do repo consumidor):

```bash
dotnet add package CambioReal.Payliance.Client
```

```csharp
// Registro via DI — credenciais vêm de config segura (env/Secret/pass), nunca versionadas.
builder.Services.AddPaylianceClient(builder.Configuration.GetSection(PaylianceOptions.SectionName));

// ...injete CambioReal.Payliance.PaylianceClient onde precisar.
```

Também há a sobrecarga `AddPaylianceClient(Action<PaylianceOptions>)` para configuração inline.
