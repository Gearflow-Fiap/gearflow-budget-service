# GearFlow: fluxo completo da Saga (Fase 4)

Para uma leitura guiada de ponta a ponta, consulte o [guia do fluxo completo](README.md).

## 1. Estratégia

**Saga orquestrada.** O orquestrador é uma state machine persistida e roda dentro do **OS Service**. A comunicação passa pelo **RabbitMQ**.

- O orquestrador é o **único** que decide o próximo passo. Os outros serviços só executam comandos e respondem com eventos.
- Cada participante faz uma **transação local + Outbox**: grava estado + Outbox atomicamente no banco; um worker publica no RabbitMQ pelo menos uma vez.
- Toda mensagem leva `CorrelationId = ServiceOrderId`, junto com o trace do OpenTelemetry.
- Todo passo que altera estado tem uma **compensação idempotente**.
- Os timeouts são processados por job persistido no OS Service, sem plugin de agendamento no broker.

**Por que orquestrada e não coreografada:** o fluxo tem muitos passos e várias compensações. Com o fluxo centralizado, o time de 5 pessoas consegue ler, depurar e demonstrar o estado de cada OS num lugar só (a tabela da saga). Na coreografia o fluxo fica espalhado entre os serviços, e é mais difícil saber "em que pé está a OS X".

## 2. Participantes

| Serviço | Banco | Papel na Saga |
|---|---|---|
| **OS Service** | SQL Server | Orquestrador. Abre a OS, guarda o status e o histórico, e mantém o estado da saga |
| **Execution Service** | PostgreSQL | Diagnóstico, catálogo e preços, estoque (reserva e liberação), fila de execução |
| **Billing Service** | MongoDB | Orçamento (aprovação/rejeição), pagamento via **Mercado Pago**, notificações |
| **Mercado Pago** | externo | Cobrança Pix (`/v1/orders`) e webhook de confirmação |

## 3. Passos da Saga e compensações

| # | Comando (orquestrador → serviço) | Serviço | Sucesso | Falha | Compensação |
|---|---|---|---|---|---|
| 1 | *(local)* abrir OS | OS | `gearflow-os-service.ServiceOrderOpened` | erro de validação (HTTP 400) | `CancelServiceOrder` (local) |
| 2 | `StartDiagnostic` | Execution | `gearflow-execution-service.DiagnosticFinished` (itens + preços) | `gearflow-execution-service.DiagnosticFailed` | `CancelDiagnostic` (remove da fila) |
| 3 | `GenerateBudget` | Billing | `gearflow-budget-service.BudgetGenerated` | `gearflow-budget-service.BudgetGenerationFailed` | `CancelBudget` |
| 4 | *(espera o cliente, timeout de aprovação)* | Billing | `gearflow-budget-service.BudgetApproved` | `gearflow-budget-service.BudgetRejected` / timeout | coberto pelo `CancelBudget` |
| 5 | `RequestPayment` | Billing → MP | `gearflow-budget-service.PaymentConfirmed` | `gearflow-budget-service.PaymentFailed` / timeout de pagamento | **`RefundPayment`** (ou cancela a order pendente) |
| 6 | `ReserveStock` | Execution | `gearflow-execution-service.StockReserved` | `gearflow-execution-service.StockInsufficient` → espera reposição até o timeout | **`ReleaseStock`** |
| 7 | `StartExecution` | Execution | `gearflow-execution-service.ExecutionFinished` | `gearflow-execution-service.ExecutionFailed` | *(ponto sem volta: daqui em diante só retry, sem compensação)* |
| 8 | *(local)* entregar | OS | `gearflow-os-service.ServiceOrderDelivered` → saga concluída | — | — |

**Ordem da compensação**: sempre do passo mais recente para o mais antigo.
`ReleaseStock` → `RefundPayment` → `CancelBudget` → `CancelDiagnostic` → OS `Canceled`.

## 4. Estados da OS / Saga

Os status do legado (`ServiceOrderStatusEnum`) foram mantidos. Entram dois novos: **`AwaitingPayment`** e **`Compensating`**.

```mermaid
stateDiagram-v2
    [*] --> Received: POST /service-orders
    Received --> InDiagnostic: gearflow-execution-service.DiagnosticStarted
    InDiagnostic --> GeneratingBudget: gearflow-execution-service.DiagnosticFinished
    GeneratingBudget --> AwaitingApproval: gearflow-budget-service.BudgetGenerated
    AwaitingApproval --> AwaitingPayment: gearflow-budget-service.BudgetApproved
    AwaitingApproval --> Compensating: gearflow-budget-service.BudgetRejected / ApprovalTimeout
    AwaitingPayment --> ReservingStock: gearflow-budget-service.PaymentConfirmed
    AwaitingPayment --> Compensating: gearflow-budget-service.PaymentFailed / PaymentTimeout
    ReservingStock --> InExecution: gearflow-execution-service.StockReserved
    ReservingStock --> AwaitingPartsOrConsumables: gearflow-execution-service.StockInsufficient
    AwaitingPartsOrConsumables --> InExecution: gearflow-execution-service.StockReplenished + gearflow-execution-service.StockReserved
    AwaitingPartsOrConsumables --> Compensating: StockTimeout
    InExecution --> Finalized: gearflow-execution-service.ExecutionFinished
    Finalized --> Delivered: POST /service-orders/{id}/deliver
    Delivered --> [*]
    Compensating --> Canceled: todas as compensações confirmadas
    Canceled --> [*]
```

## 5. Caminho feliz, ponta a ponta

```mermaid
sequenceDiagram
    autonumber
    actor Atendente
    actor Mecanico as Mecânico
    actor Cliente
    participant OS as OS Service (Orquestrador)
    participant MQ as RabbitMQ
    participant EX as Execution Service
    participant BI as Billing Service
    participant MP as Mercado Pago

    Atendente->>OS: POST /service-orders (cliente, veículo)
    OS->>OS: cria OS [Received] + inicia saga (Outbox)
    OS->>MQ: StartDiagnostic
    MQ->>EX: StartDiagnostic
    EX->>EX: coloca OS na fila de diagnóstico

    Mecanico->>EX: POST /diagnostics/{osId}/start
    EX->>MQ: gearflow-execution-service.DiagnosticStarted
    MQ->>OS: gearflow-execution-service.DiagnosticStarted → [InDiagnostic]
    Mecanico->>EX: POST /diagnostics/{osId}/finish (serviços, peças, insumos)
    EX->>EX: snapshot de preços do catálogo
    EX->>MQ: gearflow-execution-service.DiagnosticFinished (itens + preços)
    MQ->>OS: gearflow-execution-service.DiagnosticFinished → [GeneratingBudget]

    OS->>MQ: GenerateBudget (osId, nome/e-mail do cliente, itens)
    MQ->>BI: GenerateBudget
    BI->>BI: cria Budget [Pending] (Outbox)
    BI-->>Cliente: e-mail com links Aprovar / Rejeitar
    BI->>MQ: gearflow-budget-service.BudgetGenerated
    MQ->>OS: gearflow-budget-service.BudgetGenerated → [AwaitingApproval] + agenda ApprovalTimeout

    Cliente->>BI: GET /api/budgets/{id}/approve?token=...
    BI->>BI: Budget [Approved]
    BI->>MQ: gearflow-budget-service.BudgetApproved
    MQ->>OS: gearflow-budget-service.BudgetApproved → [AwaitingPayment]

    OS->>MQ: RequestPayment (budgetId, valor)
    MQ->>BI: RequestPayment
    BI->>MP: POST /v1/orders (Pix, X-Idempotency-Key = budgetId)
    MP-->>BI: order [action_required] + ticket_url / QR code
    BI->>BI: Payment [Pending]
    BI-->>Cliente: e-mail com Pix (ticket_url)
    BI->>MQ: gearflow-budget-service.PaymentRequested
    MQ->>OS: gearflow-budget-service.PaymentRequested → agenda PaymentTimeout

    Cliente->>MP: paga o Pix
    MP->>BI: POST /api/payments/webhook (x-signature)
    BI->>BI: valida assinatura + dedup
    BI->>MP: GET /v1/orders/{id} (confere status real)
    MP-->>BI: order [processed / paid]
    BI->>BI: Payment [Paid], Budget [Paid]
    BI->>MQ: gearflow-budget-service.PaymentConfirmed
    MQ->>OS: gearflow-budget-service.PaymentConfirmed → [ReservingStock]

    OS->>MQ: ReserveStock (itens)
    MQ->>EX: ReserveStock
    EX->>EX: reserva peças e insumos (transação local)
    EX->>MQ: gearflow-execution-service.StockReserved
    MQ->>OS: gearflow-execution-service.StockReserved → [InExecution]
    OS->>MQ: StartExecution
    MQ->>EX: StartExecution → fila de execução

    Mecanico->>EX: POST /executions/{osId}/jobs/{jobId}/done (cada serviço)
    EX->>EX: baixa definitiva do estoque
    EX->>MQ: gearflow-execution-service.ExecutionFinished
    MQ->>OS: gearflow-execution-service.ExecutionFinished → [Finalized]
    OS-->>Cliente: notificação "veículo pronto"

    Atendente->>OS: POST /service-orders/{id}/deliver
    OS->>OS: [Delivered] → saga concluída
```

## 6. Cenários de falha (compensação)

### 6.1 Orçamento rejeitado ou timeout de aprovação
```mermaid
sequenceDiagram
    autonumber
    actor Cliente
    participant OS as OS Service (Orquestrador)
    participant BI as Billing
    participant EX as Execution

    Cliente->>BI: GET /api/budgets/{id}/reject
    BI->>OS: gearflow-budget-service.BudgetRejected
    Note over OS: ou ApprovalTimeout disparado pelo scheduler
    OS->>OS: [Compensating]
    OS->>BI: CancelBudget
    BI->>BI: Budget [Rejected/Expired]
    BI->>OS: gearflow-budget-service.BudgetCanceled
    OS->>EX: CancelDiagnostic
    EX->>OS: gearflow-execution-service.DiagnosticCanceled
    OS->>OS: [Canceled] → saga encerrada
```

### 6.2 Pagamento não realizado ou recusado
```mermaid
sequenceDiagram
    autonumber
    participant OS as OS Service (Orquestrador)
    participant BI as Billing
    participant MP as Mercado Pago

    Note over OS: PaymentTimeout ou gearflow-budget-service.PaymentFailed (webhook)
    OS->>OS: [Compensating]
    OS->>BI: CancelBudget
    BI->>MP: cancela order pendente
    MP-->>BI: order [canceled]
    BI->>BI: Payment [Canceled], Budget [Canceled]
    BI->>OS: gearflow-budget-service.BudgetCanceled
    OS->>OS: [Canceled]
```

### 6.3 Pagou, mas faltou estoque (compensação mais completa: **demo principal**)
```mermaid
sequenceDiagram
    autonumber
    actor Cliente
    participant OS as OS Service (Orquestrador)
    participant EX as Execution
    participant BI as Billing
    participant MP as Mercado Pago

    OS->>EX: ReserveStock
    EX->>OS: gearflow-execution-service.StockInsufficient (item, necessário, disponível)
    OS->>OS: [AwaitingPartsOrConsumables] + agenda StockTimeout
    Note over OS: StockTimeout expira sem reposição
    OS->>OS: [Compensating]
    OS->>EX: ReleaseStock (libera reservas parciais)
    EX->>OS: gearflow-execution-service.StockReleased
    OS->>BI: RefundPayment
    BI->>MP: reembolso da order
    MP-->>BI: refund OK
    BI->>BI: Payment [Refunded]
    BI-->>Cliente: e-mail "pagamento estornado"
    BI->>OS: gearflow-budget-service.PaymentRefunded
    OS->>BI: CancelBudget
    BI->>OS: gearflow-budget-service.BudgetCanceled
    OS->>OS: [Canceled]
```

### 6.4 Falhas técnicas (serviço fora do ar, MP indisponível)
| Situação | Tratamento |
|---|---|
| Consumer lança exceção | Retry no worker (1s, 5s, 15s), depois fila `<origem>.dlq` + alerta |
| MP retorna 5xx/timeout | Polly (retry + circuit breaker). Se esgotar as tentativas, publica `gearflow-budget-service.PaymentFailed` e a saga compensa |
| Webhook não chegou | Job de **reconciliação** consulta `GET /v1/orders/{id}` dos pagamentos `Pending` |
| Mensagem duplicada | Inbox/dedup por `MessageId` + chaves idempotentes (`BudgetId`, `OsId`) |
| Serviço caiu no meio | A saga fica persistida no SQL Server. Quando o serviço volta, as mensagens pendentes no RabbitMQ são consumidas e o fluxo continua |

## 7. Contratos de mensagens (pacote `GearFlow.Contracts`)

| Mensagem | Tipo | Publicada por | Consumida por |
|---|---|---|---|
| `gearflow-os-service.ServiceOrderOpened` | evento | OS | OS (saga) |
| `StartDiagnostic` / `CancelDiagnostic` | comando | OS | Execution |
| `gearflow-execution-service.DiagnosticStarted` / `gearflow-execution-service.DiagnosticFinished` / `gearflow-execution-service.DiagnosticCanceled` | evento | Execution | OS |
| `GenerateBudget` / `CancelBudget` | comando | OS | Billing |
| `gearflow-budget-service.BudgetGenerated` / `gearflow-budget-service.BudgetApproved` / `gearflow-budget-service.BudgetRejected` / `gearflow-budget-service.BudgetCanceled` | evento | Billing | OS |
| `RequestPayment` / `RefundPayment` | comando | OS | Billing |
| `gearflow-budget-service.PaymentRequested` / `gearflow-budget-service.PaymentConfirmed` / `gearflow-budget-service.PaymentFailed` / `gearflow-budget-service.PaymentRefunded` | evento | Billing | OS |
| `ReserveStock` / `ReleaseStock` / `StartExecution` | comando | OS | Execution |
| `gearflow-execution-service.StockReserved` / `gearflow-execution-service.StockInsufficient` / `gearflow-execution-service.StockReplenished` / `gearflow-execution-service.StockReleased` / `gearflow-execution-service.ExecutionFinished` | evento | Execution | OS |
| `ApprovalTimeout` / `PaymentTimeout` / `StockTimeout` | agendado | OS (scheduler) | OS |

Campos comuns a todas: `CorrelationId` (= `ServiceOrderId`), `MessageId`, `OccurredOn`.

## 8. O que mostrar no vídeo

1. Caminho feliz completo (seção 5), com o trace distribuído aberto na ferramenta de observabilidade.
2. Cenário 6.3: o pagamento é estornado no painel do Mercado Pago e a OS termina `Canceled`.
3. Derrubar o Billing (`kubectl scale --replicas=0`), aprovar o orçamento, subir o Billing de novo e mostrar o fluxo continuando de onde parou.
4. Mostrar a tabela de estado da saga no SQL Server e a DLQ no Management do RabbitMQ.

## 9. RabbitMQ e ambiente local

Consulte [rabbitmq.md](rabbitmq.md) para exchanges, filas e payloads e [ambiente-local.md](ambiente-local.md) para Compose e HTTP. Timeouts são persistidos pelo orquestrador e processados por job; timeouts usam job persistido, sem plugin de agendamento. Diagramas de compensação omitem o broker: toda comunicação entre serviços usa RabbitMQ.

## Fronteira arquitetural

A implementação segue [Ports and Adapters](arquitetura-hexagonal.md). HTTP, consumers RabbitMQ e jobs chamam portas de entrada da Application. MongoDB, Mercado Pago, SMTP e publisher RabbitMQ implementam portas de saída. GearFlow.Contracts e envelopes RabbitMQ ficam nos adaptadores, com mapeamento para commands e eventos internos; o núcleo não referencia tecnologias de transporte ou persistência.

## Pagamento simulado (fluxo atual)

Com MercadoPago:UseMock=true, RequestPayment chama MockPaymentGateway e recebe Paid imediatamente. O Billing grava Payment e Budget como Paid e registra PaymentRequested e PaymentConfirmed no Outbox na mesma transação. A Saga pode continuar ao consumir PaymentConfirmed, sem webhook, Pix real ou chamada HTTP ao Mercado Pago. RefundPayment recebe confirmação simulada e publica PaymentRefunded pelo fluxo existente. Repetições mantêm a idempotência local. Os demais diagramas de interação com o provedor descrevem o modo HTTP opcional (UseMock=false).
