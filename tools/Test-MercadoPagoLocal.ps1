[CmdletBinding()]
param([switch]$ConfirmedTestCredential)
$ErrorActionPreference='Stop'
if(!$ConfirmedTestCredential){throw 'Confirme que a credencial pertence ao ambiente de teste com -ConfirmedTestCredential.'}
$root=Split-Path $PSScriptRoot -Parent
$path=Join-Path $root '.local/mercadopago.token.xml'
if(!(Test-Path -LiteralPath $path)){throw 'Configure a credencial com tools/Set-MercadoPagoTestToken.ps1.'}
$secure=Import-Clixml -LiteralPath $path
if($secure -isnot [Security.SecureString]){throw 'Formato da credencial protegida inválido.'}
$names=@('GEARFLOW_MP_ACCESS_TOKEN','GEARFLOW_MP_SANDBOX_CONFIRMED','GEARFLOW_MP_EVIDENCE_PATH')
$previous=@{}
try {
 foreach($name in $names){$previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
 $env:GEARFLOW_MP_ACCESS_TOKEN=[Net.NetworkCredential]::new('', $secure).Password
 $env:GEARFLOW_MP_SANDBOX_CONFIRMED='true'
 $env:GEARFLOW_MP_EVIDENCE_PATH=Join-Path $root '.local/mercadopago-result.json'
 # Verify the configured account is a test user before allowing any order creation.
 $identity=Invoke-RestMethod -Uri 'https://api.mercadopago.com/users/me' -Headers @{Authorization=('Bearer '+$env:GEARFLOW_MP_ACCESS_TOKEN)} -TimeoutSec 20
 if($identity.tags -notcontains 'test_user'){throw 'A conta não retornou a marca test_user; nenhuma order foi solicitada.'}
 # Invokes real application handlers, Mongo and Rabbit. Only OS and email delivery are simulated.
 & (Join-Path $PSScriptRoot 'Test-NativeInfrastructure.ps1') -Project 'tests/GearFlow.Billing.IntegrationTests' -Filter 'FullyQualifiedName~MercadoPago_sandbox_Pix_is_confirmed_by_query_without_webhook'
 if(Test-Path -LiteralPath $env:GEARFLOW_MP_EVIDENCE_PATH){Get-Content -LiteralPath $env:GEARFLOW_MP_EVIDENCE_PATH -Raw}
}
finally {
 foreach($name in $previous.Keys){[Environment]::SetEnvironmentVariable($name,$previous[$name],'Process')}
 $secure.Dispose()
}
