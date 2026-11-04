# ADR 0001 — MongoDB para Billing

Status: decisão de arquitetura existente formalizada; implementação pendente. Data: 02/11/2026.

## Decisão

Usar banco MongoDB exclusivo do Billing. Budget contém snapshots de Customer e BudgetItem; Payment e Notification são raízes distintas. Repositórios de escrita persistem raízes com versão otimista. As projeções de consulta não alteram o domínio.

Replica set é obrigatório para transações locais envolvendo estado, Inbox e Outbox. A exceção de confirmação conjunta Payment/Budget está no ADR 0005. Chamadas HTTP, SMTP e publicação AMQP acontecem fora da transação.

## Consequências

Índices únicos de ServiceOrderId em Budget, BudgetId em Payment e consumerName/messageId na Inbox fazem parte da correção, junto com controle de versão. UUID, decimal e UTC exigem mapeamentos explícitos nos adaptadores. Domain/Application não conhecem BSON nem sessões Mongo. A adoção desta decisão não comprova persistência implementada: testes com replica set real continuam necessários.
