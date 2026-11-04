[CmdletBinding()]
param([ValidateSet('All','Sonar','MercadoPago')][string]$Profile = 'All')
$groups=@{
 Sonar=@('SONAR_PROJECT_KEY','SONAR_ORG','SONAR_TOKEN')
 MercadoPago=@('MERCADO_PAGO_ACCESS_TOKEN','MERCADO_PAGO_WEBHOOK_SECRET','MERCADO_PAGO_NOTIFICATION_URL')
}
foreach($name in @('Sonar','MercadoPago')) {
 if($Profile -ne 'All' -and $Profile -ne $name){continue}
 $missing=@($groups[$name] | Where-Object {[string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($_,'Process'))})
 $https=$null; $valid=$true
 if($name -eq 'MercadoPago') {
  $https=$false
  if(!$missing.Contains('MERCADO_PAGO_NOTIFICATION_URL')) {
   $uri=$null
   $https=[uri]::TryCreate($env:MERCADO_PAGO_NOTIFICATION_URL,[UriKind]::Absolute,[ref]$uri) -and $uri.Scheme -eq 'https' -and !$uri.IsLoopback -and !$uri.UserInfo
  }
  $valid=$https
 }
 [pscustomobject]@{Integration=$name;Configured=($missing.Count -eq 0 -and $valid);MissingNames=($missing -join ', ');PublicHttps=$https}
}
# Read-only local check: never prints secret values or calls external services.
