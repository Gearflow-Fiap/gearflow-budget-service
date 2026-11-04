# ADR 0008 — Entrega durável, compensações antecipadas e decisão explícita

Status: implementado localmente; homologação externa pendente. Data: 03/11/2026.

## Contexto

Comandos podem chegar duplicados, fora de ordem ou após uma resposta remota perdida. Um cancelamento recebido antes da criação não pode permitir uma cobrança atrasada. A conclusão do Inbox antes do resultado financeiro perderia trabalho. Links GET também são abertos por scanners de e-mail.

## Decisão

CommercialFence é uma raiz de Billing identificada pela OS, com versão otimista e ciclo de vida independente de Budget e Payment. Registra a intenção de cancelamento/reembolso mesmo sem as demais entidades. Geração, aprovação e nova cobrança autorizam e atualizam essa raiz na mesma transação do negócio, serializando ações comerciais com compensações. Trata-se de uma exceção explícita à preferência por uma raiz por transação, necessária para impedir a criação concorrente após a compensação. Não contém agregados externos.

Inbox usa identidade do consumidor + MessageId, digest canônico, lease e proprietário. Sua conclusão participa da transação final de negócio/Outbox. Intenções financeiras intermediárias não concluem o recibo. O proprietário é conferido antes de cada commit; concorrência preserva a mensagem de origem para reentrega. HTTP externo continua fora da transação, seguindo ADR 0007.

O consumidor usa ACK manual, tentativas limitadas e DLQ durável com publicação confirmada antes do ACK da origem. Outbox só marca envio após publisher confirm e ausência de retorno obrigatório. Há entrega pelo menos uma vez: destinatários devem deduplicar o MessageId estável. Replay é explícito, uma mensagem por execução, auditado e preserva a identidade. Falha técnica ou resultado financeiro incerto não é traduzido em recusa de negócio.

NotificationPolicy é serviço de domínio que verifica relevância entre raízes: proposta ainda pendente, Pix ainda pagável e reembolso confirmado. Notificações obsoletas ficam Suppressed. SMTP usa lease, tentativas persistidas e Message-ID estável. Uma falha após aceitação SMTP pode gerar duplicata; não há garantia de envio exatamente uma vez.

GET de aprovação/rejeição apenas apresenta confirmação. POST exige antiforgery; PUT JSON exige token HMAC. Consultas usam JWT validado e papel autorizado ou claim service_order_id correspondente ao recurso. Modo Hmac acompanha a configuração observada no Inventory, sem pressupor emissor OIDC; a distribuição de chaves/claims precisa de acordo externo.

## Evidências e consequências

CommercialFenceTests, NotificationDeliveryTests, BusinessHttpTests, WireContractTests e cenários BDD cobrem políticas locais. MongoPaymentPersistenceTests e BillingFlowInfrastructureTests verificam persistência/entrega com infraestrutura quando habilitados. Exigem Docker ou serviços externos; testes ignorados não são evidência de execução. Os contratos locais v1 não são declarados compatíveis com os repositórios atuais sem ratificação (ver integracao-servicos.md).
