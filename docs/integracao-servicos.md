# Compatibilidade entre serviços

Inspeção das branches informadas em 03/11/2026. Contratos são locais v1 em Adapters/Messaging/WireContracts.cs; não há referência a pacote Billing compartilhado publicado.

| Fonte inspecionada | Contrato observado | Diferença para Billing |
|---|---|---|
| [OS dev Events.cs](https://github.com/Gearflow-Fiap/gearflow-os-service/blob/dev/src/GearFlow.Os.Application/Messaging/Events.cs) | os.created com cliente/veículo por identidade e nomes de serviços; budget.generated/approved/rejected | Payload cru e exchange gearflow.events; faltam snapshot de cliente, e-mail e preços. Não implementa os quatro comandos v1 nem confirmação financeira da Saga prevista |
| [Inventory main InventoryMessages.cs](https://github.com/Gearflow-Fiap/gearflow-inventory-service/blob/main/src/GearFlow.Contracts/InventoryMessages.cs) | ReserveStock, ReleaseStock, ConsumeStock e eventos de estoque | Contracts é fonte do Inventory, sem contratos Billing publicados; transporte MassTransit difere do envelope local |
| [Operation main proposta Billing/OS](https://github.com/Gearflow-Fiap/gearflow-operation-service/blob/main/docs/contracts/BUDGET_OS_INTERFACE.md) | Proposta com centavos, revisão, identificadores de linha e compensação por correlação | Ainda proposta; Billing usa Money decimal, itens Job/Part/Consumable e compensações com BudgetId/PaymentId. Não converter silenciosamente Labor/centavos/revisão |

O diagnóstico de Operation fornece preços e snapshots comerciais, mas o OS observado ainda precisa montar o comando com dados completos do cliente. Não é possível criar com segurança o orçamento a partir de os.created: IDs e nomes de serviço não determinam preços nem destinatário.

Antes da homologação conjunta, os responsáveis pelos serviços devem ratificar: envelope e transporte (incluindo MassTransit), nomes/exchanges/filas, centavos versus decimal e arredondamento, Job versus Labor, versão/revisão e linhas, compensação sem entidade e cancelamento de Pix pendente, eventos definitivos/incertos e correlação. Depois distribuir GearFlow.Contracts.Billing.V1 com versão fixa e implementar/adaptar o OS. Os nove eventos Billing atuais estão em docs/rabbitmq.md.

JWT Hmac pode validar issuer GearFlow.Api e audience GearFlow.Client como Inventory, com segredo externo de pelo menos 32 bytes. O emissor deve fornecer role BillingAdmin/BillingOperator ou service_order_id assinado. Um CPF isolado não autoriza consultar qualquer OS. Não há chave real nem Authority inventada no código.

O teste BillingFlowInfrastructureTests simula OS, provedor e e-mail nas bordas do Billing. Ele não substitui um teste com os três serviços reais. Nenhum outro repositório foi alterado e não houve homologação MP sandbox.
