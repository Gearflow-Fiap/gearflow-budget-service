# Validação independente dos outros serviços

Execução em 03/11/2026: **140 testes aprovados, zero falhas, zero ignorados** em .NET 10.0.14/SDK 10.0.403. Divisão: 41 unitários, 10 arquitetura, 22 BDD e 67 integração/componentes. Nove testes de infraestrutura executaram MongoDB 7.0.16 e RabbitMQ 4.2.0 reais; as bordas OS, pagamento remoto e entrega de e-mail do cenário completo foram simuladas. Erlang/OTP 27.3.4.18 foi usado no broker temporário.

## Reproduzir no Windows sem Docker

Binários portáteis estão na pasta .local, ignorada pelo Git. Não houve instalação de serviço, alteração do PATH da máquina ou instalação de Docker/WSL. O runner usa portas dinâmicas de loopback, node/diretório Rabbit exclusivo e databases Mongo aleatórios. Encerra seus processos ao terminar e restaura as variáveis do processo chamador. Logs e dados de teste permanecem em .local; não são backups da aplicação.

```powershell
./tools/Test-NativeInfrastructure.ps1
# Apenas recuperação:
./tools/Test-NativeInfrastructure.ps1 -Filter 'FullyQualifiedName~MessagingRecoveryTests'
```

Em uma nova máquina, extrair as distribuições oficiais e verificar checksums antes do uso:

| Distribuição | Diretório padrão | Evidência de integridade usada |
|---|---|---|
| [SDK 10.0.403 Windows x64](https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.403/dotnet-sdk-10.0.403-win-x64.zip) | .local/dotnet | SHA512 dos [metadados Microsoft](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json) |
| [MongoDB 7.0.16 Windows x64](https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-7.0.16.zip) | .local/mongo | [SHA256 oficial](https://fastdl.mongodb.org/windows/mongodb-windows-x86_64-7.0.16.zip.sha256) |
| [OTP 27.3.4.18](https://github.com/erlang/otp/releases/tag/OTP-27.3.4.18) zip Windows x64 | .local/erlang | SHA256 2af5d35d521211c16e9fa31b23f76e44b7b3284a27c6c1295fbeb382d2ff211f |
| [RabbitMQ 4.2.0](https://github.com/rabbitmq/rabbitmq-server/releases/tag/v4.2.0) zip Windows | .local/rabbit/rabbitmq_server-4.2.0 | SHA256 75241116aa173850224daef49f8e54eeb020f247aa99bce876c587ec486c2122 |

O runner aceita DotnetPath, MongodPath, ErlangHome e RabbitHome explícitos. A alternativa CI/Linux usa Testcontainers com Docker: GEARFLOW_USE_TESTCONTAINERS=true. Para endpoints existentes, usar GEARFLOW_TEST_MONGO e GEARFLOW_TEST_RABBIT somente em infraestrutura de teste: as filas/databases criadas pelos testes têm nomes aleatórios próprios.

## O que foi comprovado

Mongo: rollback estado/Outbox, versões concorrentes, leases entre consumidores, recibo Inbox final atômico e compensação anterior à criação. Rabbit: mensagens persistentes, propriedades/IDs e rejeição mandatory sem rota. Fluxo: comando duplicado gera um orçamento; decisão permite cobrança; confirmação, refund e cancelamento convergem para os eventos/estados esperados. Recuperação: conexão indisponível mantém Outbox pendente, reinício recupera envio; perda após confirm repete o mesmo MessageId; replay malsucedido mantém DLQ, depois replay confirmado mantém bytes/IDs e registra auditoria.

Não se comprovou exatamente uma vez: o teste de perda após confirm demonstra explicitamente a duplicata esperada. Nem todos os pontos possíveis de crash/corrida foram exercitados, e os outros serviços reais não participaram. MP real e SMTP de produção não foram usados.

## Publicação e manifests

`dotnet publish src/GearFlow.Billing.Api -c Release -r linux-x64 --self-contained false` passou. O pacote está em .local/publish/linux-x64 nesta máquina. A imagem Docker usa SDK 10.0.403 e ASP.NET 10.0.14; build/execução da imagem e Compose ainda dependem de Docker e não foram executados.

Kustomize 5.7.1 renderizou k8s/base; kubeconform 0.6.7 validou em modo strict a base e k8s/local-mongo/statefulset.yaml: **10 recursos válidos, zero inválidos/erros/ignorados**. Arquivos dos validadores foram verificados com checksums oficiais.

```powershell
./.local/kustomize/kustomize.exe build k8s/base | Set-Content .local/billing-rendered.yaml
./.local/kubeconform/kubeconform.exe -strict -summary .local/billing-rendered.yaml k8s/local-mongo/statefulset.yaml
```

Schemas válidos não garantem PVC RWX, ingress controller, secrets, conectividade ou funcionamento do cluster. Deploy e rollback continuam sem execução.

## Ativar integrações externas depois

`./tools/Test-ExternalConfiguration.ps1` mostra somente nomes ausentes, sem valores e sem chamadas remotas. No estado atual, Sonar e MP não estão configurados.

SonarCloud: criar organização/projeto vinculado ao repositório e definir gate com cobertura mínima de 80%. No GitHub, configurar vars SONAR_PROJECT_KEY/SONAR_ORG e secret SONAR_TOKEN, habilitar o job sonar como check obrigatório na proteção da branch e executar Billing CI. O scanner 10.4.1 foi restaurado com sucesso; nenhuma análise foi enviada. A coleta local de 1185/1262 linhas instrumentadas (93,90%) não substitui o gate Sonar.

Mercado Pago: criar aplicação/contas de teste, obter credenciais apropriadas e webhook secret; configurar HTTPS público para /api/payments/webhook e confirmar que se trata exclusivamente do ambiente/usuário de teste antes de qualquer cobrança. Usar BaseUrl https://api.mercadopago.com e flags de integração; preencher configuração externamente conforme mercado-pago.md. Demonstrar Pix, webhook assinado, reconciliação sem webhook e refund integral; guardar IDs/evidências sanitizadas. Não há credenciais no projeto e nenhuma cobrança remota foi criada nesta execução.

Nenhum recurso remoto foi criado/alterado, sem commit/push/deploy.
