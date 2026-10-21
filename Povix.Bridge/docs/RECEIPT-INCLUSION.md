# Inspeção de inclusão de recibos — etapa experimental

Este código NÃO implementa a ponte sem operadores. Não altera consenso, cofre, emissão ou resgate. O projeto continua exigindo quorum. Os verificadores de consenso BNB/POVIX descritos em [PROOF-BRIDGE.md](PROOF-BRIDGE.md) ainda não existem.

O comando C# offline verifica se um recibo está incluído numa raiz Merkle Patricia fornecida e interpreta um evento `Deposited` do cofre selecionado:

```text
Povix.Bridge.exe inspect-receipt-proof prova.json
```

Não consulta RPC, não precisa de Node.js e não envia transações. Código de saída zero significa apenas que a inclusão e a estrutura do evento correspondem aos parâmetros fornecidos. Um atacante pode fabricar um recibo e sua própria raiz: por isso a resposta SEMPRE contém `consensusVerified: false` e `usableForMint: false`. Nunca use esse comando como autorização de emissão ou de saque.

## Formato de entrada

Campos obrigatórios do objeto JSON:

| Campo | Conteúdo |
|---|---|
| `format` | `povix-receipt-inclusion-v1` |
| `receiptsRoot` | Raiz de 32 bytes, fornecida pelo chamador, ainda não autenticada |
| `transactionIndex` | Índice inteiro da transação no bloco, de 0 até Int32.MaxValue |
| `receipt` | Bytes exatos do recibo: RLP legado ou envelope tipo 1/2 |
| `proof` | Array ordenado de nós RLP, da raiz à folha, incluindo apenas nós referenciados por hash; filhos inline permanecem dentro do nó pai |
| `receiptLogIndex` | Índice local do evento dentro do recibo; NÃO é o logIndex global retornado por eth_getTransactionReceipt |
| `vault` | Endereço do cofre esperado, de 20 bytes; vínculo de custódia não autenticado por este comando |
| `assetDecimals` | Precisão declarada do ativo de origem, entre 8 e 18 para o pUSDT de oito casas |

Todos os bytes usam strings hex minúsculas com prefixo `0x`. Índices/precisão são números JSON inteiros. Não são aceitas propriedades JSON duplicadas. Tipos de recibo diferentes de legado/1/2 e recibos antigos baseados em stateRoot são recusados.

A chave do trie é RLP(transactionIndex), sem Keccak da chave. O verificador confere referências hash/inline, caminhos hex-prefix, valor exato do recibo, status de sucesso, formato do bloom/gas/logs, emissor, tópico `Deposited(bytes32,address,bytes32,uint256,uint256)`, identidades não nulas e escala exata. Valores são processados sem ponto flutuante; nativeAmount deve caber em Int64 positivo. O evento isolado não prova a identidade do token, implantação legítima do cofre ou estado de reserva.

Limites: arquivo JSON de 2 MiB, até 64 nós, 64 KiB por nó, 512 KiB totais de nós, recibo de 256 KiB, profundidade RLP de 16 e orçamento de 16.384 itens por decodificação. Nós extras, codificação não canônica e provas de ausência são recusados.

## Testes

Depois do build Release e da instalação das dependências de desenvolvimento:

```text
npm run test:receipt-proof
```

EthereumJS Trie 6.2.1 gera e verifica as provas independentemente do C#. Os testes cobrem tipos de recibo, fronteiras RLP dos índices, alteração de raiz/nó/caminho/evento/valor, entradas não canônicas e limites. Node.js é usado somente nessa infraestrutura de testes, nunca pelo cliente C#.

## Trabalho pendente para a mudança solicitada

1. Autenticar cabeçalhos, validadores e finalidade BNB dentro do consenso POVIX.
2. Definir e implementar a prova de consenso/validade POVIX verificável na BNB, incluindo tratamento de reorganizações e custo em gas.
3. Conectar provas autenticadas a claims e emissão atômica, e a um novo cofre BNB com consumo único de queimas.
4. Implementar relay C#, lançamento/migração coordenados e testes ponta a ponta de redes concorrentes e provas inválidas.

Nenhuma dessas etapas é substituída por consultar RPCs, por aceitar uma raiz informada pelo relay, por uma prova de inclusão isolada ou por adicionar um novo comitê de checkpoints.
