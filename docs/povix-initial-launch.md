# Lançamento inicial POVIX — consenso v28

Implementação no Core: `InitialLaunch`, `TokenExchange`, `Blockchain` e `Wallet`.
A criação é automática na confirmação da 1ª carteira recompensada; a compra está disponível pelas APIs assinadas do Core. A carteira
Desktop apresenta o saldo comprado como bloqueado; o DEX descobre a pool pela
cadeia e permite trocas e liquidez adicional depois da abertura. O formulário
DEX de lançamentos genéricos continua separado: não cria o lançamento nativo.

## Emissão e ativos locais

| Destino | Quantidade |
| --- | ---: |
| Primeira identidade registrada | 600 POVIX (recompensa única, bloqueada até a meta) |
| Reserva na criação única do lançamento | 359.400 POVIX |
| Total inicial POVIX | 360.000 POVIX |
| USDC local, por identidade registrada (até 10.000) | 100 USDC |
| USDT local, para a 1ª carteira recompensada | 1.000.000 USDT |

Não há emissão no bloco zero. A confirmação da 1ª carteira emite seus 600 POVIX,
completa a distribuição inicial de 600 POVIX e cria a pool na mesma operação, sem taxa ou ação manual.
Ela cria os tokens locais, credita 100 USDC e 1.000.000 USDT à primeira carteira e deposita os 359.400
POVIX na reserva, sem entregar essa reserva como saldo livre. A pool usa uma chave
pública fixa do protocolo, sem chave privada retida; sua liquidez principal não tem dono. As unidades locais têm seis casas decimais e oferta fixa.
Não possuem política de mint, pausa, queima administrativa ou vesting.

IDs reservados pelo protocolo:

- USDC: `d0c0ff968d7093a5b3e4b9a617112e66e4d62d5765833d0852387b5ea7d1ab98`
- USDT: `ab4232725e9910e3140520ca7b7c0567fb4af5aa261a712244ab0e40b03e9716`

São tokens autônomos da rede POVIX, **sem ponte ou lastro externo**. Não há
promessa de equivalência a dólar ou aos tokens emitidos em outras redes.
Tokens de mesmo símbolo e outro ID não são aceitos no lançamento inicial.
O par automático usa USDC local; USDT local continua transferível como ativo separado.
Não existe soma de reservas USDC e USDT no mesmo par nem troca do par na captação.

## Compra e desbloqueio

Cada identidade registrada pode comprar uma vez, inclusive a primeira, que
recebeu a distribuição inicial. Uma compra deposita exatamente 1 unidade do
token local selecionado e retira exatamente 6 POVIX da reserva de venda. Esses
6 POVIX brutos têm a taxa de rede descontada antes de gerar o UTXO líquido
do comprador, bloqueado pelo consenso. Não é necessário ter POVIX livres para comprar.

A identidade é a chave original do endereço que recebeu a operação `WalletCreate`.
A compra exige uma assinatura dessa chave além das assinaturas dos fundos. Usar
outros endereços de recebimento ou transferir a compra não permite repetir a
mesma identidade. Registrar outra identidade continua possível: a regra não
identifica pessoas nem garante uma única participação por arquivo de carteira.
Esse uso da identidade original também torna o recebimento do lançamento
vinculável ao registro; os demais recebimentos mantêm endereços descartáveis.

| Momento | Compras | Reserva POVIX | Reserva do token local | Compras bloqueadas? |
| --- | ---: | ---: | ---: | --- |
| Criação | 0 | 359.400 | 0 | Sim |
| Antes da última compra | 29.949 | 179.706 | 29.949 | Sim |
| Abertura | 29.950 | 179.700 | 29.950 | Não |

A última compra libera os UTXOs de todos os compradores e abre a negociação de
forma atômica. Nenhuma compra adicional é aceita. Não existe prazo, reembolso
ou liberação antecipada se a meta não for atingida. Até a abertura, nem o criador
pode trocar, retirar ou encerrar as reservas.

O estado é reconstruído das transações. Não se aceita contador, saldo ou lista
de compradores fornecidos por pares. Marcadores internos de identidade não são
UTXOs gastáveis. Cada compra tem tamanho limitado, sem serializar uma lista
crescente de 29.950 participantes nem pagar todos em uma transação gigante.
A restauração e a troca de cadeia recalculam as compras e os bloqueios.

## Principal, liquidez adicional e taxas

Na abertura, os 179.700 POVIX restantes e as 29.950 unidades captadas tornam-se
liquidez principal. `PoolParticipation.LockedShares` representa as cotas
permanentes, sem proprietário que possa resgatá-las. O fechamento é rejeitado
inclusive com assinatura válida do criador. Trocas preservam as cotas; adições
e remoções só alteram as cotas pertencentes aos provedores adicionais.

O bloqueio é de cotas, não um piso fixo de saldo: negociações alteram as reservas
pelo AMM. Provedores adicionais podem retirar toda a sua participação proporcional
às reservas atuais. A taxa existente de 0,30% das trocas fica nas reservas;
a parcela dos provedores adicionais é realizada na retirada de suas cotas.
A parcela das cotas permanentes continua dentro da pool permanentemente.

## APIs do Core

```csharp
// A chamada existente para registrar carteiras já cria a pool na 1ª recompensa.
Transaction registration = chain.CreateWalletCreationTransaction(address, pending);
// Encaminhar registration à confirmação normal. Não há criação manual da pool.
// Os tokens locais são creditados à 1ª carteira; ela pode distribuí-los
// usando as transferências de token já existentes.

Transaction purchase = buyer.CreateInitialLaunchPurchase(
    chain, pending, originalRegisteredAddress, changeAddress, fee);
// Encaminhar purchase à validação e ao bloco. Nunca creditar saldo pela mera preparação.

InitialLaunchStatus status = chain.GetInitialLaunchStatus();
long locked = chain.GetInitialLaunchLockedBalance(buyer.OwnedOneTimeAddresses);
long available = chain.GetSpendableBalance(buyer.OwnedOneTimeAddresses, pending);
```

Para assinatura externa, `TokenExchange.PrepareInitialLaunchPurchase` retorna `UnsignedTokenExchange`. Na compra,
assinar o payload com cada chave de entrada livre e com a chave da identidade;
passar esta última assinatura como segundo argumento de `Complete`.
As reservas são consumidas sem assinatura do provedor, mas somente pelas regras
exatas do consenso. As compras exigem aprovação e confirmação normais. O cadastro da 1ª carteira e sua criação automática de pool são auto-validados pelo consenso, como os demais cadastros.

## Rede e validação

O novo gênesis inclui os parâmetros de lançamento, IDs e oferta dos tokens locais
no manifesto. A rede anterior é incompatível: atualizar os nós e clientes juntos,
usar uma cadeia nova e preservar as chaves e arquivos anteriores. Não há migração
automática de saldos. O teto é 360.000 iniciais + 17.820.000 para validadores =
**18.180.000 POVIX**; os tokens locais são ativos separados desse teto.

A regressão `Tests/PrivateCoinInitialLaunchRegression.cs` exercita emissão única,
compras e assinaturas, locks, catálogo, restauração real da cadeia, criação automática e tokens
USDC/USDT, últimas compras, impossibilidade de fechamento, liquidez adicional,
taxas e contabilização pública. Os testes de fronteira usam uma fixture do estado
antes da última compra e executam o validador real; não mineram 29.950 compras.


## Inicialização dos validadores com garantias automáticas (v28)

Os 600 POVIX da primeira carteira ficam bloqueados até a meta, mas já contam
como garantia de validação e criação de blocos. A primeira compra confirma sem aprovação nem recompensa de bloco para um
validador. Sua taxa descontada sai de circulação por não haver destinatário.

As compras seguintes exigem aprovação e bloco assinado por compradores com
recibos bloqueados já confirmados. Para esses blocos, exclusivamente de compras,
basta um comprador elegível e não participante, mesmo havendo outros offline.
Os 600 iniciais e o staking manual não substituem um recibo de compra nesse regime.
O criador sozinho recebe a recompensa de bloco vigente integral; a taxa da compra
vai ao validador que a aprovou. Com vários validadores, vale a divisão normal.
Assinaturas, saldo USDC e limite de uma compra por identidade continuam obrigatórios.

As demais operações mantêm o conjunto elegível e o mínimo de dois validadores,
sem participação nas operações, conforme as regras de consenso existentes.

A primeira garantia registra o endereço da carteira. O voto ou a aprovação revela
a chave pública, que precisa corresponder ao hash desse endereço e assinar a prova.
Não é possível usar a garantia da primeira carteira com a chave de outro usuário.

Os 600 POVIX e os recebimentos líquidos de compras permanecem bloqueados apesar
das recompensas: remuneração de criação e validação é emitida em saídas separadas.
Ao atingir a meta, os dois tipos de garantia automática são liberados. Não há botão
ou staking manual para ativá-los. Staking manual continua disponível sobre outros
saldos livres; não pode consumir o saldo original bloqueado.

## Garantia automática do comprador (consenso v26)

Após a confirmação da compra inicial de 1 USDC local por 6 POVIX brutos, a saída
líquida (`6 POVIX - taxa`) permanece bloqueada e passa automaticamente a integrar
a garantia do validador identificado pela chave que assinou a compra. Não há
transação manual de staking nem gasto dessa saída para ativar a validação.
O dono precisa manter a carteira e o nó conectados para assinar aprovações e blocos;
continua proibido validar operações das quais participe.

A saída pode respaldar aprovações e votos de blocos, mas não transferências,
trocas ou desbloqueios de staking antes da meta. Recompensas são saídas separadas
e não consomem a garantia comprada. Garantia manual da mesma identidade é somada
sem criar dois validadores. Ao confirmar a compra que atinge a meta, o bloqueio
do lançamento deixa de valer: o saldo comprado fica movimentável e sai da garantia
automática, enquanto eventual staking manual continua ativo.

Ao atingir a meta, termina a elegibilidade automática dos recebimentos e dos
600 POVIX iniciais. Ambos ficam livres, e o staking manual continua ativo.
As negociações seguem o mínimo normal de dois validadores e a mesma tabela
de recompensas vigente por altura, sem reinício da emissão.

A regra atual exige consenso v28. O genesis compromete a versão e os fontes do
Core, portanto a identidade da rede muda. DEX e nós precisam da mesma versão;
esta alteração não migra automaticamente cadeias anteriores. O PR não publica
nem reinicia a rede existente.


## Bloqueio da primeira recompensa (consenso v27)

A saída de 600 POVIX da criação automática da pool agora carrega o mesmo bloqueio
até a meta que os recebimentos de compra. O Desktop mostra ambos no saldo
"Bloqueado no lançamento". A alteração exige consenso v27 e uma identidade de
rede nova, pois o genesis compromete versão e fontes; não migra cadeias v26 e não
reinicia nem publica a rede automaticamente.


## Criação de pools após a meta principal (consenso v29)

Qualquer carteira só pode criar uma pool de liquidez ou de lançamento e adicionar
liquidez depois de a pool principal confirmar 29.950 compras e atingir a reserva
de 179.700 POVIX. Antes disso, a regra também bloqueia tentativas diretas na
blockchain, mesmo antes da criação da pool principal. A criação automática da
pool principal e suas compras continuam permitidas. O DEX desabilita as ações e
informa a condição para liberação. A liquidez principal continua permanente;
provedores adicionais podem remover sua própria participação pelas regras normais.

A mudança exige Core, Desktop e DEX compatíveis com consenso v29 e altera a
identidade da rede comprometida no genesis; não migra o histórico anterior.


## Recompensa ao confirmar registros no lançamento (consenso v30)

Durante a captação da pool principal, um bloco contendo apenas novos registros
de carteira pode ser assinado por um ou mais validadores elegíveis já confirmados,
sem participação nos registros. Com um único criador, ele recebe a recompensa de
bloco vigente integral; com vários, vale a divisão normal. A garantia pode ser
os 600 POVIX iniciais ainda bloqueados, compras confirmadas ou staking elegível.
O Desktop usa esse caminho remunerado e mantém os registros em lotes separados.
A recompensa fica disponível como saída separada, sem consumir a garantia.

Sem validador elegível, o bootstrap continua sem recompensa de validador.
A criação da pool principal e a primeira compra mantêm suas exceções sem
recompensa. Recibos de registro antigo e operações comuns não ganham o mínimo
especial de um validador. Após a meta, registros voltam ao mínimo normal de dois.
Não há pagamento retroativo para blocos já confirmados sem recompensa.

Core, Desktop e DEX precisam da versão v30, que altera o compromisso do genesis
e a identidade da rede. Esta mudança não migra nem reinicia a cadeia publicada.

A recompensa é contabilizada na emissão de validadores já prevista, limitada a
17.820.000 POVIX, além dos 360.000 POVIX iniciais. O teto total permanece
18.180.000 POVIX: cada pagamento aumenta a oferta emitida e reduz o orçamento
não emitido, sem debitar a reserva da pool ou ampliar o limite.


## Garantia inicial nas compras do lançamento (consenso v31)

A carteira dos primeiros 600 POVIX também pode aprovar compras da pool principal
e assinar seus blocos durante a captação, mesmo sendo o único validador disponível.
A seleção aceita os 600 bloqueados e recibos de compras confirmadas, somando as
garantias automáticas por identidade sem duplicar o validador. A assinatura deve
corresponder à chave da garantia. Staking manual não substitui essas garantias
no regime especial de compras.

A primeira compra pode receber aprovação e bloco remunerado quando houver um
validador elegível não participante; sem aprovação, continua disponível o bootstrap
sem recompensa. O Desktop tenta obter a aprovação antes de escolher o caminho.
As compras seguintes continuam exigindo aprovação. O criador sozinho recebe a
recompensa vigente integral; a taxa vai ao aprovador. O pagamento usa a emissão
existente, com teto total inalterado de 18.180.000 POVIX.

Remetentes e destinatários não podem aprovar nem assinar o bloco da própria
compra. As garantias ficam bloqueadas até a meta e deixam de conferir elegibilidade
automática na ativação; a liquidez principal continua permanente. Core, Desktop e
DEX passam a consenso v31, alterando o compromisso do genesis e a identidade da
rede; o PR não migra, reinicia nem publica a cadeia existente.


## Registros aguardam a assinatura do validador (consenso v32)

Durante a captação, havendo garantia confirmada elegível e não participante de
um novo registro, esse registro exige bloco assinado com a recompensa vigente.
Um nó sem a chave dessa garantia deve aguardar a confirmação pelo validador;
a ausência de carteiras locais não autoriza mais confirmar sem recompensa.
O Core aplica a regra também na verificação de cadeias recebidas, impedindo que
um bloco concorrente sem assinatura suprima o pagamento. Misturar operações
com o registro não contorna essa exigência.

O Desktop usa a mesma decisão do Core e mostra que está aguardando o validador.
O bootstrap inicial mantém seu caminho sem recompensa, antes de existir garantia
confirmada, mas deve ocupar um bloco próprio. Registros seguintes que já estejam
na fila aguardam esse bloco para usar a garantia recém-confirmada. A recompensa continua sendo uma por bloco, mesmo se ele registrar
várias carteiras, dentro da tabela e do teto já previstos de 18.180.000 POVIX.
Os 600 iniciais e a reserva principal permanecem preservados. Não há pagamento
retroativo dos blocos anteriores.

A regra exige consenso v32 em nós, Desktop e DEX, com nova identidade no genesis;
não migra nem reinicia a rede existente. É preciso recompilar e distribuir os
componentes atualizados a todos os nós que produzem blocos.
