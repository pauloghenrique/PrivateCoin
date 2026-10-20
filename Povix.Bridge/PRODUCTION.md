# Candidato de produção da ponte POVIX / BNB

Esta versão adiciona execução para BNB mainnet (chain 56). O projeto continua **desativado por padrão**. Não foi publicado, auditado por terceiros nem testado com USDT real. Testes locais não substituem revisão independente dos contratos, do consenso e da infraestrutura. Antes de receber fundos, concluir revisão, ensaio de operação/recuperação em testnet e implantação com os operadores reais.

A ponte continua federada: nós POVIX verificam assinaturas, política e unicidade do depósito, mas não executam um light client BNB. Uma maioria comprometida pode inventar um novo depósito ou autorizar uma retirada indevida. Os controles abaixo limitam erros, replay e tempo de reação; não eliminam a confiança na federação.

## Onde definir os operadores

Copie `production.config.example.json` para `production.local.json`, dentro de `Povix.Bridge`. Os placeholders são intencionalmente inválidos. Cada operador deve ser uma pessoa/organização responsável por sua própria máquina, fontes de dados e chaves. Duas carteiras ou servidores administrados pela mesma pessoa continuam sob um único controle. O exemplo usa **dois operadores e exige ambas as assinaturas (2 de 2)**. Se um deles ficar indisponível ou perder a chave, emissão, retomada e autorizações de resgate ficam bloqueadas; sem recuperação da chave, os fundos podem ficar bloqueados permanentemente. Pausa emergencial e cancelamento de filas continuam disponíveis a qualquer operador. Também é aceito o modelo anterior de cinco ou mais operadores com maioria estrita de pelo menos três.

Cada operador gera **localmente**:

```sh
node src/cli.mjs create-native-operator operador-povix.local.json
node src/cli.mjs create-operator operador-bnb.local.json
```

Informe as senhas no ambiente local `NATIVE_KEYSTORE_PASSWORD` e `BRIDGE_KEYSTORE_PASSWORD`, com pelo menos 16 caracteres. Não coloque senhas no comando, no Git, em mensagens ou no Web.config. A chave nativa usa RSA 3072, scrypt N=131072/r=8/p=1 e AES-256-GCM; a BNB usa keystore cifrado compatível com ethers. Os arquivos são exclusivos, criados com permissão 600 e não são sobrescritos. O comando imprime apenas os dados públicos. Para operadores existentes, não gere novas chaves sem registrar a identidade correta; esta versão não fornece rotação de operadores.

Preencha:

- `operators`: dois endereços EVM distintos (BNB), na ordem que será publicada no contrato;
- `nativeAdministrators`: os dois endereços POVIX, hashes das chaves públicas RSA;
- `nativeSigningPublicKeys`: as duas chaves públicas RSA dos operadores. Nunca são XMLs privados. No modelo 2 de 2 não há operador substituto. Em uma federação maior, outro subconjunto válido pode refazer a proposta;
- `threshold` / `nativeThreshold`: ambos iguais a 2 no modelo de dois operadores; para cinco ou mais, maioria estrita de pelo menos 3;
- `nativeNetwork`: NetworkId efetivo, obtido do Core compilado (`mono build/native/BridgeNative.exe network`);
- `nativeSnapshots`: exports atuais de pelo menos dois nós POVIX operados independentemente; `nativeSnapshot` aponta para um desses arquivos;
- `rpcUrls`: pelo menos dois RPCs BNB HTTPS de provedores independentes; `rpcUrl` é um deles. Domínios diferentes são exigidos em chain 56, mas independência organizacional precisa ser conferida pelos operadores;
- `asset`: contrato exato do ativo na BNB, confirmado por fontes oficiais. Símbolo “USDT” não comprova identidade. Confira inclusive o emissor, custódia e riscos de representações como Binance-Peg;
- `assetCodeHash`: keccak256 do bytecode runtime do ativo, conferido pelos RPCs. Um proxy pode manter esse hash e trocar implementação: essa verificação não elimina análise de upgrades e controle do emissor;
- `reserveCap` / `dailyReleaseCap`: limites em unidades atômicas da representação, não em dólares. O exemplo usa 8 casas, reserva de 1000 e liberação diária de 10 unidades. Cada outro ativo precisa de orçamento próprio;
- `nativeConfirmations`: mínimo 120; `bscConfirmations`: mínimo 64 em produção/mainnet.

Os contratos e a política nativa guardam as identidades e limites publicados. Editar JSON posteriormente **não altera** operadores, limites ou vinculação em chain. A configuração é comparada com os contratos e a política a cada operação. Não iniciar com identidades compartilhadas, sem cópias de recuperação ou com risco de perder a maioria. A versão não possui saque administrativo, rotação ou migração automática: perda de quorum pode bloquear fundos permanentemente. A revisão prévia deve decidir se essas restrições atendem ao lançamento; caso não atendam, a ativação fica pendente de uma implementação de migração/rotação revisada.

## Compilar, publicar e vincular

Compile com dependências fixadas (`npm ci --ignore-scripts`, `npm run build`, `npm run test:native-build`) usando Node >=22.13 e o toolchain .NET Framework/Mono documentado no README. A versão do compilador Solidity é fixa. Não publicar um artefato diferente do revisado. Para as máquinas dos operadores, gere `npm run package:production -- /caminho/novo/pacote`; a pasta deve ser nova. O pacote contém somente o código de execução, binários nativos, artefatos de produção, configuração de exemplo e um manifesto SHA-256. Instale com `npm ci --omit=dev --ignore-scripts` dentro do pacote, usando seu lockfile mínimo próprio. Ele não depende de solc, Ganache nem dos testes. A revisão desta mudança atualizou ethers para 6.17.0/ws 8.21.0 e a auditoria npm do pacote mínimo retornou zero alertas conhecidos. A árvore de desenvolvimento Ganache mantém alertas em dependências empacotadas legadas: não a instalar em servidores de operadores nem a usar com chaves ou fundos reais. Reauditar versões no momento da implantação; zero alertas conhecidos não comprova segurança.

1. Concluir a revisão independente e o ensaio com os dois operadores em testnet. Preservar código, lockfile, versão do compilador, hashes dos artefatos e relatório da revisão.
2. Com `bridge: null`, publicar `ProductionVault` usando `node src/cli.mjs deploy production.local.json relay-bnb.local.json`. O cofre nasce pausado. A ferramenta confere chain e hash do ativo em ambos os RPCs. Salvar endereço, hash runtime e transação retornados em `bridge` e `vaultCodeHash`, conferir no explorador e por ambos os RPCs. Para mainnet, o contrato exige dois operadores com quorum 2 de 2, ou cinco ou mais com quorum majoritário >=3 e caps dentro de Int64.
3. Configurar uma única vez a política nativa do pUSDT pela carteira fundadora com `configure-native`. Esse bootstrap usa somente a chave da carteira fundadora, em máquina isolada, pelo formato local já documentado; não deve reunir as chaves dos operadores. Submeter a transação ao nó POVIX e confirmar em ambos os nós. Depois da configuração, a carteira fundadora deixa de ter autoridade exclusiva sobre a emissão.
4. Opcionalmente publicar `ProductionVaultFactory` com `deploy-factory`. Salvar `factory` e `factoryCodeHash` retornados e conferir o código. A factory de produção requer quorum para aprovar cada ativo, código de origem e limites próprios; não há onboarding irrestrito por usuário.
5. Rodar `node src/cli.mjs audit production.local.json` e conferir a política em ambos os nós, bytecodes, escala, reservas e limites. Um nó deve estar atualizado: o arquivo e o último bloco precisam ser recentes. Divergência ou indisponibilidade bloqueia a assinatura; não substituir uma fonte faltante por cópia controlada pelo coordenador.
6. Cada operador assina `sign-pause` para `false` com sua chave EVM e o mesmo nonce/deadline; reunir a maioria e executar `pause`. Antes de assinar ativação em modo produção, a CLI exige auditoria de reserva aprovada. Senhas/keystores não saem das máquinas dos operadores.
7. Só então configurar o DEX com `BridgeMode=production`, `BridgeEnabled=true`, `BridgeAsset`, `BridgeVault` e seus hashes. Para multiativos, também `BridgeFactory` e `BridgeFactoryCodeHash`; cadastrar pares de contrato/cofre e hashes já revisados em `Povix.Dex/App_Data/bridge-assets.json`, conforme o exemplo em `deploy/bridge-assets.example.json`. O servidor não recebe chaves privadas. Publicar o DEX em HTTPS e aplicar a configuração Release (debug desabilitado), com acesso administrativo restrito e monitoramento. Configuração ausente/inválida mantém depósitos desabilitados.

Os comandos executam transações somente quando chamados com keystore e configuração reais. Esta PR não os executou em mainnet. Não transferir USDT diretamente para o cofre: usar `deposit`; doações diretas não são emitidas como pUSDT e não há função administrativa para recuperá-las.

## Depósito e assinatura independente

O usuário usa Swap BNB. Em produção, a tela exige chain 56, versão 2, hashes publicados e espera 64 confirmações. O cofre registra depósito/destino; a tela não afirma que pUSDT foi creditado apenas com o recibo BNB.

O coordenador guarda a referência `{ "transactionHash": "0x…", "logIndex": 0 }` e executa:

```sh
node src/cli.mjs propose-native production.local.json mint referencia.json state/coordenador.sqlite
```

Salve a proposta pública em um arquivo. Cada operador, **na sua máquina e com sua própria configuração de fontes**, executa:

```sh
node src/cli.mjs sign-native production.local.json proposta.json operador-povix.local.json state/operador.sqlite
```

Cada operador confere código/configuração, recibo canônico e profundidade em dois RPCs BNB e recalcula o conteúdo assinado a partir de dois snapshots POVIX válidos e recentes. Endereço, valor, depósito e bloco de origem são vinculados ao payload. A chave privada permanece no processo local. O journal de cada operador registra fatos assinados e recusa decisões conflitantes. Uma proposta expira para assinatura após dez minutos; se expirar, use `refresh-native config.local.json proposta.json state/coordenador.sqlite`, que revalida os fatos, arquiva a proposta anterior e prepara outra com estado/revisão atual. Colete novas assinaturas. Não limpe decisões conflitantes para “forçar” aprovação.

Reúna os dois resultados públicos (ou o quorum configurado para uma federação maior) em um array `assinaturas.json` e execute:

```sh
node src/cli.mjs complete-native production.local.json proposta.json assinaturas.json
```

O comando verifica assinatura RSA, identidade, quorum, payload e consenso nos dois nós e devolve a transação assinada para submissão ao nó POVIX pelo fluxo existente. Não recolhe senhas ou keystores dos outros operadores.

O consenso v38 guarda o identificador do depósito por vínculo de custódia e o inclui nas assinaturas. Um depósito não pode gerar dois mints, mesmo com nova revisão/transação e mesmo se o journal do coordenador for perdido. O registro é reconstruído no replay e acompanha reorganizações da cadeia. Isso não detecta um depósito completamente inventado por uma maioria maliciosa; essa verificação segue federada.

A CLI não instala um relayer automático nem serviço público de fila. A troca só fica operacional quando os responsáveis mantêm o processamento/submissão das propostas e a atualização independente dos nós. Automação externa pode chamar esses comandos, preservando a separação das chaves e a política de verificação.

## Outros ativos e controle nativo

Cada perfil de ativo contém `asset`, `assetCodeHash`, `cap`, `dailyCap` (strings atômicas) e um `deadline` comum em segundos Unix. Operadores analisam contrato e governança; cada um usa `sign-asset production.local.json perfil.json operador-bnb.local.json state/operador.sqlite`. A maioria é reunida e um relay executa `create-asset production.local.json perfil.json assinaturas-ativo.json relay-bnb.local.json`. A factory verifica as assinaturas, hash runtime, limites e unicidade, criando o cofre pausado.

Use `production-asset-config production.local.json perfil.json` para obter configuração específica, validada pela factory e RPCs. Proponha `register` com `propose-native ativo.local.json register - state/coordenador-ativo.sqlite`; cada operador assina e o coordenador completa/submete como acima. O registro cria oferta zero. Depois da confirmação nos dois nós, auditar, ativar o cofre com quorum e cadastrar o ativo revisado no DEX. O usuário não é solicitado a depositar enquanto o ativo não estiver aprovado. Os journals são exclusivos por rede/cofre/token.

Para pausar ou retomar operações nativas, `propose-native ... pause - ...` ou `resume` usa o mesmo fluxo de assinaturas independentes, ligado à revisão atual. A pausa do cofre BNB e a pausa do token POVIX são controles distintos; em um incidente é preciso avaliar ambos.

## Resgate, espera e incidentes

Divida resgates maiores que o limite diário antes de queimar: `prepare-burn` recusa quantidades acima do limite, e o cofre não aceita filas que nunca poderiam ser executadas dentro dele. Não use a queima genérica de tokens para sacar: uma queima fora desse fluxo pode destruir tokens sem permitir resgate. O titular prepara uma queima com `prepare-burn` e autoriza a carteira BNB de destino com `bind-burn` (assinatura dos donos dos inputs). O fluxo original de queima local permanece; não envie chaves de titulares ao coordenador. Cada operador usa `sign-release` depois de verificar a queima canônica e a autorização do destino em dois nós, com pelo menos 120 confirmações nativas. O grupo assina o mesmo nonce/deadline, domínio v2, rede e cofre.

`release` **agenda**, sem transferir fundos, uma retirada por burn. O saldo fica reservado e não pode ser comprometido em outro resgate. A espera mínima é **24 horas após autorização**. Depois, qualquer relay pode executar `execute-release config.local.json 0xBURN_ID relay-bnb.local.json`. O limite diário é cobrado na execução. O contrato verifica saldos exatos e proíbe transferências não padronizadas, replay e pagamento duplicado.

Qualquer um dos operadores pode usar `emergency-pause` imediatamente ou `cancel-release` para cancelar uma fila suspeita. A retomada exige a maioria. Pausar não apaga filas nem reinicia seu prazo: em alerta de retirada suspeita, cancele também as filas suspeitas e mantenha a observação antes de qualquer retomada. Cancelar libera a reserva e uma nova fila exige assinatura com novo nonce; autorização anterior não pode ser reutilizada. Uma chave comprometida pode causar indisponibilidade por esses controles, mas sozinha não consegue retirar fundos.

Manter observação contínua de `Deposited`, `ReleaseQueued`, `ReleaseCancelled`, `Released`, pausas, bytecodes/implementações do emissor e blocos de ambas as redes. Rodar `audit` em monitoramento: código de saída 2 significa reserva insuficiente para as obrigações observadas; falhas de consulta/divergências também encerram com erro. Reservas podem exceder oferta por depósitos ainda não emitidos ou queimas ainda não liberadas; a auditoria não exige igualdade. Ela é uma verificação de lastro agregado, não substitui conferência de cada autorização/queima.

Testar recuperação de backups cifrados e journals (SQLite WAL/backup consistente) antes de operar. Em perda de banco, não gerar novos mints com o procedimento antigo: reconstruir o estado dos nós e recibos canônicos; mints com claim já consumido serão rejeitados pelo consenso. Em reorganização profunda, parar emissão e resgates e reconciliar cada evento. Os parâmetros de confirmação reduzem risco, sem oferecer finalidade absoluta entre cadeias. Não executar retries cegos de transações que já possuem hash.

## Compatibilidade e validação

Consenso **v38**, manifesto **schema 11**. O compromisso do Core altera genesis/NetworkId; esta alteração exige um lançamento coordenado em rede nova. Não apagar snapshots ou migrar saldos existentes automaticamente. Uma rede já financiada exige plano separado de migração e revisão antes de adotar o novo genesis. Não ativar cofres vinculados a um NetworkId anterior.

A suíte usa Ganache local e ledger POVIX real, chaves de teste independentes, RSA/assinaturas EIP-712, claims de consenso, cancelamento/nonce, pausa e avanço de 24 horas. Na chain local 31337, o limite mínimo nativo pode ser 12 para executar regressões sem minerar vinte minutos de confirmações; em chain 56/97 de produção continua 120. Há testes de rejeição do parâmetro reduzido em mainnet e de recusa de queima local sem 120 confirmações quando configurado assim. Não se trata de teste em mainnet, auditoria independente ou prova de segurança contra maioria comprometida.
