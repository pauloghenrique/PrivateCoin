# PrivateCoin

`PrivateCoin.Core` contém uma implementação inicial de blockchain UTXO e uma rede P2P TCP.

## Características

- bloco gênese sem emissão e recompensa promocional de **6 PRIVATE** por nova carteira, limitada a 180.000 PRIVATE;
- recompensa proof-of-stake em quatro fases, paga ao criador e aos confirmadores de cada bloco;
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
    var payment = alice.CreateTransaction(chain, bob.CreateReceiveAddress(), 5 * Blockchain.OneCoin);
    chain.AddBlock(new[] { payment });
}
```

O endereço retornado por `CreateReceiveAddress` deve ser entregue diretamente ao pagador e usado uma só vez. A cadeia registra somente esse identificador descartável. O arquivo de chaves de uma aplicação deve ser cifrado e protegido; a classe `Wallet` mantém as chaves apenas em memória nesta versão.

Para a rede, crie um `PeerNode`, assine os eventos de transação e cadeia, chame `Start()` e conecte aos pares conhecidos com `ConnectAsync`. Uma aplicação deve validar transações recebidas e adotar somente cadeias aceitas por `Blockchain.TryReplaceChain`.

## Aplicação Desktop

`PrivateCoin.Desktop` oferece um painel WinForms para criar e alternar entre várias carteiras, consultar o saldo de cada uma, gerar endereços de recebimento e fazer transferências. Ao criar uma carteira, o painel exibe uma única vez uma frase de recuperação formada por 12 palavras em inglês; anote as palavras na ordem apresentada e mantenha-as em segurança. Durante o desenvolvimento, os dados persistidos ficam separados em dois arquivos dentro da própria pasta `PrivateCoin.Desktop`. Em uma versão publicada sem o arquivo do projeto, eles ficam ao lado do executável:

- `Blockchain.json` contém a blockchain completa e a fila de transações pendentes, sem chaves privadas, em um envelope Base64 acompanhado pelo hash SHA-256 dos dados. Cada transferência validada é gravada imediatamente nesse arquivo enquanto aguarda a criação do bloco. Ao abrir o arquivo, o aplicativo confere o hash, restaura e valida também as pendências e rejeita conteúdo alterado antes de validar a cadeia. O arquivo pode ser distribuído para sincronizar os dados públicos da rede;
- `wallets.dat` contém os nomes, as chaves privadas e as garantias de validador das carteiras locais, cifrados para o usuário atual do Windows por DPAPI, e **não deve ser distribuído**.
- Para carteiras novas, as chaves são derivadas deterministicamente da frase de 12 palavras. Use **Recuperar com frase** em outra instalação, sem precisar copiar `wallets.dat` ou qualquer outro arquivo local; depois de sincronizar a blockchain, o aplicativo procura os endereços já utilizados. A reconstrução criptográfica é executada em segundo plano e pode levar alguns instantes, sem travar a janela. Apagar somente `wallets.dat` não altera a blockchain nem gera outra recompensa de carteira. O antigo `recovery.dat`, quando presente, continua aceito apenas para recuperar carteiras criadas por versões anteriores.

Instalações que ainda possuam o antigo `wallets.dat` combinado são migradas automaticamente para esse formato na primeira abertura.

SHA-256 é uma função de hash de mão única, e não uma criptografia reversível. Por isso, ele é usado para verificar a integridade de `Blockchain.json`; os dados públicos continuam recuperáveis pelo aplicativo. As chaves privadas permanecem efetivamente cifradas por DPAPI somente em `wallets.dat`.

O painel também permite iniciar um nó TCP, conectar a outro par, sincronizar a cadeia, assinar e propagar transações e acompanhar cada aprovação ou rejeição feita pela validação da blockchain. Na seção **Validador**, informe a quantidade de PRIVATE e use **Bloquear e ativar** para reservar a garantia da carteira selecionada; o painel passa a exibir a quantia bloqueada, impede que ela seja transferida e restaura a ativação nas próximas execuções. A blockchain nasce sem saldo no bloco gênese. Cada nova carteira recebe 6 PRIVATE em um endereço descartável gerado aleatoriamente, e a distribuição cria, salva e propaga imediatamente um novo bloco. A emissão termina quando o limite total de 180.000 PRIVATE for alcançado, depois de 30.000 carteiras recompensadas. Uma transferência validada altera imediatamente o **saldo disponível** e aguarda pelo menos dois validadores ativos. O sistema escolhe um deles para criar o bloco, os demais o confirmam e todos recebem sua parcela da recompensa; o novo bloco é salvo e propagado aos pares conectados. Para transferir entre carteiras locais, selecione a destinatária, copie o endereço exibido em **Receber em**, volte à carteira pagadora e informe esse endereço como destino. Para testar a rede localmente, abra duas instâncias em portas diferentes e conecte uma à outra pelo endereço `127.0.0.1`; as duas instâncias convergirão para a mesma cadeia válida.

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

`DistributeReward` reserva **30%** da recompensa ao criador escolhido e distribui os **70%** restantes entre os validadores que confirmaram corretamente, proporcionalmente às garantias bloqueadas. O arredondamento da menor unidade é determinístico e a soma das parcelas é sempre exatamente a recompensa prevista para a altura. O criador não pode confirmar o próprio bloco. Além disso, as carteiras que enviam ou recebem qualquer transferência do bloco são excluídas tanto da criação quanto da confirmação desse bloco; seus endereços públicos são incluídos na prova para que essa regra seja revalidada ao carregar ou sincronizar a cadeia.

A propriedade `RewardPhases` expõe as quatro fases (limites, recompensa unitária e total) para que carteiras e exploradores possam apresentar a política sem duplicar números. `ScheduledIssuance` calcula o total diretamente dessas fases e permite verificar programaticamente que a emissão prevista é exatamente **17.820.000 PRIVATE**.

Ao existir uma transferência pendente e pelo menos dois validadores locais ativos, o Desktop seleciona o criador, usa os demais validadores como confirmadores, cria o bloco e inclui nele a transação que paga as parcelas de 30%/70%. A prova contém os validadores, garantias, endereços de recompensa e o papel do criador; ela faz parte da hash e é revalidada ao carregar ou sincronizar a cadeia. As recompensas tornam-se UTXOs das carteiras e aparecem no saldo.

Antes de uso em produção, o bloqueio das garantias e as assinaturas individuais dos votos ainda devem ser transformados em transações de consenso globais. Nesta versão, a lista de validadores ativos é formada pelas carteiras da instalação que cria o bloco.
