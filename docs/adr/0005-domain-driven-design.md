# ADR 0005 — Domain-Driven Design obrigatório

Status: aceita. Data: 02/11/2026. Origem: exigência explícita do responsável pelo projeto.

## Contexto

Billing contém regras de orçamento, aprovação, pagamento e compensação. Hexagonal e CQRS estabelecem fronteiras técnicas, mas sozinhos não impedem modelos anêmicos ou regras dispersas nos handlers e adaptadores.

## Decisão

Adotar obrigatoriamente DDD estratégico e tático em conjunto com o ADR 0004. O [modelo de domínio](../modelo-dominio.md) define linguagem ubíqua, contextos, agregados e políticas iniciais. A obrigação transversal no [plano](../plano-implementacao.md) e o [AGENTS.md](../../AGENTS.md) estabelecem critérios para implementação e revisão.

Budget e Payment são raízes independentes. Regras de negócio pertencem ao domínio, com mutação encapsulada, value objects imutáveis e serviços de domínio apenas quando uma política não pertence naturalmente a uma entidade. Application coordena casos de uso e portas; adaptadores isolam os contratos externos.

Eventos de domínio pertencem ao Domain e representam fatos internos. Application deriva eventos de integração internos quando necessário, gravados pelo Outbox na transação local. Adapters traduzem para GearFlow.Contracts e envelopes RabbitMQ. Falhas de transporte não se tornam automaticamente fatos de domínio.

### Exceção explícita de consistência entre agregados

A confirmação de pagamento pode atualizar Payment e Budget na mesma transação local, junto com Inbox/Outbox aplicáveis. A exceção preserva a garantia já documentada de que Payment Paid, Budget Paid e a intenção de publicar PaymentConfirmed sejam confirmados juntos. Application coordena ambos por identidade, sem tornar Budget filho de Payment ou vice-versa.

Essa exceção exige testes de rollback e concorrência com cancelamento/reembolso. Não se estende às chamadas Mercado Pago, SMTP ou publicação RabbitMQ, que acontecem fora da transação. Outras operações entre agregados devem explicitar seus limites; novas exceções exigem ADR.

## Consequências e verificação

- Manter glossário e mapa de contextos junto do código e dos cenários BDD.
- Testar invariantes e transições pelas operações públicas, incluindo repetição e concorrência.
- Verificar dependências com testes de arquitetura e significado/encapsulamento por revisão de PR.
- Impedir aceite de mudanças com regras em controllers, consumers ou repositórios, ou mutação direta que produza estado inválido.
- Registrar revisões dos limites de contexto, agregados ou consistência antes do aceite da mudança.

Este ADR formaliza o requisito; não indica que o código ou os testes já existam. Não altera os contratos HTTP/RabbitMQ previstos.
