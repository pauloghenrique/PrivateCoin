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
