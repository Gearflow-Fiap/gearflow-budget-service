# ADR 0004 — Ports and Adapters

Status: aceita. Origem: requisito do projeto confirmado pelo responsável.

## Contexto

O Billing recebe HTTP, comandos RabbitMQ e jobs, persiste em MongoDB e integra Mercado Pago e SMTP. Os mesmos casos de uso precisam atender essas entradas e permitir testes de negócio sem infraestrutura.

## Decisão

Adotar arquitetura hexagonal (Ports and Adapters). Domain contém regras de negócio; Application implementa casos de uso CQRS e define portas de entrada e saída. HTTP, RabbitMQ e jobs são adaptadores de entrada. MongoDB, Mercado Pago, SMTP e publicação RabbitMQ são adaptadores de saída. Api é o host/composition root. Adapters referencia Application/Domain; o núcleo nunca referencia implementações concretas.

GearFlow.Contracts é contrato externo usado pelos adaptadores RabbitMQ. Mensagens internas CQRS e eventos internos pertencem à Application; mapeadores nos adaptadores fazem a tradução. Interfaces de repositório, read model, Outbox e unidade de trabalho pertencem às portas de saída da Application.

## Consequências

Regras e casos de uso podem ser testados sem infraestrutura e reutilizados entre os canais. São necessários mapeamentos explícitos, interfaces neutras e testes de arquitetura para manter as fronteiras. CQRS, Saga orquestrada, RabbitMQ e Outbox/Inbox continuam sendo decisões complementares. Não há alteração dos contratos HTTP ou contratos de mensagens RabbitMQ por esta decisão.

Detalhamento: [arquitetura hexagonal](../arquitetura-hexagonal.md).
