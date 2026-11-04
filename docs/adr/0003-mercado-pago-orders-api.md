# ADR 0003 — Porta de pagamento para Orders API Pix

Status: adaptador e casos de uso implementados; homologação externa pendente. Data: 02/11/2026.

## Decisão

Usar Orders API online/Pix, isolada por IPaymentGateway e camada anticorrupção. Nenhum payload ou enum do provedor entra no núcleo. A documentação oficial de criação, busca, consulta, assinatura, cancelamento e refund foi consultada para o adaptador; as decisões executáveis e referências estão no [ADR 0007](0007-integracao-pix-e-consistencia.md).

Persistir intenção identificada antes de HTTP; reutilizar identidade em retries e recuperar respostas perdidas por consulta. Timeout não prova recusa. Confirmação requer consulta autenticada ao provedor e conferência de referência do orçamento, order, valor e moeda. Webhook autenticado cria trabalho durável; não usa o status do corpo como evidência financeira.

## Consequências

Gateway, webhook durável, handlers, persistência Mongo e reconciliação estão implementados e ativados por configuração, com testes determinísticos. Credenciais de teste, HTTPS público e homologação real seguem pendentes. Nenhum exemplo local é pagamento homologado. Cada operação usa seu OperationId estável como chave idempotente; não se reutiliza BudgetId para create/cancel/refund. Consumer RabbitMQ, Inbox e publicação do Outbox continuam pendentes. Guia em [mercado-pago.md](../mercado-pago.md).
