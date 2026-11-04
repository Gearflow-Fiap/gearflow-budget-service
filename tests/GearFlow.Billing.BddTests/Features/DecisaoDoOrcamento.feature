# language: pt
Funcionalidade: Decisão segura e idempotente do orçamento
  O cliente decide uma proposta criada a partir do diagnóstico.
  Repetições não publicam novos fatos e um link adulterado não altera a proposta.

  Cenário: Geração repetida preserva uma única proposta e notificação
    Dado um diagnóstico válido para uma ordem de serviço
    Quando a mesma geração de orçamento é solicitada duas vezes
    Então existe apenas um orçamento, um evento e uma intenção de e-mail

  Cenário: Aprovação repetida não permite rejeição posterior
    Dado um orçamento pendente com link válido
    Quando o cliente aprova duas vezes e depois tenta rejeitar
    Então a aprovação é preservada com um único evento de decisão

  Cenário: Token adulterado não permite aprovação
    Dado um orçamento pendente com link válido
    Quando alguém tenta aprovar com token adulterado
    Então o orçamento continua pendente e não publica aprovação
