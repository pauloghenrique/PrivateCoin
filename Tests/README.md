# Regressão do cadastro e da movimentação de tokens

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
  Tests/PovixDexRegression.cs Tests/PovixDexTransferRegression.cs Tests/ValidationBatchFixture.cs Tests/LegacyConsensusFixture.cs
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
Web Crypto e respostas HTTP de teste: abrir a carteira com destino vazio e
usar o botão de recebimento devem escolher um endereço já importado, sem
adicionar uma chave exclusiva do navegador. Um destino externo é preservado
e seu efeito sobre a movimentação é explicado na revisão. A preparação deve
escolher um endereço da carteira importada para o troco, exibir seu valor
exato na revisão e distinguir saldo reservado de saldo disponível. Na rede
local real, também confere os valores retornados pela ação MVC de saldo e
pelo comprovante. Após a confirmação, os mesmos endereços do arquivo original
da carteira reconhecem exatamente o saldo anterior menos a taxa, sem contar
a quantidade do token como POVIX.

Inclui também a movimentação de tokens pelo DEX: listagem de criações
confirmadas pela chave do criador, seleção explícita da carteira, revisão dos
dois tipos de troco, assinatura Web Crypto das entradas e autorização separada
do criador. Verifica rejeição de outra carteira, de assinatura falsa e de
autorização reaproveitada em outra preparação, inclusive quando quem tenta
enviar possui tokens e assina corretamente suas próprias entradas. Após um
bloco validado, confere conservação do token e desconto de somente a taxa em
POVIX nos endereços originais. Testa ainda reserva de saldos, envio de todo o
saldo, repetição após falha temporária, histórico/comprovante e recuperação
após reinício. As transferências normais de proprietários continuam válidas
pelo consenso existente fora dessa tela do DEX.

O formulário começa com quantidade e destino vazios, como na tela real.
Confere que o clique de revisão explica o problema, que mudar a taxa atualiza
os controles também pelo evento `change`, e que token não selecionado, quantidade vazia, destino
inválido, POVIX insuficiente, ausência de saldo do token, saldo reservado e
quantidade acima do disponível não preparam nem assinam uma transferência.
A validação nativa dos campos também expõe o motivo na própria página.

Simula também um arquivo original do Desktop com as chaves do criador e da
taxa, mas sem a chave do endereço que recebeu os tokens. A consulta real da
blockchain retorna zero disponível e identifica o saldo confirmado naquele
endereço; a preparação não pode gastar esse saldo sem sua chave. O formulário
real abre essa carteira incompleta, mostra o diagnóstico e, ao abrir o backup
atualizado com a chave de recebimento, recupera o saldo disponível e assina a
transferência com Web Crypto. Ter emitido o token não acrescenta saldo sem
UTXOs próprios. Um endereço com chave presente e saldo já gasto recebe um
diagnóstico diferente de chave ausente.

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
  -out:work/PrivateCoinDesktopTokenRegression.exe \
  Tests/PrivateCoinDesktopTokenRegression.cs Tests/ValidationBatchFixture.cs Tests/LegacyConsensusFixture.cs PrivateCoin.Desktop/TokenAmount.cs
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

Para verificar o histórico v4, os lotes de 20 e a remuneração por transação:

```sh
mcs -r:PrivateCoin.Desktop/bin/Release/PrivateCoin.Core.dll -r:System.Runtime.Serialization \
  -out:work/PrivateCoinValidationRegression.exe Tests/PrivateCoinValidationRegression.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=PrivateCoin.Desktop/bin/Release mono work/PrivateCoinValidationRegression.exe
```

Confere prioridade por taxa, limites de lote, conservação das taxas e da emissão,
restauração das provas e rejeição de assinaturas falsas, duplicadas ou ausentes,
pagamentos redirecionados, taxas somadas à recompensa do bloco e ordem inválida.
Os blocos adulterados têm seus votos assinados novamente e são minerados antes
da verificação, para exercitar as regras de consenso além da integridade do hash.

Para verificar o consenso híbrido v7 e a escolha por trabalho acumulado:

```sh
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -r:System.Numerics -r:System.Runtime.Serialization \
  -out:work/PrivateCoinProofOfWorkRegression.exe \
  Tests/PrivateCoinProofOfWorkRegression.cs Tests/LegacyConsensusFixture.cs Tests/ValidationBatchFixture.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/PrivateCoinProofOfWorkRegression.exe
```

Verifica o alvo, o cálculo do trabalho, lotes completos, seleção por stake,
votos e prova de trabalho, emissão, restauração v4/v7, resgate de garantias,
confirmações, desempate, rejeição de cadeia inválida e reorganização sem
checkpoint. Confere que zeros adicionais não dão trabalho extra e que
confirmações/saldos saem da cadeia quando uma transação fica órfã.
`LegacyConsensusFixture` existe somente nos testes para produzir provas
históricas v4 e não altera a API de produção.

Para verificar o bloqueio durante reconexão, compile o Core e execute:

```sh
mcs -r:PrivateCoin.Desktop/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -out:work/PrivateCoinReconnectRegression.exe \
  Tests/PrivateCoinReconnectRegression.cs Tests/LegacyConsensusFixture.cs PrivateCoin.Desktop/NetworkReadiness.cs
MONO_PATH=PrivateCoin.Desktop/bin/Release mono work/PrivateCoinReconnectRegression.exe
```

Verifica o bloqueio inicial e offline, a sincronização com cadeia idêntica,
a invalidação de respostas antigas e a adoção de uma cadeia com mais trabalho
após reconexão. Confere também o bloqueio diante de cadeias atrasadas ou
inválidas. Não depende de WinForms nem de
carteiras de produção.

Para verificar o estado das garantias no Desktop após reorganização:

```sh
mcs -r:PrivateCoin.Desktop/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -out:work/PrivateCoinValidatorStateRegression.exe Tests/PrivateCoinValidatorStateRegression.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=PrivateCoin.Desktop/bin/Release mono work/PrivateCoinValidatorStateRegression.exe \
  PrivateCoin.Desktop/bin/Release/PrivateCoin.Desktop.exe
```

Executa o método real de reconciliação sem abrir uma janela. Confere todas as
carteiras locais, atualizações repetidas, a remoção de garantias órfãs e a
adoção das garantias da cadeia substituta. A interface visual precisa ser
verificada no Windows.

Para verificar a contagem de carteiras no consenso v8, após compilar o Core:

```sh
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Runtime.Serialization \
  -r:System.Numerics -out:work/PrivateCoinWalletValidationRegression.exe \
  Tests/PrivateCoinWalletValidationRegression.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/PrivateCoinWalletValidationRegression.exe
```

Confere 19/20 registros, confirmação sem stake, saldo pendente, 18 transferências
mais 2 carteiras, taxas apenas das transferências, divisão da recompensa,
restauração, migração v7/v8, limite promocional e rejeição de blocos adulterados.
`LegacyConsensusFixture.Fund` prepara a distribuição histórica antes do v8 e
usa lotes completos de carteiras depois da transição.
