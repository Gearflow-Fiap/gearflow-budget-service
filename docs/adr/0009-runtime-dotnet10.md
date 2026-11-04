# ADR 0009 — Atualização para .NET 10 LTS

Status: implementado e testado localmente. Data: 03/11/2026.

## Contexto e decisão

O plano inicial especificava .NET 8. Sua janela de suporte termina em 10/11/2026, enquanto .NET 10 LTS tem suporte até novembro de 2028, conforme a [política Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core). O usuário autorizou preparar a atualização do runtime junto da validação independente.

Atualizar TargetFramework da solution para net10.0, SDK fixado em 10.0.403, referências ASP.NET/JWT/Mvc.Testing em 10.0.14 e runtime da imagem em 10.0.14. CI usa .NET 10 e o gate de publicação exige revisão do runtime fora de sua janela. Não alterar contratos ou regras de domínio por causa da migração.

## Evidência e limites

140 testes passaram sem ignorados em runtime 10.0.14, incluindo infraestrutura nativa real; publicação linux-x64 passou. Auditoria NuGet transitiva não reportou pacotes vulneráveis. SDK foi extraído em .local e verificado pelo SHA512 oficial, sem substituir instalação do Windows. No desenvolvimento, usar o SDK fixado ou o executável local; o SDK global antigo não atende ao global.json atualizado. Docker não foi instalado nem imagem executada. Ver validacao-local.md para reprodução e limitações.
