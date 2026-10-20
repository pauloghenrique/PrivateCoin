# Ponte USDT BNB → POVIX (protótipo de teste)

Este módulo bloqueia um ativo de teste na BNB Smart Chain Testnet e prepara a emissão de uma representação na rede POVIX. A representação usa o símbolo **pUSDT** e pode compor uma pool **POVIX/pUSDT no Povix.Dex**. Não é um token emitido pela Tether. A rede v36 não cria os antigos USDT/USDC locais. O primeiro registro cria somente o pUSDT de protocolo, com oferta zero, e a pool POVIX/pUSDT; registros seguintes não recebem stablecoins. A ponte não fixa o preço da pool nem fornece liquidez para ela.

**Não está publicado. Não envie USDT real.** O contrato recusa a rede BNB principal (chain ID 56); permite somente 97 e a EVM local 31337. Os testes usam um ERC-20 explicitamente identificado como Mock USD, sem valor, e carteiras descartáveis. O script nativo usa chaves em arquivo somente para o laboratório; não consolide as chaves de administradores em produção.

## Fluxo implementado

1. O usuário aprova o cofre e deposita o ativo BNB, informando o endereço POVIX de destino. O contrato fixa ativo, rede nativa e ID do token representativo; o evento contém destinatário e valor.
2. Após pelo menos 15 confirmações BNB, a ferramenta verifica o recibo, o contrato emissor do evento, o bloco canônico e a quantidade. Ela prepara uma transação nativa de emissão com a política de administradores da POVIX (maioria de pelo menos três administradores).
3. O diário SQLite registra a transação assinada **antes de entregá-la ao operador**. Repetir o depósito retorna a mesma transação, inclusive após reiniciar; não prepara uma segunda emissão. Transações nativas pendentes são reservadas no diário. A transação precisa ser transmitida e confirmada na rede POVIX; preparar não significa creditar saldo.
4. Para voltar, o usuário queima pUSDT usando o comando nativo de queima. Os proprietários de todas as entradas assinam uma vinculação entre a queima e o destinatário BNB, protegida pelo domínio da ponte.
5. Cada operador verifica a queima em sua própria cópia validada da blockchain POVIX, com pelo menos 12 confirmações. A maioria assina uma autorização EIP-712 e o cofre libera o ativo BNB. A mesma queima não pode ser paga novamente.

Os comandos de pUSDT têm assinaturas vinculadas à identidade da rede POVIX; as liberações BNB usam domínio EIP-712 por rede e contrato. DEX e Desktop recolocam operações válidas da ponte na fila após reorganizações nativas.

O cofre tem limite de reserva, limite diário de resgate e pausa aprovada pelo quórum. Não possui saque administrativo alternativo. Depósitos diretos por `transfer` não geram representação; use `deposit`. Tokens com taxa de transferência são rejeitados.

A reserva é medida em unidades nativas com oito casas. A precisão do ativo é lida do contrato; ativos entre 8 e 18 casas são aceitos. Para um ativo de 18 casas, 10^10 unidades BNB equivalem a uma unidade nativa. Depósitos com resto são rejeitados. **1 unidade inteira da representação corresponde a 1 unidade inteira do ativo em custódia**, sem arredondamento. Isso não garante a cotação da pool em dólares.

## Compilar e testar

Requer Node >=22.13, Mono/mcs/xbuild com suporte ao projeto .NET Framework 4.8. Nenhum serviço de rede externo é usado pelos testes.

```sh
cd Povix.Bridge
npm ci --ignore-scripts
npm run build
sh test/native-build.sh
npm test
```

É possível selecionar os executáveis com `POVIX_XBUILD`, `POVIX_MCS` e `POVIX_MONO`. Os testes cobrem o ciclo completo com **consenso POVIX real e EVM local**, emissão, queima, precisão, pausa, limites, autorizações indevidas, retomada do diário e replay. Os inteiros de 64 bits assinados da POVIX são preservados sem perda no JSON do JavaScript.

## Configurar um laboratório

Use `config.example.json` como estrutura e substitua todos os valores. Não copie endereços ou IDs fictícios para uma implantação. `nativeNetwork` é obtido com `mono build/native/BridgeNative.exe network`. O arquivo `nativeSnapshot` pode ser o `Blockchain.json` público do Desktop (schema 5) ou um export DataContract `Block[]`. Cada operador mantém seu próprio nó sincronizado e atualiza a cópia antes da verificação. O programa valida toda a cadeia e rejeita snapshots com arquivo desatualizado há mais de cinco minutos; isto não substitui verificar que o nó acompanha a rede.

Na rede v36, a primeira carteira já registra o pUSDT de protocolo com oferta zero. Use o ID `c834c6b8054805426ae71bc9b71815feaa6728985ed403706ee26e12094d6a0d`. Não crie outro token manualmente. Ele começa sem permissão de emissão. A primeira carteira autoriza uma única configuração da ponte: ativo e cofre BNB, pelo menos três administradores nativos e uma maioria de assinaturas. Depois disso, a carteira inicial perde essa autoridade exclusiva; a política não pode ser reconfigurada por este fluxo.

Publique o cofre de teste com o ID do pUSDT e a identidade da rede nativa. Grave o endereço retornado na configuração e execute `configure-native` com as chaves locais de laboratório da primeira carteira. Transmita e confirme a transação retornada na POVIX antes de preparar emissões.

A configuração, a emissão e a queima específicas do pUSDT podem usar taxa POVIX zero com todas as assinaturas exigidas. Isso evita depender de POVIX livre antes do primeiro depósito ou da primeira compra. Essas operações não pagam recompensas de validadores e não podem ser misturadas em blocos com operações remuneradas. As demais taxas e recompensas permanecem iguais. O teto de emissão nativa continua cumulativo (`Issued`); defina o limite de laboratório adequadamente.

Não há oferta inicial positiva a lastrear nem distribuição gratuita: o primeiro depósito efetivo já gera a primeira emissão. A verificação de depósitos BNB e a unicidade da emissão ainda são responsabilidade da federação e do diário; o consenso nativo verifica o quórum e a política, não uma prova direta BNB.

Para EVM, crie keystores cifrados localmente (ou use keystores de teste existentes). `BRIDGE_KEYSTORE_PASSWORD` é lida do ambiente local; não coloque senhas em comandos compartilhados ou no Git. BNB de teste é necessário para taxas.

```sh
node src/cli.mjs create-operator state/operator-1.local.json
node src/cli.mjs deploy config.local.json state/deployer.local.json
```

Crie o diretório `state/` antes de usar esses comandos. Após publicar, grave o endereço retornado em `bridge` no JSON (minúsculas). O cofre começa pausado. Dois operadores assinam o mesmo estado, nonce e prazo Unix de até dez minutos. Agrupe as respostas de assinatura em um array JSON e envie pelo comando `pause`.

## Comandos

Todos, exceto `create-operator`, recebem primeiro o caminho da configuração. Valores monetários e limites abaixo são **unidades atômicas de oito casas**. Use `0` como taxa POVIX nas operações específicas da ponte. Não use números decimais ou ponto flutuante.

| Comando após `node src/cli.mjs` | Argumentos após configuração | Resultado |
|---|---|---|
| `deploy` | `keystore` | Publica cofre pausado na rede de teste |
| `sign-pause` | `keystore true\|false prazoUnix` | Assinatura individual do estado/nonce |
| `pause` | `autorizacoes.json keystoreTransmissor` | Envia quórum de pausa/retomada |
| `deposit` | `valorAtomico enderecoPOVIX keystoreUsuario` | Aprova ativo e deposita; retorna referência |
| `configure-native` | `chavesPrimeiraCarteira.local.json` | Prepara a configuração nativa única, com oferta ainda zero |
| `prepare-mint` | `referencia.json diario.sqlite chavesAdmin.local.json taxaPOVIX` | Retorna transação POVIX assinada para transmitir |
| `prepare-burn` | `valorAtomico chavesUsuario.local.json taxaPOVIX` | Retorna transação de queima para transmitir |
| `bind-burn` | `vinculo.json chavesUsuario.local.json` | Assina endereço BNB de destino após confirmar queima |
| `verify-burn` | `vinculoAssinado.json` | Verifica queima e assinaturas dos donos das entradas |
| `sign-release` | `vinculoAssinado.json keystoreOperador diario.sqlite prazoUnix` | Assinatura individual de resgate |
| `release` | `vinculoAssinado.json autorizacoes.json keystoreTransmissor` | Libera ativo BNB após verificação e quórum |

`referencia.json`: `{"transactionHash":"0x…","logIndex":1}`. Use o índice global retornado por `deposit`.

`vinculo.json`: `{"transactionId":"hashPOVIX64hex","outputIndex":0,"chainId":97,"bridge":"0x…","recipient":"0x…","signatures":null}`. Endereços BNB neste arquivo devem estar em minúsculas. `bind-burn` retorna o vínculo preenchido com assinaturas. O índice zero identifica a operação inteira de queima, não uma saída a resgatar.

As chaves RSA do laboratório são um array JSON de strings XML privadas, no formato exportado por `Wallet.ExportPrivateKeys()`. Isso **não** é `wallets.dat` e os comandos não leem automaticamente carteiras reais. Os testes geram esses arquivos apenas em diretório temporário. As chaves não devem ser enviadas ao servidor DEX.

## Integração com o DEX e limites

Após confirmar a representação na POVIX, ela aparece no catálogo normal de tokens. Quando a rede permitir criar pools (incluindo a conclusão do lançamento inicial exigida pelas regras atuais), crie uma pool POVIX/pUSDT no Povix.Dex e deposite os dois lados. Este módulo não altera nem contorna essa restrição. O USDT da BNB permanece no cofre enquanto pUSDT circula entre carteiras e pools. Para resgatar, primeiro é necessário possuir pUSDT disponível fora da pool. Operações BNB pagam BNB. Compras e negociações na POVIX continuam pagando POVIX; as operações específicas de governança, emissão e queima da ponte podem usar taxa zero. O preço depende das reservas da pool, do volume e da arbitragem.

O módulo é uma ferramenta de laboratório para operadores, **sem formulário público de depósito no DEX, daemon de transmissão ou publicação automática**. As transações POVIX retornadas precisam ser transmitidas pelo mecanismo do nó; a ferramenta não afirma que elas foram confirmadas.

A ponte é federada: o contrato BNB verifica assinaturas, não provas de consenso POVIX. A política nativa exige quórum, mas não verifica diretamente recibos BNB nem a unicidade dos depósitos; esse controle está no diário do coordenador de teste. Administradores maliciosos com quórum podem emitir sem lastro ou aprovar resgates falsos. Uma reorganização profunda após as confirmações pode invalidar lastro já emitido. Backups consistentes dos diários e verificação independente das duas redes são necessários; perder um diário pode permitir reemissão. Não use vários diários independentes para coordenar emissões da mesma ponte.

Antes de produção ainda são necessários: assinatura nativa distribuída sem reunião de chaves, plano de recuperação e reconciliação do diário, monitoramento de lastro, tratamento operacional de reorganizações, revisão/auditoria dos contratos e serviços e implantação própria com parâmetros e operadores reais. Não há endpoints, chaves ou USDT oficiais configurados neste repositório. Mainnet exige uma versão revisada que remova intencionalmente a restrição de teste.

A mudança do lançamento exige Core, Desktop e DEX v36 e genesis esquema 9. A identidade da rede muda; isto não migra saldos nem conecta automaticamente nós antigos à nova rede. Nenhuma rede foi reiniciada ou publicada por este módulo.

## Tela de depósito no Povix.Dex

Acesse `/swap-bnb` pelo menu **Swap BNB** (a rota `/swap-usdt` continua disponível). Sem uma factory configurada, a tela mantém o fluxo USDT → pUSDT. A carteira EVM assina a autorização e o depósito diretamente, sem enviar chaves ao DEX. A tela aceita somente BNB Smart Chain Testnet (97), com 15 confirmações; não oferece USDT real da mainnet.

Após publicar e verificar o `UsdtVault` de teste, configurar a política nativa correspondente e ativar os operadores, defina em `Povix.Dex/Web.config`:

- `BridgeTestEnabled`: `true`;
- `BridgeTestAsset`: endereço do ERC20 USDT de teste usado pelo cofre;
- `BridgeTestVault`: endereço do cofre publicado.

Não coloque credenciais, chaves ou endereços fictícios nessa configuração. A configuração padrão mantém os depósitos desabilitados. A carteira precisa de BNB de teste para gás. O cliente verifica o código dos contratos, ativo, hash da rede POVIX, identificador pUSDT, escala e estado de pausa antes de solicitar o depósito. O limite de reserva também é consultado. Esses testes não substituem a verificação do bytecode publicado e da configuração da federação pelo operador.

O usuário informa o endereço público POVIX (64 caracteres hexadecimais). A entrada aceita até oito casas decimais, sem arredondamento, com cotação 1:1. Se necessário, autoriza somente o valor da operação; uma autorização insuficiente anterior é zerada primeiro. Uma autorização suficiente preexistente é reaproveitada.

O hash da transação e o identificador do evento `Deposited` devem ser preservados pelo usuário. A tela comprova o recibo BNB e informa **aguardando emissão**, sem afirmar que o saldo nativo já foi creditado. Os operadores continuam utilizando `prepare-mint` e submetendo a transação nativa pelo fluxo existente; esta tela não inicia um coordenador e não publica um endpoint privilegiado de emissão. Se houver timeout ou mudança de rede depois do envio, consulte o hash BNB antes de repetir qualquer operação. O histórico fica disponível na carteira EVM e no explorador de teste, inclusive após recarregar a página.

Verificação da interface: `node Tests/PovixBridgeSwapRegression.js` na raiz (usa o jsdom instalado em `work/liquidity-tools/node_modules/jsdom` e ethers de `Povix.Bridge/node_modules`). O teste usa carteiras e contratos simulados; o build .NET valida o controlador. Nenhuma dessas verificações publica contratos nem movimenta fundos reais.

## Ponte de múltiplos tokens BNB (protótipo de teste)

A tela `/swap-bnb` (também disponível em `/swap-usdt`) permite informar o **endereço do contrato** de um BEP-20. Nome e símbolo não são usados para localizar reservas nem para identificar tokens. Cada contrato possui um cofre próprio e um identificador nativo derivado de `chainId + endereço do contrato`; dois contratos com o mesmo símbolo continuam sendo ativos distintos. O USDT principal mantém o identificador pUSDT reservado pelo protocolo e não pode ganhar uma segunda representação.

### Configurar a factory e os operadores

1. Compile com `npm run build`. Configure e publique a ponte pUSDT de teste pelo fluxo anterior, incluindo a política nativa e sua maioria de pelo menos três administradores.
2. Execute `node src/cli.mjs deploy-factory config.local.json operador.local.json`. A factory copia o domínio nativo, operadores, quorum e limites do cofre principal. Salve o endereço retornado no campo `factory` da configuração da ponte e em `BridgeTestFactory` no Web.config do DEX. Configure também os três campos anteriores do cofre principal. Nenhuma chave é colocada no DEX.
3. Na tela, conecte a carteira e informe o contrato BNB Testnet. Se o cofre ainda não existir, o usuário confirma uma transação `createVault` na carteira e paga o gás de criação. Essa etapa não deposita nem emite tokens. A factory permite somente um cofre por contrato e todos os novos cofres começam pausados.
4. Os operadores precisam analisar o ativo (inclusive possibilidade de taxas, rebases, bloqueios e mudanças de implementação) e ativar o cofre com a maioria das assinaturas `sign-pause`/`pause`. Para gerar sua configuração use `node src/cli.mjs asset-config config.local.json 0xCONTRATO`. O resultado pode ser salvo em `asset.local.json` e usado nos comandos de pausa, que também funcionam enquanto o cofre está pausado. Depois da ativação, selecione o token novamente na tela e deposite.

Tokens padrão com `decimals` entre 0 e 18 são aceitos pelo contrato de custódia. A representação usa `min(decimals, 8)` casas: seis casas continuam seis, zero continua zero, e tokens de 18 casas precisam de quantidades exatamente representáveis com oito casas. A ponte recusa poeira, sem arredondar. A interface identifica o contrato de origem e exibe um símbolo `p` + até nove caracteres A–Z/0–9; a identidade permanece o ID e o vínculo no registro nativo, mesmo quando símbolos coincidem.

### Primeiro depósito e registro automático

Depois das confirmações BNB, o coordenador executa:

```sh
node src/cli.mjs prepare-asset-mint config.local.json 0xCONTRATO referencia.json state/ativo.sqlite administradores.local.json 0
```

`referencia.json` contém `transactionHash` e `logIndex` do evento de depósito, como no fluxo pUSDT. O comando resolve o cofre pela factory configurada, verifica sua configuração e o depósito canônico e, se necessário, prepara automaticamente duas transações nativas **em ordem**: registro com oferta zero, seguido da emissão da quantidade depositada. A saída nesse caso tem a forma `{ "transactions": [registro, emissao] }`. Submeta ambas ao nó POVIX, nesta ordem. Quando o registro já está confirmado, o comando usa a representação existente; não altera tokens comuns previamente criados na POVIX. O journal deve ser exclusivo por ativo/cofre e conservado para impedir dupla emissão. Repetições retornam as mesmas transações assinadas; um lote retornado pode incluir um registro já confirmado, que não deve ser submetido novamente.

O registro é autorizado pela maioria dos administradores da ponte principal. O consenso vincula o identificador ao contrato BNB, impede registro duplicado ou oferta inicial positiva e exige a mesma federação. Nome nativo contém o endereço de origem (`BNB 0x…`). A emissão e o resgate seguem a confiança federada já existente: os nós POVIX não validam diretamente recibos BNB. O coordenador continua sendo operado por CLI, sem daemon ou chave de emissão no servidor do DEX; a tela informa que o crédito aguarda os operadores. O tratamento de eventos não é um serviço automático instalado por esta PR.

Para retornar à BNB, use a configuração `asset.local.json` com `prepare-burn`, `bind-burn`, `sign-release` e `release`. O cofre é parte do domínio das assinaturas; uma autorização de um token não pode resgatar outro. O saldo de reserva e o limite diário são independentes por cofre. O cap nativo continua limitando emissão cumulativa, incluindo quantidades posteriormente queimadas; queimar não reabre esse orçamento.

**Compatibilidade:** consenso **v37**, manifesto **schema 10**. A alteração do Core muda o compromisso da implementação e o genesis/NetworkId. É necessário atualizar Core, Desktop, DEX e coordenador juntos, em uma rede de teste nova, e publicar/configurar cofres com o novo domínio nativo. Não há migração de saldos nem apagamento automático de dados. Não reutilize snapshots, journals ou cofres de uma rede anterior para emitir na nova. Esta implementação não ativa BNB mainnet e não movimenta fundos reais.

Tokens com taxa de transferência são rejeitados pelo depósito; o resgate também confere os saldos exatos do cofre e do destinatário, revertendo taxas e transferências não padronizadas. Tokens com saldo variável ou contratos maliciosos não recebem suporte garantido: a pausa inicial e a análise dos operadores continuam necessárias. A detecção genérica de todo comportamento futuro de um contrato não é possível.

Validação adicional: `node Tests/PovixMultiAssetSwapRegression.js` na raiz e `npm test` em `Povix.Bridge`, após `npm run test:native-build` com o toolchain Mono. Os testes multiativos executam Ganache e um ledger POVIX real local, cobrindo origem por contrato/rede, símbolos iguais, cofres separados, 0/6/18 casas, criação pausada, registro sem oferta, primeira emissão, rejeição de registro duplicado/forjado, replay de snapshots, idempotência do journal e queima/resgate do ativo correto.
