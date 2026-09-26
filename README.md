# PrivateCoin

`PrivateCoin.Core` contém uma implementação inicial de blockchain UTXO e uma rede P2P TCP.

## Características

- bloco gênese sem tokens pré-criados;
- os primeiros **180.000 PRIVATE** são distribuídos em parcelas de **6 PRIVATE** às novas carteiras (até 30.000 carteiras);
- cada parcela é enviada a um endereço descartável gerado aleatoriamente e registrada imediatamente em um novo bloco;
- blocos de confirmação não criam tokens nem pagam recompensa de mineração;
- blocos ligados por SHA-256 e prova de trabalho;
- política de emissão em quatro fases de 2 milhões de blocos e seleção determinística de validadores ponderada pelas moedas bloqueadas;
- transações assinadas com RSA/SHA-256 e validação contra gasto duplo;
- privacidade por endereços descartáveis: a carteira cria uma chave nova para cada recebimento, portanto não existe um endereço público permanente no blockchain;
- propagação P2P de transações e blockchains, com enquadramento, limite de tamanho, deduplicação e retransmissão (gossip);
- sincronização ao conectar, usando a cadeia válida mais longa e um desempate determinístico pela hash do bloco mais recente.

## Uso básico

```csharp
using (var alice = new Wallet())
using (var bob = new Wallet())
{
    var chain = new Blockchain();
    Block rewardBlock;
    chain.TryAddWalletCreationReward(alice.CreateReceiveAddress(), out rewardBlock);
    var payment = alice.CreateTransaction(chain, bob.CreateReceiveAddress(), 2 * Blockchain.OneCoin);
    chain.AddBlock(new[] { payment });
}
```

O endereço retornado por `CreateReceiveAddress` deve ser entregue diretamente ao pagador e usado uma só vez. A cadeia registra somente esse identificador descartável. O arquivo de chaves de uma aplicação deve ser cifrado e protegido; a classe `Wallet` mantém as chaves apenas em memória nesta versão.

Para a rede, crie um `PeerNode`, assine os eventos de transação e cadeia, chame `Start()` e conecte aos pares conhecidos com `ConnectAsync`. Uma aplicação deve validar transações recebidas e adotar somente cadeias aceitas por `Blockchain.TryReplaceChain`.

## Aplicação Desktop

`PrivateCoin.Desktop` oferece um painel WinForms para criar e alternar entre várias carteiras, consultar o saldo de cada uma, gerar endereços de recebimento e fazer transferências. Durante o desenvolvimento, os dados persistidos ficam separados em dois arquivos dentro da própria pasta `PrivateCoin.Desktop`. Em uma versão publicada sem o arquivo do projeto, eles ficam ao lado do executável:

- `Blockchain.json` contém a blockchain completa e a fila de transações pendentes, sem chaves privadas, em um envelope Base64 acompanhado pelo hash SHA-256 dos dados. Cada transferência validada é gravada imediatamente nesse arquivo, mesmo antes de completar os 2 MiB necessários para mineração. Ao abrir o arquivo, o aplicativo confere o hash, restaura e valida também as pendências e rejeita conteúdo alterado antes de validar a cadeia. O arquivo pode ser distribuído para sincronizar os dados públicos da rede;
- `wallets.dat` contém os nomes, as chaves privadas e as garantias de validador das carteiras locais, cifrados para o usuário atual do Windows por DPAPI, e **não deve ser distribuído**.

Instalações que ainda possuam o antigo `wallets.dat` combinado são migradas automaticamente para esse formato na primeira abertura.

SHA-256 é uma função de hash de mão única, e não uma criptografia reversível. Por isso, ele é usado para verificar a integridade de `Blockchain.json`; os dados públicos continuam recuperáveis pelo aplicativo. As chaves privadas permanecem efetivamente cifradas por DPAPI somente em `wallets.dat`.

O painel também permite iniciar um nó TCP, conectar a outro par, sincronizar a cadeia, assinar e propagar transações e acompanhar cada aprovação ou rejeição feita pela validação da blockchain. Na seção **Validador**, informe a quantidade de PRIVATE e use **Bloquear e ativar** para reservar a garantia da carteira selecionada; o painel passa a exibir a quantia bloqueada, impede que ela seja transferida e restaura a ativação nas próximas execuções. A blockchain nasce sem saldo no bloco gênese. Cada nova carteira recebe 6 PRIVATE em um endereço descartável gerado aleatoriamente, e a distribuição cria, salva e propaga imediatamente um novo bloco. A emissão termina quando o limite total de 180.000 PRIVATE for alcançado, depois de 30.000 carteiras recompensadas. Uma transferência validada altera imediatamente o **saldo disponível** das carteiras e seus tokens já podem ser usados em outra transferência, mesmo enquanto ela aguarda confirmação. As transações válidas ficam na fila até que seu conteúdo serializado atinja **2 MiB** (2 × 1024 × 1024 bytes); somente nesse momento a mineração começa automaticamente, sem exigir um botão, para confirmar o lote em um novo bloco e propagá-lo aos pares conectados. Esses blocos de confirmação não emitem tokens adicionais. Para transferir entre carteiras locais, selecione a destinatária, copie o endereço exibido em **Receber em**, volte à carteira pagadora e informe esse endereço como destino. Para testar a rede localmente, abra duas instâncias em portas diferentes e conecte uma à outra pelo endereço `127.0.0.1`; as duas instâncias convergirão para a mesma cadeia válida.

### Descoberta automática de nós

Assim como o Bitcoin, cada instalação precisa conhecer pelo menos um nó inicial (bootstrap) para entrar pela primeira vez na rede. Configure um ou mais nós públicos em `App.config`, separados por vírgula:

```xml
<add key="PeerSeeds" value="seed.exemplo.com:4777,203.0.113.10:4777" />
```

Também é possível definir os seeds sem alterar o arquivo por meio da variável de ambiente `PRIVATECOIN_PEERS`. Ao abrir, o Desktop inicia o nó automaticamente, conecta aos seeds, troca sua lista de endereços com eles, tenta conexões adicionais periodicamente e solicita a sincronização da blockchain em cada nova conexão. `AutoStartNode` e `ListenPort` controlam o início automático e a porta local.

Pelo menos um seed precisa estar permanentemente online, ter IP/DNS público e encaminhar a porta TCP configurada para o computador que executa o nó. Nós atrás de CGNAT continuam precisando de IPv4 público, encaminhamento de porta, VPN ou relay; descoberta de pares não atravessa NAT por si só.

> Esta é uma base técnica, não software pronto para custodiar dinheiro real. Uma rede de produção também precisa de descoberta autenticada de pares, protocolo de consenso/forks, proteção contra Sybil/DoS, auditoria criptográfica e backups seguros.

## Emissão e validadores

A emissão destinada ao consenso dura 8 milhões de blocos (aproximadamente 20 anos, considerando 2 milhões de blocos a cada cinco anos) e totaliza **17.820.000 PRIVATE**:

| Fase | Alturas | Recompensa por bloco | Total da fase |
| --- | ---: | ---: | ---: |
| 1 | 1–2.000.000 | 3,61 PRIVATE | 7.220.000 PRIVATE |
| 2 | 2.000.001–4.000.000 | 2,80 PRIVATE | 5.600.000 PRIVATE |
| 3 | 4.000.001–6.000.000 | 1,50 PRIVATE | 3.000.000 PRIVATE |
| 4 | 6.000.001–8.000.000 | 1,00 PRIVATE | 2.000.000 PRIVATE |

`ProofOfStake` representa as regras determinísticas do consenso. Os participantes informam as moedas bloqueadas em garantia por meio de `ValidatorStake`; `SelectCreator` escolhe o criador de forma ponderada pelo valor bloqueado, usando a hash anterior e a altura como semente. Em cada nova altura o sorteio é refeito, portanto um participante pode criar um bloco em uma rodada e confirmar outro bloco em uma rodada posterior.

`DistributeReward` reserva **30%** da recompensa ao criador escolhido e distribui os **70%** restantes entre os validadores que confirmaram corretamente, proporcionalmente às garantias bloqueadas. O arredondamento da menor unidade é determinístico e a soma das parcelas é sempre exatamente a recompensa prevista para a altura. O criador não pode confirmar o próprio bloco.

Essas rotinas são a política de consenso que deverá ser usada pelo protocolo de votação. Antes de uso em produção, o bloqueio/desbloqueio da garantia, as assinaturas dos votos, quórum, penalidades (*slashing*) e mensagens P2P de proposta/confirmação ainda precisam ser persistidos e validados na cadeia.
