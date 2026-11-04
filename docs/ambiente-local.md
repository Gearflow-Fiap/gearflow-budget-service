# Ambiente local: banco, RabbitMQ e HTTP

RabbitMQ/Inbox/Outbox, SMTP, Swagger e HTTP autenticado estão implementados. O Compose ativa os componentes com WireMock; consulte [estado executável](estado-implementacao.md), [operação](operacao.md) e [compatibilidade externa](integracao-servicos.md). Execução Docker e homologação real continuam pendentes.

## Subir as dependências

Pré-requisitos: Docker Engine com Compose v2 e containers Linux. No host, SDK 10.0.403 fixado no global.json e runtime ASP.NET Core 10.0.14.

```powershell
Copy-Item .env.example .env
# Revise .env; não coloque credenciais reais no Git.
docker compose config --quiet
docker compose up -d
docker compose ps -a
docker compose logs mongo-init rabbitmq-init rabbitmq
```

`mongo-init` inicia rs0 de forma idempotente e espera o primary, terminando com exit 0. rabbitmq-init importa exchanges, filas e bindings de docker/rabbitmq/definitions.json pela Management API após o broker ficar saudável e deve terminar com exit 0. A API depende dos dois inicializadores concluídos. Volumes persistem banco e mensagens RabbitMQ; a readiness da API também deve verificar a topologia.

| Serviço | No host | Na rede Compose |
|---|---|---|
| MongoDB Billing | `mongodb://localhost:27017/?replicaSet=rs0&directConnection=true` | `mongodb://mongo:27017/?replicaSet=rs0` |
| RabbitMQ AMQP | `localhost:5672` | `rabbitmq:5672` |
| RabbitMQ Management | http://localhost:15672 | `http://rabbitmq:15672` |
| Mailpit UI | http://localhost:8025 | `http://mailpit:8025` |
| SMTP | `localhost:1025` | `mailpit:1025` |
| WireMock | http://localhost:8089 | `http://wiremock:8080` |
| API | http://localhost:5080 | `http://billing-api:8080` |
| Swagger | http://localhost:5080/swagger | `http://billing-api:8080/swagger` |

Mongo anuncia `mongo:27017` no replica set; conexão pelo host usa directConnection=true por ser ambiente de nó único. RabbitMQ usa o host rabbitmq dentro do Compose e localhost fora dele, porta 5672. Management: usuário gearflow e senha gearflow-local por padrão; alterar RABBITMQ_USERNAME/RABBITMQ_PASSWORD no .env antes da primeira inicialização. Credenciais existentes no volume não são rotacionadas apenas alterando .env.

WireMock monta as fixtures versionadas em docker/wiremock/mappings: criar/consultar/cancelar/reembolsar orders e respostas de erro, timeout e JSON inválido. São contratos isolados para BudgetId/valor explícitos, não uma simulação completa com OS. Cenário, dados e ativação estão em [mercado-pago.md](mercado-pago.md).

## Subir o host HTTP

RabbitMQ/Inbox/Outbox, SMTP, Swagger e HTTP autenticado estão implementados. O Compose ativa os componentes com WireMock; consulte [estado executável](estado-implementacao.md), [operação](operacao.md) e [compatibilidade externa](integracao-servicos.md). Execução Docker e homologação real continuam pendentes.

```powershell
docker compose --profile app up -d --build
docker compose logs -f billing-api
```

Alternativa: rodar no host após subir as dependências:

```powershell
$env:ConnectionStrings__Mongo='mongodb://localhost:27017/?replicaSet=rs0&directConnection=true'
$env:Mongo__Database='gearflow_billing'
$env:RabbitMq__Host='localhost'
$env:RabbitMq__Port='5672'
$env:RabbitMq__VirtualHost='/'
$env:RabbitMq__Username='gearflow'
$env:RabbitMq__Password='gearflow-local'
$env:RabbitMq__CommandsExchange='gearflow.commands.v1'
$env:RabbitMq__EventsExchange='gearflow.events.v1'
$env:RabbitMq__CommandsQueue='gearflow.billing.commands.v1'
$env:RabbitMq__DeadLetterExchange='gearflow.deadletters.v1'
$env:Email__Host='localhost'
$env:Email__Port='1025'
$env:MercadoPago__BaseUrl='http://localhost:8089'
$env:Billing__PublicBaseUrl='http://localhost:5080'
$env:Billing__ApprovalTokenSecret=[guid]::NewGuid().ToString('N')
$env:ASPNETCORE_ENVIRONMENT='Development'
# Configure também secrets MP/HMAC e Jwt:Authority/Jwt:Audience em user-secrets.
dotnet run --project src/GearFlow.Billing.Api --urls http://localhost:5080
```

`.env` é interpolado pelo Compose; dotnet run não carrega esse arquivo automaticamente. JWT exige emissor real configurado, mesmo no ambiente local; não desabilitar autenticação. Tokens de aprovação usam segredo HMAC local. O Compose não inclui emissor JWT ou coletor OTEL.

## Rotas e verificações

RabbitMQ/Inbox/Outbox, SMTP, Swagger e HTTP autenticado estão implementados. O Compose ativa os componentes com WireMock; consulte [estado executável](estado-implementacao.md), [operação](operacao.md) e [compatibilidade externa](integracao-servicos.md). Execução Docker e homologação real continuam pendentes.

```powershell
# Infraestrutura disponível agora:
docker compose exec mongo mongosh --quiet --eval 'db.hello().isWritablePrimary'
docker compose exec rabbitmq rabbitmqctl list_exchanges name type
docker compose exec rabbitmq rabbitmqctl list_queues name messages consumers
docker compose exec rabbitmq rabbitmqctl list_bindings source_name destination_name routing_key
# Probes do host atual:
Invoke-RestMethod http://localhost:5080/health/live
Invoke-RestMethod http://localhost:5080/health/ready
```

`/health/live`: processo vivo. `/health/ready` permanece 503 nesta etapa, porque consumer/publisher RabbitMQ não estão conectados. O contrato alvo deverá verificar Mongo primary, RabbitMQ/topologia e workers, sem depender da disponibilidade do provedor de pagamento. Não esperar consumers no painel RabbitMQ com o host atual.

Exemplo de consulta autenticada (substituir token e UUID):

```powershell
curl.exe -H "Authorization: Bearer <jwt>" http://localhost:5080/api/budgets/<budgetId>
```

Para webhook real, definir MERCADO_PAGO_BASE_URL=https://api.mercadopago.com, credenciais de teste válidas e MERCADO_PAGO_NOTIFICATION_URL com HTTPS público de um túnel para a porta 5080. localhost não é acessível pelo Mercado Pago. Recriar o container após editar .env.

A Saga completa exige OS Service + SQL Server e Execution Service + PostgreSQL dos respectivos repositórios. Conectá-los à mesma rede Docker ou usar a porta AMQP do host; cada serviço acessa somente seu banco. Contratos em [rabbitmq.md](rabbitmq.md).

```powershell
# Para sem apagar dados:
docker compose --profile app down
# Logs de infraestrutura:
docker compose logs --tail 100 rabbitmq mongo
```

Ambiente apenas local, com portas ligadas a 127.0.0.1 e Mongo sem autenticação e RabbitMQ com credenciais locais de desenvolvimento; um nó não oferece alta disponibilidade.

## Fronteira arquitetural

A implementação segue [Ports and Adapters](arquitetura-hexagonal.md). HTTP, consumers RabbitMQ e jobs chamam portas de entrada da Application. MongoDB, Mercado Pago, SMTP e publisher RabbitMQ implementam portas de saída. GearFlow.Contracts e envelopes RabbitMQ ficam nos adaptadores, com mapeamento para commands e eventos internos; o núcleo não referencia tecnologias de transporte ou persistência.
