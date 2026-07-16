# Payliance — Discovery

Status: descoberta e SDK concluídos (2026-07-15); gaps P1 (status por transação) fechados
2026-07-16. Sonda §0.8 **verde** (staging E produção respondem; auth XML aceita — `errorMsg`
vazio). Provider order position: **8 of 9**.
Verified: 2026-07-15/16, contra `pass cambio-real-v2/providers/payliance/staging-env` no staging
vivo (`staging.secure.tranfusionboc.com`) + legado `cerebro` (read-only).

## 1. Perfil e contrato

**`Sync`** utilitário de payout ACH/eCheck US (o legado usa para conciliar exports do EnvioBr):
gateway **XML de endpoint único** (`POST /api/transactions.aspx`, `Content-Type: text/xml`).
Autenticação EMBUTIDA no envelope (`<gateway test="..."><authorization>merchantID/password/
locationID`). **Erros vêm com HTTP 200** + `errorMsg` (queries) ou `ValidationMessage`
(payments) no corpo. Legado desabilita verificação TLS em dev (não replicado).

## 2. Matriz

| # | Operação | Recurso SDK | Efeito | Status staging |
|---|---|---|---|---|
| 1 | `query type="settle"`/`settlenoreturn` | `GetSettlementsAsync` | read | ✅ vivo (auth aceita, 0 registros no intervalo) |
| 2 | `query type="return"` | `GetReturnsAsync` | read | ✅ vivo |
| 3 | `transaction` (eCheck PPD, tranCode 3) | `ExportPaymentAsync` | **financial-write** | 🔴 contrato-only (§0.5) |
| 4 | `refund` (AuthorizationID) | `RefundPaymentAsync` | **financial-write** | 🔴 contrato-only |
| 5 | `void` (AuthorizationID) | `VoidPaymentAsync` | **financial-write** | 🔴 contrato-only |
| 6 | `query type="return"` + `query type="settlenoreturn"` combinados, filtrados por ID | `GetTransactionStatusAsync` | read (**derivado**) | ✅ vivo (2026-07-16 — ambas as queries reais respondem; ID inexistente resolve `NotFound` sem erro) |

Sem webhooks; conciliação é por query de intervalo de datas (`MM/dd/yyyy` — formato do legado).
Registros de settlement/return expostos como dicionário cru (campos variam; o legado consome
`id`/`amount`/`reference`).

**Status por transação (#6) é derivado, não um `Retrieve` nativo.** `transactions.aspx` não
oferece consulta pontual por `AuthorizationId`/`uniqueTranId` — confirmado 2026-07-16 contra o
legado `cerebro` read-only (`PaylianceRepository`, `PaylianceApiController`,
`PaylianceQuerySettlement`, `PaylianceQueryReturn`, `AbstractRequest`: os únicos `query type`
usados em qualquer lugar do legado são `settle`/`settlenoreturn`/`return`, sempre por intervalo de
datas; nunca por ID). `GetTransactionStatusAsync` compõe as duas queries reais (`return` primeiro,
depois `settlenoreturn`) e filtra client-side pelo identificador — exatamente o que
`PaylianceRepository::all/settlements/returns` já fazem para popular a UI (`resources/views/
relatorios/payliance/index.blade.php`), só que por ID em vez de para listagem paginada. O código
NACHA de devolução (`ReturnCode`) é o `returnReason` cru do XML (ex.: `R01`), já presente no
protocolo real de `return` (visto no exemplo de resposta em `PaylianceQueryReturn.php`).

**`QueryInstitution` (elegibilidade ACH de routing number) não foi implementado.** Busca extensa
no legado `cerebro` (grep case-insensitive por `institution`/`queryinstitution`/`retrieve`,
histórico git dos arquivos `Payliance*`, rotas `web.php`/`api.php`, controller JS/blade) não
encontrou qualquer referência — o legado nunca ofereceu essa funcionalidade nem uma aproximação
dela. É documentada apenas na API REST/JSON atual da Payliance (`sandbox.api.payliance.com`, produto
diferente do gateway XML legado). Sem uma sonda dedicada contra `transactions.aspx` confirmando um
`query type` equivalente, implementar seria inventar um tranCode/operação não confirmado — por
isso o gap permanece aberto. Ver `provider-protocol/docs/gateways/coverage/payliance.md` (gap
best-effort, recomendação de sonda antes de comprometer implementação).

## 3. Limites e decisões

SDK = gateway XML fielmente modelado (System.Xml.Linq); atributo `test` configurável (legado usa
`true` fora de produção). Gateway = `/v1/payliance/*` canônico; payment/refund/void documentados
FINANCIAL-WRITE. `GetTransactionStatusAsync` é composição de leitura sobre operações reais, não uma
operação nova do protocolo — nenhuma contradição arquitetural.
