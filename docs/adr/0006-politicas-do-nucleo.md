# ADR 0006 — Políticas executáveis do núcleo Billing

Status: implementada no núcleo; decisões de integração aguardam alinhamento externo. Data: 02/11/2026.

## Valores, snapshots e prazos

- BRL, decimal; Money arredonda na criação para duas casas, AwayFromZero. Subtotal arredondado por linha; total soma subtotais. Quantidade positiva com até três casas. Preço não negativo; total do orçamento estritamente positivo. Overflow é rejeitado pelo runtime e nunca produz wraparound.
- Customer e itens são snapshots imutáveis fornecidos pela OS; Billing não consulta catálogo. Preços são os recebidos, sujeitos à confirmação com OS/Execution.
- E-mail remove espaços externos, preserva a caixa do local-part e normaliza domínio para minúsculas. Não aceitar display-name no campo de endereço.
- Datas em UTC. ExpiresOn do comando é o prazo efetivo, obrigatório e posterior à criação. Aprovação/rejeição exige CreatedOn ≤ agora < ExpiresOn. Expiração explícita somente para Pending e agora ≥ ExpiresOn.
- Uma proposta por ServiceOrderId. Repetição com mesmo cliente, sequência de itens e prazo retorna a identidade existente; snapshot divergente retorna budget.conflict. MessageId e deduplicação de falhas/compensação sem entidade são responsabilidade da Inbox futura.

## Matriz de orçamento

| Estado | Operação | Resultado |
|---|---|---|
| Pending dentro do prazo | Approve / Reject | Approved / Rejected |
| Approved / Rejected | Mesma decisão com mesmo conteúdo | Sem novo efeito/evento |
| Estado diferente | Decisão conflitante | Erro, sem alteração |
| Pending vencido | Expire | Expired |
| Approved | AwaitPayment | AwaitingPayment |
| AwaitingPayment | MarkPaid | Paid |
| Paid | MarkRefunded após evidência financeira | Refunded |
| Pending, Approved, Rejected, Expired, AwaitingPayment, Refunded | Cancel após resolver cobrança | Canceled |
| Paid | Cancel | Exigir reembolso |
| Paid / Refunded | Confirmação repetida | Sem regressão |
| Canceled | Cancel repetido | Sem novo efeito/evento |

Refunded em Budget registra a compensação financeira antes do CancelBudget subsequente da Saga. Payment continua autoridade do reembolso. Nenhuma nova transação entre agregados é implicitamente autorizada por esta matriz: as portas e o domínio não são handlers de confirmação/refund. A implementação desses casos de uso deverá explicitar a consistência e testar as corridas, conforme ADR 0005.

## Pagamento e operações incertas

Payment.Request exige orçamento aprovado, mesma OS e valor. Uma operação tem ID estável, tipo e estado técnico distintos de PaymentStatus. Uma operação Started/Uncertain impede nova intenção, mas permite retomar a mesma. HTTP deve ocorrer depois de persistir a intenção.

Create confirmado registra order, Pix, ticket HTTPS e validade futura, passando a Pending. Pending + evidência consultada e compatível pode passar a Paid, Failed ou Canceled; Paid pode passar a Refunded. Repetições não emitem novo fato. Falha atrasada não regride Paid/Refunded/Canceled. Confirmar uma cobrança Canceled revela pagamento tardio e exige coordenação explícita de reembolso. Confirmar durante uma tentativa de cancelamento encerra essa tentativa como Rejected e permite a intenção de refund. O handler transacional futuro deve reconciliar o orçamento correspondente; essa corrida não está entregue pela presença do modelo.

Não há serviço de domínio adicional: as políticas implementadas pertencem naturalmente às raízes e value objects. Application coordena, sem publicar no broker.

## HTTP planejado

GET de link de e-mail deve abrir confirmação sem mutação. POST/PUT autenticado pelo token efetiva a decisão. Isso reduz acionamentos involuntários por scanners de e-mail. Token HMAC tem propósito/versionamento explícito, ID e expiração assinados, comparação de tempo constante e segredo de pelo menos 32 bytes; não registrar tokens em logs.

Consultas futuras exigirão JWT e política de acesso por identidade/papel definidos com OS/Auth. Não há bypass de desenvolvimento nem endpoints de negócio abertos nesta entrega. Erros planejados usam Problem Details, code estável, 400 para entrada inválida, 403 para autorização/token, 404 para recurso ausente e 409 para conflito/concorrência; política completa de claims continua pendente.

## Evidência e limites

Testes de invariantes/transições em DomainTests; cenários Given/When/Then em BudgetScenarios; dependências e encapsulamento em DependencyTests; HMAC/dispatcher/DI em SecurityAndDispatchTests. Rollback dos handlers usa fake transacional e não comprova semântica do Mongo. Testes de concorrência, Inbox, índices e integração continuam pendentes.
