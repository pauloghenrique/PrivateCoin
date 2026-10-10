# POVIX

`PrivateCoin.Core` contém uma implementação inicial de blockchain UTXO e uma rede P2P TCP.

## Características

- bloco gênese sem emissão e recompensa promocional de **6 POVIX** por nova carteira, limitada a 180.000 POVIX;
- consenso híbrido: stake escolhe o criador, votos são verificados e o bloco também exige prova de trabalho;
- blocos ligados por SHA-256 e prova de trabalho;
- escolha da cadeia válida por maior trabalho acumulado, sem checkpoint; emissão em quatro fases e seleção do criador ponderada pelo stake;
- transações assinadas com RSA/SHA-256 e validação contra gasto duplo;
- fila de validação por maior taxa, com 20 operações por bloco, incluindo a criação de carteiras e taxas pagas aos validadores das transações mediante assinaturas individuais;
- privacidade por endereços descartáveis: a carteira cria uma chave nova para cada recebimento, portanto não existe um endereço público permanente no blockchain;
- propagação P2P de transações e blockchains, com enquadramento, limite de tamanho, deduplicação e retransmissão (gossip);
- sincronização ao conectar, usando a cadeia válida mais longa e um desempate determinístico pela hash do bloco mais recente.
- identidade de rede vinculada a uma versão de consenso e a um bloco gênese canônico; pares incompatíveis são desconectados antes de seus dados serem propagados.

## Uso básico

```csharp
using System.Collections.Generic;

using (var alice = new Wallet())
using (var bob = new Wallet())
{
    var chain = new Blockchain();
    var pending = new List<Transaction>();
    // Registra a distribuição inicial e já libera 6 POVIX, sem esperar o lote.
    pending.Add(chain.CreateWalletCreationTransaction(alice.CreateReceiveAddress(), pending));
    var payment = alice.CreateTransaction(chain, pending, bob.CreateReceiveAddress(), Blockchain.OneCoin, 1);
    pending.Add(payment);
    chain.ValidatePendingTransactions(pending);
    // O comprovante da carteira e a transferência contam como duas operações.
    // Reúna 20 operações para confirmar as transferências e pagar os validadores.
}
```

O endereço retornado por `CreateReceiveAddress` deve ser entregue diretamente ao pagador e usado uma só vez. A cadeia registra somente esse identificador descartável. O arquivo de chaves de uma aplicação deve ser cifrado e protegido; a classe `Wallet` mantém as chaves apenas em memória nesta versão.

Para a rede, crie um `PeerNode`, assine os eventos de transação e cadeia, chame `Start()` e conecte aos pares conhecidos com `ConnectAsync`. Uma aplicação deve validar transações recebidas e adotar somente cadeias aceitas por `Blockchain.TryReplaceChain`.

## Tokens nativos de quantidade fixa

O Core oferece `TokenCreate` e `TokenTransfer`; a versão **10** do consenso
exige 20 operações distintas por bloco. Transferências e operações de tokens
exigem provas assinadas; registros de carteira contam sem essas provas.
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

O botão **Pasta da carteira**, no cartão **Carteira**, abre o Explorador do
Windows com o `wallets.dat` em uso selecionado. Use esse arquivo ao abrir a
carteira no DEX, especialmente quando existem cópias em pastas antigas.

SHA-256 é uma função de hash de mão única, e não uma criptografia reversível. Por isso, ele é usado para verificar a integridade de `Blockchain.json`; os dados públicos continuam recuperáveis pelo aplicativo. As chaves privadas permanecem efetivamente cifradas por DPAPI somente em `wallets.dat`.

O painel também permite iniciar um nó TCP, conectar a outro par, sincronizar a cadeia, assinar e propagar transações e acompanhar cada aprovação ou rejeição feita pela validação da blockchain. Na seção **Validador**, informe a quantidade de POVIX e use **Bloquear e ativar** para reservar a garantia da carteira selecionada; o painel passa a exibir a quantia bloqueada, impede que ela seja transferida e restaura a ativação nas próximas execuções. Use **Desbloquear** para desativar o validador, liberar toda a garantia para transferências e salvar esse novo estado. A blockchain nasce sem saldo no bloco gênese. Cada nova carteira recebe 6 POVIX em um endereço descartável gerado aleatoriamente, e o registro entra na fila compartilhada, é salvo e propagado como operação pendente. Cada criação conta uma vez, sem taxa nem aprovação individual por tokens bloqueados; os 6 POVIX ficam disponíveis imediatamente, antes do lote. Criar carteira, bloquear e desbloquear garantias apenas registram operações validadas na fila, sem gerar blocos isolados nem exigir aprovação individual dos tokens bloqueados. O saldo inicial e o estado da garantia ficam disponíveis na fila validada, persistida e propagada aos pares. A emissão termina quando o limite total de 180.000 POVIX for alcançado, depois de 30.000 carteiras recompensadas. Uma transferência validada permanece na fila pendente e reserva os UTXOs de entrada para impedir gasto duplo, sem alterar o **saldo disponível** nem permitir o gasto de troco ou recebimentos ainda não confirmados. A fila respeita as dependências de entradas; entre operações prontas, prioriza maior taxa, horário e identificador. O sistema calcula três opções de taxa a partir do tamanho atual da fila, e o usuário escolhe por múltipla escolha entre **Econômica**, **Normal** e **Prioritária**. A taxa mínima é uma unidade atômica, equivalente a **0,00000001 POVIX**, acrescentada por nível de congestionamento na opção econômica; as opções normal e prioritária aplicam multiplicadores de 2× e 4×, respectivamente, sempre limitadas a 1 POVIX. A taxa selecionada é descontada além do valor enviado e paga ao validador da transação, mediante prova assinada separada dos votos do bloco. Ao reunir 20 operações, contando transferências validadas, registros de carteiras e operações de garantia, as primeiras 20 operações em ordem de dependência e prioridade são incluídas no bloco e as demais continuam pendentes. Um lote composto somente por carteiras e operações de garantia pode ser confirmado com prova de trabalho sem validadores com tokens bloqueados, permitindo a distribuição inicial. A transferência só se efetiva depois que pelo menos dois validadores elegíveis criam e validam esse bloco. O sistema escolhe um deles para criar o bloco, os demais o confirmam e todos recebem sua parcela da recompensa; o novo bloco é salvo e propagado aos pares conectados. Para transferir entre carteiras locais, selecione a destinatária, copie o endereço exibido em **Receber em**, volte à carteira pagadora e informe esse endereço como destino. Para testar a rede localmente, abra duas instâncias em portas diferentes e conecte uma à outra pelo endereço `127.0.0.1`; as duas instâncias convergirão para a mesma cadeia válida.

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

Esses identificadores são separação de protocolo, não atestado do binário. Não se deve aceitar uma transação ou bloco por causa da versão anunciada, nem usar hash do DLL como regra de consenso: esse mecanismo seria falsificável por um cliente hostil e impediria implementações independentes. As garantias e os votos são verificados contra o estado da cadeia, além da prova de trabalho descrita abaixo.

## Emissão e validadores

### Validação de transações e taxas

`SelectApprovedValidationBatch` seleciona 20 operações validadas, por taxa,
horário e identificador, respeitando as dependências. Cada operação conta uma
vez: transferências, criações/movimentações de tokens, carteiras e garantias.
Com menos de 20, nenhum bloco é criado.

Transferências de POVIX e operações de tokens recebem uma `TransactionApproval`
assinada por uma carteira com garantia bloqueada. A prova identifica a garantia,
um bloco ancestral e o endereço do validador; o remetente e o destinatário não
podem aprovar sua própria operação. O Desktop aprova e propaga a operação antes
do bloco. O DEX exibe **Validada**, com zero confirmações e sem hash de bloco.
Carteiras e bloqueios/desbloqueios continuam validados sem essa assinatura e
sem taxa individual.

Assim que a aprovação é aceita, a taxa escolhida fica disponível para a carteira
que validou. Seu crédito usa o identificador da movimentação e o índice seguinte
às saídas declaradas (`Outputs.Count`), sem alterar a assinatura do remetente.
Esse crédito pode financiar outra operação antes do bloco. Repetições e reinícios
preservam um único crédito; a confirmação conserva a mesma referência e não paga
a taxa novamente. Recebimentos comuns, tokens e troco aguardam o bloco.

O bloco inclui as 20 operações e suas provas, cobertas pelo hash e pelos votos.
Sua transação de recompensa contém somente a emissão prevista, sem taxas.
As fases, os valores e a divisão de 30%/70% da recompensa permanecem iguais.
Após o fim da emissão, as taxas continuam pertencendo aos validadores das operações.

Novos blocos usam `ConsensusVersion = 11`. Ao restaurar, o Core revalida o
histórico v0/v4/v7/v8/v9/v10, preservando as regras antigas de pagamento de taxas,
e permite sua extensão em v11 sem voltar a uma versão anterior. Caches DEX
v3/v4/v7/v8/v9/v10 são revalidados antes da migração. Nós e clientes DEX devem
atualizar juntos; a versão anunciada nunca substitui a validação dos dados.

`AddProofOfStakeBlock` continua sendo a entrada para produzir um bloco de
consenso: exige exatamente 20 transações distintas, garantias globais válidas,
criador escolhido por stake e provas assinadas. O bloco só é acrescentado
depois de satisfazer também a prova de trabalho. Blocos recebidos passam pelas
mesmas regras. `AddBlock` aceita apenas lotes completos de 20 operações
sem transferências: criações de carteira, bloqueios e desbloqueios de garantia.
Cada uma conta como uma validação e não exige prova individual dos validadores
com tokens bloqueados. Operações de garantia não cobram taxa de validador.
Lotes mistos exigem os votos do bloco e provas individuais das transferências
e operações de tokens. A recompensa do bloco mantém seu cálculo e divisão.

`CreateWalletCreationTransaction` retorna a própria operação `WalletCreate`,
com 6 POVIX para as primeiras 30.000 carteiras (180.000 POVIX). Não altera a
cadeia nem minera um bloco. Depois do limite, o registro conta sem emitir POVIX.
O Core valida a fila completa e disponibiliza os créditos iniciais e o estado
das garantias antes do bloco; recebimentos e troco de transferências comuns
continuam indisponíveis até a confirmação. A fila e suas dependências são salvas
e propagadas em ordem determinística. Ao completar o lote, as mesmas operações
são incluídas na cadeia, sem duplicar o saldo.

Históricos v9 preservam suas `WalletDistribution` e seus comprovantes de valor
zero por `WalletDistributionId`. `GetUncountedWalletCreations` recupera apenas
esses comprovantes históricos. Eles podem completar um lote v10 sem nova emissão.
Pendências v8 mantêm o próprio registro e não precisam de um bloco de migração.
Endereços repetidos, gastos duplos e referências órfãs são rejeitados ao validar
pendências e cadeias recebidas.

### Trabalho acumulado, confirmações e reorganizações

`ProofOfWork.Target` mantém o alvo fixo existente de 12 bits (hash iniciada por
`000`). O trabalho de um bloco vem de `floor(2^256 / (target + 1))`: **4096**
unidades. Zeros adicionais encontrados por acaso não aumentam esse valor.
`ChainWork` soma o trabalho validado em `BigInteger`, incluindo o gênese.
Com esse alvo fixo, mais trabalho equivale a mais blocos; não existe preferência
automática pela cadeia menor. Em empate, vence a menor hash da ponta.

Cada nó valida integralmente a cadeia candidata, incluindo assinaturas de
transações e votos, garantias no estado anterior, prova de trabalho, gastos
duplos, conservação de ativos e emissão. Trabalho não torna um bloco inválido
aceitável. `TrySynchronizeChain` também distingue uma cadeia idêntica válida
de uma cadeia antiga com menos trabalho, evitando liberar operações com uma
resposta atrasada durante reconexão.

`GetConfirmations` conta o bloco de inclusão e os seguintes; `GetConfirmationWork`
soma o trabalho desse trecho. A consulta do explorador expõe trabalho e
confirmações. Se uma transação sai da cadeia após reorganização, sua contagem
volta a zero. O Desktop e o DEX recolocam transações órfãs ainda válidas na fila,
e os comprovantes do DEX refletem a cadeia atual, inclusive após reinício.

**Não há checkpoint nem finalização irreversível.** Uma cadeia válida com maior
trabalho pode substituir o histórico local. Seis confirmações são uma referência,
não uma garantia de segurança de 100%. O alvo atual é baixo e não tem ajuste
automático: esta implementação experimental não tem a resistência econômica
da rede Bitcoin. As regras de stake, distribuição promocional e seleção de
validadores também tornam este protocolo diferente do Bitcoin.

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

Ao completar um lote de 20 operações que inclua transferências e existir pelo menos dois validadores locais ativos, o Desktop seleciona o criador, usa os demais validadores como confirmadores, cria o bloco e inclui nele a transação que paga as parcelas de 30%/70%. A prova contém os validadores, garantias, endereços de recompensa e o papel do criador; ela faz parte da hash e é revalidada ao carregar ou sincronizar a cadeia. As recompensas tornam-se UTXOs das carteiras e aparecem no saldo.

O bloqueio da garantia é publicado como uma transação `StakeLock`: ela consome UTXOs assinados, cria uma saída de garantia que não pode ser usada por uma transferência comum e vincula globalmente o valor, a chave pública do validador e o endereço de recompensa. Todo nó reconstrói o conjunto ativo a partir dessas saídas não gastas na cadeia e nas operações de garantia validadas da fila. Ao validar um bloco, considera somente a cadeia anterior e as operações do próprio lote; uma garantia pendente fora dele não autoriza seus votos. Cada criador e confirmador também assina individualmente o mesmo payload determinístico do voto; as chaves, papéis e assinaturas integram a hash do bloco e são verificados novamente ao carregar ou sincronizar a cadeia.

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

A escolha da cadeia é determinística: maior trabalho acumulado e, em empate,
menor hash da ponta. Essa proteção evita produção isolada no Desktop atualizado,
mas não estabelece finalização nem obriga nós antigos ou outros clientes a
seguir a mesma política. Uma cadeia válida com maior trabalho ainda pode substituir a local.
