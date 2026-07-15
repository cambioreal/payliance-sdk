# payliance-sdk

Cliente .NET tipado (`CambioReal.Payliance.Client`) para o **gateway XML da Payliance**
(eCheck/ACH US): payment PPD, refund, void e queries de settlement/return. Particularidades
encapsuladas: endpoint XML único, autenticação embutida no envelope e **erros com HTTP 200**
(`errorMsg`/`ValidationMessage` no corpo).

Validação viva (2026-07-15, staging): queries settle/return 200 com auth aceita. 7 unit + 2
sandbox verdes. Payment/refund/void = **financial-write**, nunca executados (goal §0.5).

Secrets: `pass cambio-real-v2/providers/payliance/staging-env`. Discovery:
`docs/providers/payliance/discovery.md`.
