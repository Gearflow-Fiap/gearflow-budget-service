# Modelo de domínio e linguagem ubíqua

Modelo inicial obrigatório para implementação, conforme [ADR 0005](adr/0005-domain-driven-design.md). Refinamentos devem acompanhar descobertas do negócio e respeitar os critérios de revisão do [plano](plano-implementacao.md).

O núcleo inicial está implementado em src/GearFlow.Billing.Domain. Políticas executáveis, matriz de estados e limites da entrega estão no [ADR 0006](adr/0006-politicas-do-nucleo.md); evidências e pendências na [rastreabilidade](rastreabilidade.md). Os trechos abaixo que mencionam definições futuras são resolvidos pelo ADR 0006 para o núcleo; não implicam integração já entregue.

A integração Pix e novas exceções de consistência estão no [ADR 0007](adr/0007-integracao-pix-e-consistencia.md). Snapshots neutros reconstituem raízes com validação e sem emitir novos eventos; JSON/BSON ficam nos Adapters. Registro da cobrança e confirmação de refund atualizam Payment/Budget/Outbox transacionalmente. Pagamento tardio consultado pode ser reconhecido após cancelamento, mantendo visível a obrigação de reembolso. PaymentAttempt continua entidade interna; seu progresso técnico atualiza versão, sem inventar fatos de negócio de falha.

## Contextos delimitados

| Contexto | Autoridade | Relação com Billing |
|---|---|---|
| OS | Ordem de serviço, estado da Saga, próximos passos e timeouts | Envia comandos; recebe eventos; Billing não decide a sequência |
| Execution | Diagnóstico, catálogo/preços, estoque e execução física | Itens/preços chegam ao Billing via comando do OS como snapshot |
| Billing | Orçamento, decisão do cliente, cobrança, pagamento e compensação financeira | Possui MongoDB próprio; publica resultados ao OS |
| Mercado Pago | Processamento externo da cobrança | Gateway traduz estados e payloads para conceitos Billing |

Fluxo de integração: Execution → OS → Billing → OS, por contratos RabbitMQ. Billing ↔ Mercado Pago usa HTTP por camada anticorrupção. Não há banco compartilhado nem entidades compartilhadas entre contextos. GearFlow.Contracts compartilha mensagens de integração, não o modelo de domínio.

## Glossário

| Termo | Significado local / nome no código |
|---|---|
| Orçamento | Proposta com itens, preços e prazo; Budget |
| Item de orçamento | Componente de serviço/peça/consumível e cálculo de subtotal; BudgetItem |
| Cliente do orçamento | Snapshot de nome/e-mail para esta proposta; Customer |
| Aprovação | Aceite da proposta; não comprova pagamento; Approve |
| Rejeição | Recusa da proposta; Reject |
| Cobrança Pix | Solicitação de pagamento ao provedor; RequestPayment |
| Pagamento | Registro local do ciclo financeiro de um orçamento; Payment |
| Tentativa | Registro de uma operação financeira externa e seu resultado; PaymentAttempt |
| Cancelamento | Encerramento do orçamento e, quando aplicável, da cobrança pendente; CancelBudget |
| Reembolso | Devolução integral de pagamento confirmado; RefundPayment |
| Notificação | Registro do ciclo de envio de e-mail; Notification |
| Resultado incerto | Chamada externa cujo efeito ainda precisa ser consultado/reconciliado; não significa recusa |

## Agregados e objetos

| Elemento | Classificação inicial | Regras |
|---|---|---|
| Budget | Raiz de agregado | Identidade própria; controla itens, total, prazo e decisão; uma proposta por ServiceOrderId conforme contrato atual |
| BudgetItem | Value object interno | Sem ciclo de vida independente no escopo atual; ReferenceId identifica o item de catálogo externo, não uma entidade compartilhada; imutável, substituído pela raiz quando permitido |
| Customer | Value object de snapshot | Nome/e-mail imutáveis; não representa o cadastro global de cliente |
| Money | Value object | Valor decimal/moeda BRL; regras explícitas de precisão/arredondamento e operações compatíveis |
| EmailAddress | Value object | Validação na criação, igualdade por valor com política explícita de normalização |
| Payment | Raiz de agregado | Um por BudgetId; controla status, valor, vínculo e operações financeiras; referencia Budget por ID |
| PaymentAttempt | Entidade interna de Payment | Identificador estável distingue operações/tentativas com valores iguais; alteração passa pela raiz; sem repositório próprio |
| Notification | Raiz de agregado de suporte | Identidade e ciclo de envio/retry próprios; referencia orçamento/pagamento/OS por identidade; falha de envio não desfaz fato de negócio |

Repositórios persistem raízes completas. Projeções de leitura podem consultar dados sem carregar agregados, mas não executam mutações. Modelos BSON e payloads de transporte permanecem fora do núcleo.

## Invariantes e políticas

- Budget calcula total a partir de itens válidos: quantidade positiva com até três casas e preço não negativo. Money usa duas casas/AwayFromZero; soma de subtotais arredondados por linha. Total zero é rejeitado, conforme ADR 0006.
- Aprovação/rejeição respeitam estado e prazo; um agregado não pode aceitar decisões conflitantes. Validar token/autorização na Application e elegibilidade no domínio.
- Cobrança exige orçamento aprovado, mesma OS, valor e moeda compatíveis. Consultas ao provedor são traduzidas pelo gateway; regras sobre o resultado permanecem no domínio.
- Pagamento confirmado e reembolso exigem evidência do resultado remoto. Timeout técnico não autoriza transição para falha definitiva.
- Repetições preservam idempotência; eventos atrasados não regressam estados terminais. A matriz completa de estados/compensações deve ser consolidada na etapa 1 do plano.
- Relógio controlável torna regras temporais determinísticas; entidades não acessam diretamente rede, banco ou relógio global.
- Confirmação conjunta de Payment/Budget é a exceção de consistência do ADR 0005; chamadas externas nunca mantêm transação Mongo aberta.

Serviços de domínio só são introduzidos quando uma política não couber naturalmente nos objetos acima, com justificativa no modelo. Dispatcher, gateway e envio de e-mail não são serviços de domínio.

## Eventos e revisão

Fatos internos podem incluir orçamento gerado/aprovado/rejeitado/cancelado e pagamento confirmado/reembolsado. Seus nomes/tipos internos serão definidos na implementação; não reutilizar envelopes externos como eventos de domínio. Os nove eventos públicos e respectivos payloads continuam definidos em [rabbitmq.md](rabbitmq.md).

Para cada PR de negócio, verificar linguagem, propriedade das regras, encapsulamento, limites de consistência, tradução externa e testes de comportamento. Testes de dependência não detectam sozinhos um modelo anêmico. Mudanças do modelo atualizam este documento e, quando alteram decisões arquiteturais, um ADR.

## Ampliação executável de 03/11/2026

CommercialFence é raiz local por ServiceOrderId: registra cancelamento/reembolso antecipado, mesmo sem Budget/Payment, e serializa autorização de geração/aprovação/nova cobrança por versão. Sua transação com outra raiz é uma exceção documentada no ADR 0008. NotificationPolicy é serviço de domínio para relevância de entrega; Suppressed representa comunicação obsoleta e não envio SMTP. Inbox/Outbox são mecanismos de entrega dos adaptadores, sem entrada no modelo de domínio.

No mapa de contextos, Operation é o repositório de diagnóstico/execução fornecido pelo usuário; Inventory é o contexto de estoque. Ambos permanecem externos. O nome Execution nos diagramas anteriores expressa o papel lógico, não comprova a existência do repositório ou do contrato implantado. Diferenças observadas e pendências de tradução estão em integracao-servicos.md.
