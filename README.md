# POVIX

`PrivateCoin.Core` contém uma implementação inicial de blockchain UTXO e uma rede P2P TCP.

## Características

- bloco gênese sem emissão e recompensa promocional de **6 POVIX** por nova carteira, limitada a 180.000 POVIX;
- recompensa proof-of-stake em quatro fases, paga ao criador e aos confirmadores de cada bloco;
- blocos ligados por SHA-256 e prova de trabalho;
- política de emissão em quatro fases de 2 milhões de blocos e seleção determinística de validadores ponderada pelas moedas bloqueadas;
- transações assinadas com RSA/SHA-256 e validação contra gasto duplo;
- fila de validação ordenada pela opção de taxa escolhida pelo usuário entre valores calculados conforme o congestionamento, paga integralmente ao criador do bloco;
- privacidade por endereços descartáveis: a carteira cria uma chave nova para cada recebimento, portanto não existe um endereço público permanente no blockchain;
- propagação P2P de transações e blockchains, com enquadramento, limite de tamanho, deduplicação e retransmissão (gossip);
- sincronização ao conectar: certificados de quórum têm prioridade quando a finalização está ativada; o modo legado usa comprimento e desempate por hash.
- identidade de rede vinculada a uma versão de consenso e a um bloco gênese canônico; pares incompatíveis são desconectados antes de seus dados serem propagados.

## Uso básico

```csharp
using (var alice = new Wallet())
using (var bob = new Wallet())
{
    var chain = new Blockchain();
    Block rewardBlock;
    chain.TryAddWalletCreationReward(alice.CreateReceiveAddress(), out rewardBlock);
    var payment = alice.CreateTransaction(chain, bob.CreateReceiveAddress(), 5 * Blockchain.OneCoin);
    chain.AddBlock(new[] { payment });
}
```

O endereço retornado por `CreateReceiveAddress` deve ser entregue diretamente ao pagador e usado uma só vez. A cadeia registra somente esse identificador descartável. O arquivo de chaves de uma aplicação deve ser cifrado e protegido; a classe `Wallet` mantém as chaves apenas em memória nesta versão.

Para a rede, crie um `PeerNode`, assine os eventos de transação e cadeia, chame `Start()` e conecte aos pares conhecidos com `ConnectAsync`. Uma aplicação deve validar transações recebidas e adotar somente cadeias aceitas por `Blockchain.TryReplaceChain`.

## Tokens nativos de quantidade fixa

O Core oferece `TokenCreate` e `TokenTransfer` desde a versão **3** do consenso. O protocolo atual é a versão **4**, com mensagens de finalização por quórum.
São regras nativas em C#, sem máquina virtual ou suporte a Solidity. A API
permite criar tokens com nome de até 64 caracteres, símbolo de 1 a 10 letras
maiúsculas A–Z, de 0 a 8 casas decimais e quantidade positiva em unidades
atômicas (`long`). A quantidade é fixa: não há emissão adicional nem queima.
Símbolos podem se repetir; use sempre o identificador do token para distingui-los.
O identificador deriva da primeira entrada POVIX consumida na criação, que não
pode ser reutilizada. Nome, símbolo, quantidade e ativos das saídas são assinados.

Criação e transferência pagam uma taxa em POVIX. A validação conserva POVIX e
cada token separadamente, rejeita gasto duplo e impede que tokens paguem taxas,
se tornem POVIX ou sejam usados como garantia de validador. Saídas pendentes
não ficam disponíveis para gasto antes da confirmação. `GetBalance`,
`GetSpendableBalance` e os totais do livro-caixa continuam expressos em POVIX;
`GetTokenBalance` consulta o saldo de um token e `GetTokens` lista as definições
confirmadas. `GetTokenBalances` lista todos os tokens, com saldo confirmado dos
endereços consultados, bloco de criação e confirmações, usando uma única visão
da cadeia. `GetUnspentOutputs` inclui todos os ativos; consumidores devem
consultar `Output.AssetId` (`null` significa POVIX). As saídas do livro-caixa
expõem também `AssetId`.

### Visualizar tokens no PrivateCoin.Desktop

Selecione a carteira e clique em **Ver tokens**, no cartão **Carteira**. A tela
lê os registros confirmados da blockchain local e mostra nome, símbolo,
quantidade total, saldo da carteira selecionada, casas decimais, identificador,
bloco de criação e confirmações. O identificador completo pode ser copiado.
Todos os tokens da rede aparecem por padrão, incluindo os de saldo zero;
**Somente tokens com saldo** restringe a lista à carteira selecionada.

O `Povix.Dex` já envia uma operação nativa `TokenCreate` para a rede existente.
Depois da confirmação pelos validadores, o nó do Desktop recebe o bloco pela
sincronização P2P existente. A lista acompanha novos blocos e transferências
automaticamente, verificando a cadeia a cada cinco segundos. O botão **Atualizar**
relê imediatamente a blockchain local. Criações e transferências pendentes
não alteram os dados confirmados, e uma troca de cadeia atualiza a lista.

Para visualizar um token criado no DEX, mantenha o nó do Desktop conectado à
mesma rede e aguarde o status **Confirmado** no comprovante do DEX. Para receber
o saldo no Desktop, copie um endereço dessa carteira e use-o em **Destino dos
tokens** antes de criar no DEX. O registro do token é público, mas o saldo
pertence aos endereços de recebimento; o mesmo nome de carteira ou símbolo
não concede controle do saldo. Endereços gerados no navegador usam chaves que
estão no backup do DEX e não são acrescentadas ao `wallets.dat` do Desktop.

Exemplo com uma carteira que já tenha POVIX confirmado:

```csharp
var pending = new Transaction[0];
// 100.000 unidades com 2 casas decimais = 1.000,00 MTK.
var creation = wallet.CreateTokenTransaction(chain, pending,
    "Meu Token", "MTK", 2, 100000, wallet.CreateReceiveAddress(),
    Blockchain.CalculateAutomaticFee(pending.Length, 1));
chain.ValidatePendingTransactions(new[] { creation });
// Envie a transação pela rede e aguarde sua inclusão em bloco validado.
string tokenId = creation.Token.Id;

// Depois da confirmação: 2.500 unidades = 25,00 MTK.
var transfer = wallet.CreateTokenTransferTransaction(chain, pending,
    tokenId, recipientOneTimeAddress, 2500,
    Blockchain.CalculateAutomaticFee(pending.Length, 1));
chain.ValidatePendingTransactions(new[] { transfer });
// Envie também esta transação e aguarde a confirmação.
long confirmedBalance = chain.GetTokenBalance(wallet.OwnedOneTimeAddresses, tokenId);
```

O `Povix.Dex` oferece o cadastro em `/tokens/criar`, com assinatura local por
carteira e confirmação acompanhada na blockchain existente. Consulte
[configuração e importação da carteira](Povix.Dex/README.md).
O Desktop ainda não tem formulário de criação, transferência ou exibição dos
saldos de tokens. Nenhum token é criado
apenas ao compilar o projeto ou executar os testes.

**Ativação:** clientes de consenso 3 aceitam as novas operações a partir do
primeiro bloco após o gênese e preservam a validação de cadeias antigas sem
tokens. O handshake desconecta clientes de consenso 2. A publicação exige uma
atualização coordenada dos nós antes da primeira transação com tokens; esta
alteração não agenda nem executa atualização de uma rede em funcionamento.
Veja `Tests/README.md` para executar a regressão de tokens.

## Povix Swap no site

`PrivateCoin.Site` inclui o **Povix Swap** em `/swap`, integrado à API de
ordens do serviço `PrivateCoin.Swap`. O par inicial é POVIX nativo ↔ USDT EVM
com seis casas decimais, preço definido pela tesouraria e reservas verificadas
nas blockchains. Ordens e reservas são persistidas em SQLite; depósitos e
pagamentos precisam de provas de transação confirmadas.

O operador realiza o pagamento por sua carteira externa. O site não guarda
chaves privadas, não assina transações e só confirma a liquidação depois de
verificá-la na blockchain. A operação está desativada por padrão até que rede,
contrato, preço, carteiras, fundos e proxy estejam configurados. Consulte
[instalação e limites operacionais](PrivateCoin.Swap/README.md).

A demonstração anterior continua em `/Home/SwapDemo`, identificada como
simulação com valores fixos e saldos fictícios. Rode
`python3 Tests/PovixLiquidityRegression.py` para validar o serviço e
`node Tests/PovixSwapRegression.js` para validar a demonstração.

## Aplicação Desktop

### Atualizações do Desktop

O botão **Buscar atualização** consulta o endereço HTTPS configurado em `UpdateManifestUrl`. A consulta também acontece silenciosamente ao iniciar o aplicativo. Quando existe uma versão superior à versão do executável, o Desktop pede confirmação, baixa o pacote ZIP, confere seu SHA-256, instala os arquivos somente depois de encerrar o processo e abre a versão nova. `Blockchain.json`, `wallets.dat`, `peers.dat` e `recovery.dat` nunca podem ser fornecidos pelo pacote e são preservados durante a cópia.

O manifesto publicado deve ter este formato (a versão usa o formato do `AssemblyVersion`):

```json
{
  "version": "1.1.0.0",
  "downloadUrl": "https://downloads.exemplo.com/povix/PrivateCoin.Desktop-1.1.0.zip",
  "sha256": "SHA256_DO_ARQUIVO_ZIP_EM_64_CARACTERES_HEXADECIMAIS",
  "releaseNotes": "Resumo opcional das mudanças."
}
```

O ZIP deve colocar `PrivateCoin.Desktop.exe` e os demais arquivos publicados diretamente na raiz. Tanto o manifesto quanto o pacote precisam usar HTTPS; publique primeiro o pacote, calcule o SHA-256 e só então atualize o manifesto. Builds sem um canal oficial podem deixar `UpdateManifestUrl` vazio, caso em que nenhuma conexão automática é feita.

`PrivateCoin.Desktop` oferece um painel WinForms para criar e alternar entre várias carteiras, consultar o saldo de cada uma, gerar endereços de recebimento e fazer transferências. Ao criar uma carteira, o painel exibe uma única vez uma frase de recuperação formada por 12 palavras em inglês; anote as palavras na ordem apresentada e mantenha-as em segurança. Os dados persistidos ficam separados nos seguintes arquivos:

- A pasta oculta `%LOCALAPPDATA%\PrivateCoin\.privatecoin` guarda `Blockchain.json`, `wallets.dat` e `recovery.dat` nas instalações novas. O aplicativo também procura primeiro um `wallets.dat` já existente na antiga pasta `.privatecoin` ao lado do executável ou dentro de `PrivateCoin.Desktop`; assim, uma carteira real salva no local usado pelas versões anteriores não é ocultada por um arquivo novo criado em `%LOCALAPPDATA%`. Arquivos ainda mais antigos, salvos fora de `.privatecoin`, são migrados automaticamente na primeira abertura;
- `.privatecoin/Blockchain.json` contém a blockchain completa, a fila de transações pendentes e o cadastro público das carteiras conhecidas pela instalação. Para cada carteira, o cadastro grava nome, todos os endereços, quantidade confirmada de tokens, garantia de validador e um identificador criptográfico de recuperação — nunca a frase nem as chaves privadas. Tudo fica em um envelope Base64 acompanhado pelo hash SHA-256 dos dados. Cada transferência validada é gravada imediatamente nesse arquivo enquanto aguarda a criação do bloco, mas só altera os saldos depois que o bloco é criado e validado. Ao abrir o arquivo, o aplicativo confere o hash, restaura as pendências e recalcula cada saldo a partir dos UTXOs confirmados, rejeitando dados inconsistentes;
- `.privatecoin/wallets.dat` contém os nomes, os endereços e o material privado necessário para assinar pelas carteiras locais, além das garantias de validador. Os endereços gravados são conferidos contra as chaves durante a abertura. O arquivo inteiro é cifrado para o usuário atual do Windows por DPAPI e **não deve ser distribuído**;
- `peers.dat` é o catálogo público e recriável dos endereços P2P descobertos, incluindo histórico de tentativas, sucessos e falhas; ele permite reiniciar sem depender imediatamente dos seeds;
- Para carteiras novas, as chaves são derivadas deterministicamente da frase de 12 palavras. Use **Recuperar com frase** em outra instalação depois de sincronizar o `Blockchain.json`: o identificador da frase localiza o cadastro, e a quantidade e a sequência exatas de endereços são reconstruídas e verificadas. Caso o arquivo ainda use um formato anterior, o aplicativo mantém a busca pelos endereços usados na blockchain como compatibilidade. A reconstrução criptográfica é executada em segundo plano e pode levar alguns instantes, sem travar a janela. Apagar somente `wallets.dat` não altera a blockchain nem gera outra recompensa de carteira. O antigo `recovery.dat`, quando presente, continua aceito apenas para recuperar carteiras criadas por versões anteriores.

Instalações que ainda possuam o antigo `wallets.dat` combinado são migradas automaticamente para esse formato na primeira abertura.

SHA-256 é uma função de hash de mão única, e não uma criptografia reversível. Por isso, ele é usado para verificar a integridade de `Blockchain.json`; os dados públicos continuam recuperáveis pelo aplicativo. As chaves privadas permanecem efetivamente cifradas por DPAPI somente em `wallets.dat`.

O painel também permite iniciar um nó TCP, conectar a outro par, sincronizar a cadeia, assinar e propagar transações e acompanhar cada aprovação ou rejeição feita pela validação da blockchain. Na seção **Validador**, informe a quantidade de POVIX e use **Bloquear e ativar** para reservar a garantia da carteira selecionada; o painel passa a exibir a quantia bloqueada, impede que ela seja transferida e restaura a ativação nas próximas execuções. Use **Desbloquear** para desativar o validador, liberar toda a garantia para transferências e salvar esse novo estado. A blockchain nasce sem saldo no bloco gênese. Cada nova carteira recebe 6 POVIX em um endereço descartável gerado aleatoriamente, e a distribuição cria, salva e propaga imediatamente um novo bloco. A emissão termina quando o limite total de 180.000 POVIX for alcançado, depois de 30.000 carteiras recompensadas. Uma transferência validada permanece na fila pendente e reserva os UTXOs de entrada para impedir gasto duplo, sem alterar o **saldo disponível** nem permitir o gasto de troco ou recebimentos ainda não confirmados. A fila é ordenada pela maior taxa e usa horário e identificador como desempate determinístico. O sistema calcula três opções de taxa a partir do tamanho atual da fila, e o usuário escolhe por múltipla escolha entre **Econômica**, **Normal** e **Prioritária**. A taxa mínima é uma unidade atômica, equivalente a **0,00000001 POVIX**, acrescentada por nível de congestionamento na opção econômica; as opções normal e prioritária aplicam multiplicadores de 2× e 4×, respectivamente, sempre limitadas a 1 POVIX. A taxa selecionada é descontada além do valor enviado e creditada integralmente ao criador do bloco. A transferência só se efetiva depois que pelo menos dois validadores elegíveis criam e validam o bloco. O sistema escolhe um deles para criar o bloco, os demais o confirmam e todos recebem sua parcela da recompensa; o novo bloco é salvo e propagado aos pares conectados. Para transferir entre carteiras locais, selecione a destinatária, copie o endereço exibido em **Receber em**, volte à carteira pagadora e informe esse endereço como destino. Para testar a rede localmente, abra duas instâncias em portas diferentes e conecte uma à outra pelo endereço `127.0.0.1`; as duas instâncias convergirão para a mesma cadeia válida.

### Descoberta automática de nós

Assim como o Bitcoin, cada instalação precisa conhecer pelo menos um nó inicial (bootstrap) para entrar pela primeira vez na rede. Configure um ou mais nós públicos em `App.config`, separados por vírgula:

```xml
<add key="PeerSeeds" value="seed.exemplo.com:4777,203.0.113.10:4777" />
```

Abrir a porta não faz outros nós descobrirem essa instalação automaticamente. A distribuição oficial precisa trazer domínios DNS seed reais em `PeerSeeds` (o repositório deixa o valor vazio porque não possui um domínio público). Quando não existe `peers.dat` nem seed configurado, o Desktop registra esse diagnóstico no painel.

Também é possível definir os seeds sem alterar o arquivo por meio da variável de ambiente `POVIX_PEERS` (os nomes legados `PONEX_PEERS`, `NOX_PEERS` e `PRIVATECOIN_PEERS` também são aceitos). Como no modelo do Bitcoin, os seeds são usados para o bootstrap, não como servidores centrais: depois do primeiro contato, o Desktop troca endereços com os pares, tenta em paralelo os endereços recém-descobertos, mantém até oito conexões e grava os candidatos no arquivo `peers.dat`. Endereços recebidos de um par têm prioridade sobre entradas antigas do cache, enquanto os seeds configurados continuam disponíveis para recuperação da rede. Assim, um `peers.dat` cheio de pares indisponíveis não impede a conexão aos pares anunciados durante a sessão. Como são conexões de saída, os clientes não precisam abrir portas nem alterar o roteador. `AutoStartNode` e `ListenPort` controlam o início automático e a porta local.

O botão **Ver pares** lista separadamente os endereços conectados e os apenas conhecidos. O catálogo persistente registra última tentativa, último sucesso e quantidade de falhas, priorizando endereços que funcionaram recentemente e mantendo no máximo 2.048 candidatos.

A rede ainda precisa ter alguns nós publicamente alcançáveis, mas eles são pares comuns e não um nó primário. Computadores atrás de CGNAT participam pelas conexões que eles mesmos iniciam. Depois que cada cliente conhece vários nós públicos, desligar o seed original não derruba a rede; os endereços aprendidos continuam disponíveis no `peers.dat`.

Liberar a porta somente no roteador pode não ser suficiente: a regra deve ser TCP, apontar para o IPv4 local correto e a mesma porta de `ListenPort`, e o Firewall do Windows também deve permitir o executável. Teste a porta a partir de outra conexão de internet; muitos roteadores não suportam NAT loopback e, por isso, um teste feito dentro da própria rede usando o IP público falha mesmo quando a regra externa está correta. Em CGNAT, o endereço WAN exibido pelo roteador é privado/compartilhado e nenhum encaminhamento local cria uma entrada pública. O listener usa dual-stack IPv4/IPv6 e o conector tenta todos os endereços DNS, com preferência por IPv4 e timeout por tentativa.

Por padrão, `EnableNatTraversal` é `true`: como clientes BitTorrent, o aplicativo tenta criar e renovar sozinho um mapeamento TCP temporário, primeiro por UPnP IGD e depois por NAT-PMP. O painel informa qual protocolo ficou ativo. Ao fechar o nó, o mapeamento é removido; não é necessário configurar manualmente o roteador. Se ambos estiverem indisponíveis, o cliente continua participando normalmente pelas conexões de saída e pelos pares descobertos através do seed.

Nenhuma técnica local consegue aceitar conexões diretas através de todo tipo de NAT. UPnP/NAT-PMP dependem do suporte do roteador e não atravessam CGNAT. Nesses casos, assim como um cliente BitTorrent passivo, o nó ainda descobre endereços, inicia conexões para nós alcançáveis e troca novos pares por gossip, sem qualquer configuração manual.

> Esta é uma base técnica, não software pronto para custodiar dinheiro real. Uma rede de produção também precisa de descoberta autenticada de pares, protocolo de consenso/forks, proteção contra Sybil/DoS, auditoria criptográfica e backups seguros.

### Alterações do código e regras de consenso

A rede não consegue provar qual executável um par está usando: um programa modificado pode anunciar a mesma versão. A proteção correta é cada nó validar localmente todos os dados recebidos. Alterar apenas o código de um nó não muda as regras aceitas pelos demais; blocos que violem hashes, assinaturas, UTXOs, emissão ou prova de consenso são rejeitados por eles.

Para impedir que uma cadeia criada com outras regras seja confundida com a rede POVIX, o Core fixa o bloco gênese e inclui `NetworkId` e `ConsensusVersion` em todas as mensagens P2P. Mensagens com outra identidade ou versão são descartadas e a conexão é encerrada. Uma mudança intencional nas regras exige incrementar `ConsensusVersion`, definir uma política de ativação por altura e distribuir a atualização; sem adesão suficiente, ela cria um fork em vez de substituir silenciosamente a rede existente.

Esses identificadores são separação de protocolo, não atestado do binário. Não se deve aceitar uma transação ou bloco por causa da versão anunciada, nem usar hash do DLL como regra de consenso: esse mecanismo seria falsificável por um cliente hostil e impediria implementações independentes. Antes de produção, as garantias de validadores e seus votos também precisam existir como transações e assinaturas verificáveis globalmente, como descrito abaixo.

## Emissão e validadores

A emissão destinada ao consenso dura 8 milhões de blocos (aproximadamente 20 anos, considerando 2 milhões de blocos a cada cinco anos) e totaliza **17.820.000 POVIX**:

| Fase | Alturas | Recompensa por bloco | Total da fase |
| --- | ---: | ---: | ---: |
| 1 | 1–2.000.000 | 3,61 POVIX | 7.220.000 POVIX |
| 2 | 2.000.001–4.000.000 | 2,80 POVIX | 5.600.000 POVIX |
| 3 | 4.000.001–6.000.000 | 1,50 POVIX | 3.000.000 POVIX |
| 4 | 6.000.001–8.000.000 | 1,00 POVIX | 2.000.000 POVIX |

`ProofOfStake` representa as regras determinísticas do consenso. Os participantes informam as moedas bloqueadas em garantia por meio de `ValidatorStake`; `SelectCreator` escolhe o criador de forma ponderada pelo valor bloqueado, usando a hash anterior e a altura como semente. Em cada nova altura o sorteio é refeito, portanto um participante pode criar um bloco em uma rodada e confirmar outro bloco em uma rodada posterior.

`DistributeReward` reserva **30%** da recompensa ao criador escolhido e distribui os **70%** restantes entre os validadores que confirmaram corretamente, proporcionalmente às garantias bloqueadas. O arredondamento da menor unidade é determinístico e a soma das parcelas é sempre exatamente a recompensa prevista para a altura. O criador não pode confirmar o próprio bloco. Além disso, as carteiras que enviam ou recebem qualquer transferência do bloco são excluídas tanto da criação quanto da confirmação desse bloco; seus endereços públicos são incluídos na prova para que essa regra seja revalidada ao carregar ou sincronizar a cadeia.

A propriedade `RewardPhases` expõe as quatro fases (limites, recompensa unitária e total) para que carteiras e exploradores possam apresentar a política sem duplicar números. `ScheduledIssuance` calcula o total diretamente dessas fases e permite verificar programaticamente que a emissão prevista é exatamente **17.820.000 POVIX**.

Ao existir uma transferência pendente e pelo menos dois validadores locais ativos, o Desktop seleciona o criador, usa os demais validadores como confirmadores, cria o bloco e inclui nele a transação que paga as parcelas de 30%/70%. A prova contém os validadores, garantias, endereços de recompensa e o papel do criador; ela faz parte da hash e é revalidada ao carregar ou sincronizar a cadeia. As recompensas tornam-se UTXOs das carteiras e aparecem no saldo.

O bloqueio da garantia é publicado como uma transação `StakeLock`: ela consome UTXOs assinados, cria uma saída de garantia que não pode ser usada por uma transferência comum e vincula globalmente o valor, a chave pública do validador e o endereço de recompensa. Todo nó reconstrói o conjunto ativo a partir dessas saídas não gastas e rejeita blocos cuja garantia não exista na cadeia. Cada criador e confirmador também assina individualmente o mesmo payload determinístico do voto; as chaves, papéis e assinaturas integram a hash do bloco e são verificados novamente ao carregar ou sincronizar a cadeia.

### Reconexão do Desktop

O Desktop inicia aguardando sincronização. Criar carteiras com recompensa,
enviar transferências, ativar/desativar stake e produzir blocos exige ao menos
um par conectado e o recebimento de uma cadeia validada. Ao perder todos os
pares, o estado de sincronização é invalidado imediatamente; respostas que já
estavam na fila da interface não podem liberar uma sessão posterior. Uma
cadeia idêntica validada também conclui a sincronização. Pendências válidas
voltam a ser processadas depois da sincronização.

A primeira abertura cria a carteira local, mas não emite uma recompensa nem
cria um bloco. Para criar outra carteira com recompensa é necessário primeiro
conectar a uma rede existente. Uma rede completamente nova precisa de um
procedimento separado de inicialização; não existe exceção automática offline.

Sem checkpoint de finalização configurado, a escolha permanece no modo legado:
mais blocos e, em empate, menor hash da ponta. Esse modo não garante preferência
pela cadeia mantida online. A migração para certificados está descrita abaixo.

### Finalização por votos dos validadores (migração experimental)

O consenso 4 exige finalização por votos e um checkpoint comum para iniciar um nó. O conjunto
de validadores de cada altura vem dos stakes confirmados **antes** do bloco,
nunca de uma lista enviada pelo candidato. Um bloco só se torna finalizado
com assinaturas RSA válidas de pelo menos duas chaves distintas representando
**mais de dois terços** do stake total registrado. O payload vincula a assinatura
ao hash completo do bloco e à identidade da política de ativação.

`TryReplaceChain` compara certificados, não comprimento:
uma cadeia menor com novos blocos finalizados substitui uma cadeia local maior
sem certificados, preservando todo o prefixo já finalizado. Certificados conflitantes
são rejeitados; o nó não desfaz um bloco finalizado. Uma cadeia sem novos
certificados não substitui a local. Propostas sem certificado podem ser votadas,
mas não autorizam a produção do bloco seguinte nem aparecem no painel de tokens
como confirmadas. `GetConfirmedView()` fornece a visão dos blocos finalizados.

O Desktop troca propostas e mensagens `finality-vote`, salva os certificados
junto aos blocos e persiste a decisão de voto **antes** de assinar em
`finality-votes.journal`. O mesmo validador não assina hashes conflitantes na
mesma altura, inclusive após reinício. Preserve esse diário junto ao backup
do nó: recuperar somente as chaves da carteira não recupera o histórico de
votos. Uma chave validadora não pode operar simultaneamente em instalações
com diários independentes. Erros de gravação ou diário corrompido bloqueiam
a votação.

A proposta PoS usa o criador selecionado pelo conjunto global de stakes
elegíveis. Só esse criador precisa ter a chave local para propor; os votos
de finalização são coletados de outros nós, sem reunir suas chaves privadas.
A seleção e a distribuição monetária do roster PoS permanecem determinísticas.
Os votos de finalização são distintos dos registros de recompensa desse roster.
Explorer e DEX validam certificados e seguem a mesma regra de cadeia, sem
possuir chaves de validadores.

#### Ativação na rede existente

Não é necessário prever alturas futuras. Escolha **uma única vez** um bloco
já existente, cuja cadeia válida contenha ao menos dois stakes de chaves distintas.
Todos os operadores precisam concordar no mesmo hash e altura desse bloco.
Não derive um checkpoint diferente em cada nó e não troque o checkpoint a cada
novo bloco. O checkpoint é a raiz de confiança da migração; novos nós obtêm
essa referência acordada antes de verificar os certificados posteriores.

O utilitário somente de leitura extrai uma sugestão do arquivo público
`Blockchain.json` (ou de um JSON público com `Blocks`). Ele não lê `wallets.dat`,
não altera arquivos e não ativa a rede:

```sh
mcs -r:PrivateCoin.Desktop/bin/Release/PrivateCoin.Core.dll \
  -r:System.Core -r:System.Runtime.Serialization \
  -out:work/PrivateCoinFinalityCheckpoint.exe Tests/PrivateCoinFinalityCheckpoint.cs
MONO_PATH=PrivateCoin.Desktop/bin/Release mono \
  work/PrivateCoinFinalityCheckpoint.exe /caminho/para/Blockchain.json
```

No Desktop, abra **Atividade da rede → Checkpoint** após sincronizar.
Escolha a altura de um bloco existente e clique em **Gerar candidato**.
A janela exibe o hash, a identidade da política e os stakes daquela altura.
**Salvar candidato** exporta um JSON com dados públicos. Outro operador pode
usar **Conferir arquivo recebido** para comparar o bloco e todos os registros
de validadores com sua própria cadeia. **Copiar configuração** apenas copia
os valores: nenhuma dessas ações altera App.config nem ativa a votação.
A mineração local de novas pendências fica pausada enquanto a janela está
aberta; os outros nós da rede continuam operando.

Se a votação já estiver configurada, a janela exporta o checkpoint fixo
da política existente, nunca a altura variável da ponta. Avançar a cadeia
não altera a referência. Importar um arquivo apenas confirma correspondência
com a cadeia local, não o acordo ou a assinatura dos demais operadores.

O utilitário também pode exportar e conferir o mesmo formato público:

```sh
mono work/PrivateCoinFinalityCheckpoint.exe Blockchain.json --export checkpoint.json
mono work/PrivateCoinFinalityCheckpoint.exe Blockchain.json --verify checkpoint.json
mono work/PrivateCoinFinalityCheckpoint.exe Blockchain.json --export checkpoint-altura.json --height 100
```

Substitua `100` por uma altura existente com ao menos dois validadores distintos.
A opção `--export` recusa sobrescrever arquivos existentes. Para usar o
utilitário depois da ativação, copie a configuração de finalização do nó
para `PrivateCoinFinalityCheckpoint.exe.config`, especialmente ao exportar
a política já ativa.

Depois do acordo, copie os valores emitidos de `FinalityAnchorHeight`,
`FinalityAnchorHash` e `RequireFinality=true` para o App.config do Desktop
(executável: PrivateCoin.Desktop.exe.config), e para os Web.config do Explorer
e do DEX. Preserve o backup da rede e do diário, distribua os binários de
consenso 4 e reinicie os nós de forma coordenada. Nós com versões ou políticas
diferentes são desconectados. O cache público do DEX aceita migração do
consenso 3; blocos e transações continuam sendo verificados.

Os arquivos de configuração exigem `RequireFinality=true`; desativá-lo é
rejeitado. Não existe mais adoção de cadeia pelo comprimento. O checkpoint
continua vazio porque não foi fornecida uma referência acordada de produção:
a rede real **ainda não foi ativada**. Sem essa referência o nó não inicia.
O Desktop abre uma janela de preparação que permite selecionar somente um
arquivo público `Blockchain.json` e gerar/conferir um candidato, sem abrir
carteiras, iniciar pares ou editar a configuração. DEX e Explorer também
exigem a referência. Os utilitários podem inspecionar arquivos históricos sem
política, mas essas cadeias de inspeção não podem adotar cadeias de pares.

Ao reconectar, o nó obtém e verifica os certificados mais recentes da rede;
ao desconectar, conserva o último prefixo finalizado e seu diário. Eventos de
conexão/desconexão não aprovam checkpoints e não alteram a raiz de confiança.
Estar online não substitui o quórum dos validadores.

#### Limites deste protocolo

Esta implementação oferece certificados sequenciais e bloqueio persistente
de voto, mas **não** implementa um protocolo BFT completo com prevote/precommit,
rodadas, troca de líder e recuperação de disponibilidade. Votos honestos
divididos entre propostas diferentes podem paralisar a altura; o criador
selecionado indisponível também pode impedir propostas PoS. Não há desbloqueio
por timeout, porque isso permitiria dupla assinatura e finalizações conflitantes.
Uma minoria não ganha autoridade apenas por permanecer online. Sem quórum,
a rede aguarda. Mesmo com quórum, a segurança exige menos de um terço do stake
com comportamento bizantino e preservação do histórico de votos dos validadores.

É uma migração experimental para revisão e testes, não uma substituição
auditada de Tendermint/CometBFT ou de outro motor BFT de produção. Não deve
ser ativada em uma rede financeira de produção antes de resolver as rodadas
e a recuperação de disponibilidade e revisar a segurança do protocolo.
