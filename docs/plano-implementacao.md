# Plano de implementação do GearFlow Billing

Data: 03/11/2026. Implementação local ampliada; [estado executável](estado-implementacao.md) e [rastreabilidade](rastreabilidade.md) registram evidências. Checkboxes indicam código/decisão local, nunca homologação externa. Aceites com infraestrutura permanecem condicionais.

## Objetivo e escopo

Implementar integralmente o serviço de orçamento e pagamento descrito no README, nos guias de fluxo, Saga, RabbitMQ, ambiente local, arquitetura hexagonal e ADR 0004. O estado inicial continha apenas documentação e infraestrutura local. Agora há src/tests, solution, Dockerfile, CI/CD e manifests; validação de produção não está concluída.

O Billing será participante da Saga: recebe comandos, executa operações locais e responde com eventos. OS, orquestração, diagnóstico, estoque, execução e respectivos bancos pertencem aos outros repositórios. O plano cobre seus contratos e a validação integrada, não transfere essas responsabilidades para o Billing.

Preservar as alterações existentes no repositório. Implementar por PRs pequenos, com testes e documentação acompanhando cada funcionalidade.

## Obrigação transversal: Domain-Driven Design (DDD)

**A implementação deve seguir DDD estratégico e tático. Este é um requisito obrigatório de arquitetura e de aceite de todos os PRs que alterem comportamento de negócio.** Arquitetura hexagonal e CQRS complementam esse requisito; sua presença isolada não comprova conformidade com DDD.

1. **Contextos delimitados e linguagem ubíqua:** documentar o contexto Billing e seu relacionamento com OS e Execution em um mapa de contextos. Manter glossário de orçamento, aprovação, cobrança, pagamento, cancelamento e reembolso, refletido nos nomes do código, contratos e cenários BDD. Cliente e itens recebidos são snapshots locais, sem importar agregados de outros contextos.
2. **Agregados e consistência:** Budget e Payment são raízes de agregado com identidade e ciclo de vida próprios. Documentar propriedade dos objetos internos, invariantes e limites de consistência. Referenciar outros agregados por identidade; alterações nos objetos internos passam pela raiz. A transação local já prevista para confirmação de Payment + Budget + Outbox é uma exceção explícita entre agregados e deve ter justificativa registrada em ADR e testes de concorrência.
3. **Modelo de domínio com comportamento:** cálculos, elegibilidade, invariantes e transições pertencem às entidades, value objects ou serviços de domínio. Não permitir setters públicos ou coleções mutáveis que contornem as regras. Handlers coordenam carregamento, autorização, chamadas às portas e persistência; controllers, consumers, jobs e repositórios não decidem regras de negócio. FluentValidation valida a entrada, sem substituir a proteção das invariantes no domínio.
4. **Entidades e value objects:** classificar BudgetItem, Customer, PaymentAttempt e Notification segundo identidade e ciclo de vida, justificando as escolhas. Usar value objects imutáveis, com igualdade por valor e validação na criação, para conceitos que os exijam, incluindo dinheiro (valor/moeda) e e-mail. Não transformar automaticamente cada tabela/documento em entidade ou cada campo em value object.
5. **Serviços de domínio:** usar somente para políticas de negócio que não pertençam naturalmente a uma entidade/value object. Permanecem independentes de HTTP, Mongo, RabbitMQ, SMTP e Mercado Pago. Evitar serviços que apenas concentrem toda a lógica de agregados anêmicos.
6. **Eventos de domínio e integração:** fatos de negócio internos são eventos de domínio, com significado e momento de emissão definidos. Eventos publicados para a Saga são contratos de integração distintos, derivados desses fatos quando aplicável. Application coordena a gravação no Outbox e Adapters traduz para GearFlow.Contracts/envelopes. Não publicar no broker dentro do agregado nem expor contratos externos no núcleo; falhas técnicas não devem ser inventadas como fatos de domínio.
7. **Repositórios e leitura:** repositórios de escrita persistem e reconstituem raízes de agregado preservando encapsulamento. Não criar repositórios para alterar isoladamente objetos internos. Queries usam projeções/read models e nunca alteram o domínio.
8. **Camada anticorrupção:** adaptadores traduzem vocabulário, estados e erros do Mercado Pago e dos contratos externos para conceitos locais. Modelos do provedor, BSON, atributos de persistência e envelopes não entram no Domain/Application.
9. **Evidência de conformidade:** cada mudança de negócio deve apresentar testes de invariantes/transições pelo comportamento público, cenários BDD pertinentes e atualização do glossário/modelo quando necessária. Testes de arquitetura verificam dependências; revisão do modelo verifica semântica, encapsulamento e localização das regras. Cobertura numérica sozinha não comprova DDD.

Um PR de negócio não está pronto se introduzir regra fora do domínio, permitir estado inválido por mutação direta ou violar os limites de contexto/agregado. Qualquer revisão desses limites exige justificativa em ADR, atualização do plano e evidência de testes antes do aceite.

## 1. Consolidar decisões e contratos — P0

- [x] Criar [matriz de rastreabilidade](rastreabilidade.md) ligando cada requisito aos casos de uso, contratos, testes e evidências, incluindo lacunas explícitas.
- [x] Registrar [ADR 0005](adr/0005-domain-driven-design.md) tornando DDD obrigatório e criar [modelo inicial, glossário e mapa de contextos](modelo-dominio.md), com agregados, invariantes, eventos e limites de consistência; instruções em [AGENTS.md](../AGENTS.md). Formalização documental concluída; validação com o time e implementação seguem pendentes.
- [ ] Fechar com OS/Execution os contratos v1 de docs/rabbitmq.md: quatro comandos recebidos, nove eventos publicados, envelope, preços, moeda, correlação e versionamento.
- [ ] Definir origem, versão e distribuição do pacote GearFlow.Contracts. Usá-lo somente nos adaptadores.
- [x] Registrar os ADRs 0001–0003 citados no README; pagamento antes da reserva e snapshot formalizados como decisões locais, com alinhamento externo ainda pendente.
- [x] Harmonizar divergências: timeout técnico não significa pagamento recusado; consulta/repetição idempotente deve resolver resultado incerto antes de publicar falha definitiva.
- [x] Completar matriz de estados: cancelamento em Pending/Approved/Rejected/Expired, pagamento confirmado durante cancelamento, webhook atrasado e compensação sem entidade criada.
- [x] Definir precisão/arredondamento monetário, orçamento de total zero, prazo efetivo e repetição divergente no [ADR 0006](adr/0006-politicas-do-nucleo.md), com testes do núcleo.
- [x] Definir contrato de erros HTTP, autorização de consultas por usuário/papel e origem do JWT.
- [x] Revisar links GET que alteram estado: documentar risco de scanners de e-mail e decidir se GET abre confirmação e PUT/POST efetiva a decisão. Atualizar contratos se houver mudança.
- [x] Conferir documentação oficial de Pix, assinatura, identificadores, estados, busca, cancelamento/refund e idempotência. Adaptador/testes e referências no [ADR 0007](adr/0007-integracao-pix-e-consistencia.md); homologação sandbox continua pendente.

Aceite: decisões registradas e contratos com exemplos aprovados pelo time; questões externas identificadas com responsável. Pode-se avançar no núcleo sem credenciais reais.

## 2. Criar a base da solution — P0

- [x] Criar solution e projetos Domain, Application, Adapters e Api; atualização para .NET 10 LTS implementada e validada conforme ADR 0009.
- [x] Criar UnitTests, IntegrationTests, ArchitectureTests e BddTests. IntegrationTests incluem testes reais condicionais via Testcontainers; Reqnroll inclui três cenários executados.
- [x] Centralizar versões de dependências, nullable, warnings como erros, build determinístico e SDK no global.json.
- [x] Implementar Result, erros internos, IClock e infraestrutura commands/queries/handlers/IDispatcher próprio, sem MediatR. Casos de uso remotos seguem nas etapas específicas.
- [ ] Criar decorators de validação e logging, DI e opções validadas no startup.
- [x] Adicionar testes de arquitetura: Domain independente; Application depende apenas de Domain/runtime; Contracts e tecnologias fora do núcleo; encapsulamento do modelo.
- [x] Criar CI inicial de restore/build/test e upload de evidências. Comandos equivalentes executados localmente; execução do workflow remoto não verificada. Expandir os gates nas etapas seguintes.

Aceite: solution compila, testes de arquitetura passam e host inicia com configuração válida.

## 3. Implementar domínio e portas — P0

- [x] Budget, BudgetItem, Customer, Payment, PaymentAttempt e Notification criados, encapsulados e testados localmente. Persistência/reconstituição e casos de uso remotos implementados; infraestrutura real ainda não executada.
- [ ] Classificar entidades/value objects, implementar encapsulamento das raízes Budget/Payment e eventos de domínio; documentar políticas que exigem serviço de domínio e a exceção transacional entre agregados.
- [x] Cálculo com decimal, validação de itens, moeda BRL, datas UTC e snapshots imutáveis de cliente/preços, com testes de comportamento.
- [ ] Transições de aprovação, rejeição, expiração, cobrança, pagamento, cancelamento e reembolso conforme matriz consolidada.
- [ ] Proteger estados terminais contra mensagens atrasadas e repetições; modelar progresso técnico de operações externas sem confundi-lo com status de negócio.
- [x] Implementar interfaces neutras de repositório, read models, gateway, SMTP, tokens, Outbox, unidade de trabalho e relógio. Adaptadores implementados; ver rastreabilidade.
- [ ] Testar invariantes, arredondamento, limites temporais, transições inválidas e compensações repetidas/sem efeito anterior.

Aceite: regras testadas sem Mongo, broker ou HTTP; estados e valores inválidos são rejeitados. A revisão DDD confirma linguagem ubíqua, limites dos agregados, value objects, encapsulamento e separação entre eventos de domínio e integração.

## 4. Implementar persistência e entrega confiável — P0

Atualização: Mongo, Inbox final transacional, Outbox/AMQP, confirms, retries, DLQ e replay implementados. Quatro testes Mongo, um Rabbit, um de fluxo e três de recuperação passaram com infraestrutura nativa real. Pontos adicionais de corrida/crash e homologação entre serviços permanecem abertos; ver validacao-local.md.

- [x] Mapear documentos Mongo, UUIDs, decimals, datas e versões para controle otimista de concorrência.
- [x] Implementar repositórios e projeções; inicialização idempotente dos índices descritos no README.
- [x] Implementar UoW com mesma sessão/transação para estado, Inbox e Outbox no replica set.
- [x] Implementar Inbox única por consumerName/messageId; falhas não podem bloquear reprocessamento correto.
- [x] Implementar Outbox com aquisição de trabalho segura entre réplicas, lease, recuperação após crash e tentativas persistidas.
- [x] Implementar publisher RabbitMQ.Client: persistência, mandatory, confirms, Basic.Return e reconexão. Marcar envio somente após sucesso confirmado.
- [x] Implementar consumidores com ack após commit, prefetch inicial 1, três retries em 1s/5s/15s e cancelamento correto no shutdown.
- [x] Implementar DLQ explícita, preservação de payload inválido, erro sanitizado e ack da origem somente após publicação confirmada.
- [x] Criar procedimento auditável de replay com mesmos IDs e sem retirar a mensagem antes da confirmação.
- [x] Validar envelope, versão, routing keys, correlationId e propagação de trace.

Aceite: testes com Mongo/RabbitMQ reais comprovam rollback, duplicatas, crash após publicação, mensagem sem rota, falha da DLQ e concorrência entre duas instâncias.

## 5. Entregar orçamento e decisão do cliente — P0

- [x] GenerateBudgetCommand: validar snapshot, calcular total, salvar Pending e emitir BudgetGenerated; falha de negócio gera BudgetGenerationFailed via Outbox.
- [x] Consumir GenerateBudget e mapear contratos externos para tipos internos.
- [x] Implementar HMAC de aprovação com expiração, comparação segura e proteção contra exposição em logs.
- [x] Implementar ApproveBudgetCommand e RejectBudgetCommand com eventos e idempotência.
- [x] Implementar GetBudgetByIdQuery e GetBudgetByServiceOrderQuery.
- [x] Expor rotas de orçamento documentadas, páginas HTML de resultado, validação e respostas de erro consistentes.
- [x] Implementar templates HTML, envio SMTP e histórico de notificações com retry persistido. Falha de SMTP não deve recriar orçamento nem repetir evento de negócio.
- [x] Definir deduplicação de notificações; SMTP pode aceitar um e-mail antes de um crash, então não prometer envio exatamente uma vez.

Aceite: comando RabbitMQ gera um único orçamento; Mailpit recebe o link; decisão válida altera estado e publica evento; token adulterado/expirado e decisões concorrentes são tratados.

## 6. Entregar criação da cobrança Pix — P0

Atualização: gateway, workflow durável, Rabbit, SMTP e HTTP/JWT implementados; testes determinísticos aprovados. Homologação MP e entre serviços pendente.

- [x] Implementar IPaymentGateway via HttpClient tipado, com JSON externo restrito ao adaptador e testes de contrato.
- [x] RequestPaymentCommand valida orçamento aprovado, OS, valor e moeda; cria um único Payment por Budget.
- [x] Persistir intenção da operação antes do HTTP e resultado depois; nunca manter transação Mongo aberta na chamada externa.
- [x] Criar order Pix com chave idempotente documentada, registrar tentativas, ID, QR, ticket e validade.
- [x] Implementar timeout de 10s, até três retries, circuit breaker, Retry-After e orçamento de 55s por requisição HTTP; testes de retry, 4xx, 429 e timeout real por tentativa.
- [x] Recuperar timeout/crash após criação remota por idempotência e consulta; não criar nova cobrança para resolver incerteza.
- [x] Publicar PaymentRequested via Outbox, atualizar Budget para AwaitingPayment e solicitar e-mail com Pix.
- [x] Implementar GetPaymentByIdQuery, GetPaymentByBudgetQuery e rotas autenticadas.
- [x] Versionar stubs WireMock para sucesso, recusa, erro de validação, 429, 5xx, timeout e resposta inválida.

Aceite: comandos repetidos ou concorrentes produzem uma única cobrança; testes verificam payload, headers, retries e recuperação após resposta perdida.

## 7. Confirmar pagamento por webhook e reconciliação — P0

Atualização: endpoint assinado, fila Mongo payment_work, worker com lease, handlers de confirmação/recuperação e reconciliação estão implementados e registrados quando MercadoPago:Enabled=true. payment_work exerce a deduplicação/trabalho de webhook e aplica TTL somente após conclusão. Sem comprovação de duas réplicas/Mongo real neste ambiente; não marcar o aceite de integração apenas pelos fakes.

- [x] Validar assinatura HMAC conforme manifesto, IDs, comparação constante e tolerância de cinco minutos; corpo não fornece evidência financeira. Testes de adulteração, ID, replay temporal e formato em segundos/milissegundos.
- [x] Receber e persistir notificação/trabalho durável antes de responder 200; executar consulta externa fora da requisição para cumprir o limite documentado de 22s.
- [x] Implementar processed_webhooks (equivalente implementado como payment_work) com índice único e TTL de 30 dias; retenção não substitui idempotência do domínio.
- [x] ProcessPaymentWebhookCommand consulta order e verifica vínculo, valor e moeda antes de confirmar.
- [x] Atualizar Payment e Budget com Outbox na mesma transação; emitir PaymentConfirmed ou PaymentFailed apenas para resultados definitivos.
- [x] ReconcilePaymentsCommand a cada 5 min para Pending há mais de 10 min; incluir recuperação de operações externas incertas conforme política definida.
- [ ] Coordenar workers entre réplicas e resolver concorrência entre webhook, reconciliação e compensação.

Aceite: webhook duplicado, inválido, perdido, atrasado ou fora de ordem não duplica efeito; pagamento confirmado atualiza ambos os agregados e gera resultado consistente.

## 8. Implementar cancelamentos e reembolsos — P0

Atualização: cancel/refund, fencing antecipado, compensação repetida e SMTP implementados conforme ADRs 0007/0008. Pagamento tardio continua visível para o orquestrador; alinhamento de compensação por correlação com OS/Operation pendente.

- [x] CancelBudgetCommand cancela orçamento e eventual order pendente; motivo distingue cancelamento de expiração.
- [x] RefundPaymentCommand faz reembolso integral e publica PaymentRefunded somente após confirmação remota.
- [x] Usar identificação/idempotência estável por operação, respeitando o contrato do provedor.
- [x] Tratar inexistência, repetição, cancelamento já concluído e reembolso já realizado como compensações idempotentes conforme contrato.
- [ ] Se o pagamento for confirmado durante cancelamento, reconciliar e coordenar reembolso com o orquestrador antes de encerrar compensação.
- [x] Falha técnica persistente segue retry/DLQ e intervenção; não publicar BudgetCanceled/PaymentRefunded prematuramente.
- [x] Registrar histórico e enviar notificação de reembolso com processamento durável.

Aceite: testes cobrem Pix pendente, pagamento já pago, corrida confirmação/cancelamento, timeout de refund, crash após refund e compensação repetida.

## 9. Completar host, segurança e operação local — P1

- [x] Configurar JWT Authority/Audience, autorização, HMAC e assinatura de webhook sem bypass em desenvolvimento.
- [x] Swagger apenas em Development; documentar todos os DTOs e erros.
- [x] Health/live e health/ready: Mongo primary, RabbitMQ, topologia e workers; falha do provedor externo não controla readiness.
- [x] Configuração validada, secrets externos e logs sem tokens, QR, credenciais ou dados pessoais desnecessários.
- [x] Dockerfile multi-stage, .dockerignore e execução compatível com o perfil app do Compose.
- [x] Completar configuração OTEL e emissor JWT no guia local; adicionar mappings WireMock e collection/environment Postman sem segredos.
- [ ] Verificar o fluxo local com Compose desde um ambiente limpo, sem apagar volumes existentes do usuário.

Aceite: dependências e API sobem pelos comandos documentados; consultas exigem JWT; probes e Swagger apresentam o comportamento previsto.

## 10. Observabilidade e validação de qualidade — P1

- [ ] Instrumentar ASP.NET, HttpClient, Mongo e RabbitMQ; propagar traceparent/tracestate, correlação e causação.
- [ ] Logs estruturados por ServiceOrderId/CorrelationId, BudgetId e PaymentId.
- [x] Métricas previstas: orçamentos, decisões, pagamentos, reembolsos, tempo de aprovação e DLQ; adicionar atraso do Outbox, falhas de notificação e operações incertas.
- [x] Criar runbooks para broker/Mongo indisponível, Outbox parado, replay DLQ, webhook perdido e pagamento incerto.
- [ ] Completar testes unitários, arquitetura, integração Testcontainers/WireMock e BDD Reqnroll.
- [ ] Cobertura mínima de 80%, coverlet.runsettings conforme exclusões documentadas e evidências em docs/coverage.
- [ ] Configurar SonarCloud e quality gate obrigatório; contratos, handlers e fluxos de concorrência devem ter testes significativos além da meta numérica.

Aceite: build/testes/quality gate aprovados; uma OS pode ser acompanhada por trace e correlação; falhas deixam evidência operacional útil.

## 11. Kubernetes e CI/CD — P1

- [x] Criar Deployment com duas réplicas, probes, recursos e shutdown gracioso; Service ClusterIP e HPA 2–5/CPU 70%.
- [x] Criar Ingress, ConfigMap e Secret de exemplo; expor rotas de negócio e webhook com HTTPS e política de acesso às probes.
- [x] Criar Mongo StatefulSet/PVC e inicialização rs0; separar configuração local de requisitos de produção, incluindo autenticação, backup e restauração.
- [ ] Integrar RabbitMQ e coletor compartilhados do gearflow-infra-k8s; coordenar topologia, permissões, TLS e filas de produção com infraestrutura.
- [x] Completar GitHub Actions: build, testes, cobertura, Sonar gate, imagem versionada, push após merge main, deploy e smoke de readiness.
- [ ] Configurar secrets, registry, ambiente/namespace, proteção de main, aprovação mínima e checks obrigatórios no provedor.
- [x] Definir estratégia de rollback e compatibilidade de mensagens/dados entre versões.

Aceite: manifests validados e deploy no ambiente definido com duas réplicas; smoke passa; rollback é demonstrável. Publicação e configuração remota dependem de acesso aos ambientes do time.

## 12. Homologação integrada e entrega — P1

- [ ] Com OS e Execution reais: OS → diagnóstico → orçamento → aprovação → Pix → confirmação → estoque → execução → entrega.
- [ ] Demonstrar rejeição, expiração de aprovação, Pix expirado, cobrança recusada e webhook perdido.
- [ ] Demonstrar falta de estoque após pagamento com ReleaseStock → RefundPayment → CancelBudget → CancelDiagnostic coordenados pelo OS.
- [ ] Demonstrar duplicatas, reinício dos workers, indisponibilidade do broker e recuperação de DLQ.
- [ ] Validar integração Mercado Pago em ambiente de teste com HTTPS público e credenciais apropriadas, separada dos testes determinísticos.
- [ ] Atualizar README e guias com comportamento implementado, comandos executáveis, links reais de Sonar/evidências, Postman, responsáveis e limitações conhecidas.

Aceite final: todos os requisitos rastreados têm implementação e evidência; fluxo feliz e compensações funcionam; não há placeholders apresentados como funcionalidade pronta.

## Ordem de execução e PRs

| PR | Entrega | Depende de |
|---|---|---|
| 01 | Decisões, contratos, ADRs e rastreabilidade | Alinhamento do time |
| 02 | Solution, dispatcher, CI inicial e arquitetura | 01 |
| 03 | Domínio, portas e testes | 02 |
| 04 | Mongo, índices, UoW, Inbox/Outbox | 03 |
| 05 | RabbitMQ, confirms, retry, DLQ e replay | 04 |
| 06 | Orçamento, aprovação/rejeição, SMTP e HTTP | 05 |
| 07 | Gateway Pix, cobrança, consultas e stubs | 06 |
| 08 | Webhook durável e reconciliação | 07 |
| 09 | Cancelamento/reembolso e corridas | 08 |
| 10 | Docker, segurança, probes e Postman | 09; itens básicos iniciam no 02 |
| 11 | OTEL, BDD, cobertura e Sonar | 10; instrumentação/testes acompanham todos os PRs |
| 12 | Kubernetes e pipeline de entrega | 11 + infraestrutura |
| 13 | Homologação entre serviços e evidências finais | 12 + OS/Execution |

Marcos: M1 (PR 01–05) base confiável; M2 (06) orçamento completo; M3 (07–09) pagamento e compensação; M4 (10–11) serviço verificável; M5 (12–13) entrega integrada.

Não há prazo de calendário assumido: capacidade da equipe, disponibilidade dos outros serviços e acessos externos ainda não foram informados. Estimar cada PR após o fechamento dos contratos e revisar o prazo ao terminar M1.

## Dependências externas e responsáveis a atribuir

| Dependência | Responsável esperado | Necessária para |
|---|---|---|
| Contracts e decisões da Saga | Billing + OS + Execution | Compatibilidade e homologação |
| Emissor JWT, claims e permissões | OS/Auth | Consultas autenticadas |
| Credenciais e webhook HTTPS Mercado Pago | Responsável pela integração | Homologação real |
| Registry, cluster, Ingress, RabbitMQ e OTEL | Infraestrutura | Deploy e trace integrado |
| SonarCloud, GitHub Secrets e proteção de branch | Mantenedor do repositório | Quality gate e CI/CD |
| OS/Execution operacionais | Equipes dos serviços | Aceite ponta a ponta |

## Definição de pronto por entrega

Código respeita as fronteiras e todos os critérios da obrigação transversal de DDD; contratos e configuração estão documentados; testes relevantes passam; nenhuma credencial é versionada; repetição/concorrência têm comportamento definido; operações externas possuem recuperação; e o aceite pode ser reproduzido por outra pessoa. A revisão de PR deve verificar explicitamente a conformidade DDD e registrar eventuais decisões em ADR. Marcar checkboxes somente após evidência, não apenas após criar arquivos.
