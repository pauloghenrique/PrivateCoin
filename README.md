# PrivateCoin

`PrivateCoin.Core` contém uma implementação inicial de blockchain UTXO e uma rede P2P TCP.

## Características

- saldo inicial de **1.000.000 PRIVATE**, com 8 casas decimais, creditado à primeira carteira no bloco gênese;
- recompensa inicial de mineração de **18 PRIVATE** para a carteira principal, reduzida pela metade a cada 180.000 blocos;
- recompensa promocional de **18 PRIVATE** ao criar uma carteira, disponível somente antes do primeiro halving;
- blocos ligados por SHA-256 e prova de trabalho;
- transações assinadas com RSA/SHA-256 e validação contra gasto duplo;
- privacidade por endereços descartáveis: a carteira cria uma chave nova para cada recebimento, portanto não existe um endereço público permanente no blockchain;
- propagação P2P de transações e blockchains, com enquadramento, limite de tamanho, deduplicação e retransmissão (gossip);
- sincronização ao conectar, usando a cadeia válida mais longa e um desempate determinístico pela hash do bloco mais recente.

## Uso básico

```csharp
using (var alice = new Wallet())
using (var bob = new Wallet())
{
    var chain = new Blockchain(alice.CreateReceiveAddress());
    var payment = alice.CreateTransaction(chain, bob.CreateReceiveAddress(), 25 * Blockchain.OneCoin);
    chain.AddBlock(new[] { payment }, alice.CreateReceiveAddress());
}
```

O endereço retornado por `CreateReceiveAddress` deve ser entregue diretamente ao pagador e usado uma só vez. A cadeia registra somente esse identificador descartável. O arquivo de chaves de uma aplicação deve ser cifrado e protegido; a classe `Wallet` mantém as chaves apenas em memória nesta versão.

Para a rede, crie um `PeerNode`, assine os eventos de transação e cadeia, chame `Start()` e conecte aos pares conhecidos com `ConnectAsync`. Uma aplicação deve validar transações recebidas e adotar somente cadeias aceitas por `Blockchain.TryReplaceChain`.

## Aplicação Desktop

`PrivateCoin.Desktop` oferece um painel WinForms para criar e alternar entre várias carteiras, consultar o saldo de cada uma, gerar endereços de recebimento e fazer transferências. Durante o desenvolvimento, os dados persistidos ficam separados em dois arquivos dentro da própria pasta `PrivateCoin.Desktop`. Em uma versão publicada sem o arquivo do projeto, eles ficam ao lado do executável:

- `Blockchain.json` contém a blockchain completa, sem chaves privadas, em um envelope Base64 acompanhado pelo hash SHA-256 dos dados. Ao abrir o arquivo, o aplicativo confere o hash e rejeita conteúdo alterado antes de validar a cadeia. O arquivo pode ser distribuído para sincronizar os dados públicos da rede;
- `wallets.dat` contém apenas os nomes e as chaves privadas das carteiras locais, cifrados para o usuário atual do Windows por DPAPI, e **não deve ser distribuído**.

Instalações que ainda possuam o antigo `wallets.dat` combinado são migradas automaticamente para esse formato na primeira abertura.

SHA-256 é uma função de hash de mão única, e não uma criptografia reversível. Por isso, ele é usado para verificar a integridade de `Blockchain.json`; os dados públicos continuam recuperáveis pelo aplicativo. As chaves privadas permanecem efetivamente cifradas por DPAPI somente em `wallets.dat`.

O painel também permite iniciar um nó TCP, conectar a outro par, sincronizar a cadeia, assinar e propagar transações e acompanhar cada aprovação ou rejeição feita pela validação da blockchain. Antes do primeiro halving, cada nova carteira recebe a recompensa inicial de 18 PRIVATE em um bloco que é salvo e propagado automaticamente; a partir do bloco em que a recompensa é reduzida, novas carteiras são criadas sem esse bônus. Uma transferência validada altera imediatamente o **saldo disponível** das carteiras e seus tokens já podem ser usados em outra transferência, mesmo enquanto ela aguarda confirmação. As transações válidas ficam na fila até que seu conteúdo serializado atinja **2 MiB** (2 × 1024 × 1024 bytes); somente nesse momento a mineração começa automaticamente, sem exigir um botão, para confirmar o lote em um novo bloco e propagá-lo aos pares conectados. Cada bloco minerado credita a recompensa à carteira principal; após cada intervalo de 180.000 blocos, a recompensa é reduzida pela metade. Para transferir entre carteiras locais, selecione a destinatária, copie o endereço exibido em **Receber em**, volte à carteira pagadora e informe esse endereço como destino. Para testar a rede localmente, abra duas instâncias em portas diferentes e conecte uma à outra pelo endereço `127.0.0.1`; as duas instâncias convergirão para a mesma cadeia válida.

### Descoberta automática de nós

Assim como o Bitcoin, cada instalação precisa conhecer pelo menos um nó inicial (bootstrap) para entrar pela primeira vez na rede. Configure um ou mais nós públicos em `App.config`, separados por vírgula:

```xml
<add key="PeerSeeds" value="seed.exemplo.com:4777,203.0.113.10:4777" />
```

Também é possível definir os seeds sem alterar o arquivo por meio da variável de ambiente `PRIVATECOIN_PEERS`. Ao abrir, o Desktop inicia o nó automaticamente, conecta aos seeds, troca sua lista de endereços com eles, tenta conexões adicionais periodicamente e solicita a sincronização da blockchain em cada nova conexão. `AutoStartNode` e `ListenPort` controlam o início automático e a porta local.

Pelo menos um seed precisa estar permanentemente online, ter IP/DNS público e encaminhar a porta TCP configurada para o computador que executa o nó. Nós atrás de CGNAT continuam precisando de IPv4 público, encaminhamento de porta, VPN ou relay; descoberta de pares não atravessa NAT por si só.

> Esta é uma base técnica, não software pronto para custodiar dinheiro real. Uma rede de produção também precisa de descoberta autenticada de pares, protocolo de consenso/forks, proteção contra Sybil/DoS, auditoria criptográfica e backups seguros.
