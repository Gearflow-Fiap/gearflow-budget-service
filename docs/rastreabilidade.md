# Rastreabilidade da implementação

Data: 03/11/2026. Implementação local e homologação são critérios distintos.

| Requisito | Código/evidência local | Falta validar externamente |
|---|---|---|
| DDD/CQRS | Domain/Application, snapshots, CommercialFence, NotificationPolicy; 41 Unit + 10 Architecture + 22 BDD | Revisão do time |
| Mongo/consistência | MongoSession, repositórios, índices, Inbox final/Outbox na mesma sessão; quatro MongoFact | Executados quatro testes reais; ampliar corridas distribuídas |
| Rabbit/entrega | RabbitCommandWorker, MongoInbox, RabbitPublisher, OutboxWorker, DlqReplay; WireContractTests | Rabbit, fluxo e recuperação Outbox/DLQ executados; ampliar falhas entre réplicas |
| Orçamento/decisão | Generate/Review/Cancel, HMAC, GET confirmação, POST antiforgery, PUT; BusinessHttpTests | Fluxo real Mailpit/OS |
| Pix/compensação | PaymentWorkflow, MercadoPagoGateway, intenções/idempotência/reconciliação; GatewayTests/PaymentScenarios | Mercado Pago sandbox, HTTPS e corrida distribuída |
| Webhook | assinatura, fila payment_work durável, consulta antes da confirmação; PaymentWebhookTests | Provedor real; retenção e recuperação em produção |
| SMTP | SmtpEmailSender/NotificationComposer/Worker; SMTP loopback e políticas de entrega | SMTP real e reenvio após falha |
| API/JWT | BillingEndpoints/JwtOptions/ApiExceptionHandler/Swagger; testes JWT/CSRF/ownership | Emissor/claims e chaves do time |
| Observabilidade | OTLP, fontes próprias, métricas, sanitização de queries, coletor local | Correlação com outros serviços, dashboards/alertas |
| Infra local | Compose, Dockerfile, WireMock | Execução Compose/imagem ainda depende de Docker; processos nativos testados |
| Qualidade | 140 aprovados/zero ignorados; Reqnroll/Testcontainers; coleta Coverlet | Gate global ≥80% e CI/Sonar remoto |
| Kubernetes/CD | k8s/base, Mongo apenas local, workflows ci.yml/delivery.yml; testes YAML e 10 schemas Kubernetes válidos | Schema/cluster, secrets, registry, rollout/rollback e restauração |
| Contratos/Saga real | v1 local em WireContracts, comparação integracao-servicos.md | Ratificar feed/transportes e alterar OS; nenhum fluxo real entre serviços demonstrado |

Nenhum teste ignorado conta como aprovado. BillingFlowInfrastructureTests usa Mongo/Rabbit reais quando habilitado, com OS/provedor/e-mail simulados. Não se confunde com homologação ponta a ponta. ADRs 0007/0008/0009 explicitam exceções entre raízes e limites de entrega pelo menos uma vez.



Validação .NET 10 sem Docker e evidências: [validacao-local.md](validacao-local.md) e [resumo verificável](evidencias/validacao-2026-11-03.json).

Atualização MP local: 150 testes locais aprovados + um teste remoto opt-in aprovado. Pix de teste BRL 50 confirmado via API, sem webhook, com estado Billing e Outbox. Regressores APRO, isolamento de Development, rota ausente e total de paginação string/número. Refund remoto e entrega de webhook seguem pendentes. Ver mercado-pago-local.md.
