# Contratos RabbitMQ v1

Especificação a implementar com `RabbitMQ.Client`. JSON UTF-8, propriedades camelCase, datas UTC ISO 8601, IDs UUID, valores monetários decimal em BRL. Pacote compartilhado: `GearFlow.Contracts`. Evolução compatível permite campos opcionais; mudança incompatível cria exchange/filas e contrato v2.

## Padrão dos nomes de eventos

Todo evento externo usa `<nome-do-repositório>.<Evento>`, tanto no campo `messageType` do envelope quanto na routing key AMQP. O nome do repositório é preservado em minúsculas com hífens; o evento usa PascalCase. Exemplo: `gearflow-budget-service.BudgetApproved`.

| Repositório produtor | Padrão / exemplo |
|---|---|
| gearflow-budget-service | gearflow-budget-service.BudgetApproved |
| gearflow-execution-service | gearflow-execution-service.DiagnosticFinished |
| gearflow-os-service | gearflow-os-service.ServiceOrderOpened |

Os tipos C# internos continuam identificadores válidos, como BudgetApprovedEvent; o adaptador traduz para o nome externo completo. Comandos mantêm os nomes e routing keys já definidos (por exemplo, GenerateBudget / billing.GenerateBudget). Exchanges e filas mantêm seus nomes; bindings de eventos usam o prefixo do repositório produtor. Consumidores devem ser atualizados junto com os produtores; mensagens antigas com outro nome precisam de tratamento explícito durante a migração.

## Exchanges, filas e routing keys

Exchanges duráveis do tipo topic: `gearflow.commands.v1`, `gearflow.events.v1` e `gearflow.deadletters.v1`. Topologia importada pelo serviço rabbitmq-init via Management API, após o broker ficar saudável, a partir de docker/rabbitmq/definitions.json.

| Exchange | Routing key | Fila destino | Produtor → consumidor | Tipos |
|---|---|---|---|---|
| gearflow.commands.v1 | billing.<MessageType> | gearflow.billing.commands.v1 | OS → Billing | GenerateBudget, RequestPayment, CancelBudget, RefundPayment |
| gearflow.commands.v1 | execution.<MessageType> | gearflow.execution.commands.v1 | OS → Execution | StartDiagnostic, CancelDiagnostic, ReserveStock, ReleaseStock, StartExecution |
| gearflow.events.v1 | gearflow-budget-service.<Evento> | gearflow.os.billing.events.v1 | Billing → OS | gearflow-budget-service.BudgetGenerated, gearflow-budget-service.BudgetGenerationFailed, gearflow-budget-service.BudgetApproved, gearflow-budget-service.BudgetRejected, gearflow-budget-service.BudgetCanceled, gearflow-budget-service.PaymentRequested, gearflow-budget-service.PaymentConfirmed, gearflow-budget-service.PaymentFailed, gearflow-budget-service.PaymentRefunded |
| gearflow.events.v1 | gearflow-execution-service.<Evento> | gearflow.os.execution.events.v1 | Execution → OS | gearflow-execution-service.DiagnosticStarted, gearflow-execution-service.DiagnosticFinished, gearflow-execution-service.DiagnosticFailed, gearflow-execution-service.DiagnosticCanceled, gearflow-execution-service.StockReserved, gearflow-execution-service.StockInsufficient, gearflow-execution-service.StockReplenished, gearflow-execution-service.StockReleased, gearflow-execution-service.ExecutionFinished, gearflow-execution-service.ExecutionFailed |
| gearflow.events.v1 | gearflow-os-service.<Evento> | gearflow.os.events.v1 | OS → assinante de demonstração | gearflow-os-service.ServiceOrderOpened, gearflow-os-service.ServiceOrderDelivered |

Exemplo: gearflow-budget-service.BudgetApproved é publicado em gearflow.events.v1 com routing key gearflow-budget-service.BudgetApproved. Bindings gearflow-budget-service.*, gearflow-execution-service.* e gearflow-os-service.* entregam as mensagens às filas acima. O sufixo usa exatamente o nome do tipo, com maiúsculas/minúsculas preservadas.

Instâncias do mesmo módulo competem pela mesma fila. Cada módulo que precise de uma cópia independente cria sua própria fila durável e binding no exchange de eventos; não compartilhar a fila do OS entre módulos diferentes. A fila gearflow.os.events.v1 é uma assinatura de demonstração sem consumidor implementado.

Cada fila possui <nome>.dlq ligada ao exchange gearflow.deadletters.v1 com routing key igual ao nome da fila original. Filas classic duráveis em nó único para desenvolvimento, sem expiração automática de mensagens. Produção deve definir quorum queues, cluster, TLS, permissões e retenção operacional. A aplicação publica explicitamente na DLQ com publisher confirms; não depende de dead-lettering automático ou plugin de agendamento.

## Envelope obrigatório

Propriedades AMQP: MessageId = messageId, CorrelationId = serviceOrderId, ContentType = application/json, mensagem persistente. Routing key conforme a tabela; serviceOrderId é correlação, não chave de particionamento. Headers traceparent e tracestate quando disponíveis. causationId referencia a mensagem de origem (null para origem HTTP).

```json
{
  "messageId": "e02a43e6-4568-4417-88f0-e578f402ed1c",
  "messageType": "gearflow-budget-service.BudgetApproved",
  "schemaVersion": 1,
  "occurredOn": "2026-10-29T00:00:00Z",
  "correlationId": "2b817623-634a-42bd-87ef-979e0b147ff3",
  "serviceOrderId": "2b817623-634a-42bd-87ef-979e0b147ff3",
  "causationId": null,
  "producer": "gearflow-budget-service",
  "payload": {
    "budgetId": "82e252dd-c294-4e02-a8c6-c1bd4a03cae6",
    "totalAmount": 350.00,
    "currency": "BRL",
    "approvedOn": "2026-10-29T00:00:00Z"
  }
}
```

## Payloads do Billing

Os campos abaixo são obrigatórios salvo indicação. `serviceOrderId` vem no envelope de todas as mensagens. `item` = `{ referenceId, type: Job|Part|Consumable, description, unitPrice, quantity }`, preços não negativos, quantidade positiva; total recalculado pelo domínio.

| Tipo | Payload |
|---|---|
| GenerateBudget | customer: {name, email}, items: item[], expiresOn |
| RequestPayment | budgetId, amount, currency (BRL); validar valor contra orçamento aprovado |
| CancelBudget | budgetId, reason |
| RefundPayment | budgetId, paymentId, reason; reembolso integral |
| gearflow-budget-service.BudgetGenerated | budgetId, totalAmount, currency, expiresOn |
| gearflow-budget-service.BudgetGenerationFailed | code, reason; budgetId opcional se a criação falhar |
| gearflow-budget-service.BudgetApproved | budgetId, totalAmount, currency, approvedOn |
| gearflow-budget-service.BudgetRejected | budgetId, rejectedOn, reason opcional |
| gearflow-budget-service.BudgetCanceled | budgetId, canceledOn, reason |
| gearflow-budget-service.PaymentRequested | budgetId, paymentId, amount, currency, mercadoPagoOrderId, expiresOn |
| gearflow-budget-service.PaymentConfirmed | budgetId, paymentId, amount, currency, mercadoPagoOrderId, paidOn |
| gearflow-budget-service.PaymentFailed | budgetId, code, reason; paymentId e mercadoPagoOrderId opcionais se a criação falhar |
| gearflow-budget-service.PaymentRefunded | budgetId, paymentId, amount, currency, refundedOn, refundId opcional para compensação sem cobrança |

Links de aprovação, QR e tokens não são necessários para orquestrar; ficam na consulta HTTP e nas notificações. Falha técnica de compensação vai à DLQ e mantém a Saga em Compensating para intervenção/retry; não publicar sucesso antes de confirmar o resultado externo.

## Payloads dos demais participantes (contrato proposto)

| Tipo | Payload |
|---|---|
| gearflow-os-service.ServiceOrderOpened | customer: {name,email}, vehicleId, openedOn |
| gearflow-os-service.ServiceOrderDelivered | deliveredOn |
| StartDiagnostic | vehicleId |
| CancelDiagnostic | reason |
| gearflow-execution-service.DiagnosticStarted | startedOn |
| gearflow-execution-service.DiagnosticFinished | items: item[], finishedOn |
| gearflow-execution-service.DiagnosticFailed / gearflow-execution-service.ExecutionFailed | code, reason |
| gearflow-execution-service.DiagnosticCanceled | canceledOn, reason |
| ReserveStock | items: {referenceId,quantity}[] |
| ReleaseStock | reason |
| gearflow-execution-service.StockReserved | reservationId, items: {referenceId,quantity}[] |
| gearflow-execution-service.StockInsufficient | items: {referenceId,required,available}[] |
| gearflow-execution-service.StockReplenished | items: {referenceId,quantity}[]; envelope por OS aguardando esse estoque |
| gearflow-execution-service.StockReleased | releasedOn |
| StartExecution | reservationId |
| gearflow-execution-service.ExecutionFinished | finishedOn |

Esses contratos devem ser compartilhados entre os três repositórios. ApprovalTimeout, PaymentTimeout e StockTimeout são registros internos persistidos do orquestrador, processados por job, sem fila de agendamento no broker.

## Entrega e falhas

1. Adaptador baseado no cliente oficial RabbitMQ.Client 7.x, AMQP 0-9-1. Publicar mensagens persistentes, usar publisher confirms e mandatory=true; tratar Basic.Return como falha de roteamento mesmo que exista confirmação do broker. Só marcar Outbox enviado após confirmação e ausência de retorno.
2. Consumir com autoAck=false. Transação Mongo grava Inbox única (consumerName,messageId), estado e Outbox na mesma sessão. BasicAck somente após commit local; duplicata já processada recebe ack sem repetir efeitos. Após perda de conexão antes do ack, a mensagem pode ser reentregue.
3. Worker Outbox publica e aguarda confirmação antes de marcar enviado. Crash entre publicar e marcar pode duplicar; Inbox resolve. Chamadas MP usam chave idempotente e reconciliação; não manter transação Mongo aberta durante HTTP externo.
4. Retry técnico no worker: 3 retries após a tentativa inicial, intervalos 1s, 5s e 15s, mantendo entrega sem ack. Não usar nack/requeue imediato em loop. Prefetch inicial 1 por consumidor; heartbeat/conexão e timeout de processamento devem permitir a janela de retry.
5. Esgotado retry: publicar no exchange gearflow.deadletters.v1 com routing key da fila original, envelope original, erro sanitizado, attempts e failedOn. Aguardar publisher confirm e verificar ausência de Basic.Return antes de dar ack na origem. Se publicar falhar, não confirmar a origem; interromper consumo/reconectar com backoff. DLQ pode duplicar após crash, exigindo replay idempotente.
6. Tipo/versão desconhecido e JSON inválido seguem o mesmo caminho de DLQ, preservando bytes originais em base64 quando necessário. Erros de negócio publicam evento de falha via Outbox e recebem ack após commit.
7. Não presumir ordem global por OS: múltiplos consumidores, retries e reentregas podem alterar a ordem observada. A Saga valida estado, duplicatas e eventos atrasados. Paralelismo por OS e controle de concorrência devem ser implementados quando necessários.
8. Replay após correção é operacional e auditado: republicar no exchange/routing key original com o mesmo messageId/correlationId. Inbox registra apenas sucessos. Não remover da DLQ antes de confirmar republicação; respeitar dependências da Saga.

RabbitMQ não torna MongoDB e broker uma única transação. Entrega pelo menos uma vez + Outbox/Inbox permanece necessária. Timeouts continuam em job persistido do OS; nenhum plugin delayed-message é exigido.

Referências: [cliente .NET oficial](https://www.rabbitmq.com/client-libraries/dotnet), [publisher confirms e acknowledgements](https://www.rabbitmq.com/docs/confirms) e [importação de topologia](https://www.rabbitmq.com/docs/definitions).

## Fronteira arquitetural

A implementação segue [Ports and Adapters](arquitetura-hexagonal.md). HTTP, consumers RabbitMQ e jobs chamam portas de entrada da Application. MongoDB, Mercado Pago, SMTP e publisher RabbitMQ implementam portas de saída. GearFlow.Contracts e envelopes RabbitMQ ficam nos adaptadores, com mapeamento para commands e eventos internos; o núcleo não referencia tecnologias de transporte ou persistência.
