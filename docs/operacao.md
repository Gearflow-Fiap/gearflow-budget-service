# Operação e entrega

O pagamento simulado (`MercadoPago__UseMock=true`) retorna confirmação imediata
para cobranças válidas, sem HTTP Mercado Pago, Pix real ou webhook. O mock usa
transações locais próprias para consultar o pagamento persistido durante busca,
reconciliação e reembolso; essas consultas também exigem uma sessão Mongo ativa.
O fluxo continua validando aprovação e valor do orçamento e persiste Payment,
Budget e Outbox normalmente. Repetir o comando não duplica os eventos.

## Desenvolvimento

Copiar .env.example para .env, revisar segredos locais e executar `docker compose --profile app up -d --build`. O perfil ativa Mongo replica set, Rabbit, SMTP Mailpit, HTTP e provedor WireMock. OTEL envia para o coletor local com exportador debug: `docker compose logs otel-collector`. Swagger: http://localhost:5080/swagger; e-mails: http://localhost:8025. Os segredos de exemplo são exclusivos de desenvolvimento. Não remover volumes para atualizar configuração. Docker/Compose ainda não foram executados neste ambiente; Mongo/Rabbit e fluxo Billing foram validados com processos nativos temporários conforme validacao-local.md.

GET /health/live verifica processo. GET /health/ready exige Mongo primário, topologia Rabbit e workers ativos; configuração incompleta retorna 503. Mercado Pago indisponível não bloqueia readiness: operações incertas são reconciliadas. Swagger existe somente em Development. Entradas comerciais vêm pelo Rabbit, não por POST REST de orçamento.

## Exercitar as entradas locais

Importar docs/postman/Billing.postman_collection.json e Local.postman_environment.json. Preencher JWT externo válido e IDs obtidos nos eventos; copiar approvalToken do link recebido no Mailpit. O navegador efetiva a confirmação com POST protegido; a collection permite PUT com HMAC. Não exportar environment preenchido com tokens reais.

Para simular somente o comando inicial do OS, gerar um payload local e publicar com Management local:

```powershell
$env:RABBITMQ_USERNAME = 'gearflow'
$env:RABBITMQ_PASSWORD = 'gearflow-local' # usar o valor do .env local
$payload = @{
 customer = @{name='Cliente de teste';email='cliente@example.com'}
 items = @(@{referenceId=[guid]::NewGuid().ToString();type='Job';description='Diagnóstico';unitPrice=350;quantity=1})
 expiresOn = [DateTimeOffset]::UtcNow.AddHours(48).ToString('o')
}
$payload | ConvertTo-Json -Depth 10 | Set-Content generate.local.json
./tools/Publish-LocalCommand.ps1 -ServiceOrderId ([guid]::NewGuid()) -PayloadPath generate.local.json
```

O script é um simulador do produtor, restrito ao Rabbit Management HTTP loopback. Sucesso significa roteamento, não conclusão. Conferir evento BudgetGenerated na fila gearflow.os.billing.events.v1 e e-mail no Mailpit. Cada execução cria nova identidade e timestamp. Fixtures WireMock são de IDs/valores fixos conforme mercado-pago.md: não anunciar pagamento dinâmico ponta a ponta a partir deste exemplo. Os outros payloads locais são RequestPayment {budgetId,amount,currency}, CancelBudget {budgetId,reason}, RefundPayment {budgetId,paymentId,reason}.

## Recuperação de falhas

Broker indisponível: manter mensagens sem ACK e Outbox pendente; restaurar conectividade/TLS/credenciais e observar redução da idade do Outbox. Não marcar mensagens como enviadas manualmente. Mongo sem primary: restaurar replica set e quorum, conferir readiness; não substituir por standalone porque transações são necessárias.

DLQ: inspecionar payload, código e identidade sem divulgar dados pessoais. Corrigir causa antes de replay. Com a configuração completa do serviço, executar uma vez:

```powershell
$env:Operations__ReplayDlq = 'true'
$env:Operations__Actor = 'identidade-do-operador-ticket'
dotnet run --project src/GearFlow.Billing.Api -c Release --no-build
Remove-Item Env:Operations__ReplayDlq, Env:Operations__Actor
```

Cada execução busca uma mensagem, valida o envelope, mantém IDs, grava replay_audit e confirma a republicação antes do ACK da DLQ. Mensagem inválida permanece na fila; corrigir contrato no produtor e tratar com auditoria. Não alterar o corpo de um MessageId já registrado no Inbox: digest diferente é conflito. Reconciliação financeira consulta o provedor com operação/idempotência persistidas. Nunca criar nova chave para repetir uma cobrança incerta.

SMTP: conferir failed_notifications, tentativas e lease. Corrigir host/TLS/credenciais; entregas temporárias têm backoff e limite de oito tentativas. Após esgotar tentativas, triagem operacional deve determinar reenvio; não há endpoint que force envio. Aceitação SMTP seguida de crash pode duplicar e-mail.

## Kubernetes e CI/CD

Base em k8s/base; Mongo local sem autenticação em k8s/local-mongo serve apenas para desenvolvimento. Produção exige Mongo autenticado com replica set, Rabbit TLS/quorum, SMTP TLS, ingresso HTTPS, coletor OTLP e volume RWX para as chaves DataProtection. Restringir acesso ao volume, manter cópia protegida e usar armazenamento criptografado. Não trocar chaves durante rollout.

Provisionar externamente no namespace gearflow o Secret gearflow-billing-secrets com: ConnectionStrings__Mongo, RabbitMq__Username, RabbitMq__Password, MercadoPago__AccessToken, MercadoPago__WebhookSecret, MercadoPago__NotificationUrl, Billing__ApprovalTokenSecret, Jwt__Secret, Email__Host, Email__Port, Email__FromAddress, Email__Username e Email__Password. Provisionar o Secret TLS gearflow-billing-tls e revisar hosts/issuer/audience/filas do ConfigMap. Não versionar valores reais. A aplicação não cria secrets de produção.

CI verifica build/testes, testes reais via Testcontainers e manifests. Sonar depende de SONAR_PROJECT_KEY, SONAR_ORG e SONAR_TOKEN; configurar quality gate com cobertura mínima de 80% e proteção de branch. Ausência de Sonar bloqueia publicação. Delivery usa GHCR por SHA e ambiente production; configurar aprovação desse ambiente, KUBECONFIG_BASE64, BILLING_HOST e KUBECTL_VERSION. Executa dry-run no servidor, apply, rollout e smoke interno. Nada disso foi executado remotamente nesta entrega.

Rollback: selecionar SHA previamente validado e alterar a imagem do Deployment com kubectl set image, aguardar rollout e verificar readiness, métricas/Outbox e compatibilidade de snapshots/contratos. Não reverter dados nem restaurar backup automaticamente após efeitos financeiros externos. .NET 10 LTS/SDK 10.0.403 e runtime 10.0.14 foram validados localmente conforme ADR 0009; acompanhar patches e janela de suporte.

## Backup e restauração

Responsável de infraestrutura deve configurar snapshots consistentes do replica set (ou ferramenta gerenciada equivalente), retenção, criptografia e teste de restauração em ambiente isolado. Incluir budgets, payments, commercial_fences, inbox, outbox, notifications, payment_work e replay_audit; restaurar também keyring DataProtection protegido. Não executar restauração no banco ativo. Antes de reabrir workers no banco restaurado, conciliar cobranças/refunds no Mercado Pago e mensagens no broker, pois um backup pode anteceder efeitos externos já efetivados. Registrar RPO/RTO medidos no ensaio; não há valores homologados nesta entrega.


