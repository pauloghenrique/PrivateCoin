# Lançamento inicial POVIX — consenso v22

Implementação no Core: `InitialLaunch`, `TokenExchange`, `Blockchain` e `Wallet`.
A criação é automática na confirmação da 1ª carteira recompensada; a compra está disponível pelas APIs assinadas do Core. A carteira
Desktop apresenta o saldo comprado como bloqueado; o DEX descobre a pool pela
cadeia e permite trocas e liquidez adicional depois da abertura. O formulário
DEX de lançamentos genéricos continua separado: não cria o lançamento nativo.

## Emissão e ativos locais

| Destino | Quantidade |
| --- | ---: |
| Primeira identidade registrada | 600 POVIX (recompensa única) |
| Reserva na criação única do lançamento | 359.400 POVIX |
| Total inicial POVIX | 360.000 POVIX |
| USDC local, para a 1ª carteira recompensada | 1.000.000 USDC |
| USDT local, para a 1ª carteira recompensada | 1.000.000 USDT |

Não há emissão no bloco zero. A confirmação da 1ª carteira emite seus 600 POVIX,
completa a distribuição inicial de 600 POVIX e cria a pool na mesma operação, sem taxa ou ação manual.
Ela emite os tokens locais uma única vez para a 1ª carteira e deposita os 359.400
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
6 POVIX aparecem como um UTXO do comprador, bloqueado pelo consenso.
A taxa de rede é paga à parte em POVIX livres; os 6 comprados não podem pagá-la.

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


## Inicialização dos validadores com uma única recompensa

As carteiras posteriores registram suas identidades com saldo inicial zero.
A primeira carteira pode ceder garantia a dois validadores independentes usando
`CreateSponsoredStakeLockTransaction(chain, pending, validatorPublicKey, amount)`.
Essa operação de garantia existente é auto-validada e não emite moedas novas:
o valor sai dos 600 POVIX do titular e fica sob a chave do validador destinatário.
Depois das garantias confirmadas, os validadores podem aprovar as transferências
normais do titular para os demais participantes. A aprovação de transferências
não é dispensada para inicializar a rede.
