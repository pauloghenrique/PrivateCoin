# Povix.Dex — cadastro de tokens nativos

A página inicial abre `/Tokens/Create`. O cadastro recebe nome, símbolo (A–Z,
até 10 caracteres), casas decimais (0–8), oferta em unidades atômicas, endereço
destinatário e JSON de uma transação assinada. O servidor confere o formulário
contra o conteúdo assinado e usa `Blockchain.ValidatePendingTransactions`
antes de transmitir com `PeerNode.BroadcastAsync`.

A aplicação participa da mesma rede do PrivateCoin.Core, com o mesmo genesis e
consenso. Não cria blocos, recompensas ou uma blockchain separada. A criação
só é confirmada quando um bloco dos validadores contém a transação. A página
`/Tokens/Status/{id}` consulta a cadeia sincronizada a cada 10 segundos.

## Assinatura na carteira

A assinatura é feita localmente, por uma carteira que já possua POVIX para a
taxa e uma cópia sincronizada da blockchain. A tela não importa a carteira nem
assina transações; a interface atual exige o JSON assinado. Não enviar chaves
privadas ou frase de recuperação ao servidor.

Exemplo na aplicação da carteira, com `wallet`, `chain` e `pending` existentes:

```csharp
string destination = wallet.CreateReceiveAddress();
var tx = wallet.CreateTokenTransaction(chain, pending,
    "Meu Token", "MTK", 2, 100000, destination,
    Blockchain.CalculateAutomaticFee(pending.Count(), 1));
// Salvar a carteira pelo mecanismo local existente, incluindo os novos
// endereços destinatário e de troco, ANTES de exportar/enviar a transação.
using (var stream = new System.IO.MemoryStream())
{
    new System.Runtime.Serialization.Json.DataContractJsonSerializer(
        typeof(Transaction)).WriteObject(stream, tx);
    string json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
    // Exportar apenas json e preencher a tela com os mesmos dados de tx.Token.
}
```

100000 unidades atômicas com 2 casas representam 1000 tokens. As quantidades
usam `long`; não há conversão por ponto flutuante.

## Execução

Compilar a solução no Visual Studio com .NET Framework 4.8 e restaurar os
pacotes NuGet de `packages.config`. Hospedar no IIS/IIS Express. O projeto
referencia diretamente `PrivateCoin.Core`.

`Web.config` define `PeerSeeds` (mesmo seed do site existente) e `ListenPort`
4780, diferente das portas do desktop e do explorer. Configurar conexão TCP
com os peers e permissão de escrita em `App_Data` para o cache de peers.
O envio é bloqueado até a sincronização e a conexão com pelo menos um peer.

A fila e a cadeia sincronizada deste serviço ficam em memória. Após reinício,
o nó ressincroniza; uma transação ainda não confirmada pode ser reenviada
com o mesmo JSON/ID. Reenvios em uma mesma execução não duplicam a reserva
dos inputs. Confirmações e reorgs são consultados na cadeia atual. A aplicação
não garante inclusão em bloco nem entrega ao peer apenas pelo retorno do envio.

## Testes

Da raiz do repositório, com Mono:

```sh
mkdir -p work/dex-tokens
mcs -out:work/dex-tokens/DexTokenRegression.exe \
  -r:System.Core -r:System.Numerics -r:System.Runtime.Serialization \
  -r:System.Net.Http -r:System.Xml.Linq -r:System.Web -r:System.Web.Mvc \
  -r:System.Configuration -r:System.ComponentModel.DataAnnotations \
  PrivateCoin.Core/*.cs DEXPovix/Models/*.cs DEXPovix/Services/*.cs \
  Tests/DexTokenRegression.cs
mono work/dex-tokens/DexTokenRegression.exe
```

Os testes usam uma cadeia descartável: serialização assinada, divergência de
cadastro, JSON inválido e excessivo, assinatura alterada, estado pendente e
registro depois de confirmação pelos validadores. Não enviam à rede pública.
