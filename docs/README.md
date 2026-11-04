# GearFlow — guia do fluxo completo

RabbitMQ/Inbox/Outbox, SMTP, Swagger e HTTP autenticado estão implementados. O Compose ativa os componentes com WireMock; consulte [estado executável](estado-implementacao.md), [operação](operacao.md) e [compatibilidade externa](integracao-servicos.md). Execução Docker e homologação real continuam pendentes.

## Quem faz o quê

| Participante | Responsabilidade |
|---|---|
| Operações/OS (OS Service) | Abre a ordem de serviço, mantém seu status e orquestra a Saga: decide o próximo passo e as compensações |
| Execução (Execution Service) | Faz diagnóstico, informa itens/preços, reserva/libera estoque e executa os serviços |
| Orçamento e Pagamento (Billing, este repositório) | Gera orçamento, registra decisão do cliente, cria cobrança Pix, confirma pagamento e realiza cancelamento/reembolso |
| Cliente | Aprova/rejeita o orçamento e paga o Pix |
| Mercado Pago | Processa a cobrança e informa atualizações ao Billing por webhook HTTP |
| RabbitMQ | Transporta comandos e eventos entre os módulos |
| SMTP/Mailpit | Entrega ou permite visualizar os e-mails de orçamento e pagamento |

Operações/OS é o nome usado nesta documentação para o orquestrador. Cada módulo acessa somente seu banco: OS usa SQL Server, Execução usa PostgreSQL e Billing usa MongoDB.

## Fluxo principal, passo a passo

| Passo | Origem → destino | Comunicação | Resultado |
|---|---|---|---|
| 1 | Atendente → Operações/OS | HTTP: abertura da OS | OS criada em Received; Saga iniciada |
| 2 | Operações/OS → Execução | Comando StartDiagnostic via RabbitMQ | Diagnóstico entra na fila |
| 3 | Mecânico → Execução → Operações/OS | HTTP no módulo de Execução; eventos gearflow-execution-service.DiagnosticStarted e gearflow-execution-service.DiagnosticFinished | Diagnóstico concluído com itens, quantidades e preços |
| 4 | Operações/OS → Billing | Comando GenerateBudget com cliente e itens/preços | Billing calcula total, salva Budget Pending e solicita e-mail de aprovação |
| 5 | Billing → Operações/OS | Evento gearflow-budget-service.BudgetGenerated | OS fica AwaitingApproval; orquestrador acompanha prazo |
| 6 | Cliente → Billing | HTTP de aprovação/rejeição com token assinado | Budget Approved ou Rejected |
| 7 | Billing → Operações/OS | Evento gearflow-budget-service.BudgetApproved no caminho feliz | OS fica AwaitingPayment |
| 8 | Operações/OS → Billing | Comando RequestPayment | Billing valida orçamento aprovado e valor antes de cobrar |
| 9 | Billing → Mercado Pago | HTTP: POST /v1/orders para Pix, com chave de idempotência | Order criada; Billing registra Payment Pending e dados do Pix |
| 10 | Billing → Cliente e Operações/OS | E-mail com Pix e evento gearflow-budget-service.PaymentRequested via RabbitMQ | Cliente recebe instruções; orquestrador acompanha prazo do pagamento |
| 11 | Cliente → Mercado Pago | Pagamento do Pix | Provedor processa o pagamento |
| 12 | Mercado Pago → Billing | HTTP: POST /api/payments/webhook | Billing verifica assinatura e deduplica a notificação |
| 13 | Billing → Mercado Pago | HTTP: GET /v1/orders/{id} | Billing confere o estado real; o webhook sozinho não comprova pagamento |
| 14 | Billing → Operações/OS | Após confirmação real: Payment Paid, Budget Paid e evento gearflow-budget-service.PaymentConfirmed | OS segue para reserva de estoque |
| 15 | Operações/OS → Execução | Comando ReserveStock; resposta gearflow-execution-service.StockReserved | Estoque reservado |
| 16 | Operações/OS → Execução | Comando StartExecution; resposta gearflow-execution-service.ExecutionFinished ao terminar | OS fica Finalized |
| 17 | Atendente → Operações/OS | HTTP: entrega do veículo | OS Delivered; evento gearflow-os-service.ServiceOrderDelivered; Saga concluída |

**Aprovação de orçamento e confirmação de pagamento são etapas diferentes.** gearflow-budget-service.BudgetApproved avisa Operações/OS para solicitar a cobrança. gearflow-budget-service.PaymentConfirmed permite ao orquestrador seguir para estoque e execução. Billing não inicia Execução diretamente.

```mermaid
sequenceDiagram
    participant OS as Operações/OS
    participant EX as Execução
    participant MQ as RabbitMQ
    participant BI as Billing
    actor C as Cliente
    participant MP as Mercado Pago
    OS->>MQ: StartDiagnostic
    MQ->>EX: StartDiagnostic
    EX->>MQ: gearflow-execution-service.DiagnosticFinished (itens/preços)
    MQ->>OS: gearflow-execution-service.DiagnosticFinished
    OS->>MQ: GenerateBudget
    MQ->>BI: GenerateBudget
    BI-->>C: E-mail com orçamento e links
    BI->>MQ: gearflow-budget-service.BudgetGenerated
    MQ->>OS: gearflow-budget-service.BudgetGenerated
    C->>BI: HTTP: aprovar orçamento
    BI->>MQ: gearflow-budget-service.BudgetApproved
    MQ->>OS: gearflow-budget-service.BudgetApproved
    OS->>MQ: RequestPayment
    MQ->>BI: RequestPayment
    BI->>MP: HTTP: criar order Pix
    MP-->>BI: Order e dados Pix
    BI-->>C: E-mail com Pix
    BI->>MQ: gearflow-budget-service.PaymentRequested
    MQ->>OS: gearflow-budget-service.PaymentRequested
    C->>MP: Paga Pix
    MP->>BI: HTTP: webhook
    BI->>MP: HTTP: consultar estado real
    MP-->>BI: Pagamento confirmado
    BI->>MQ: gearflow-budget-service.PaymentConfirmed
    MQ->>OS: gearflow-budget-service.PaymentConfirmed
    OS->>MQ: ReserveStock
    MQ->>EX: ReserveStock
    EX->>MQ: gearflow-execution-service.StockReserved
    MQ->>OS: gearflow-execution-service.StockReserved
    OS->>MQ: StartExecution
    MQ->>EX: StartExecution
    EX->>MQ: gearflow-execution-service.ExecutionFinished
    MQ->>OS: gearflow-execution-service.ExecutionFinished
    Note over OS: Atendente entrega veículo; OS Delivered
```

## O que o Billing recebe e envia

Comandos recebidos de Operações/OS, exchange `gearflow.commands.v1`, fila `gearflow.billing.commands.v1`:

| Comando | Routing key | Ação |
|---|---|---|
| GenerateBudget | billing.GenerateBudget | Criar orçamento e solicitar notificação |
| RequestPayment | billing.RequestPayment | Criar cobrança Pix |
| CancelBudget | billing.CancelBudget | Cancelar orçamento e eventual order pendente |
| RefundPayment | billing.RefundPayment | Reembolsar pagamento confirmado |

Eventos publicados pelo Billing, exchange `gearflow.events.v1`, fila do destinatário `gearflow.os.billing.events.v1`:

| Evento | Routing key | Destino / significado |
|---|---|---|
| gearflow-budget-service.BudgetGenerated | gearflow-budget-service.BudgetGenerated | Operações/OS: orçamento gerado |
| gearflow-budget-service.BudgetGenerationFailed | gearflow-budget-service.BudgetGenerationFailed | Operações/OS: falha ao gerar orçamento |
| gearflow-budget-service.BudgetApproved | gearflow-budget-service.BudgetApproved | Operações/OS: cliente aprovou |
| gearflow-budget-service.BudgetRejected | gearflow-budget-service.BudgetRejected | Operações/OS: cliente rejeitou |
| gearflow-budget-service.BudgetCanceled | gearflow-budget-service.BudgetCanceled | Operações/OS: cancelamento concluído |
| gearflow-budget-service.PaymentRequested | gearflow-budget-service.PaymentRequested | Operações/OS: cobrança criada, ainda não paga |
| gearflow-budget-service.PaymentConfirmed | gearflow-budget-service.PaymentConfirmed | Operações/OS: pagamento confirmado |
| gearflow-budget-service.PaymentFailed | gearflow-budget-service.PaymentFailed | Operações/OS: cobrança/pagamento falhou |
| gearflow-budget-service.PaymentRefunded | gearflow-budget-service.PaymentRefunded | Operações/OS: compensação de reembolso concluída |

Toda mensagem identifica a OS por serviceOrderId/correlationId, além de messageId único e payload. Billing recebe a aprovação/rejeição do cliente e o webhook por HTTP. Mercado Pago não publica no RabbitMQ. Contratos completos e exemplos: [rabbitmq.md](rabbitmq.md).

## Mercado Pago: onde entra e onde configurar

A integração é um adaptador de saída em `src/GearFlow.Billing.Adapters/Out/PaymentGateway/MercadoPago/`, implementando a porta `IPaymentGateway` definida na Application. Casos de uso não dependem do HttpClient ou dos modelos do provedor. O código ainda será criado.

| Operação | Gatilho | Comportamento previsto |
|---|---|---|
| Criar Pix | RequestPayment | Criar order com chave idempotente; registrar ID, valor e dados do Pix |
| Consultar order | Webhook ou job de reconciliação | Verificar estado real antes de alterar domínio/publicar resultado |
| Cancelar order pendente | CancelBudget | Confirmar cancelamento antes de informar compensação concluída |
| Reembolsar pagamento | RefundPayment | Confirmar reembolso antes de publicar gearflow-budget-service.PaymentRefunded |

Configuração em [.env.example](../.env.example), passada à API pelo [Compose](../docker-compose.yml):

| Variável | Uso |
|---|---|
| MERCADO_PAGO_BASE_URL | Local: http://wiremock:8080 dentro do Compose; integração real: https://api.mercadopago.com |
| MERCADO_PAGO_ACCESS_TOKEN | Credencial do provedor; nunca versionar token real |
| MERCADO_PAGO_WEBHOOK_SECRET | Verificar assinatura recebida no webhook |
| MERCADO_PAGO_NOTIFICATION_URL | URL pública HTTPS terminando em /api/payments/webhook para receber notificações reais |
| BILLING_APPROVAL_TOKEN_SECRET | Assinar links de aprovação/rejeição; é separado do segredo do Mercado Pago |

WireMock sobe sem mappings: a integração local não simula pagamentos até cadastrar os stubs. Para usar Mercado Pago real em desenvolvimento, configurar credenciais apropriadas e túnel HTTPS para a API local. Detalhes de integração na [seção 9 do README](../README.md#9-integração-com-o-mercado-pago) e instruções no [guia local](ambiente-local.md).

## Rejeição, timeout e falhas

| Cenário | Fluxo previsto |
|---|---|
| Cliente rejeita | Billing publica gearflow-budget-service.BudgetRejected; Operações/OS inicia compensação e envia CancelBudget e CancelDiagnostic conforme etapas realizadas |
| Prazo de aprovação expira | Job persistido no OS dispara timeout; OS solicita CancelBudget; Billing confirma gearflow-budget-service.BudgetCanceled |
| Pix não é pago / order pendente expira | OS trata timeout, solicita CancelBudget e Billing cancela eventual order pendente, reconciliando o resultado |
| Falha definitiva de cobrança/pagamento | Billing publica gearflow-budget-service.PaymentFailed; OS coordena compensação |
| Pagamento confirmado, mas estoque insuficiente | OS aguarda reposição; se prazo expirar, libera reservas parciais, solicita RefundPayment e depois CancelBudget |
| Webhook perdido | Job de reconciliação consulta pagamentos Pending e confirma o resultado real pela API do Mercado Pago |
| Timeout técnico ao criar/cancelar/reembolsar order | Resultado pode ser incerto: consultar/repetir com idempotência antes de declarar estado definitivo; não assumir ausência de cobrança |
| Erro técnico persistente no consumidor | Retry e depois DLQ; alerta e intervenção/replay, mantendo a Saga aguardando a conclusão correta |
| Falha após iniciar execução | Conforme proposta atual, recuperar por retry/intervenção; não desfazer automaticamente serviço físico executado |

A compensação depende do que realmente ocorreu. Quando aplicável, a ordem é ReleaseStock → RefundPayment → CancelBudget → CancelDiagnostic → OS Canceled. Se o Pix ainda está pendente, cancelar a order em vez de reembolsar cobrança não confirmada. Se pagamento for confirmado durante cancelamento/timeout, reconciliar e acionar reembolso antes de encerrar a Saga.

## Persistência e entrega confiável

Estado de negócio, Inbox e Outbox são gravados na mesma transação local. Um worker publica o Outbox com publisher confirms; o consumidor envia ack após commit local. A entrega pode se repetir e a Inbox deduplica por consumidor/messageId. Não existe transação única entre MongoDB, RabbitMQ e Mercado Pago. Efeitos externos exigem idempotência e reconciliação. A Saga valida transições e eventos atrasados.

O núcleo Domain/Application segue [Ports and Adapters](arquitetura-hexagonal.md). HTTP, consumidores e jobs são entradas; MongoDB, RabbitMQ publisher, Mercado Pago e SMTP são saídas.

## Como subir e acompanhar

```powershell
Copy-Item .env.example .env
docker compose up -d
docker compose ps -a
docker compose logs mongo-init rabbitmq-init
```

| Recurso | Endereço / observação |
|---|---|
| RabbitMQ Management | http://localhost:15672 — gearflow / gearflow-local por padrão |
| Mailpit (e-mails locais) | http://localhost:8025 |
| WireMock | http://localhost:8089 — cadastrar stubs antes dos testes |
| API | http://localhost:5080; Swagger em /swagger |
| Rotas de orçamento/pagamento | [Contrato HTTP no README](../README.md#10-api-rest) |

O Compose padrão sobe somente dependências. Quando API e Dockerfile existirem, usar `docker compose --profile app up -d --build`. Para demonstrar o fluxo completo, também subir Operações/OS e Execução com seus bancos nos respectivos repositórios. Não há endpoint HTTP de criação de orçamento no Billing: esse passo entra por GenerateBudget via RabbitMQ.

Diagramas detalhados e estados da Saga: [fluxo-saga.md](fluxo-saga.md). Payloads e garantias do transporte: [rabbitmq.md](rabbitmq.md). Comandos, portas e configuração: [ambiente-local.md](ambiente-local.md).


Guias executáveis atuais: [operação e exemplos](operacao.md), [Postman](postman/Billing.postman_collection.json), [contratos entre serviços](integracao-servicos.md) e [ADR de consistência](adr/0008-entrega-e-compensacao-duraveis.md).
