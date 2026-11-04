# ADR 0002 — Saga orquestrada pelo OS

Status: decisão existente formalizada; compatibilidade com OS/Execution pendente. Data: 02/11/2026.

## Decisão

Billing participa da Saga, recebe GenerateBudget, RequestPayment, CancelBudget e RefundPayment e publica os resultados definidos em rabbitmq.md. OS mantém sequência, prazos e compensações. Billing não acessa bancos de outros contextos nem inicia reserva de estoque ou execução.

Preservar a sequência documentada de pagamento antes da reserva. Se faltar estoque após pagamento, OS coordena ReleaseStock, RefundPayment, CancelBudget e CancelDiagnostic. Esta sequência e os contratos v1 ainda exigem alinhamento com as equipes dos outros serviços; não há aprovação externa presumida.

## Consequências

Mensagens podem duplicar e chegar fora de ordem. Inbox, Outbox, estados terminais e idempotência de operações remotas são obrigatórios. Falha técnica persistente de compensação exige retry/DLQ e intervenção, sem anunciar sucesso prematuro. Não existe transação distribuída entre Mongo, RabbitMQ e provedor financeiro.
