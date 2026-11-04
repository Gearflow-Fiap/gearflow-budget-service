> Fluxo atual: pagamento simulado com `MercadoPago__UseMock=true` (padrão no Compose e no ConfigMap Kubernetes). Ao receber RequestPayment, o gateway retorna Paid imediatamente; Billing persiste Payment/Budget e publica PaymentRequested e PaymentConfirmed pelo Outbox. Não chama a API Mercado Pago, não exige credenciais/webhook e não gera Pix real. RefundPayment também retorna sucesso simulado; cobrança já paga exige reembolso antes do cancelamento. Mongo, RabbitMQ, aprovação e demais regras continuam ativos. Para execução direta, definir também MercadoPago__Enabled=true e a configuração normal de persistência. Para retomar a integração HTTP, definir MercadoPago__UseMock=false e configurar o provedor conforme os guias anteriores. Evidências sandbox abaixo são históricas e não são pré-requisito do fluxo simulado.

# Estado executável da implementação

Atualizado em 03/11/2026. Implementação local não equivale a homologação externa.

Serviço em .NET 10 LTS, SDK 10.0.403/runtime 10.0.14. Implementados domínio DDD, CQRS/validação, Mongo/UoW, Inbox final transacional, Outbox/AMQP, confirms/mandatory, ACK manual/retries/DLQ/replay, SMTP, API/JWT/CSRF, Orders/Pix/webhook/reconciliação/refund e telemetria. Contratos de comando/evento permanecem v1 locais.

## Evidência atual

**150 testes locais aprovados, zero falhas e zero ignorados**: 41 unitários, 10 arquitetura, 22 BDD e 77 integração/componentes. O teste remoto opt-in passou separadamente, totalizando 151 cenários distintos validados. Nove testes usaram Mongo e Rabbit reais, com processos portáteis temporários no projeto. O cenário financeiro local simulou OS, Mercado Pago e entrega de e-mail. Adicionalmente, um Pix sandbox real de BRL 50,00 foi criado e confirmado por consulta na API Mercado Pago, sem webhook; ver mercado-pago-local.md. Foram validados duplicatas, consistência local, fluxo completo Billing, recuperação do Outbox e replay da DLQ. Detalhes e reprodução em [validação local](validacao-local.md).

Publicação linux-x64 passou. Auditoria transitiva NuGet sem vulnerabilidades reportadas. Kustomize/kubeconform validaram 10 recursos Kubernetes, sem erros. Docker/Compose e imagem Docker não foram executados; nenhum cluster foi acessado. SonarScanner restaurado, sem análise externa. Credencial MP de teste protegida por DPAPI em .local; sem valores em arquivos versionados. Cobertura local das linhas instrumentadas: 1193/1270 (93.94%); não substitui gate global Sonar.

```powershell
# Usa executáveis portáteis já baixados nesta máquina:
./tools/Test-NativeInfrastructure.ps1
# Em máquina com Docker e SDK definido no global.json:
$env:GEARFLOW_USE_TESTCONTAINERS = 'true'
dotnet test GearFlow.Billing.sln -c Release
```

## Falta para homologação/produção

Ratificar/distribuir contratos e adaptar o OS real (ver integracao-servicos.md), definir claims/chaves JWT, criar/configurar SonarCloud, homologar entrega de webhook com HTTPS público e compensações/refund no provedor. Pix sandbox por consulta já validado. Executar Docker/Compose e imagem, configurar ambiente de produção, CI remoto, secrets, deploy/rollback e restauração de backup. Sonar ainda não configurado; a credencial MP foi fornecida pelo usuário, e a conta test_user foi verificada.

[Operação](operacao.md), [rastreabilidade](rastreabilidade.md), [ADR 0008](adr/0008-entrega-e-compensacao-duraveis.md) e [ADR 0009](adr/0009-runtime-dotnet10.md). Alterações previamente no índice preservadas. Sem commit/push, deploy ou cobrança remota.

