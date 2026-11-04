# Instruções de implementação

## DDD obrigatório

Este projeto exige Domain-Driven Design estratégico e tático. Antes de alterar comportamento de negócio, consultar [ADR 0005](docs/adr/0005-domain-driven-design.md), [modelo de domínio](docs/modelo-dominio.md), [arquitetura hexagonal](docs/arquitetura-hexagonal.md) e [plano](docs/plano-implementacao.md).

- Usar a linguagem ubíqua e respeitar o contexto Billing. Não acessar bancos de OS/Execution nem importar seus agregados.
- Concentrar invariantes, cálculos e transições em entidades, value objects ou serviços de domínio. Handlers coordenam os casos de uso; adaptadores traduzem e integram.
- Preservar encapsulamento: raízes controlam alterações internas, sem setters públicos ou coleções mutáveis que permitam contornar regras.
- Budget e Payment são raízes distintas, referenciadas por identidade. Repositórios de escrita operam sobre raízes; queries usam projeções sem alterar estado.
- Manter value objects imutáveis com igualdade por valor e validação na criação. Justificar a classificação dos objetos segundo identidade e ciclo de vida.
- Separar eventos de domínio (Domain), eventos internos de integração (Application) e contratos/envelopes externos (Adapters). Publicação ocorre pelo Outbox, nunca pelo agregado.
- Não introduzir MongoDB, RabbitMQ, SMTP, HTTP, modelos Mercado Pago ou GearFlow.Contracts no núcleo. Application define portas e depende de Domain.
- Mudanças de negócio exigem testes de invariantes/transições e cenários BDD pertinentes. Testes de arquitetura verificam dependências; revisão semântica verifica o modelo.
- Atualizar glossário, decisões e documentação quando o modelo mudar. Alterar limites de contexto/agregado ou consistência exige ADR com justificativa e testes.

## Critérios de revisão

Um PR de negócio só está pronto quando as regras acima foram verificadas, os testes relevantes passaram e a documentação reflete o comportamento. Cobertura de 80%, CQRS ou separação em pastas não substituem essa revisão. Não marcar tarefas de implementação como concluídas apenas por documentá-las.
