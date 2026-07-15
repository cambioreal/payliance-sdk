# Payliance — Discovery

Status: descoberta e SDK concluídos (2026-07-15). Sonda §0.8 **verde** (staging E produção
respondem; auth XML aceita — `errorMsg` vazio). Provider order position: **8 of 9**.
Verified: 2026-07-15, contra `pass cambio-real-v2/providers/payliance/staging-env` no staging
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

Sem webhooks; conciliação é por query de intervalo de datas (`MM/dd/yyyy` — formato do legado).
Registros de settlement/return expostos como dicionário cru (campos variam; o legado consome
`id`/`amount`/`reference`).

## 3. Limites e decisões

SDK = gateway XML fielmente modelado (System.Xml.Linq); atributo `test` configurável (legado usa
`true` fora de produção). Gateway = `/v1/payliance/*` canônico; payment/refund/void documentados
FINANCIAL-WRITE. Nenhuma contradição arquitetural.
