# POVIX

`PrivateCoin.Core` contém uma implementação inicial de blockchain UTXO e uma rede P2P TCP.

## Características

- bloco gênese sem emissão e recompensa inicial única de **600 POVIX** para a primeira carteira, limitada a 600 POVIX (uma única carteira), seguida do lançamento de 359.400 POVIX;
- consenso híbrido: stake escolhe o criador, votos são verificados e o bloco também exige prova de trabalho;
- blocos ligados por SHA-256 e prova de trabalho;
- escolha da cadeia válida por maior trabalho acumulado, sem checkpoint; emissão em quatro fases e seleção do criador ponderada pelo stake;
- transações assinadas com RSA/SHA-256 e validação contra gasto duplo;
- fila de validação por maior taxa, com até 20 operações por bloco ou espera de 10 segundos, incluindo a criação de carteiras e taxas pagas aos validadores das transações mediante assinaturas individuais;
- privacidade por endereços descartáveis: a carteira cria uma chave nova para cada recebimento, portanto não existe um endereço público permanente no blockchain;
- propagação P2P de transações e blockchains, com enquadramento, limite de tamanho, deduplicação e retransmissão (gossip);
- sincronização ao conectar, usando a cadeia válida mais longa e um desempate determinístico pela hash do bloco mais recente.
- identidade de rede vinculada a uma versão de consenso e a um bloco gênese canônico; pares incompatíveis são desconectados antes de seus dados serem propagados.

## Configurações registradas no genesis

O bloco zero contém `Rules.CoreConfigurationManifest`: um manifesto determinístico
com todas as constantes públicas e privadas dos tipos de `PrivateCoin.Core`,
inclusive versões históricas, valores dos tipos de transação, emissão, taxas,
dificuldade, confirmações sugeridas, limites de tokens, conexões, mensagens,
cache de peers, tempos de reconexão e UPnP e parâmetros de geração de chaves e
assinatura, além das quatro fases de recompensa completas.
A codificação ordena as chaves ordinalmente, usa números invariantes e prefixos
de comprimento; o texto inteiro participa do SHA-256 do bloco zero.
`GenesisHash` e `NetworkId` são resultados derivados do genesis, fora do manifesto
para evitar dependência circular. A prova de trabalho do genesis é determinística.
Novos parâmetros fixos devem ser constantes para entrar automaticamente nele.

`RulesHash` inclui esse manifesto e é exigido em **cada** mensagem P2P, junto da identidade
da rede e da versão do consenso. Valor divergente ou ausente desconecta o peer
antes de aprender seus endereços, executar eventos ou retransmitir a mensagem.
Uma alteração local dos parâmetros muda as regras canônicas e a identidade da rede.
A restauração rejeita manifesto ausente ou diferente, mesmo com prova de
trabalho refeita; blocos posteriores não podem redefinir esse manifesto.

Adicionar o manifesto muda o genesis e inicia uma **rede incompatível com a
anterior**. `Blockchain.GenesisHash`, `Blockchain.RulesHash` e `Blockchain.NetworkId`
são calculados a partir das regras e fontes incorporadas na compilação.
Os caches da rede anterior não são migrados ou apagados automaticamente; uma
implantação exige dados de cadeia separados e atualização coordenada dos nós.
Carteiras e chaves devem ser preservadas, mas saldos da cadeia anterior não
são transportados para o novo genesis.

Portas, sementes de conexão, caminhos, opções de NAT e chaves privadas são
configurações locais e não pertencem ao manifesto público. A checagem P2P
compara a configuração declarada; ela não atesta o código executado remotamente.
Um cliente malicioso pode declarar o hash esperado, por isso a aplicação deve
continuar validando transações e cadeias pelas regras de consenso do Core.

## Uso básico

```csharp
using System.Collections.Generic;

using (var alice = new Wallet())
using (var bob = new Wallet())
{
    var chain = new Blockchain();
    var pending = new List<Transaction>();
    // Registra a distribuição inicial; os 600 POVIX da primeira carteira aguardam confirmação em bloco.
    pending.Add(chain.CreateWalletCreationTransaction(alice.CreateReceiveAddress(), pending));
    chain.AddBlock(pending); // Uma criação por bloco, confirmando os 600 POVIX iniciais.
    pending.Clear();
    pending.Add(alice.CreateTransaction(chain, pending, bob.CreateReceiveAddress(), Blockchain.OneCoin, 1));
    chain.ValidatePendingTransactions(pending);
    // A transferência será incluída em um bloco, com aprovação assinada e dois validadores.
}
```

O endereço retornado por `CreateReceiveAddress` deve ser entregue diretamente ao pagador e usado uma só vez. A cadeia registra somente esse identificador descartável. O arquivo de chaves de uma aplicação deve ser cifrado e protegido; a classe `Wallet` mantém as chaves apenas em memória nesta versão.

Para a rede, crie um `PeerNode`, assine os eventos de transação e cadeia, chame `Start()` e conecte aos pares conhecidos com `ConnectAsync`. Uma aplicação deve validar transações recebidas e adotar somente cadeias aceitas por `Blockchain.TryReplaceChain`.

## Tokens nativos de quantidade fixa

O Core oferece `TokenCreate` e `TokenTransfer`; a versão **18** do consenso
aceita de 1 a 20 operações por bloco. Transferências e operações de tokens
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

### Visualizar e movimentar tokens no PrivateCoin.Desktop

Selecione a carteira e clique em **Ver tokens**, no cartão **Carteira**. A tela
lê os registros confirmados da blockchain local e mostra nome, símbolo,
quantidade total, saldo em blocos e saldo disponível da carteira selecionada,
casas decimais, identificador, bloco de criação e validações. Tokens pendentes, mesmo aprovados por validador, só aparecem após sua criação
ser confirmada em bloco.
O identificador completo pode ser copiado.
Por padrão, **Somente tokens com saldo** mostra apenas tokens com saldo
disponível maior que zero. Troque a carteira no seletor dessa tela para consultar
seus respectivos tokens; desmarque o filtro para consultar o registro público.

Para movimentar, selecione o token, informe o endereço de destino, a quantidade
nas casas decimais desse token e a taxa em POVIX. Clique em **Revisar e enviar**
e confira a carteira, o identificador, o destino, a quantidade e a taxa antes
de confirmar. A carteira que possui o saldo assina a transferência e paga a
taxa em POVIX; ter criado o token não concede saldo em outra carteira. O troco
do token e de POVIX permanece na carteira de envio. O nó precisa estar conectado
e sincronizado, e os arquivos locais precisam estar acessíveis para salvar
as chaves de troco e a transação antes de propagá-la.

O `Povix.Dex` já envia uma operação nativa `TokenCreate` para a rede existente.
Depois da aprovação por um validador, o nó do Desktop recebe a operação e sua
prova pela sincronização P2P existente. Os tokens aprovados e a movimentação de
seus saldos aparecem após a confirmação em bloco. A lista acompanha validações, blocos e
transferências automaticamente a cada cinco segundos; **Atualizar** relê os
dados imediatamente. Criações e movimentações fora de blocos não acrescentam saldo, mesmo aprovadas. Entradas
utilizadas em envios pendentes ficam reservadas e reduzem o saldo disponível,
impedindo gasto duplo. Uma troca de cadeia atualiza a lista. A tela informa o
identificador da transação enviada, que será confirmada em um bloco com até 20 operações.
Transações pendentes salvas são propagadas novamente depois da sincronização,
inclusive após reconexão ou reinício; não é necessário criar outro envio.

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
O Desktop permite consultar e movimentar os saldos de tokens confirmados.
A criação de tokens é feita no DEX. Nenhum token é criado
apenas ao compilar o projeto ou executar os testes.

**Ativação:** clientes de consenso 18 aceitam novas operações com confirmação
em bloco e preservam a validação do histórico até v17. O handshake desconecta
clientes com outra versão de consenso. A publicação exige uma
atualização coordenada dos nós e da DEX antes da primeira produção de bloco v18; esta
alteração não agenda nem executa atualização de uma rede em funcionamento.
Veja `Tests/README.md` para executar a regressão de tokens.

## Povix.Dex no site

`PrivateCoin.Site` apresenta o **Povix.Dex** na página inicial, com informações
sobre tokens nativos, pools de liquidez e trocas na blockchain Povix.
O conteúdo, o menu e o rodapé oferecem acesso a https://dex.povix.org.
As páginas locais `/swap`, `/Home/Swap` e `/Home/SwapDemo` foram removidas.

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

- `wallets.dat` e `recovery.dat` ficam na mesma pasta do executável `PrivateCoin.Desktop.exe`, independentemente da pasta de trabalho. Na primeira abertura, os arquivos da pasta de dados anterior são copiados sem sobrescrever arquivos já presentes ao lado do executável; os originais são preservados. A blockchain continua na pasta de dados usada pela instalação;
- `.privatecoin/Blockchain.json` contém a blockchain completa, a fila de transações pendentes e o cadastro público das carteiras conhecidas pela instalação. Para cada carteira, o cadastro grava nome, todos os endereços, quantidade confirmada de tokens, garantia de validador e um identificador criptográfico de recuperação — nunca a frase nem as chaves privadas. Tudo fica em um envelope Base64 acompanhado pelo hash SHA-256 dos dados. Cada transferência validada é gravada imediatamente nesse arquivo enquanto aguarda a criação do bloco, mas só altera os saldos depois que o bloco é criado e validado. Ao abrir o arquivo, o aplicativo confere o hash, restaura as pendências e recalcula cada saldo a partir dos UTXOs confirmados, rejeitando dados inconsistentes;
- `wallets.dat`, ao lado do executável, contém os nomes, os endereços e o material privado necessário para assinar pelas carteiras locais, além das garantias de validador. Os endereços gravados são conferidos contra as chaves durante a abertura. O arquivo inteiro é cifrado para o usuário atual do Windows por DPAPI e **não deve ser distribuído**;
- `peers.dat` é o catálogo público e recriável dos endereços P2P descobertos, incluindo histórico de tentativas, sucessos e falhas; ele permite reiniciar sem depender imediatamente dos seeds;
- Para carteiras novas, as chaves são derivadas deterministicamente da frase de 12 palavras. Use **Recuperar com frase** em outra instalação depois de sincronizar o `Blockchain.json`: o identificador da frase localiza o cadastro, e a quantidade e a sequência exatas de endereços são reconstruídas e verificadas. Caso o arquivo ainda use um formato anterior, o aplicativo mantém a busca pelos endereços usados na blockchain como compatibilidade. A reconstrução criptográfica é executada em segundo plano e pode levar alguns instantes, sem travar a janela. Apagar somente `wallets.dat` não altera a blockchain nem gera outra recompensa de carteira. O antigo `recovery.dat`, quando presente, continua aceito apenas para recuperar carteiras criadas por versões anteriores.

Instalações que ainda possuam o antigo `wallets.dat` combinado são migradas automaticamente para esse formato na primeira abertura.

O botão **Pasta da carteira**, no cartão **Carteira**, abre o Explorador do
Windows com o `wallets.dat` em uso selecionado. Use esse arquivo ao abrir a
carteira no DEX, especialmente quando existem cópias em pastas antigas.

SHA-256 é uma função de hash de mão única, e não uma criptografia reversível. Por isso, ele é usado para verificar a integridade de `Blockchain.json`; os dados públicos continuam recuperáveis pelo aplicativo. As chaves privadas permanecem efetivamente cifradas por DPAPI somente em `wallets.dat`.

O painel inicia um nó TCP, conecta pares, sincroniza a cadeia, assina e propaga transações. Cada bloco inclui até 20 operações: criação de carteira, transferência de POVIX, criação de token, movimentação de token e bloqueio ou desbloqueio de garantia. Ao criar uma carteira, os 600 POVIX só ficam disponíveis após o bloco de registro; apenas a primeira carteira recebe 600 POVIX; as seguintes recebem zero.

Na seção **Validador**, **Bloquear e ativar** reserva a entrada da garantia e solicita seu bloco. A ativação só ocorre depois da confirmação desse bloco. **Desbloquear** solicita outro bloco; a garantia é liberada e o validador desativado após a confirmação. Carteiras e garantias não cobram taxa individual e podem ter seus blocos minerados com prova de trabalho, permitindo iniciar a rede sem stake.

Transferências e operações de tokens aguardam aprovação assinada e um bloco próprio criado e confirmado por pelo menos dois validadores elegíveis. Remetentes e destinatários não podem validar sua própria operação. A fila prioriza taxa, horário e identificador, respeitando dependências. Entradas pendentes ficam reservadas para impedir gasto duplo; troco, recebimentos e taxas só são liberados no bloco. A taxa escolhida entre **Econômica**, **Normal** e **Prioritária** é descontada além do valor enviado e paga ao validador da operação, separadamente da recompensa do bloco.

A mineração automática reúne até 20 operações aprovadas. Produz o bloco ao completar o lote ou após 10 segundos desde a primeira operação pronta, mesmo com apenas uma operação. Salva cada bloco e o propaga aos pares. Para transferir entre carteiras locais, copie um endereço de **Receber em** da destinatária e use-o na carteira pagadora. Para testar a rede, abra duas instâncias em portas diferentes e conecte uma à outra por `127.0.0.1`.

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

`SelectApprovedValidationBatch` seleciona até 20 operações validadas, por taxa,
horário e identificador. Movimentações ainda não aprovadas e operações que
dependem de aprovações ausentes continuam pendentes. O produtor usa
`GetApprovedTransactions` e `BlockProductionScheduler` para observar toda a fila,
incluindo operações que excedem o primeiro lote. Um lote de 20 é produzido
imediatamente; lotes de 1 a 19 são produzidos após 10 segundos desde a primeira
operação pronta observada pelo nó. Novas chegadas não reiniciam o prazo e as
operações restantes preservam seu tempo de espera. Filas vazias não geram blocos.

O prazo usa um relógio monotônico local e orienta a produção, sem impor uma regra
de relógio aos outros nós. Ao reiniciar, o produtor volta a observar as pendências
restauradas. A confirmação ainda depende de conexão, sincronização, aprovações,
validadores elegíveis e prova de trabalho; os 10 segundos são um gatilho para
produção. O produtor separa operações quando seu conjunto de participantes
impedir um lote de ter dois validadores elegíveis, preservando suas dependências.

Transferências de POVIX e operações de tokens recebem uma `TransactionApproval`
assinada por uma carteira com garantia bloqueada. A prova identifica a garantia,
um bloco ancestral e o endereço do validador; o remetente e o destinatário não
podem aprovar sua própria operação. O Desktop aprova e propaga a operação antes
do bloco. O DEX exibe **Validada**, com zero confirmações e sem hash de bloco.
Carteiras e bloqueios/desbloqueios continuam validados sem essa assinatura e
sem taxa individual.

A aprovação não efetiva a operação. Até o bloco, as entradas ficam reservadas,
e os tokens, os recebimentos, o troco, as taxas e as garantias não se tornam
novos saldos disponíveis. O DEX mostra **Validada**, com uma aprovação, zero
confirmações e sem altura ou hash de bloco. Só a inclusão na blockchain muda
o comprovante para **Confirmado** e libera os saldos.

A taxa pertence à carteira que assinou a aprovação e é paga uma vez na
confirmação do bloco. Seu crédito usa o identificador da operação e o índice
seguinte às saídas declaradas (`Outputs.Count`), preservando as assinaturas.
A criação registra a definição e a quantidade do token no bloco; movimentações
atualizam o remetente e o destinatário no bloco. Garantias só ativam ou desativam
validadores após seu bloqueio ou desbloqueio ser confirmado em bloco.

O DEX envia o valor exato mostrado na opção de taxa à preparação, e o navegador
confere esse valor antes de assinar. Mudanças na fila não aumentam a taxa escolhida.
O saldo distingue taxas de valores reservados. Transações recebidas pelo Desktop
durante a sincronização são retidas até a cadeia ser validada, em vez de descartadas.

Cada bloco inclui de 1 a 20 operações e suas provas, cobertas pelo hash e pelos votos.
Sua transação de recompensa contém somente a emissão prevista, sem taxas.
As fases, os valores e a divisão de 30%/70% da recompensa permanecem iguais.
Após o fim da emissão, as taxas continuam pertencendo aos validadores das operações.

O consenso 18 permite `LiquiditySwap` sem assinatura do provedor nas duas
reservas bloqueadas da pool. A rede verifica o par, os ativos, o provedor e a
cotação; as entradas livres do comprador continuam assinadas. Criação e
encerramento exigem as assinaturas do dono. Blocos históricos v17 mantêm a
exigência original de assinatura das reservas. A ativação requer atualizar o
DEX e os nós da rede em conjunto.

Novos blocos usam `ConsensusVersion = 21`. Ao restaurar, o Core revalida o
histórico v0/v4/v7/v8/v9/v10/v11/v12/v13/v14/v15/v16/v17/v18/v19/v20 com suas regras originais e permite
sua extensão em v21. Blocos v16 preservam o limite de 1 a 20 operações. Blocos v15 continuam exigindo uma operação; blocos históricos
v4 a v14 mantêm os lotes originais de 20 quando exigidos por suas regras.
Caches DEX v3/v4/v7/v8/v9/v10/v11/v12/v13/v14/v15/v16/v17/v18/v19/v20 são revalidados
antes da migração. Nós e clientes DEX precisam atualizar juntos, pois o handshake
rejeita versões diferentes. As pendências antigas passam a aguardar inclusão
em bloco, sem apagar transações ou alterar saldos já confirmados.

`AddProofOfStakeBlock` continua sendo a entrada para produzir um bloco de
consenso: exige de 1 a 20 operações, garantias globais confirmadas,
criador escolhido por stake e provas assinadas. O bloco só é acrescentado
depois de satisfazer também a prova de trabalho. Blocos recebidos passam pelas
mesmas regras. `AddBlock` aceita de 1 a 20 operações sem transferência:
criação de carteira, bloqueio ou desbloqueio de garantia.
Elas podem compartilhar um bloco e não exigem prova individual dos validadores
com tokens bloqueados. Operações de garantia não cobram taxa de validador.
Transferências e operações de tokens exigem votos do bloco e sua prova
individual assinada. A recompensa do bloco mantém seu cálculo e divisão.

`CreateWalletCreationTransaction` retorna a própria operação `WalletCreate`,
com 600 POVIX para a primeira carteira. Não altera a
cadeia nem minera um bloco. Depois do limite, o registro conta sem emitir POVIX.
O Core valida a fila completa e reserva suas entradas. Créditos iniciais, tokens,
garantias, taxas, recebimentos e troco continuam indisponíveis até a confirmação
do bloco. A fila e suas dependências são salvas
e propagadas em ordem determinística. Cada operação é incluída uma única vez em um bloco, sem duplicar o saldo.

Históricos v9 preservam suas `WalletDistribution` e seus comprovantes de valor
zero por `WalletDistributionId`. `GetUncountedWalletCreations` recupera apenas
esses comprovantes históricos. Eles podem ser confirmados em blocos v18 sem nova emissão.
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

Quando há uma transferência aprovada e pelo menos dois validadores locais elegíveis ativos, o Desktop seleciona o criador, usa os demais validadores como confirmadores, cria o bloco e inclui nele a transação que paga as parcelas de 30%/70%. A prova contém os validadores, garantias, endereços de recompensa e o papel do criador; ela faz parte da hash e é revalidada ao carregar ou sincronizar a cadeia. As recompensas tornam-se UTXOs das carteiras e aparecem no saldo.

O bloqueio da garantia é publicado como uma transação `StakeLock`: ela consome UTXOs assinados, cria uma saída de garantia que não pode ser usada por uma transferência comum e vincula globalmente o valor, a chave pública do validador e o endereço de recompensa. Todo nó reconstrói o conjunto ativo a partir das saídas não gastas confirmadas na cadeia. Ao validar um bloco v15, v16, v17 ou v18, considera somente a cadeia anterior; uma garantia pendente não autoriza seus votos. Cada criador e confirmador também assina individualmente o mesmo payload determinístico do voto; as chaves, papéis e assinaturas integram a hash do bloco e são verificados novamente ao carregar ou sincronizar a cadeia.

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


## Regras comprometidas no bloco gênesis

O gênesis contém `Rules`: total de 18.180.000 POVIX em unidades atômicas,
600 para a recompensa única da primeira carteira,
359.400 para o lançamento inicial,
17.820.000 para validadores, todas as quatro fases de emissão, divisão de
30%/70%, precisão, taxas, alvo da prova de trabalho, limite de operações e
prazo de produção. O gênesis não emite moedas antecipadamente.
`Blockchain.MaximumSupply` é o teto de 360.000 POVIX da distribuição gratuita mais a reserva de lançamento;
`Rules.TotalSupplyAtomicUnits` explicita a soma desse teto com o dos validadores.

O produtor escolhe até 20 operações prontas e elegíveis e inicia um lote ao
atingir esse limite ou após 10 segundos desde a primeira observação local.
O prazo local não pode ser comprovado por outro nó: tempo decorrido não dispensa
aprovações, assinaturas, garantia confirmada, conservação de ativos nem mineração.
Filas vazias ou sem operações elegíveis não produzem blocos.

Todos os fontes compilados do `PrivateCoin.Core` são incorporados como recursos.
Seu compromisso SHA-256 ordenado é registrado em `Rules.ImplementationHash`;
assim, alterações em regras numéricas ou em lógica de validação também alteram
`RulesHash`, o gênesis determinístico e o `NetworkId`.
Os fontes são normalizados para LF para manter a identidade entre Windows/Linux
e Debug/Release. Por ser uma política estrita, até alterações de comentários ou
de código auxiliar do Core mudam a identidade; alterações apenas no Desktop não.
Compile pelo `.csproj`: a ausência dos recursos impede inicializar o protocolo.

Cada mensagem P2P exige a mesma rede, versão e `RulesHash`, antes de aprender
endereços, entregar eventos ou retransmitir mensagens. Cadeias recebidas também
são validadas no Core antes da entrega e retransmissão. Um gênesis adulterado,
ausente ou redefinido em outro bloco é rejeitado, mesmo após nova mineração.

O `Blockchain.json` do Desktop usa esquema 5: `Genesis` é a projeção legível do
bloco zero, que também permanece no payload `Data` protegido por SHA-256.
O carregamento valida ambas as cópias. O arquivo é um registro público, não uma
configuração que autoriza editar as regras da rede.

Esta mudança cria uma rede com outro gênesis. No Desktop, quando o arquivo
`Blockchain.json` é rejeitado na abertura por alteração, inconsistência ou regras
incompatíveis, ele é apagado e recriado com o gênesis canônico, sem mensagem de
erro na tela. O cadastro público e as pendências rejeitadas são descartados;
`wallets.dat` e `recovery.dat` são preservados e o nó aguarda sincronização com
os pares compatíveis para restaurar o histórico e os saldos confirmados.
Se as configurações ou o código do Core foram alterados, o novo gênesis é
gerado com essas regras locais e terá outro `GenesisHash`, `RulesHash` e
`NetworkId`: o nó começa sua própria cadeia, sem entrar na rede que o rejeitou.
Somente pares com as mesmas regras podem participar dessa cadeia. Editar apenas
o JSON não altera as regras do executável. Falhas de acesso
ao disco e de abertura da carteira continuam sendo informadas. Não há remintagem
nem migração do histórico antigo. Os números de versão de consenso históricos
continuam disponíveis apenas em cadeias ancoradas no novo gênesis.

O compromisso detecta incompatibilidades entre builds normais. Não é atestação
remota: software malicioso pode anunciar os hashes oficiais ou adulterar seu
binário/recursos. Os nós honestos continuam validando toda cadeia e rejeitando
resultados contrários às suas regras; nenhum hash anunciado prova que um peer
executa exatamente o código oficial.

### Liquidez de usuários nas pools (v19)

Na criação em Povix.Dex, “Permitir que usuários adicionem e removam liquidez”
define uma permissão permanente, desabilitada por padrão. Pools existentes
continuam com liquidez de usuários desabilitada. Quando habilitada, cada aporte
recebe participação proporcional aos dois ativos; a retirada paga apenas a
participação da carteira que assina, incluindo sua proporção das taxas acumuladas.
A interface recebe o limite do primeiro ativo no aporte e uma porcentagem da
participação na retirada. Arredondamentos usam inteiros: aportes arredondam para
cima e retiradas para baixo. Quantidades menores que uma unidade atômica são
rejeitadas. O criador mantém participação positiva até encerrar a pool; não pode
encerrá-la enquanto outros provedores tiverem participação.

Permissão e participações integram o payload assinado e são verificadas pelo Core;
as reservas continuam fora do saldo livre. A taxa da rede exige POVIX livres.
A v19 preserva a leitura dos blocos v18 e caches anteriores. Nós e clientes devem
ser atualizados juntos para validar novas operações; a identidade de rede segue
a regra existente de hash dos fontes do Core.

Na criação da pool, a opção de recompensa permite escolher **Participação nas
taxas** (padrão) ou **Bônus por permanência**. A escolha é registrada nos metadados
do DEX e exibida na revisão, no catálogo e no comprovante. Pools antigas assumem
Participação nas taxas. O modo Bônus por permanência registra a configuração
definida pelo criador. O pagamento ainda não está ativo: as regras cadastradas
precisam ser executadas pelo mecanismo de posições e reservas do Core. A escolha
por si só não altera as taxas e a distribuição proporcional das reservas.

O criador também cadastra a parcela das taxas destinada ao bônus (0,01% a 100%,
com duas casas decimais), a reserva mínima planejada em POVIX e a regra de saída
antecipada: retirada com perda do bônus ou bloqueio até o fim do prazo. Essas
regras são metadados persistentes de configuração; ainda não financiam uma reserva,
separam taxas, bloqueiam retiradas por tempo ou pagam bônus. O fluxo atual de
liquidez não muda. A escolha do prazo em dias, meses ou anos e a execução
financeira exigem implementação no mecanismo de posições e de reservas do Core.

### Pool de lançamento (v21)

Em Pools → Criar pool, “Criar pool de lançamento” recebe qualquer token nativo
confirmado e outro ativo confirmado para captação (POVIX ou token distinto). O
comprovante de criação do token oferece um atalho com o token pré-selecionado.
O menu Lançamentos permite a participação pública sem misturar o catálogo com
Minhas pools, que continua limitado à carteira criadora. Apenas unidades
desbloqueadas do token podem ser depositadas; vesting e pausa continuam sendo
validados. A devolução determinística de uma captação fracassada permanece
permitida mesmo se o token for pausado, sem desativar a pausa para outros usos.
O criador deposita os tokens destinados aos compradores mais os tokens da
liquidez inicial e faz um aporte inicial positivo no ativo de captação, menor
que a meta. Esse aporte conta como compra do criador. A meta é expressa no ativo
de captação; a quantidade de tokens para venda define a quantidade comprometida
quando a meta é completada. Não se usa o número de transações como meta.

Durante a captação, as duas reservas são custodiadas pelo consenso: nem o
criador consegue transferi-las, encerrar a pool ou usá-las em swaps. Novos
aportes atualizam apenas a contribuição da carteira que assina e não podem
exceder a meta. A abertura é parte do último aporte: uma única transação
confirmada entrega os tokens a cada comprador proporcionalmente ao total
aportado, forma a liquidez com 100% da captação e libera a negociação.
A alocação usa divisão inteira para baixo; tokens residuais permanecem na
liquidez. Os compradores recebem tokens, não participações LP pelo lançamento.
A participação inicial de liquidez pertence à carteira criadora e segue as
regras normais de administração e encerramento após a abertura. O criador
escolhe se permitirá aportes e retiradas proporcionais de outros provedores
depois da abertura. O lançamento utiliza a participação nas taxas da pool;
não ativa o bônus de permanência que permanece apenas como configuração.

O prazo é um bloco limite, não uma data estimada. Contribuições precisam ser
confirmadas em altura menor que esse limite. A partir da altura limite, uma
captação incompleta permite devolução integral de todos os aportes, incluindo
o inicial do criador, e retorno de todos os tokens depositados ao criador.
Qualquer carteira pode pagar a taxa para executar essa devolução conjunta;
não é necessária assinatura do criador, e os destinatários não podem ser
alterados. Taxas de rede previamente pagas não são reembolsadas. A devolução
não é executada em segundo plano: deve ser revisada, assinada e confirmada.
Os aportes não têm retirada antes desse limite. O limite de participantes é
100 endereços; cada endereço pode fazer vários aportes. O bloco limite deve
ser futuro e estar a até 1.000.000 de blocos da criação.

Criação, aportes, abertura e devolução são assinados localmente, propagados pelo
P2P e verificados pelo Core, inclusive durante restauração e reorganizações.
A interface mostra progresso, prazo e cada saída antes da assinatura. A taxa
da rede sempre usa POVIX livres, inclusive em lançamentos token/token.
Blocos v19/v20 continuam válidos, mas as operações de lançamento exigem v21; nós
e clientes devem ser atualizados juntos. O hash dos fontes do Core continua
a integrar a identidade da rede conforme a configuração existente.


## Lançamento inicial nativo — consenso v28

As regras executáveis e os exemplos de uso estão em [docs/povix-initial-launch.md](docs/povix-initial-launch.md).
A confirmação da 1ª carteira credita 600 POVIX bloqueados até a meta e cria automaticamente
a pool com 359.400 POVIX na reserva. A primeira carteira recebe 100 USDC locais e 1.000.000 de USDT local.
Cada uma das primeiras 10.000 identidades registradas recebe 100 USDC, incluindo
a primeira, sem ultrapassar 1.000.000 USDC emitidos. O crédito depende da confirmação
do registro e não se repete ao reabrir ou recuperar a mesma identidade. Após o limite,
novos registros continuam sem crédito USDC. A nova regra altera o genesis e requer
nós/DEX/Desktop compatíveis com v28; não migra saldos da cadeia anterior.
Esses tokens têm seis casas decimais, IDs reservados e oferta fixa, sem ponte,
lastro externo, emissão adicional, pausa ou queima administrativa.
O par automático usa **USDC local**; USDT local continua transferível como ativo separado.

Cada identidade registrada pode comprar uma vez: 1 token local por 6 POVIX brutos, com a taxa da rede descontada desse recebimento.
O valor líquido fica bloqueado e não é necessário ter POVIX antes da compra.
A primeira compra confirma sem validador, pois ainda não existem recibos de
lançamento bloqueados. As compras seguintes exigem aprovação e bloco assinado
por pelo menos uma identidade não participante que possua POVIX bloqueados
numa compra já confirmada. Um único detentor elegível pode produzir o bloco,
mesmo com outros detentores indisponíveis. Recebe a recompensa vigente do bloco
(integral quando atua sozinho) e a taxa da compra que aprovou. Sem validador na
primeira compra, a taxa descontada não tem destinatário e sai de circulação.
Blocos desse regime contêm somente compras do lançamento. As assinaturas, saldo
USDC e uma participação por identidade continuam obrigatórios. Ao atingir a
meta, a elegibilidade dos recibos termina: a rede mantém a tabela normal de
recompensas e o mínimo normal de dois validadores com staking para negociações.
A primeira carteira recompensada também pode participar. Após 29.950 compras, restam 179.700 POVIX e 29.950 unidades do
ativo escolhido na pool; os compradores são desbloqueados e as trocas são liberadas.
Não existe prazo ou devolução antecipada. Identidade significa a chave do endereço
original registrado, não uma pessoa: endereços novos de recebimento não substituem
essa chave e registrar outra carteira continua sendo possível.

O principal usa cotas sem proprietário, permanentemente bloqueadas no consenso.
Outros provedores podem adicionar e retirar suas cotas. A taxa existente de 0,30%
fica nas reservas e beneficia proporcionalmente as cotas; a parte das cotas
permanentes permanece na pool. O principal não pode ser encerrado nem pelo criador.

A mudança altera o gênesis, o manifesto e a identidade da rede. Requer cadeia nova
e atualização coordenada dos nós e clientes. Preserve as chaves e os arquivos da
rede anterior; não há migração automática dos saldos. O cronograma de 17.820.000
POVIX dos validadores permanece igual, resultando em teto total de 18.180.000 POVIX.


### Registro da carteira no PrivateCoin.Desktop 1.0.1

Depois de conectar e sincronizar com uma rede compatível, selecione a carteira
principal e clique em **Registrar carteira**. O botão **Criar carteira** também
prepara o registro da nova carteira. Ambos usam o mesmo fluxo do Core:
a primeira carteira confirmada recebe 600 POVIX e cria automaticamente a pool
com 359.400 POVIX; as seguintes recebem zero de recompensa inicial.
A primeira também recebe os dois tokens locais de um milhão de unidades cada.

O botão distingue registro pendente e confirmado, impede uma segunda inscrição
e salva as chaves antes de publicar a operação. O quadro de conexão mostra se a
pool aguarda confirmação, está em captação ou já abriu. A reserva principal não
é somada ao saldo disponível. A primeira abertura continua sem emitir moedas
isoladamente; é necessário conectar e sincronizar antes de registrar.
