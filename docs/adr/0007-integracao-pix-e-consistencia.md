# ADR 0007 — Integração Pix, operações duráveis e consistência local

Status: implementada; validação com Mongo real e homologação MP pendentes. Data: 02/11/2026.

## Decisão

O adaptador MercadoPagoGateway implementa IPaymentGateway usando Orders API online/Pix com processamento automático. Contratos HTTP/JSON ficam nos Adapters. Application coordena PaymentWorkflow, sem HttpClient, Mongo ou envelopes externos. Valores, elegibilidade e transições continuam no domínio.

Persistir Payment e PaymentAttempt antes de HTTP. OperationId é UUID estável por create/cancel/refund e forma X-Idempotency-Key. A criação usa valor/duração derivados da intenção persistida; não recalcular duração em retries. A validade local inicial do Pix é 30 minutos. Criação assíncrona pode registrar order sem QR/ticket; consulta posterior preenche os dados, sem novo PaymentRequested. Token e QR não são registrados nos logs.

Mementos neutros pertencem ao núcleo; o adaptador serializa esses snapshots em JSON privado do Billing, com validação na reconstituição e sem emitir eventos. IDs de documento/referências são UUIDs canônicos em string D; valores de índices/projeções monetárias usam Decimal128 e datas de metadados/TTL usam BSON UTC. Snapshot não é envelope de integração nem modelo do provedor. Alterar o formato persistido requer migração/versionamento antes de implantação sobre dados existentes.

Após resposta perdida, buscar por external_reference e período desde CreatedOn, obter a order completa e conferir vínculo/valor/moeda. Resultado vazio não provoca troca de chave. Antes do prazo original pode-se repetir o POST com a mesma chave/corpo; depois do prazo, ausência de order exige intervenção, sem iniciar nova cobrança. Múltiplas orders também exigem reconciliação manual.

Cada requisição HTTP tem até 10s, até quatro tentativas e orçamento total de 55s, incluindo Retry-After e backoff com jitter. Caller cancellation não é repetido. Não repetir 4xx, exceto 429. Cinco chamadas esgotadas abrem o circuit breaker local por 30s. POST de cancel/refund sempre usa chave própria e consulta posterior; resposta aceita/processando não comprova compensação concluída. Refund integral envia corpo vazio; confirmação exige evidência de refund processado com valor integral e ID.

## Exceções explícitas entre agregados

Além da confirmação já permitida no ADR 0005, registrar a cobrança atualiza Payment, Budget.AwaitingPayment e Outbox na mesma transação Mongo. Confirmar refund atualiza Payment.Refunded, Budget.Refunded, Outbox e intenção de notificação na mesma transação. Isso impede anunciar cobrança/reembolso sem o estado local correspondente. Nenhum HTTP ocorre dentro dessas transações. Intenção financeira tem commit separado antes de chamar o provedor.

Cancelamento financeiro e cancelamento do orçamento têm commits locais separados: primeiro confirmar a cobrança cancelada, depois o handler relê Payment e Budget e cancela o orçamento com Outbox. Crash entre essas fases é recuperável por repetição. Se o pagamento vencer a corrida, o cancelamento não anuncia sucesso.

Confirmação consultada e compatível depois de cancelamento local é uma exceção explícita à proteção usual do estado terminal: Payment.Canceled → Paid e Budget.Canceled → Paid por RecognizeLatePayment, preservando motivo/histórico e publicando PaymentConfirmed. Isso registra dinheiro recebido e permite à OS coordenar refund. Não há reembolso automático decidido pelo Billing. Refunded nunca regride por observação tardia Paid.

## Recepção e recuperação

POST /api/payments/webhook valida HMAC-SHA256 do manifesto com data.id da query, x-request-id e ts, comparação constante, normalização de ID e tolerância temporal de cinco minutos. Exige assinatura mesmo em Development; data.id do corpo deve corresponder ao recurso autenticado, mas status/valor do corpo não são usados para confirmar pagamento. Configurar tópico Order e URL HTTPS no painel do MP.

Somente após inserir trabalho com write concern majority responde 200. payment_work usa chave derivada dos campos assinados, lease de dois minutos e owner exclusivo; worker tem deadline de 100s. A consulta ao provedor ocorre fora da requisição HTTP. Falhas são persistidas com backoff limitado a 15min, sem inventar PaymentFailed. TTL de 30 dias só sobre completedOn, para nunca apagar trabalho pendente durante indisponibilidade. Esta coleção cumpre o papel de deduplicação/trabalho do webhook, em vez de uma processed_webhooks separada.

Reconciliação a cada cinco minutos seleciona Created/Pending sem atualização há dez minutos, operações financeiras incompletas e cancelados dos últimos 30 dias (para pagamento tardio). lastReconciledOn permite rodízio de lotes, e enfileiramento usa chave por PaymentId/janela de cinco minutos. Índices únicos e versões otimistas protegem escritas entre réplicas. Inbox RabbitMQ e publisher Outbox continuam fora desta entrega; readiness do serviço completo permanece 503.

## Evidência e limites

MercadoPagoGatewayTests valida payloads/headers, retries, timeout real por tentativa, circuit breaker, busca e mapeamentos. PaymentWebhookTests cobre assinatura, persistência antes do ACK e DI completo. PaymentScenarios cobre rollback, resposta perdida, duplicatas e transições pelos casos de uso. SnapshotTests valida reconstituição sem emitir novos eventos. Dois MongoFact testam transação/versão e fila/lease com replica set real quando GEARFLOW_TEST_MONGO está definido; ficam explicitamente ignorados sem esse ambiente. Nenhum teste com fake comprova a semântica transacional do Mongo.

Fonte e operações conferidas: [Pix](https://www.mercadopago.com.br/developers/en/docs/checkout-api-orders/payment-integration/websites/pix), [busca](https://www.mercadopago.com.br/developers/en/reference/online-payments/checkout-api/search-order/get), [refund](https://www.mercadopago.com.br/developers/en/reference/online-payments/checkout-api/refund-order/post), [webhook de order](https://www.mercadopago.com.br/developers/en/docs/checkout-api-orders/notifications) e [normalização do ID no HMAC](https://www.mercadopago.com.ar/developers/en/docs/wallet-connect/notifications).

## Homologação Pix local em 03/11/2026

TestMode no adaptador envia APRO exclusivamente em Development. WebhookEnabled=false só é aceito nesse modo; não registra endpoint e permite testar por consulta sem segredo de assinatura/HTTPS. Produção preserva webhook assinado e rejeita esse modo. Conta test_user e Pix BRL 50,00 confirmados pela API real, com estado Billing/Outbox; detalhes em mercado-pago-local.md. Paginação de busca aceita total inteiro como número ou string conforme resposta real; incerteza e deduplicação mantêm a política anterior.
