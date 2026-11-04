[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$directory=Join-Path (Split-Path $PSScriptRoot -Parent) '.local'
New-Item -ItemType Directory -Force $directory | Out-Null
$token=Read-Host 'Access Token do ambiente de TESTE Mercado Pago' -AsSecureString
if($token.Length -lt 10){throw 'Token inválido.'}
$token | Export-Clixml -LiteralPath (Join-Path $directory 'mercadopago.token.xml')
Write-Output 'Credencial protegida pelo usuário Windows atual em .local; nenhum valor foi exibido.'
