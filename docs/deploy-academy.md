# Billing no EKS do Academy

Alvo: cluster `gearflow`, região `us-east-1`, node group Active, um nó x86_64.
Este guia sobe somente o Billing deste repositório e suas dependências. OS e
Execution precisam dos próprios repositórios/deploys para executar a Saga completa.

## Configuração única no console

O workflow detecta `computeConfig.enabled`: clusters Auto Mode usam o overlay
`k8s/academy-auto` e provisionador `ebs.csi.eks.amazonaws.com`, sem instalar o
add-on EBS CSI convencional. Para esses clusters, a etapa 1 abaixo não se aplica.
A primeira correção de um bootstrap anterior pode recriar os StatefulSets e
somente PVCs antigos Pending sem volume associado. PVCs já vinculados a dados
bloqueiam a migração automática; nunca são excluídos pelo script. Auto Mode tem
cobrança adicional de gerenciamento de compute; não é a opção mínima indicada
para este laboratório. Sua revisão de custos deve ser feita separadamente.

1. EKS > gearflow > Add-ons: instalar **Amazon EBS CSI Driver** na versão compatível.
   O driver precisa de uma identidade IAM com permissões para provisionar EBS;
   use uma associação/role permitida pelo laboratório. Instalar o add-on sem as
   permissões não basta. Não é necessário EFS nem EKS Auto Mode.
2. EKS > gearflow > Access: a role das credenciais temporárias do Actions deve
   ter acesso Kubernetes para aplicar recursos (incluindo StorageClass). Em modo
   API/API_AND_CONFIG_MAP, uma access entry da role com AmazonEKSClusterAdminPolicy
   e escopo cluster atende ao laboratório. Use o ARN IAM da role, não o ARN STS
   temporário da sessão. Isso é separado das permissões AWS.
3. O endpoint EKS deve ser alcançável pelo runner GitHub. O nó deve conseguir
   baixar imagens do ECR e Docker Hub; subnets públicas precisam de IPv4 público
   e rota para Internet Gateway. A role do nó precisa permitir pull do ECR.

Referências:
- https://docs.aws.amazon.com/eks/latest/userguide/ebs-csi.html
- https://docs.aws.amazon.com/eks/latest/userguide/access-entries.html

## GitHub

Em Settings > Secrets and variables > Actions, criar estas **Repository variables**:

| Nome | Valor |
| --- | --- |
| BILLING_DEPLOY_PROFILE | academy |
| EKS_CLUSTER_NAME | gearflow |
| AWS_REGION | us-east-1 |

Criar o environment **academy** e cadastrar os secrets nele ou no repositório:

| Nome | Conteúdo |
| --- | --- |
| AWS_ACCESS_KEY_ID | Credencial atual do Academy |
| AWS_SECRET_ACCESS_KEY | Credencial atual do Academy |
| AWS_SESSION_TOKEN | Token atual do Academy |
| JWT_SECRET | Segredo aleatório com pelo menos 32 caracteres; compartilhar com o emissor de JWT do GearFlow |
| BILLING_APPROVAL_TOKEN_SECRET | Outro segredo aleatório com pelo menos 32 caracteres |
| RABBITMQ_PASSWORD | Senha aleatória forte e estável para o usuário gearflow |

Não enviar esses valores no chat nem versioná-los. Secrets do environment
production não são automaticamente disponíveis para academy. Atualizar o trio AWS
quando expirar. Não trocar a senha do Rabbit apenas no GitHub depois da primeira
instalação: o usuário já estará persistido e exige rotação também no broker.

## Executar

Enviar os arquivos para a branch main. Actions > **Billing Academy EKS** >
**Run workflow** > main. O disparo manual executa os testes antes do deploy;
pushes seguintes na main disparam a entrega após Billing CI aprovado.
Com BILLING_DEPLOY_PROFILE=academy, o workflow antigo Billing delivery fica
inativo para evitar aplicar o perfil production no mesmo namespace.

O workflow cria o repositório ECR gearflow-billing se necessário, publica a imagem
com tag única, cria secrets, aplica manifests e verifica /health/ready. Os nós
baixam a imagem por sua role IAM; não há token temporário GHCR gravado nos pods.
Configurar uma lifecycle policy no ECR posteriormente para limpar imagens antigas.

Recursos: Billing (uma réplica), Mongo (replica set de um membro), RabbitMQ
(classic, uma instância), Mailpit e três volumes gp3 totalizando 4 GiB.
Sem Ingress, HPA, Load Balancer, EFS ou coletor de telemetria. O rollout Recreate
evita dois pods disputando o volume RWO das chaves; existe breve indisponibilidade.

O perfil usa **Development** explicitamente: Mongo sem autenticação, Rabbit/SMTP
sem TLS, pagamentos simulados e Swagger habilitado. É um ambiente de demonstração
com serviços ClusterIP, não uma configuração de produção. E-mails ficam no Mailpit;
não são enviados a destinatários reais. Os volumes preservam dados entre recriações
dos pods e escala a zero, mas não há backup nem alta disponibilidade.

## Acessar do computador

Com AWS CLI, kubectl e as credenciais temporárias configuradas localmente:

```sh
aws eks update-kubeconfig --name gearflow --region us-east-1
kubectl get pods,pvc -n gearflow
kubectl port-forward -n gearflow service/gearflow-billing 5080:80
```

Abrir http://localhost:5080/swagger e http://localhost:5080/health/ready.
Para e-mails, em outro terminal:

```sh
kubectl port-forward -n gearflow service/mailpit 8025:8025
```

Abrir http://localhost:8025. Os links de aprovação usam localhost:5080 neste perfil.

## Broker em repositório separado

Integração preparada para https://github.com/Gearflow-Fiap/gearflow-infra-broker.
Por enquanto manter cluster `gearflow`, conta atual e BILLING_BROKER_MODE ausente
ou `local`. Não alterar cluster/conta nem ativar `shared` até o broker ser
implantado no mesmo cluster. O CD atual daquele repositório aponta para
`gearflow-fase4`/outra conta e coordena Terraform; não o executar esperando
alterar automaticamente o cluster atual.

Quando o broker estiver pronto no namespace `messaging` do cluster de destino:

1. Confirmar StatefulSet rabbitmq pronto e Secret rabbitmq-budget-credentials
   (username gearflow_budget, password) criado pelo infra-broker.
2. Configurar a Repository variable BILLING_BROKER_MODE=shared no Billing.
3. Executar Billing Academy EKS. O workflow copia somente a credencial de Budget
   entre namespaces, sem exibi-la, e aplica labels exigidas pela NetworkPolicy.
4. A conexão passa a rabbitmq.messaging.svc.cluster.local:5672, vhost gearflow,
   filas quorum. Não usa o usuário administrador ou a senha RABBITMQ_PASSWORD.

O perfil shared não aplica Service/StatefulSet RabbitMQ nem aguarda o broker
local. O infra-broker continua responsável por usuários, topologia, disco e
ciclo de vida. Remover os manifests do perfil não exclui os objetos antigos:
o broker local e seu volume permanecem preservados, sem migração automática das
mensagens. Antes de removê-lo, parar produtores antigos, drenar/migrar filas e
conferir mensagens pendentes; classic não vira quorum por alteração de config.
Essa retirada deve ser coordenada entre os dois repositórios.

Trocar de cluster/conta não migra Mongo, chaves de aprovação, filas ou volumes.
Planejar a cópia de dados e as credenciais AWS quando essa troca for autorizada.

## Diagnóstico e limpeza

- PVC Pending: verificar add-on EBS, permissões IAM, eventos e disponibilidade do
  nó na zona do volume. WaitForFirstConsumer pode manter PVC Pending até existir pod.
  Para um único nó, manter o node group em uma subnet/zona fixa evita que, após
  escala a zero, ele volte em outra zona e não consiga montar os discos existentes.
- Forbidden: falta acesso Kubernetes da role do Actions.
- ImagePullBackOff: verificar permissões ECR do nó e acesso à internet.
- Insufficient memory: 4 GiB é ponto de partida; outros serviços podem exigir mais.
- O workflow mostra pods/PVCs/eventos quando falha; isso não significa deploy aprovado.

Escalar nós para zero derruba as aplicações e reduz EC2, mas EKS, EBS e imagens
continuam consumindo créditos. A StorageClass usa Retain para impedir exclusão
automática dos dados: remover o cluster/namespace não limpa os discos retidos.
Ao encerrar definitivamente o laboratório, revisar e excluir conscientemente os
volumes e imagens que não serão mais necessários.
