> Fluxo atual: pagamento simulado com `MercadoPago__UseMock=true` (padrão no Compose e no ConfigMap Kubernetes). Ao receber RequestPayment, o gateway retorna Paid imediatamente; Billing persiste Payment/Budget e publica PaymentRequested e PaymentConfirmed pelo Outbox. Não chama a API Mercado Pago, não exige credenciais/webhook e não gera Pix real. RefundPayment também retorna sucesso simulado; cobrança já paga exige reembolso antes do cancelamento. Mongo, RabbitMQ, aprovação e demais regras continuam ativos. Para execução direta, definir também MercadoPago__Enabled=true e a configuração normal de persistência. Para retomar a integração HTTP, definir MercadoPago__UseMock=false e configurar o provedor conforme os guias anteriores. Evidências sandbox abaixo são históricas e não são pré-requisito do fluxo simulado.

# Mercado Pago Pix local, sem webhook

Em 03/11/2026 o fluxo foi executado contra a API Mercado Pago com conta marcada test_user: Pix de teste BRL 50,00, confirmado por consulta, Budget Paid e PaymentConfirmed publicado no Rabbit. Nenhuma URL pública, túnel ou webhook foi usado. A aprovação do cliente/OS e a entrega de e-mail são simuladas; os handlers Billing, Mongo, Rabbit e gateway Mercado Pago são reais. Esta evidência não inclui refund no provedor real.

## Repetir nesta máquina

```powershell
./tools/Test-MercadoPagoLocal.ps1 -ConfirmedTestCredential
```

O comando inicia processos temporários de Mongo/Rabbit e o host de aplicação Billing, envia GenerateBudget e RequestPayment pelo Rabbit, aprova pelo handler e consulta o mesmo pagamento com RecoverPaymentCommand a cada três segundos até confirmar. Não inicia uma API HTTP pública nem precisa abrir navegador. Ao terminar, encerra os processos e limpa somente as filas/database de teste gerados para essa execução. O resultado sanitizado fica em .local/mercadopago-result.json. Cada execução bem-sucedida cria uma nova order de teste de R$ 50,00 na conta Mercado Pago.

A credencial fornecida foi guardada em .local/mercadopago.token.xml, protegida por DPAPI para o usuário Windows atual, fora do Git e do contexto Docker. O token não é argumento de comando do processo de teste. A Public Key não é necessária para esse backend. O script exige confirmação de credencial de teste e verifica a marca test_user via GET /users/me antes de criar a order.

Para substituir a credencial, executar no PowerShell do usuário Windows:

```powershell
./tools/Set-MercadoPagoTestToken.ps1
```

O prompt é oculto; não colocar token em arquivo versionado ou em comando salvo no histórico. Outra máquina/usuário precisa cadastrar sua própria credencial e disponibilizar os binários descritos em validacao-local.md. O teste remoto não executa no CI comum: exige GEARFLOW_MP_SANDBOX_CONFIRMED=true e credencial em memória, preparados pelo script acima.

## Configuração do serviço HTTP local

Para usar a API normalmente com infraestrutura local já disponível:

```text
ASPNETCORE_ENVIRONMENT=Development
MercadoPago__Enabled=true
MercadoPago__TestMode=true
MercadoPago__WebhookEnabled=false
MercadoPago__BaseUrl=https://api.mercadopago.com/
MercadoPago__AccessToken=<credencial externa de TESTE>
Billing__PublicBaseUrl=http://localhost:5080
```

Manter as configurações Mongo replica set, Rabbit, SMTP e JWT descritas no guia local. WebhookSecret não é exigido nesse modo, e a rota /api/payments/webhook não é registrada. Produção rejeita TestMode ou desativação do webhook; o comportamento padrão continua webhook habilitado com assinatura obrigatória. O modo de teste adiciona somente payer.first_name=APRO no adaptador; não altera nome/snapshot do cliente no domínio. TestMode não converte uma credencial de produção em teste.

Na execução normal, a reconciliação mantém sua política de cinco minutos, selecionando pagamentos Pending há mais de dez minutos. O teste acima chama o mesmo caso de recuperação em intervalos curtos para validar a confirmação sem aguardar esse prazo. Consultas GET da API Billing leem a projeção local; não forçam consulta remota.

## Evidência e descoberta de contrato

A API de busca da conta de teste retornou paging.total como string JSON. O adaptador agora aceita inteiros não negativos tanto como número quanto como string; continua recusando duplicatas e formatos inválidos. Há testes de regressão para essa diferença observada no provedor.

Resultado sanitizado versionado em docs/evidencias/mercado-pago-local-2026-11-03.json. Nenhum token, QR ou dado pessoal do pagador está nesse arquivo. A assinatura/entrega real de webhook permanece sem homologação e requer URL HTTPS acessível pelo Mercado Pago quando for testada.

Referências: [Pix de teste com APRO](https://www.mercadopago.com.br/developers/pt/docs/checkout-api-orders/integration-test/pix), [consultar order](https://www.mercadopago.com.br/developers/pt/reference/online-payments/checkout-api/get-order/get), [notificações HTTPS](https://www.mercadopago.com.br/developers/pt/docs/checkout-api-orders/notifications).

