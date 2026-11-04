[CmdletBinding()]
param(
 [string]$DotnetPath,
 [string]$MongodPath,
 [string]$ErlangHome,
 [string]$RabbitHome,
 [string]$Filter = '',
 [ValidateSet('GearFlow.Billing.sln','tests/GearFlow.Billing.IntegrationTests')][string]$Project = 'GearFlow.Billing.sln'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (!$DotnetPath) { $DotnetPath = Join-Path $root '.local/dotnet/dotnet.exe' }
if (!$MongodPath) { $MongodPath = (Get-ChildItem (Join-Path $root '.local/mongo') -Recurse -Filter mongod.exe | Select-Object -First 1).FullName }
if (!$ErlangHome) { $ErlangHome = Join-Path $root '.local/erlang' }
if (!$RabbitHome) { $RabbitHome = Join-Path $root '.local/rabbit/rabbitmq_server-4.2.0' }
foreach ($path in @($DotnetPath,$MongodPath,(Join-Path $ErlangHome 'bin/erl.exe'),(Join-Path $RabbitHome 'sbin/rabbitmq-server.bat'))) {
 if (!$path -or !(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Test executable missing: $path. See docs/validacao-local.md." }
}
function Free-Port {
 $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
 $listener.Start()
 try { return $listener.LocalEndpoint.Port } finally { $listener.Stop() }
}
$port=Free-Port; $dist=Free-Port; $epmd=Free-Port
$run=Join-Path $root ('.local/rabbit-runs/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $run | Out-Null
@"
listeners.tcp.1 = 127.0.0.1:$port
distribution.listener.interface = 127.0.0.1
loopback_users.guest = true
"@ | Set-Content (Join-Path $run 'rabbitmq.conf')
$variables=@{
 DOTNET_ROOT=(Split-Path $DotnetPath -Parent); GEARFLOW_TEST_MONGOD=$MongodPath; GEARFLOW_TEST_MONGO=''
 GEARFLOW_TEST_RABBIT="amqp://guest:guest@127.0.0.1:$port/"; GEARFLOW_USE_TESTCONTAINERS='false'
 ERLANG_HOME=$ErlangHome; RABBITMQ_BASE=$run; RABBITMQ_CONFIG_FILE=(Join-Path $run 'rabbitmq.conf')
 RABBITMQ_NODENAME=('billingtest'+[guid]::NewGuid().ToString('N').Substring(0,8)+'@localhost')
 RABBITMQ_DIST_PORT="$dist"; ERL_EPMD_ADDRESS='127.0.0.1'; ERL_EPMD_PORT="$epmd"
 ERL_CRASH_DUMP=(Join-Path $run 'erl_crash.dump')
 RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS=('-setcookie '+[guid]::NewGuid().ToString('N')+' -kernel inet_dist_use_interface {127,0,0,1} +S 2:2')
}
$previous=@{}; $process=$null
Push-Location $root
try {
 foreach($name in $variables.Keys) { $previous[$name]=[Environment]::GetEnvironmentVariable($name,'Process'); [Environment]::SetEnvironmentVariable($name,$variables[$name],'Process') }
 # No service installation or machine environment changes. Only this process tree is started/stopped.
 $batch=Join-Path $RabbitHome 'sbin/rabbitmq-server.bat'
 $process=Start-Process -FilePath $env:ComSpec -ArgumentList @('/d','/s','/c',('""'+$batch+'""')) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'stdout.log') -RedirectStandardError (Join-Path $run 'stderr.log')
 $deadline=[DateTimeOffset]::UtcNow.AddSeconds(90)
 do {
  if($process.HasExited) { throw "Temporary Rabbit exited. Logs: $run" }
  if([DateTimeOffset]::UtcNow -gt $deadline) { throw "Temporary Rabbit did not start. Logs: $run" }
  Start-Sleep -Milliseconds 250
  $ready=(Get-Content (Join-Path $run 'stdout.log') -Raw -ErrorAction SilentlyContinue) -match 'Starting broker.*completed'
 } until ($ready)
 $arguments=@('test',$Project,'-c','Release','--collect:XPlat Code Coverage','--settings','coverlet.runsettings','--logger','trx')
 if($Filter) { $arguments+=@('--filter',$Filter) }
 & $DotnetPath @arguments
 if($LASTEXITCODE -ne 0) { throw "Test run failed (exit $LASTEXITCODE). Logs: $run" }
}
finally {
 if($process) { try { if(!$process.HasExited) {$process.Kill($true); $process.WaitForExit()} } finally {$process.Dispose()} }
 $epmdExecutable=Get-ChildItem $ErlangHome -Filter epmd.exe -Recurse | Select-Object -First 1
 if($epmdExecutable) { & $epmdExecutable.FullName -port $epmd -kill | Out-Null }
 foreach($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name,$previous[$name],'Process') }
 Pop-Location
}
