# Integração Mercado Pago — Orders API Pix

Implementada por IPaymentGateway → MercadoPagoGateway. Decisões de consistência e recuperação no [ADR 0007](adr/0007-integracao-pix-e-consistencia.md).

## Teste local homologado sem webhook

Ver [mercado-pago-local.md](mercado-pago-local.md): Pix de teste BRL 50,00 confirmado por consulta em 03/11/2026. APRO habilitado somente em Development/TestMode. O roteiro HTTPS abaixo aplica-se ao teste de entrega real de webhook, que continua pendente.

## Entrada e fluxo

Não foi criada rota REST para cadastrar dados ou pedir cobrança. Os commands são internos e chamados pelo consumer RabbitMQ:

| Comando | Handler | Resultado |
|---|---|---|
| RequestPaymentCommand | RequestPaymentHandler | Intenção durável → HTTP Pix → Payment/ Budget/Outbox |
| ProcessPaymentWebhookCommand | ProcessPaymentWebhookHandler | Consulta order; não recebe status como evidência |
| RecoverPaymentCommand | RecoverPaymentHandler | Recupera create/cancel/refund com identidade persistida |
| ReconcilePaymentsCommand | ReconcilePaymentsHandler | Seleciona candidatos e enfileira trabalho durável |
| RefundPaymentCommand | RefundPaymentHandler | Refund integral e confirmação posterior |
| CancelBudgetCommand | CancelBudgetHandler + IPaymentCompensator | Resolve cobrança antes de cancelar orçamento |

RabbitMQ/Inbox/Outbox, SMTP, Swagger e HTTP autenticado estão implementados. O Compose ativa os componentes com WireMock; consulte [estado executável](estado-implementacao.md), [operação](operacao.md) e [compatibilidade externa](integracao-servicos.md). Execução Docker e homologação real continuam pendentes.

## Ativar

Por padrão MercadoPago:Enabled=false. Para ativar, fornecer:

```text
MercadoPago__Enabled=true
MercadoPago__BaseUrl=https://api.mercadopago.com/
MercadoPago__AccessToken=<segredo externo da conta de teste>
MercadoPago__WebhookSecret=<segredo de assinatura da aplicação>
ConnectionStrings__Mongo=<conexão com replica set do Billing>
Mongo__Database=gearflow_billing
Billing__ApprovalTokenSecret=<pelo menos 32 bytes, segredo externo>
Billing__PublicBaseUrl=https://<host-publico-do-billing>
```

Obter credenciais apropriadas para Orders da conta de teste e configurar a URL HTTPS pública `/api/payments/webhook` com tópico **Order (Mercado Pago)** no painel de Webhooks da aplicação. Configurar teste e produção separadamente. NotificationUrl do Compose é apenas lembrete/configuração histórica: o adaptador não envia campo notification_url sem suporte no contrato Orders; a URL é configurada no painel. Não colocar tokens no código, logs ou URLs.

Em Development, BaseUrl também aceita HTTP em loopback ou host wiremock. Fora de Development exige HTTPS no host api.mercadopago.com; redirects são desativados. Credenciais placeholder são rejeitadas. Quando habilitado, o webhook sempre exige assinatura; sua desativação só é aceita em Development/TestMode. O host verifica existência de primary replica set e inicializa coleções/índices antes de iniciar workers.

## Verificar localmente

```powershell
dotnet restore GearFlow.Billing.sln
dotnet build GearFlow.Billing.sln -c Release --no-restore
dotnet test GearFlow.Billing.sln -c Release --no-build --logger trx

# Opcional, quando houver replica set descartável acessível:
$env:GEARFLOW_TEST_MONGO = 'mongodb://localhost:27017/?replicaSet=rs0&directConnection=true'
dotnet test tests/GearFlow.Billing.IntegrationTests -c Release --filter FullyQualifiedName~MongoPaymentPersistenceTests
```

Os testes Mongo criam e removem somente banco com nome gerado gearflow_test_<uuid>; nunca usam Mongo__Database da aplicação. Infraestrutura pode ser habilitada também por Testcontainers ou mongod nativo; ver validacao-local.md. Os testes HTTP usam HttpMessageHandler controlado, sem acessar a conta MP. Pix sandbox sem webhook foi executado com sucesso; entrega HTTPS/webhook real continua pendente.

RabbitMQ/Inbox/Outbox, SMTP, Swagger e HTTP autenticado estão implementados. O Compose ativa os componentes com WireMock; consulte [estado executável](estado-implementacao.md), [operação](operacao.md) e [compatibilidade externa](integracao-servicos.md). Execução Docker e homologação real continuam pendentes.

## Stubs WireMock

Fixtures explícitas para BudgetId `01234567-0123-4123-8123-012345678901`, valor BRL 350,00 e order `ORDLOCAL001`. São contratos isolados de teste, não um simulador de múltiplos clientes e não um fluxo ponta a ponta com OS.

O cenário `billing-pix` começa em Started; criar Pix passa a pending. Para simular confirmação via painel/admin local do WireMock, alterar esse cenário para paid. Cancelamento passa a canceled e refund de paid passa a refunded. Reiniciar o cenário via endpoint administrativo do WireMock, sem remover volumes Mongo. GET de ORDERROR400, ORDERROR422, ORDERROR429 e ORDERROR500 simula erros; ORDTIMEOUT demora 11s; ORDINVALID retorna JSON inválido; ORDREJECTED retorna recusa definitiva. IDs e payloads dos stubs não devem ser usados como dinheiro/QR reais.

## Operação e pendências

- Timeout, erro HTTP, JSON inválido e estado desconhecido não viram PaymentFailed. Guardar IDs da intenção e permitir reconciliação; não trocar chave para resolver incerteza.
- Em payment_work, consultar attempts, lastError, availableOn e leaseUntil para diagnosticar trabalho parado. Logs só incluem códigos/tipos, sem corpo do provedor. Não apagar trabalho pendente como forma de retry.
- gateway.duplicate_orders, payment.recovery_required e refund sem intenção local exigem conferência manual com o provedor. O worker mantém trabalho persistido com backoff; não publica sucesso fictício.
- Confirmação tardia após cancelamento registra Paid e PaymentConfirmed; OS deve coordenar refund. Sem OS conectado não existe homologação dessa coordenação.
- Eventos são entregues pelo publisher Outbox com confirms; Inbox transacional deduplica os comandos conforme ADR 0008.
- Alterações locais de estado de negócio nunca devem ser feitas editando documentos Mongo para simular um fluxo aprovado.
