# Automação C# com operadores

O cliente oferece dois processos de longa duração, `run-coordinator` e `run-operator`, dentro do mesmo `Povix.Bridge.exe`, sem Node.js em operação. O coordenador descobre depósitos confirmados, publica propostas, reúne assinaturas e transmite a emissão aos peers POVIX. Cada operador verifica independentemente os RPCs BNB e seus snapshots POVIX antes de assinar com suas chaves locais. O resgate também pode ser processado automaticamente depois que o titular submete uma queima e autoriza o destino BNB.

Continua sendo uma ponte federada. Automatizar não remove operadores nem altera a exigência de duas assinaturas. Não há publicação de contratos, financiamento ou ativação de mainnet por estes comandos. A revisão independente e os ensaios em testnet descritos em PRODUCTION.md continuam necessários.

## Preparar a ponte

Compile em Release conforme VISUAL-STUDIO.md. Configure e publique o cofre, confirme a configuração nativa, audite e ative a ponte pelo procedimento de PRODUCTION.md. A automação não publica contratos, registra ativos, troca operadores nem retira pausas. Execute uma instância por par ativo/cofre/representação, com stateDirectory separado. O ativo precisa estar previamente aprovado, registrado e ativo nas duas redes.

Os nós POVIX continuam responsáveis por sincronizar a rede, manter os snapshots atualizados e minerar/confirmar transações. Os processos da ponte não mineram e não sobrescrevem Blockchain.json. O coordenador usa conexões P2P de saída pelo protocolo existente, que inclui NetworkId, versão de consenso e RulesHash, sem abrir porta ou UPnP. Uma segunda troca hello/addr confirma que o peer processou o quadro antes de fechar a conexão curta. Conexão ou broadcast não significam confirmação: a inclusão é reconhecida pelos dois snapshots independentes.

## Configurar a fila pública

Crie um compartilhamento de arquivos acessível automaticamente pelas três máquinas, por exemplo uma pasta SMB com endereço UNC no Windows. Todos apontam exchangeDirectory para o mesmo conteúdo. Não sincronize arquivos privados nessa pasta. Não exponha SMB à Internet: use rede privada/VPN e ACLs de usuários distintos.

A estrutura é:

- `requests/<jobId>.json`: propostas imutáveis publicadas pelo coordenador;
- `signatures/<endereco-do-operador>/<jobId>.json`: respostas públicas assinadas;
- `burns/<requestId>.json`: vínculos de queima autorizados pelos titulares.

Configure permissões: o coordenador escreve requests e lê signatures; cada operador lê requests e só escreve nas suas próprias pastas de signatures, uma para seu endereço POVIX e outra para seu endereço BNB em minúsculas. Permita inclusão de solicitações públicas em burns somente ao processo de entrada autorizado. Crie as pastas e ACLs antes de iniciar os serviços. A CLI não configura permissões SMB/Windows nem distribui arquivos entre máquinas; o compartilhamento fornece esse transporte automaticamente.

O conteúdo da fila é tratado como não confiável: jobs têm hash e escopo de rede/cofre/token, assinaturas são verificadas, arquivos têm limites e publicação por rename após flush durável. Dados públicos fabricados não substituem verificações independentes. Alterar/apagar arquivos ou ocupar a fila pode causar indisponibilidade; ACLs e monitoramento são necessários. Não usar links simbólicos/junctions nos caminhos. Use caminhos absolutos; unidades mapeadas interativamente podem não existir na conta de execução do serviço.

## Coordenador

Copie `automation.coordinator.example.json` para `automation.coordinator.local.json`. Ajuste:

- `exchangeDirectory`: fila pública compartilhada;
- `stateDirectory`: diretório LOCAL e exclusivo, fora da fila;
- `startBlock`: bloco de publicação do cofre ou primeiro bloco que precisa ser reconciliado; não pule depósitos históricos;
- `nativePeers`: peers POVIX reais no formato host:porta;
- `relayKeystore`: carteira BNB exclusiva do relay, com BNB para gas, DIFERENTE das carteiras dos operadores;
- `processReleases`: true para também agendar/executar resgates;
- `maxGasPriceWei` / `maxGasLimit`: tetos locais antes de assinar uma transação do relay; por padrão 5 gwei e 1.000.000 de gas;
- `enabled`: true apenas depois de concluir a configuração.

Para criar uma carteira relay separada, use `Povix.Bridge.exe create-operator relay-bnb.local.json`; o nome do comando apenas gera um keystore EVM, não cadastra esse endereço como operador. Financie essa carteira com BNB para gas na rede escolhida.

Não informe nativeOperatorKeystore ou evmOperatorKeystore no coordenador. Configure BRIDGE_KEYSTORE_PASSWORD no ambiente local da conta do relay, sem argumentos de senha ou scripts versionados.

```text
Povix.Bridge.exe run-coordinator production.local.json automation.coordinator.local.json
```

O scan consulta todos os RPCs, usa o menor head confirmado e janelas de até 100 blocos. O cursor só avança depois de guardar os depósitos no journal. Emissão é serializada por cofre/token para respeitar a revisão nativa. O próximo depósito é processado após a inclusão do anterior nos dois snapshots; isso não representa finalidade absoluta contra reorganizações profundas.

## Cada operador

Copie `automation.operator.example.json` para `automation.operator.local.json` em cada máquina. Configure stateDirectory LOCAL distinto, o mesmo compartilhamento público e SOMENTE seus próprios keystores. As identidades POVIX e BNB devem corresponder ao mesmo índice nas listas nativeAdministrators/operators da configuração. Não copie chaves de outro operador.

Use a configuração production.local.json própria do operador, com RPCs e snapshots de fontes independentes. Configure NATIVE_KEYSTORE_PASSWORD e BRIDGE_KEYSTORE_PASSWORD no ambiente da conta local.

```text
Povix.Bridge.exe run-operator production.local.json automation.operator.local.json
```

As chaves passam a ser usadas por um serviço automático: proteja a conta e a máquina de cada operador, pois não haverá revisão humana por operação.

Os serviços assinam automaticamente apenas emissão por depósito verificado e autorização de resgate por queima verificada. Nunca assinam automaticamente register, pause, resume, mudança de limites ou cadastro de ativos. Divergências, pausas, auditoria sem lastro e dados obsoletos bloqueiam o ciclo. Chaves privadas não são publicadas na fila nem enviadas ao DEX/coordenador.

## Resgate

O titular continua autorizando a queima e o destino BNB, conforme PRODUCTION.md. O serviço não guarda chaves do titular nem queima saldo sem sua autorização. Depois de submeter/confirmar a queima e gerar o vínculo assinado, envie-o à fila:

```text
Povix.Bridge.exe enqueue-burn production.local.json automation.coordinator.local.json queima-assinada.json
```

O comando valida a queima antes de publicar a solicitação pública. O coordenador prepara um mesmo nonce/deadline para os operadores; ambos verificam e assinam independentemente. O relay agenda a retirada, espera no mínimo 24 horas e executa quando o limite diário permite. Uma fila previamente cancelada não é reautorizada automaticamente, mesmo se o titular reenviar o arquivo. Investigue o cancelamento antes de qualquer ação manual.

Para processar um ciclo e sair, útil em diagnóstico e testes:

```text
Povix.Bridge.exe run-coordinator production.local.json automation.coordinator.local.json --once
Povix.Bridge.exe run-operator production.local.json automation.operator.local.json --once
```

## Operação e recuperação

Use Ctrl+C para parada normal. Configure execução na inicialização e reinício após falha no Agendador de Tarefas do Windows, com conta dedicada e acesso aos caminhos UNC. Distribua a pasta Release inteira. Injete segredos pelo mecanismo da infraestrutura; não coloque senhas nos argumentos das tarefas. Não é um serviço Windows registrado automaticamente e não usa Node.js/NSSM.

Logs vão para stderr. Erros de rede/validação bloqueiam e são reavaliados no próximo ciclo; falhas de filesystem/journal encerram o processo para evitar assinar depois de uma gravação incompleta. Antes de reiniciar após esse tipo de falha, reconcilie a causa e o journal. Preserve backup cifrado das chaves e backup consistente dos journals fechados.

Mudanças no hash do último bloco BNB escaneado, no bloco de confirmação de transações do relay ou desaparecimento de claims nativos já incluídos bloqueiam a automação para reconciliação de reorganizações.

Transações nativas são persistidas antes do broadcast e retransmitidas com o mesmo ID enquanto aguardam inclusão. Transações BNB são assinadas e o raw/hash é gravado antes de broadcast; após falha ou perda da resposta, o serviço procura esse mesmo hash e só retransmite os mesmos bytes. Mantém no máximo um nonce EVM em trânsito por coordenador. Não use a carteira relay em outro software ou em outra instância. Taxas acima dos tetos bloqueiam novas assinaturas. Alterar o teto não revoga uma transação já assinada/persistida, que continua sendo reconciliada pelos bytes e hash originais. Transação substituída, revertida ou presa por preço de gas exige reconciliação: não apague o pending/journal para liberar um novo nonce.

Os journals são exclusivos por processo, rede/cofre/token e papel. Perda de journal exige parada e reconciliação independente. A fila limita cada diretório a 1000 JSONs; a lista histórica de depósitos também limita a 1000 e o journal continua com limite de 64 MiB. Planeje arquivamento revisado antes de atingir esses limites. Não há compactação automática; não apague decisões para contornar os limites. Para arquivar arquivos públicos, preserve backups e só remova jobs encerrados, com os serviços parados; journals e decisões anteriores continuam preservados.

A tela atual do Povix.Dex inicia o depósito e exibe o comprovante. A automação descobre o evento diretamente na BNB, sem o usuário encaminhar o hash. O crédito aparece na carteira POVIX após confirmação; a tela ainda não consulta o progresso do daemon nem oferece a queima/resgate. Não interprete a mensagem de espera da tela como falha do serviço nem repita depósitos já enviados.

## Validação

`npm run test:automation` usa BNB local simulada e o ledger POVIX real, com dois processos de operadores, transmissão P2P, reinícios e perda simulada da resposta RPC depois do envio, cancelamento de resgate, recusa de operação administrativa disfarçada de emissão e bloqueio por divergência/reorganização. Node/Ganache são apenas infraestrutura de desenvolvimento; não são instalados nos operadores. O teste não publica contratos reais e não substitui validação Windows/testnet ou auditoria independente.
