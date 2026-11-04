# Coleta atual — .NET 10 / 03/11/2026

140 aprovados, zero falhas e zero ignorados. União arquivo+linha dos quatro relatórios da execução tools/Test-NativeInfrastructure.ps1: **1185/1262 linhas instrumentadas (93,90%)**. Inclui nove testes reais de infraestrutura nativa. Não é quality gate Sonar aprovado. Excluídos apenas assemblies de testes e código gerado conforme coverlet.runsettings. Auditoria transitiva NuGet sem vulnerabilidades reportadas. CI remoto, Sonar e sandbox MP não executados.

Os relatórios TRX/Cobertura/OpenCover ficam em tests/*/TestResults (ignorados no Git). Limites e reprodução em [validacao-local.md](../validacao-local.md).

## Histórico anterior à migração

# Evidência de cobertura

Última coleta de cobertura da integração MP em 02/11/2026: 98 testes aprovados e 2 testes Mongo explicitamente ignorados por ausência de replica set configurado. União dos relatórios: 766/900 linhas (85,11%) nos assemblies instrumentados/carregados. Após testar também as cinco fixtures WireMock versionadas, a suíte atual tem 103 testes aprovados e os mesmos 2 ignorados. A API agora é referenciada pelos testes de endpoint, mas startup/workers e persistência real não foram exercitados nessa coleta. Este resultado não substitui o gate global/Sonar nem comprova concorrência em Mongo. Job de CI separado foi preparado para executar os dois MongoFact com replica set descartável; ainda não executado remotamente.

## Evidência histórica do núcleo inicial

Execução local de 02/11/2026: 53 testes aprovados. Configuração: coverlet.runsettings na raiz. CI armazena TRX e Cobertura em artefato test-results.

Comando reproduzível:

```powershell
dotnet build GearFlow.Billing.sln -c Release
dotnet test GearFlow.Billing.sln -c Release --no-build --collect:"XPlat Code Coverage" --settings coverlet.runsettings --logger trx
```

Na execução local, a união das linhas dos relatórios mais recentes dos quatro projetos cobriu 441 de 470 linhas (93,83%) dos assemblies carregados pelos testes. A união usa arquivo+número da linha e considera coberta a linha atingida em qualquer projeto. A API não é referenciada pelos testes e não está incluída nesse denominador. Portanto esse valor não comprova a meta global de 80% para o serviço completo e não é um gate Sonar aprovado.

Os testes de arquitetura inspecionam referências dos assemblies e encapsulamento público. Os de integração atuais exercitam DI, dispatcher e HMAC. Rollback nos cenários do handler usa fake, não Mongo real. Cenários de persistência, contratos externos, concorrência entre réplicas e broker continuam pendentes. Não se substitui revisão semântica de DDD por cobertura.

## Coleta atual — 03/11/2026

131 testes aprovados, seis ignorados. União arquivo+linha dos quatro relatórios mais recentes: 1027/1262 linhas (81.38%). Inclui API/adaptadores carregados; testes reais ignorados deixam infraestrutura sem cobertura. Não é gate Sonar aprovado. Valores históricos acima não representam o serviço atual; meta global de 80% permanece pendente.



Auditoria NuGet com dependências transitivas em 03/11/2026: nenhum pacote vulnerável reportado pelas fontes consultadas. Coleta final: 1027/1262 linhas (81.38%). Relatórios completos ficam nos diretórios TestResults ignorados pelo Git e nos artefatos do CI quando executado.



## Atualização após Pix sandbox local — 03/11/2026

150 testes locais aprovados (sem falhas/ignorados; teste remoto excluído explicitamente dessa execução), mais um teste opt-in remoto aprovado separadamente. Coleta local atual: 1193/1270 linhas instrumentadas (93.94%). Mercado Pago sandbox confirmou BRL 50,00 por consulta; evidências em mercado-pago-local.md. Não foi executado gate Sonar.

