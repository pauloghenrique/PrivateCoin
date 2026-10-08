# Regressão do cadastro de tokens

Compile `Povix.Dex` e `PrivateCoin.Core` em Release, após restaurar os pacotes
NuGet. A regressão exige Node.js 20+ com Web Crypto e .NET Framework 4.8 (ou Mono).
Ela cria carteiras descartáveis, usa dois nós TCP locais e grava apenas dados
de teste em `work/dex-regression`; não usa a carteira nem os peers de produção.

Exemplo com Mono instalado:

```sh
mkdir -p work/dex-regression
mcs -r:Povix.Dex/bin/Povix.Dex.dll -r:Povix.Dex/bin/PrivateCoin.Core.dll \
  -r:System.Web.Extensions -r:System.Web -r:System.Core \
  -r:System.ComponentModel.DataAnnotations -out:work/PovixDexRegression.exe \
  Tests/PovixDexRegression.cs
MONO_PATH=Povix.Dex/bin mono work/PovixDexRegression.exe \
  work/dex-regression "$(command -v node)"
```

Use um diretório de teste vazio a cada execução. A regressão verifica conversão
exata até `Int64.MaxValue`, carteira cifrada interoperável com .NET, rejeição de
backup alterado, revisão do payload completo, assinatura Web Crypto aceita pelo
Core, sessão, rejeição de assinatura inválida, reserva de UTXOs, propagação P2P,
repetição idempotente, reinício, confirmação proof-of-stake e preservação das
chaves de endereços gerados no navegador.

Para verificar abertura do arquivo e seleção entre carteiras, compile também
`Povix.WalletBridge` em Release e execute:

```sh
mcs -r:Povix.WalletBridge/bin/Release/Povix.WalletBridge.exe \
  -r:Povix.Dex/bin/PrivateCoin.Core.dll -r:System.Web.Extensions -r:System.Core \
  -out:work/PovixWalletFileRegression.exe Tests/PovixWalletFileRegression.cs
MONO_PATH=Povix.WalletBridge/bin/Release:Povix.Dex/bin mono \
  work/PovixWalletFileRegression.exe work/wallet-files-regression "$(command -v node)"
```

Essa regressão usa uma coleção descartável com duas carteiras. Verifica os
nomes, a seleção explícita e o isolamento das chaves, a preservação das demais
carteiras no backup, a comunicação real com o auxiliar via loopback e a
rejeição de origens/Host não autorizados. A abertura DPAPI é substituída somente
nesse teste em Linux; a leitura real deve ser verificada no Windows, com o
mesmo usuário que possui o arquivo.

Para verificar o início do auxiliar sem argumentos (como F5 ou duplo clique),
o início com `--origin` e os erros de endereço/porta, após compilar o auxiliar:

```sh
mcs -r:Povix.WalletBridge/bin/Release/Povix.WalletBridge.exe -r:System.Core \
  -main:PovixWalletStartupRegression -out:work/PovixWalletStartupRegression.exe \
  Tests/PovixWalletStartupRegression.cs Povix.WalletBridge/Program.cs
MONO_PATH=Povix.WalletBridge/bin/Release mono work/PovixWalletStartupRegression.exe
```

Este teste inicia o servidor real de loopback e exige a porta local 4781 livre.
Ele exercita o fluxo de início diretamente; a proteção de plataforma do
executável continua exigindo Windows. A depuração F5 no Visual Studio e a
abertura DPAPI real precisam ser verificadas no Windows.

Para verificar os tokens exibidos pelo Desktop, compile `PrivateCoin.Desktop`
em Release e execute:

```sh
mcs -r:PrivateCoin.Desktop/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -out:work/PrivateCoinDesktopTokenRegression.exe \
  Tests/PrivateCoinDesktopTokenRegression.cs PrivateCoin.Desktop/TokenAmount.cs
MONO_PATH=PrivateCoin.Desktop/bin/Release mono \
  work/PrivateCoinDesktopTokenRegression.exe work/desktop-tokens-regression
```

A regressão cria tokens com a mesma preparação/assinatura nativa usada pelo
DEX, confirma blocos com validadores descartáveis e sincroniza dois nós reais
de loopback. Confere metadados e saldo por carteira, precisão até `Int64.MaxValue`,
distinção por identificador entre símbolos iguais, confirmação de transferências
e atualização após troca de cadeia. Criações e transferências pendentes ficam
fora dos registros/saldos confirmados. Os dados de produção não são acessados.
A interface WinForms ainda deve ser verificada visualmente no Windows.

Para verificar o bloqueio durante reconexão, compile o Core e execute:

```sh
mcs -r:PrivateCoin.Desktop/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -out:work/PrivateCoinReconnectRegression.exe \
  Tests/PrivateCoinReconnectRegression.cs PrivateCoin.Desktop/NetworkReadiness.cs
MONO_PATH=PrivateCoin.Desktop/bin/Release mono work/PrivateCoinReconnectRegression.exe
```

Verifica o bloqueio inicial e offline, a sincronização com cadeia idêntica,
a invalidação de respostas antigas, a adoção de uma cadeia maior após
reconexão e a rejeição de cadeia inválida. Não depende de WinForms nem de
carteiras de produção.

### Certificados de finalização

```sh
mcs -r:PrivateCoin.Desktop/bin/Release/PrivateCoin.Core.dll \
  -r:System.Core -r:System.Runtime.Serialization \
  -out:work/PrivateCoinFinalityRegression.exe Tests/PrivateCoinFinalityRegression.cs
MONO_PATH=PrivateCoin.Desktop/bin/Release mono \
  work/PrivateCoinFinalityRegression.exe work/finality-regression --p2p
```

Use um diretório vazio. O teste cobre quórum estritamente superior a 2/3,
stakes ponderados, mínimo de duas chaves, rejeição de duplicação, falsificação
e replay, certificados conflitantes, persistência após reinício, corrupção
do diário, proposta de um criador com validadores distribuídos, preferência
pela cadeia menor finalizada e rejeição da cadeia maior não certificada.
`--p2p` acrescenta troca de votos entre nós TCP locais, incompatibilidade da
política no handshake e reconexão de um terceiro nó com uma cadeia isolada maior.
O arquivo `bootstrap-public.json` gerado serve apenas como fixture descartável
para testar o utilitário de checkpoint; não é um checkpoint de produção.

Esses testes não demonstram disponibilidade sob partições, rodadas BFT ou
segurança de produção. As limitações do protocolo estão no README principal.
