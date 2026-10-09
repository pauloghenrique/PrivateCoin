# Regressão do cadastro de tokens

Compile `Povix.Dex` e `PrivateCoin.Core` em Release, após restaurar os pacotes
NuGet. A regressão exige Node.js 20+ com Web Crypto e .NET Framework 4.8 (ou Mono).
Ela cria carteiras descartáveis, usa dois nós TCP locais e grava apenas dados
de teste em `work/dex-regression`; não usa a carteira nem os peers de produção.

Exemplo com Mono instalado:

```sh
mkdir -p work/dex-regression
mcs -r:Povix.Dex/bin/Povix.Dex.dll -r:Povix.Dex/bin/PrivateCoin.Core.dll \
  -r:Povix.Dex/bin/System.Web.Mvc.dll \
  -r:System.Web.Extensions -r:System.Web -r:System.Core \
  -r:System.ComponentModel.DataAnnotations -out:work/PovixDexRegression.exe \
  Tests/PovixDexRegression.cs Tests/FinalityTestSupport.cs
MONO_PATH=Povix.Dex/bin mono work/PovixDexRegression.exe \
  work/dex-regression "$(command -v node)"
```

Use um diretório de teste vazio a cada execução. A regressão verifica conversão
exata até `Int64.MaxValue`, carteira cifrada interoperável com .NET, rejeição de
backup alterado, revisão do payload completo, assinatura Web Crypto aceita pelo
Core, sessão, rejeição de assinatura inválida, reserva de UTXOs, propagação P2P,
repetição idempotente, reinício, confirmação proof-of-stake e preservação das
chaves de endereços gerados no navegador.

A regressão do formulário executa o script real de criação com a carteira
Web Crypto e respostas HTTP de teste: a preparação deve escolher um endereço
da carteira importada sem adicionar uma chave para o troco, exibir seu valor
exato na revisão e distinguir saldo reservado de saldo disponível. Na rede
local real, também confere os valores retornados pela ação MVC de saldo e
pelo comprovante. Após a confirmação, os mesmos endereços do arquivo original
da carteira reconhecem exatamente o saldo anterior menos a taxa, sem contar
a quantidade do token como POVIX.

A preparação e o envio passam também pelas ações MVC reais, usando contextos
HTTP de teste e a coleção de sessão do ASP.NET. Confere que a preparação grava
a sessão (necessário para conservar seu cookie), que outra requisição com a
mesma sessão envia a assinatura do navegador e que os erros diferenciam sessão,
expiração, assinaturas, conflito de UTXOs, rede indisponível e preparação perdida
após reinício. Também verifica recuperação do comprovante já aceito durante a
reconexão. O ciclo HTTP completo de cookies no IIS deve ser verificado no Windows.

Para verificar abertura do arquivo e seleção entre carteiras, compile também
`Povix.WalletBridge` e `PrivateCoin.Desktop` em Release e execute:

```sh
mcs -r:Povix.WalletBridge/bin/Release/Povix.WalletBridge.exe \
  -r:Povix.Dex/bin/PrivateCoin.Core.dll -r:System.Web.Extensions -r:System.Core \
  -out:work/PovixWalletFileRegression.exe Tests/PovixWalletFileRegression.cs
MONO_PATH=Povix.WalletBridge/bin/Release:Povix.Dex/bin mono \
  work/PovixWalletFileRegression.exe work/wallet-files-regression "$(command -v node)" \
  PrivateCoin.Desktop/bin/Release/PrivateCoin.Desktop.exe
```

Essa regressão usa uma coleção descartável com duas carteiras com chaves e
uma carteira sem endereços, além de uma coleção contendo apenas essa carteira
vazia. Verifica os
nomes, a seleção explícita e o isolamento das chaves, a preservação das demais
carteiras no backup (inclusive a vazia), a rejeição da seleção sem endereços,
a comunicação real com o auxiliar via loopback e a
rejeição de origens/Host não autorizados. A abertura DPAPI é substituída somente
nesse teste em Linux; a leitura real deve ser verificada no Windows, com o
mesmo usuário que possui o arquivo.

A coleção do teste é serializada pelos tipos e pelo método reais do Desktop,
incluindo os metadados privados e o formato legado. Também confere UTF-8 com
BOM e códigos separados para senha da cópia, DPAPI e formato, sem expor dados
da carteira nos erros HTTP. Os testes de início abaixo verificam ainda a leitura
local `--check-wallet`, os contadores públicos, o arquivo original preservado e
a ocultação do conteúdo das exceções.

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
  -r:System.Runtime.Serialization -out:work/PrivateCoinDesktopTokenRegression.exe \
  Tests/PrivateCoinDesktopTokenRegression.cs Tests/FinalityTestSupport.cs \
  PrivateCoin.Desktop/TokenAmount.cs
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
  -r:System.Runtime.Serialization -r:System.Security \
  -out:work/PrivateCoinReconnectRegression.exe \
  Tests/PrivateCoinReconnectRegression.cs Tests/FinalityTestSupport.cs \
  PrivateCoin.Desktop/NetworkReadiness.cs \
  PrivateCoin.Desktop/WalletStore.cs PrivateCoin.Desktop/RecoveryPhraseGenerator.cs
MONO_PATH=PrivateCoin.Desktop/bin/Release mono work/PrivateCoinReconnectRegression.exe \
  work/reconnect-regression
```

Verifica o bloqueio inicial e offline, a sincronização com cadeia idêntica,
a invalidação de respostas antigas, a adoção de uma continuação maior e a
rejeição de cadeias que divergem abaixo do checkpoint finalizado, menores,
maiores e do mesmo tamanho, inclusive
com desconexão e reconexão TCP real em loopback. Um prefixo mais curto não
libera operações nem reescreve o histórico. Confere a identidade persistente do nó, a
preservação da origem dos blocos na serialização e no armazenamento do Desktop,
a cópia pública da cadeia entre instalações, hashes alteradas, compatibilidade
legada e rejeição de cadeias inválidas. Não depende de WinForms nem de carteiras
de produção. A regressão de tokens do Desktop também verifica a origem das
recompensas, de stake e dos blocos proof-of-stake e a vinculação aos votos dos
validadores, além da preservação dos tokens e saldos ao rejeitar uma cadeia
divergente menor no trecho finalizado. A regressão do DEX verifica ainda que prefixos antigos não
concluem a sincronização e que conflitos no trecho finalizado não alteram comprovantes
confirmados nem o cache persistente da rede.

## Regressão da finalização por stake

Compile o Core em Release e use um diretório vazio para cada execução. Os
checkpoints persistidos não podem ser sobrescritos por outro histórico de teste.

```sh
mkdir -p work/finality-regression
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -r:System.Runtime.Serialization -r:System.Security \
  -out:work/PrivateCoinFinalityRegression.exe \
  Tests/PrivateCoinFinalityRegression.cs Tests/FinalityTestSupport.cs \
  PrivateCoin.Desktop/WalletStore.cs PrivateCoin.Desktop/RecoveryPhraseGenerator.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/PrivateCoinFinalityRegression.exe \
  work/finality-regression
```

Verifica o limite estrito de mais de 2/3, votos ponderados pelo stake anterior,
rejeição de assinaturas falsas, duplicadas ou para outro bloco/pai, certificados
sem quorum e finalização que tenta pular um pai. Confere a combinação de votos
parciais recebidos em snapshots separados, o registro persistente contra voto
duplo, restauração do checkpoint, rejeição de gravações antigas e preservação do
arquivo após falha. Testa forks de todos os tamanhos contra o checkpoint,
mudanças permitidas no trecho provisório, novos checkpoints comprovados e troca
de votos e certificados entre dois nós TCP reais. Usa somente chaves e dados
descartáveis.
