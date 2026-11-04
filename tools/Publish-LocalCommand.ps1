# Somente desenvolvimento: simula o produtor OS usando RabbitMQ Management.
[CmdletBinding()]
param(
 [ValidateSet('GenerateBudget','RequestPayment','CancelBudget','RefundPayment')][string]$Command = 'GenerateBudget',
 [Parameter(Mandatory)][guid]$ServiceOrderId,
 [Parameter(Mandatory)][string]$PayloadPath,
 [guid]$MessageId = [guid]::NewGuid(),
 [uri]$ManagementUrl = 'http://localhost:15672',
 [string]$Username = $env:RABBITMQ_USERNAME,
 [string]$Password = $env:RABBITMQ_PASSWORD
)
$ErrorActionPreference = 'Stop'
if (!$ManagementUrl.IsLoopback -or $ManagementUrl.Scheme -ne 'http') { throw 'Este simulador permite apenas Management HTTP local.' }
if (!$Username -or !$Password) { throw 'Defina RABBITMQ_USERNAME e RABBITMQ_PASSWORD do ambiente local.' }
if ($ServiceOrderId -eq [guid]::Empty -or $MessageId -eq [guid]::Empty) { throw 'Identidades não podem ser vazias.' }
$payload = Get-Content -LiteralPath $PayloadPath -Raw | ConvertFrom-Json
$envelope = [ordered]@{
 messageId=$MessageId.ToString('D'); messageType=$Command; schemaVersion=1
 occurredOn=[DateTimeOffset]::UtcNow.ToString('o'); correlationId=$ServiceOrderId.ToString('D')
 serviceOrderId=$ServiceOrderId.ToString('D'); causationId=$null
 producer='gearflow-os-service'; payload=$payload
}
$body = @{
 properties=@{ content_type='application/json'; delivery_mode=2; message_id=$MessageId.ToString('D'); correlation_id=$ServiceOrderId.ToString('D') }
 routing_key="billing.$Command"; payload=($envelope | ConvertTo-Json -Depth 20 -Compress); payload_encoding='string'
} | ConvertTo-Json -Depth 25
$authorization = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${Username}:$Password"))
$result = Invoke-RestMethod -Method Post -Uri ([uri]::new($ManagementUrl, '/api/exchanges/%2F/gearflow.commands.v1/publish')) -Headers @{Authorization="Basic $authorization"} -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body))
if (!$result.routed) { throw 'Mensagem sem rota. Confira a topologia local.' }
Write-Output "Encaminhada: $MessageId. Confira Inbox/Outbox e eventos; roteamento não comprova conclusão do caso de uso."
