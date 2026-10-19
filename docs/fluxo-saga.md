# GearFlow: fluxo completo da Saga (Fase 4)

## 1. Estratégia

**Saga orquestrada.** O orquestrador é uma state machine do MassTransit e roda dentro do **OS Service**. A comunicação passa pelo **RabbitMQ**.

- O orquestrador é o **único** que decide o próximo passo. Os outros serviços só executam comandos e respondem com eventos.
- Cada participante faz uma **transação local + Outbox**: grava no banco e publica a mensagem de forma atômica.
- Toda mensagem leva `CorrelationId = ServiceOrderId`, junto com o trace do OpenTelemetry.
- Todo passo que altera estado tem uma **compensação idempotente**.
- Os timeouts são agendados pelo orquestrador (scheduler do MassTransit).

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
| 1 | *(local)* abrir OS | OS | `ServiceOrderOpened` | erro de validação (HTTP 400) | `CancelServiceOrder` (local) |
| 2 | `StartDiagnostic` | Execution | `DiagnosticFinished` (itens + preços) | `DiagnosticFailed` | `CancelDiagnostic` (remove da fila) |
| 3 | `GenerateBudget` | Billing | `BudgetGenerated` | `BudgetGenerationFailed` | `CancelBudget` |
| 4 | *(espera o cliente, timeout de aprovação)* | Billing | `BudgetApproved` | `BudgetRejected` / timeout | coberto pelo `CancelBudget` |
| 5 | `RequestPayment` | Billing → MP | `PaymentConfirmed` | `PaymentFailed` / timeout de pagamento | **`RefundPayment`** (ou cancela a order pendente) |
| 6 | `ReserveStock` | Execution | `StockReserved` | `StockInsufficient` → espera reposição até o timeout | **`ReleaseStock`** |
| 7 | `StartExecution` | Execution | `ExecutionFinished` | `ExecutionFailed` | *(ponto sem volta: daqui em diante só retry, sem compensação)* |
| 8 | *(local)* entregar | OS | `ServiceOrderDelivered` → saga concluída | — | — |

**Ordem da compensação**: sempre do passo mais recente para o mais antigo.
`ReleaseStock` → `RefundPayment` → `CancelBudget` → `CancelDiagnostic` → OS `Canceled`.

## 4. Estados da OS / Saga

Os status do legado (`ServiceOrderStatusEnum`) foram mantidos. Entram dois novos: **`AwaitingPayment`** e **`Compensating`**.

```mermaid
stateDiagram-v2
    [*] --> Received: POST /service-orders
    Received --> InDiagnostic: DiagnosticStarted
    InDiagnostic --> GeneratingBudget: DiagnosticFinished
    GeneratingBudget --> AwaitingApproval: BudgetGenerated
    AwaitingApproval --> AwaitingPayment: BudgetApproved
    AwaitingApproval --> Compensating: BudgetRejected / ApprovalTimeout
    AwaitingPayment --> ReservingStock: PaymentConfirmed
    AwaitingPayment --> Compensating: PaymentFailed / PaymentTimeout
    ReservingStock --> InExecution: StockReserved
    ReservingStock --> AwaitingPartsOrConsumables: StockInsufficient
    AwaitingPartsOrConsumables --> InExecution: StockReplenished + StockReserved
    AwaitingPartsOrConsumables --> Compensating: StockTimeout
    InExecution --> Finalized: ExecutionFinished
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
    EX->>MQ: DiagnosticStarted
    MQ->>OS: DiagnosticStarted → [InDiagnostic]
    Mecanico->>EX: POST /diagnostics/{osId}/finish (serviços, peças, insumos)
    EX->>EX: snapshot de preços do catálogo
    EX->>MQ: DiagnosticFinished (itens + preços)
    MQ->>OS: DiagnosticFinished → [GeneratingBudget]

    OS->>MQ: GenerateBudget (osId, nome/e-mail do cliente, itens)
    MQ->>BI: GenerateBudget
    BI->>BI: cria Budget [Pending] (Outbox)
    BI-->>Cliente: e-mail com links Aprovar / Rejeitar
    BI->>MQ: BudgetGenerated
    MQ->>OS: BudgetGenerated → [AwaitingApproval] + agenda ApprovalTimeout

    Cliente->>BI: GET /budgets/{id}/approve?token=...
    BI->>BI: Budget [Approved]
    BI->>MQ: BudgetApproved
    MQ->>OS: BudgetApproved → [AwaitingPayment]

    OS->>MQ: RequestPayment (budgetId, valor)
    MQ->>BI: RequestPayment
    BI->>MP: POST /v1/orders (Pix, X-Idempotency-Key = budgetId)
    MP-->>BI: order [action_required] + ticket_url / QR code
    BI->>BI: Payment [Pending]
    BI-->>Cliente: e-mail com Pix (ticket_url)
    BI->>MQ: PaymentRequested
    MQ->>OS: PaymentRequested → agenda PaymentTimeout

    Cliente->>MP: paga o Pix
    MP->>BI: POST /payments/webhook (x-signature)
    BI->>BI: valida assinatura + dedup
    BI->>MP: GET /v1/orders/{id} (confere status real)
    MP-->>BI: order [processed / paid]
    BI->>BI: Payment [Paid], Budget [Paid]
    BI->>MQ: PaymentConfirmed
    MQ->>OS: PaymentConfirmed → [ReservingStock]

    OS->>MQ: ReserveStock (itens)
    MQ->>EX: ReserveStock
    EX->>EX: reserva peças e insumos (transação local)
    EX->>MQ: StockReserved
    MQ->>OS: StockReserved → [InExecution]
    OS->>MQ: StartExecution
    MQ->>EX: StartExecution → fila de execução

    Mecanico->>EX: POST /executions/{osId}/jobs/{jobId}/done (cada serviço)
    EX->>EX: baixa definitiva do estoque
    EX->>MQ: ExecutionFinished
    MQ->>OS: ExecutionFinished → [Finalized]
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

    Cliente->>BI: GET /budgets/{id}/reject
    BI->>OS: BudgetRejected
    Note over OS: ou ApprovalTimeout disparado pelo scheduler
    OS->>OS: [Compensating]
    OS->>BI: CancelBudget
    BI->>BI: Budget [Rejected/Expired]
    BI->>OS: BudgetCanceled
    OS->>EX: CancelDiagnostic
    EX->>OS: DiagnosticCanceled
    OS->>OS: [Canceled] → saga encerrada
```

### 6.2 Pagamento não realizado ou recusado
```mermaid
sequenceDiagram
    autonumber
    participant OS as OS Service (Orquestrador)
    participant BI as Billing
    participant MP as Mercado Pago

    Note over OS: PaymentTimeout ou PaymentFailed (webhook)
    OS->>OS: [Compensating]
    OS->>BI: CancelBudget
    BI->>MP: cancela order pendente
    MP-->>BI: order [canceled]
    BI->>BI: Payment [Canceled], Budget [Canceled]
    BI->>OS: BudgetCanceled
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
    EX->>OS: StockInsufficient (item, necessário, disponível)
    OS->>OS: [AwaitingPartsOrConsumables] + agenda StockTimeout
    Note over OS: StockTimeout expira sem reposição
    OS->>OS: [Compensating]
    OS->>EX: ReleaseStock (libera reservas parciais)
    EX->>OS: StockReleased
    OS->>BI: RefundPayment
    BI->>MP: reembolso da order
    MP-->>BI: refund OK
    BI->>BI: Payment [Refunded]
    BI-->>Cliente: e-mail "pagamento estornado"
    BI->>OS: PaymentRefunded
    OS->>BI: CancelBudget
    BI->>OS: BudgetCanceled
    OS->>OS: [Canceled]
```

### 6.4 Falhas técnicas (serviço fora do ar, MP indisponível)
| Situação | Tratamento |
|---|---|
| Consumer lança exceção | Retry do MassTransit (ex.: 3x com backoff), depois **fila `_error` (DLQ)** + alerta |
| MP retorna 5xx/timeout | Polly (retry + circuit breaker). Se esgotar as tentativas, publica `PaymentFailed` e a saga compensa |
| Webhook não chegou | Job de **reconciliação** consulta `GET /v1/orders/{id}` dos pagamentos `Pending` |
| Mensagem duplicada | Inbox/dedup por `MessageId` + chaves idempotentes (`BudgetId`, `OsId`) |
| Serviço caiu no meio | A saga fica persistida no SQL Server. Quando o serviço volta, as mensagens pendentes no RabbitMQ são consumidas e o fluxo continua |

## 7. Contratos de mensagens (pacote `GearFlow.Contracts`)

| Mensagem | Tipo | Publicada por | Consumida por |
|---|---|---|---|
| `ServiceOrderOpened` | evento | OS | OS (saga) |
| `StartDiagnostic` / `CancelDiagnostic` | comando | OS | Execution |
| `DiagnosticStarted` / `DiagnosticFinished` / `DiagnosticCanceled` | evento | Execution | OS |
| `GenerateBudget` / `CancelBudget` | comando | OS | Billing |
| `BudgetGenerated` / `BudgetApproved` / `BudgetRejected` / `BudgetCanceled` | evento | Billing | OS |
| `RequestPayment` / `RefundPayment` | comando | OS | Billing |
| `PaymentRequested` / `PaymentConfirmed` / `PaymentFailed` / `PaymentRefunded` | evento | Billing | OS |
| `ReserveStock` / `ReleaseStock` / `StartExecution` | comando | OS | Execution |
| `StockReserved` / `StockInsufficient` / `StockReplenished` / `StockReleased` / `ExecutionFinished` | evento | Execution | OS |
| `ApprovalTimeout` / `PaymentTimeout` / `StockTimeout` | agendado | OS (scheduler) | OS |

Campos comuns a todas: `CorrelationId` (= `ServiceOrderId`), `MessageId`, `OccurredOn`.

## 8. O que mostrar no vídeo

1. Caminho feliz completo (seção 5), com o trace distribuído aberto na ferramenta de observabilidade.
2. Cenário 6.3: o pagamento é estornado no painel do Mercado Pago e a OS termina `Canceled`.
3. Derrubar o Billing (`kubectl scale --replicas=0`), aprovar o orçamento, subir o Billing de novo e mostrar o fluxo continuando de onde parou.
4. Mostrar a tabela de estado da saga no SQL Server e a DLQ no painel do RabbitMQ.
