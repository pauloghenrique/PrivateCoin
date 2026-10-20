# Regressão do cadastro e da movimentação de tokens

## Lista de tokens e menu

Execute `node Tests/PovixDexTokenListUiRegression.js` após instalar o `jsdom@26`
em `work/liquidity-tools`, conforme o comando abaixo. Verifica o menu **Token**,
lista por carteira, metadados como texto seguro, links de edição, renúncia,
falha de atualização, respostas antigas após desconexão e carteira sem tokens.

## Políticas de tokens e administração (v20 em rascunho)

A regressão também cobre a regra opcional de queima própria da pool: padrão sem
queima, percentual e fixo independentes da taxa do token, ambos os sentidos,
POVIX isento, mínimo líquido, imutabilidade, confirmação e replay da oferta.
O teste de liquidez assina no navegador uma pool token/token com taxas fixas e
confirma o swap no nó P2P.

A regressão também cobre queima automática percentual e fixa, débito adicional,
precisão e limites numéricos, saldo insuficiente para a taxa, rejeição de omissão,
subpagamento, sobrepagamento e desvio do troco, compatibilidade com o Desktop e
reconstrução da oferta. A integração usa taxa de 1%, confere os campos de criação
e assina uma transferência que entrega 100, queima 1 e confirma a oferta reduzida.

Após compilar o Core e o DEX em Release e restaurar NuGet:

```sh
mkdir -p work
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -r:System.Runtime.Serialization -out:work/TokenPolicyRegression.exe \
  Tests/PrivateCoinTokenPolicyRegression.cs Tests/LegacyConsensusFixture.cs \
  Tests/ValidationBatchFixture.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/TokenPolicyRegression.exe
```

Verifica emissão limitada, teto acumulado após queima, duas assinaturas distintas,
rejeição de duplicadas e assinaturas ausentes, queima pelo titular, pausa inclusive
em pools, retomada, revisão e replay, reconstrução da cadeia, liberação por blocos,
rejeição de modo confidencial e impossibilidade de emitir POVIX por uma operação
de token. Uma criação de carteira auto-validada não pode carregar comandos de token.

Para conferir formulário, Web Crypto, propagação e persistência:

```sh
npm install --prefix work/liquidity-tools --cache work/npm-cache --no-audit --no-fund jsdom@26
mcs -r:Povix.Dex/bin/Povix.Dex.dll -r:Povix.Dex/bin/PrivateCoin.Core.dll \
  -r:Povix.Dex/bin/System.Web.Mvc.dll -r:System.Runtime.Serialization \
  -r:System.Web.Extensions -r:System.Web -r:System.Core -r:System.Numerics \
  -r:System.ComponentModel.DataAnnotations -main:PovixDexTokenPolicyMain \
  -out:work/PovixDexTokenPolicy.exe Tests/PovixDexRegression.cs \
  Tests/PovixDexTransferRegression.cs Tests/PovixDexLiquidityRegression.cs \
  Tests/PovixDexProfileRegression.cs Tests/PovixDexTokenPolicyRegression.cs \
  Tests/ValidationBatchFixture.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=Povix.Dex/bin mono work/PovixDexTokenPolicy.exe \
  work/token-policy-integration "$(command -v node)"
```

Use um diretório de execução vazio e acesso a sockets locais. A suíte usa duas
chaves administrativas, testa campos condicionais e resumo seguro, rejeita
adulteração antes de assinar, verifica novas tentativas após HTTP 503, propagação
P2P, idempotência, sessão e recibo após reinício. Não valida privacidade
confidencial, migração de rede nem aparência visual em IIS.

## Configurações do Core no genesis

Após compilar `PrivateCoin.Core` em Release, execute:

```sh
mkdir -p work
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -r:System.Runtime.Serialization -out:work/PrivateCoinGenesisConfigurationRegression.exe \
  Tests/PrivateCoinGenesisConfigurationRegression.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/PrivateCoinGenesisConfigurationRegression.exe
```

Verifica manifesto no genesis, prova de trabalho, independência de cultura,
serialização, rejeição de manifesto ausente/adulterado (também após remineração),
rejeição do genesis antigo e dois peers TCP locais: hash de configuração
ausente ou diferente desconecta sem executar eventos; configurações iguais
sincronizam normalmente. Requer acesso a sockets de loopback, sem rede externa.

## Pools e trocas atômicas

Após compilar `Povix.Dex` e `PrivateCoin.Core` em Release e restaurar NuGet:

```sh
npm install --prefix work/liquidity-tools --cache work/npm-cache --no-audit --no-fund jsdom@26
mcs -r:Povix.Dex/bin/Povix.Dex.dll -r:Povix.Dex/bin/PrivateCoin.Core.dll \
  -r:Povix.Dex/bin/System.Web.Mvc.dll \
  -r:System.Runtime.Serialization -r:System.Web.Extensions -r:System.Web -r:System.Core -r:System.Numerics \
  -r:System.ComponentModel.DataAnnotations -main:PovixDexLiquidityMain \
  -out:work/PovixDexLiquidityRegression.exe \
  Tests/PovixDexRegression.cs Tests/PovixDexTransferRegression.cs \
  Tests/PovixDexLiquidityRegression.cs Tests/PovixDexProfileRegression.cs \
  Tests/ValidationBatchFixture.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=Povix.Dex/bin mono work/PovixDexLiquidityRegression.exe \
  work/liquidity-regression "$(command -v node)"
```

Use um diretório de execução vazio. Os testes geram somente carteiras, tokens
e nós descartáveis locais. Verificam pools POVIX/token e token/token, exclusão
das reservas do saldo livre, cotações sem overflow, os dois sentidos da troca,
sessão, assinaturas falsas, concorrência, troca com assinatura somente do comprador,
propagação P2P, aprovação separada da confirmação, conservação independente
dos ativos, taxa nativa, idempotência, reinício e reorganização da cadeia.
Também verifica trocas POVIX/token e token/token com a carteira já selecionada
controlando o saldo livre e a pool: uma única revisão assina as entradas livres,
sem solicitar outra carteira. O envio direto rejeita assinaturas incompletas,
assinaturas falsas do comprador e sessões diferentes. A tela Liquidity/Market
acompanha pendente → validada → confirmada sem navegar, restaura o comprovante
após recarregar, preserva o status em falhas e limpa confirmações após uma
reorganização. Repetir um HTTP 503 reutiliza as mesmas assinaturas.
Inclui a regressão de disponibilidade: criações e movimentações normais no DEX
não podem pagar taxas ou quantidades com reservas de pools ativas. Verifica
o saldo livre em todas as telas, a criação pendente/validada/confirmada, a
habilitação automática da pool na interface e o bloqueio na validação da
blockchain contra gastos com assinaturas válidas do dono, desvios de reservas,
alteração do provedor, cotação adulterada e combinação de pools diferentes.
Confere o encerramento assinado, taxa paga pelo saldo livre, devolução intacta
dos dois ativos após confirmação, restauração dos bloqueios sem cadastro DEX,
migração assinada de reservas antigas e restauração de caches v16/v17 em v18.
Confere pools e trocas históricas v17 com assinaturas do provedor, rejeição de
reservas sem assinatura em blocos v17 e ativação de trocas automáticas em v18.
Verifica também a renúncia assinada pelo criador: bloqueio de encerramento no
servidor, rejeição de preparação assinada antes da renúncia, reservas fora do
saldo livre, preservação das trocas e persistência após reinício. Exercita o
token em ambos os lados da pool e preserva as permissões de outros provedores.
O teste Node carrega o HTML das telas e os scripts reais em jsdom, abre a
carteira pelo formulário, rejeita preparações alteradas e assina com Web
Crypto; essas assinaturas são depois validadas pelo Core e incluídas em blocos
locais. jsdom é usado somente pelos testes, sem dependência nova na aplicação.

## Cadastro e movimentação existentes

Compile `Povix.Dex` e `PrivateCoin.Core` em Release, após restaurar os pacotes
NuGet. A regressão exige Node.js 20+ com Web Crypto e .NET Framework 4.8 (ou Mono).
Ela cria carteiras descartáveis, usa dois nós TCP locais e grava apenas dados
de teste em `work/dex-regression`; não usa a carteira nem os peers de produção.

Exemplo com Mono instalado:

```sh
mkdir -p work/dex-regression
mcs -r:Povix.Dex/bin/Povix.Dex.dll -r:Povix.Dex/bin/PrivateCoin.Core.dll \
  -r:Povix.Dex/bin/System.Web.Mvc.dll \
  -r:System.Web.Extensions -r:System.Web -r:System.Core -r:System.Numerics \
  -r:System.ComponentModel.DataAnnotations -out:work/PovixDexRegression.exe \
  Tests/PovixDexRegression.cs Tests/PovixDexTransferRegression.cs Tests/PovixDexProfileRegression.cs Tests/ValidationBatchFixture.cs Tests/LegacyConsensusFixture.cs
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
  Tests/PrivateCoinDesktopTokenRegression.cs Tests/ValidationBatchFixture.cs Tests/LegacyConsensusFixture.cs \
  PrivateCoin.Desktop/TokenAmount.cs PrivateCoin.Desktop/TokenWalletOperations.cs
MONO_PATH=PrivateCoin.Desktop/bin/Release mono \
  work/PrivateCoinDesktopTokenRegression.exe work/desktop-tokens-regression
```

A regressão cria tokens com a mesma preparação/assinatura nativa usada pelo
DEX, confirma blocos com validadores descartáveis e sincroniza dois nós reais
de loopback. Confere metadados e saldo por carteira, precisão até `Int64.MaxValue`,
distinção por identificador entre símbolos iguais, confirmação de transferências
e atualização após troca de cadeia. Criações sem aprovação não acrescentam
saldo; mesmo aprovadas, criações e movimentações aguardam o bloco antes
de liberar saldo para transferências pela operação real do Desktop. Aprovações falsas
não liberam tokens. Os dados de produção não são acessados.
Exercita também a listagem padrão de saldos positivos, o isolamento entre
carteiras, leitura exata das quantidades nos formatos pt-BR/en-US, rejeição de
destino inválido, valor inexato, saldo insuficiente e taxa sem POVIX. As
transferências usam a mesma operação do Desktop, propagam por P2P e verificam
reserva contra gasto duplo, conservação do token, troco sob as chaves da
carteira e envio de todo o saldo, que retira o token da lista após confirmação.
A interface WinForms ainda deve ser verificada visualmente no Windows.

Para verificar o saldo POVIX exibido na tela principal após um envio:

```sh
mcs -r:PrivateCoin.Desktop/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -r:System.Runtime.Serialization -out:work/PrivateCoinDesktopBalanceRegression.exe \
  Tests/PrivateCoinDesktopBalanceRegression.cs Tests/LegacyConsensusFixture.cs Tests/ValidationBatchFixture.cs PrivateCoin.Desktop/WalletBalanceSummary.cs
MONO_PATH=PrivateCoin.Desktop/bin/Release mono work/PrivateCoinDesktopBalanceRegression.exe
```

Confere que o troco da carteira que envia continua visível antes do bloco,
com desconto exato do valor e da taxa, sem liberar novo gasto. Inclui garantia
bloqueada, envio completo, transferência para a própria carteira, múltiplas
saídas, reinício com pendências e confirmação posterior sem duplicar saldo.
Também verifica o troco POVIX de operações de token sem somar sua quantidade
ao saldo nativo. A dica do saldo na tela principal separa o disponível para
enviar do troco aguardando confirmação em bloco.

Para verificar o histórico v4, os lotes de 20 e a remuneração por transação:

```sh
mcs -r:PrivateCoin.Desktop/bin/Release/PrivateCoin.Core.dll -r:System.Runtime.Serialization \
  -out:work/PrivateCoinValidationRegression.exe Tests/PrivateCoinValidationRegression.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=PrivateCoin.Desktop/bin/Release mono work/PrivateCoinValidationRegression.exe
```

Confere prioridade por taxa, limites de lote, conservação das taxas e da emissão,
restauração das provas e rejeição de assinaturas falsas, duplicadas ou ausentes,
pagamentos redirecionados, taxas somadas à recompensa do bloco e ordem inválida.
Os blocos históricos adulterados têm seus votos assinados novamente e são minerados antes
da verificação, para exercitar as regras de consenso além da integridade do hash.

Para verificar o consenso híbrido v7 e a escolha por trabalho acumulado:

```sh
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -r:System.Numerics -r:System.Runtime.Serialization \
  -out:work/PrivateCoinProofOfWorkRegression.exe \
  Tests/PrivateCoinProofOfWorkRegression.cs Tests/LegacyConsensusFixture.cs Tests/ValidationBatchFixture.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/PrivateCoinProofOfWorkRegression.exe
```

Verifica o alvo, o cálculo do trabalho, blocos individuais, seleção por stake,
votos e prova de trabalho, emissão, restauração v4/v7, resgate de garantias,
confirmações, desempate, rejeição de cadeia inválida e reorganização sem
checkpoint. Confere que zeros adicionais não dão trabalho extra e que
confirmações/saldos saem da cadeia quando uma transação fica órfã.
`LegacyConsensusFixture` existe somente nos testes para preparar carteiras
confirmadas e provas históricas v4/v10–v15; não altera a API de produção.

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

Para verificar carteiras, garantias e transferências confirmadas em bloco no consenso v16:

```sh
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Runtime.Serialization \
  -r:System.Numerics -out:work/PrivateCoinWalletValidationRegression.exe \
  Tests/PrivateCoinWalletValidationRegression.cs Tests/LegacyConsensusFixture.cs Tests/ValidationBatchFixture.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/PrivateCoinWalletValidationRegression.exe
```

Confere os 6 POVIX indisponíveis até o bloco, bloqueio e desbloqueio de garantias
confirmados, bootstrap com uma operação por bloco, transferências com provas assinadas,
reserva contra gasto duplo, persistência, rejeição de blocos vazios ou com mais de 20 operações e limite
de emissão. `LegacyConsensusFixture.FundBatch` prepara carteiras confirmadas;
`SelfBlock` confirma cada garantia em um bloco individual.

Para verificar a migração v8/v9/v10 sem reproduzir a distribuição:

```sh
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Runtime.Serialization \
  -r:System.Numerics -out:work/PrivateCoinImmediateMigrationRegression.exe \
  Tests/PrivateCoinImmediateMigrationRegression.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/PrivateCoinImmediateMigrationRegression.exe
```

Verifica saldos históricos v8/v9, pendências v8 aguardando confirmação sem blocos de
migração, comprovantes v9 contabilizados em v10 sem nova emissão, restauração e
rejeição de distribuições isoladas, retrocesso de versão e endereços repetidos.

Para verificar operações pendentes recebidas por dois peers TCP locais:

```sh
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -out:work/PrivateCoinPendingLedgerRegression.exe Tests/PrivateCoinPendingLedgerRegression.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/PrivateCoinPendingLedgerRegression.exe \
  work/pending-ledger-regression
```

Confere a propagação de criação, bloqueio e transferência,
as reservas e a ausência de ativação de garantias no peer receptor e a confirmação
após mineração de lotes parciais. Propaga também um bloco v16 real com 20 operações
e verifica sua adoção, garantia confirmada e transferência ainda pendente.
Usa somente carteiras e arquivos descartáveis.

## Aprovação prévia e confirmação em bloco (consenso v16)

`PrivateCoinTokenApprovalRegression.cs` verifica assinaturas, garantia confirmada,
reserva de saldo, rejeição de provas falsas, pagamento exato da taxa somente no
bloco, conservação da emissão, restauração e leitura do histórico.
`PrivateCoinTokenChangeRegression.cs` confere que o troco fica pendente e só pode
financiar uma garantia após o bloco. `PrivateCoinTokenConfirmationRegression.cs`
verifica registro, quantidade e movimentação disponíveis somente após o bloco,
metadados reais, reserva contra gasto duplo e reinício com aprovações pendentes.

Compile cada arquivo com `LegacyConsensusFixture.cs` e `ValidationBatchFixture.cs`,
referenciando o Core, `System.Core`, `System.Numerics` e `System.Runtime.Serialization`.
`PrivateCoinDeferredValidationRegression.cs` usa também `NetworkReadiness.cs` e
verifica que sincronizar libera a validação, mantendo saldo e taxa pendentes.

A regressão DEX verifica `pending → validated → confirmed`, listagem e movimentação
somente após criação em bloco, taxa única, repetição, persistência e reorganização.
Execute `node Tests/PovixApprovalStatusRegression.js` para conferir os comprovantes,
a remoção de hashes antigas, a separação de aprovações e confirmações em bloco
e a indicação de saldo e taxa aguardando o limite de 20 operações ou 10 segundos.

## Sincronização DEX/Desktop antes da validação

Após compilar `Povix.Dex` em Release, execute:

```sh
mcs -r:Povix.Dex/bin/Povix.Dex.dll -r:Povix.Dex/bin/PrivateCoin.Core.dll \
  -r:System.Core -r:System.Numerics -r:System.Web.Extensions \
  -r:System.Runtime.Serialization -r:System.ComponentModel.DataAnnotations \
  -out:work/PovixDexDesktopSynchronizationRegression.exe \
  Tests/PovixDexDesktopSynchronizationRegression.cs Tests/LegacyConsensusFixture.cs \
  PrivateCoin.Desktop/NetworkReadiness.cs
MONO_PATH=Povix.Dex/bin mono work/PovixDexDesktopSynchronizationRegression.exe \
  work/dex-desktop-sync-regression
```

Use um diretório vazio a cada execução. Dois peers TCP reais reproduzem o
caso em que o Desktop rejeita o cache antigo do DEX antes de este adotar sua
cadeia mais recente. A regressão usa o `NetworkReadiness` real do Desktop e
confere que a resposta com a cadeia adotada libera criações e transferências
para validação. Antes da correção, a criação fica retida nesse ponto.
Confere aprovação assinada, ausência de bloco aos 9 segundos, produção e
confirmação de um bloco assinado aos 10 segundos com relógio injetado, saldo
do token, nova sincronização após desconectar e conectar um DEX com cache
vazio e ausência de respostas repetidas a uma cadeia idêntica. Usa somente
carteiras e dados descartáveis.

## Produção com limite e prazo

```sh
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -r:System.Numerics -r:System.Runtime.Serialization \
  -out:work/PrivateCoinBlockProductionRegression.exe \
  Tests/PrivateCoinBlockProductionRegression.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/PrivateCoinBlockProductionRegression.exe
```

Usa um relógio injetado para verificar os 10 segundos sem esperas reais: a vigésima
operação dispara o lote imediatamente, chegadas posteriores não reiniciam o prazo,
35 operações deixam 15 com a espera original e filas vazias não geram blocos.
Confere dependências, duplicatas e lotes menores quando os validadores não podem
produzir o conjunto completo. Exercita blocos reais de 1, 2 e 20 operações,
aprovações, saldo, taxas, criação de tokens, bloqueio/desbloqueio e restauração.
Blocos recebidos com 21 operações, lotes v15 com mais de uma e retrocesso de versão
são rejeitados. As regressões de aprovação também restauram um bloco assinado v15
e estendem sua cadeia com um lote v16 de 20 operações.

A regressão também verifica a edição do perfil local: assinatura Web Crypto
validada pelo servidor, rejeição de carteira alheia, sessão diferente, site
inseguro e payload alterado; repetição do envio, preservação dos dados da
blockchain, renúncia bloqueando preparações anteriores, persistência após
reinício e rejeição de perfil salvo com assinatura adulterada.

Para testar o fluxo da tela de edição sem servidor:

```sh
node Tests/PovixDexProfileUiRegression.js
```

Confere seleção explícita da carteira e do token, revisão, confirmação antes
de assinar, nova tentativa após HTTP 503, bloqueio após renúncia e limpeza
dos campos ao bloquear a carteira. A validação visual em IIS/HTTPS permanece
necessária; o teste usa um DOM simulado.


## Gênesis e compatibilidade das regras

Compile o Core pelo projeto para incorporar os compromissos dos fontes:

```sh
xbuild PrivateCoin.Core/PrivateCoin.Core.csproj /p:Configuration=Release
mkdir -p work
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -r:System.Runtime.Serialization -r:System.Numerics -r:System.Security \
  -out:work/PrivateCoinGenesisRulesRegression.exe \
  Tests/PrivateCoinGenesisRulesRegression.cs PrivateCoin.Desktop/WalletStore.cs \
  PrivateCoin.Desktop/RecoveryPhraseGenerator.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/PrivateCoinGenesisRulesRegression.exe
```

São 43 verificações: gênesis determinístico, total e fases de emissão, alteração
de cada campo mesmo após mineração, ausência de regras, redefinição em outro
bloco, isolamento do cache canônico, persistência real no `Blockchain.json`,
adulteração da projeção legível e peers TCP reais. Verifica desconexão por
hash divergente ou ausente, rede antiga, versão diferente e cadeia inválida
mesmo quando o peer anuncia o hash oficial. Um peer compatível entrega a cadeia.
O teste usa somente diretórios e sockets locais descartáveis.

## Transferências de tokens entre carteiras no explorador

```sh
mkdir -p work
mcs -r:System.Net.Http -r:System.Core -r:System.Runtime.Serialization \
  -r:System.Numerics -r:System.Xml -r:System.Xml.Linq \
  -out:work/PrivateCoinSiteTokenTransferRegression.exe PrivateCoin.Core/*.cs \
  PrivateCoin.Site/Models/HomeViewModel.cs Tests/PrivateCoinSiteTokenTransferRegression.cs
mono work/PrivateCoinSiteTokenTransferRegression.exe
```

Verifica nome e quantidade enviada, sem somar troco do token ou de POVIX,
inclusive quando a entrada tem o tipo genérico `Transfer`. Cobre taxa em POVIX,
metadados ausentes, casas decimais distintas e transferências nativas.

## Valores de trocas no explorador

```sh
mkdir -p work
mcs -r:System.Net.Http -r:System.Core -r:System.Runtime.Serialization \
  -r:System.Numerics -r:System.Xml -r:System.Xml.Linq \
  -out:work/PrivateCoinSiteSwapLedgerRegression.exe PrivateCoin.Core/*.cs \
  PrivateCoin.Site/Models/HomeViewModel.cs Tests/PrivateCoinSiteSwapLedgerRegression.cs
mono work/PrivateCoinSiteSwapLedgerRegression.exe
```

Verifica os valores e nomes dos dois lados da troca, POVIX em qualquer lado
da pool, tokens com casas decimais distintas e metadados ausentes. Usa entradas
de livro caixa construídas localmente com reservas e troco deliberadamente
diferentes das quantidades negociadas, sem rede ou carteira de produção.

## Página de detalhes da transação (Razor)

Após compilar `PrivateCoin.Site`, execute na raiz do repositório:

```sh
mkdir -p work
mcs -r:PrivateCoin.Site/bin/System.Web.Mvc.dll \
  -r:PrivateCoin.Site/bin/System.Web.Razor.dll \
  -r:PrivateCoin.Site/bin/System.Web.WebPages.Razor.dll -r:System.Web \
  -out:work/PrivateCoinSiteTransactionViewRegression.exe \
  Tests/PrivateCoinSiteTransactionViewRegression.cs
MONO_PATH=PrivateCoin.Site/bin mono work/PrivateCoinSiteTransactionViewRegression.exe
```

Valida a página com o parser Razor usado pelo MVC. Detecta erros nos loops
aninhados que a compilação padrão do projeto não verifica e que causavam
"An error occurred while processing your request." ao abrir os detalhes.

## Sessão compartilhada da carteira do DEX

Execute `node Tests/PovixDexWalletSessionRegression.js` e
`node Tests/PovixDexWalletMenuRegression.js` (sem dependências externas).
Verifica conexão DPAPI sem senha informada, seleção preservada na navegação,
assinatura após restauração, novas chaves, backup cifrado exportado, isolamento
de abas, desconexão e descarte de sessões inválidas.

## Permissão e participação na liquidez (v19)

Use a mesma compilação da regressão de liquidez, acrescente
`Tests/PovixDexPublicLiquidityRegression.cs`, selecione
`-main:PovixDexPublicLiquidityMain` e execute com um diretório vazio.
A suíte verifica criação habilitada, aporte proporcional, retirada integral da
participação de outro usuário, assinaturas Web Crypto pelos formulários reais,
sessão, envio idempotente, propagação P2P, bloqueio de retirada por não titulares,
rejeição de desvios e alteração de participações mesmo com assinaturas válidas,
preservação das participações nas trocas, bloqueio de encerramento com outros
provedores e restauração da blockchain e do cache DEX. A suíte existente também
verifica que a permissão permanece desabilitada por padrão e rejeita aportes.
Os clones de blocos usam DataContractJsonSerializer para preservar o manifesto
genesis imutável.

## Pools de lançamento (v21)

Após a compilação Release e instalação do jsdom conforme a seção de liquidez:

```sh
mcs -r:Povix.Dex/bin/Povix.Dex.dll -r:Povix.Dex/bin/PrivateCoin.Core.dll \
  -r:Povix.Dex/bin/System.Web.Mvc.dll -r:System.Runtime.Serialization \
  -r:System.Web.Extensions -r:System.Web -r:System.Core -r:System.Numerics \
  -r:System.ComponentModel.DataAnnotations -main:PovixDexLaunchMain \
  -out:work/PovixDexLaunchRegression.exe \
  Tests/PovixDexLaunchRegression.cs Tests/PovixDexPublicLiquidityRegression.cs \
  Tests/PovixDexRegression.cs Tests/PovixDexTransferRegression.cs \
  Tests/PovixDexLiquidityRegression.cs Tests/PovixDexProfileRegression.cs \
  Tests/ValidationBatchFixture.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=Povix.Dex/bin mono work/PovixDexLaunchRegression.exe \
  work/launch-regression "$(command -v node)"
```

Use um diretório de execução vazio. Verifica captação POVIX/token e token/token,
formulários reais com Web Crypto, assinaturas por sessão, concorrência e retries,
proibição de swaps e encerramento na captação, tentativa de exceder a meta,
abertura atômica e entrega proporcional, swaps normais após a abertura, limite
por altura da cadeia, devolução integral por outra carteira sem assinatura do
criador, restauração e reorganização que revoga a abertura órfã. Ataques com
novas assinaturas válidas não podem alterar a meta, o prazo, as participações
iniciais de liquidez, os destinatários dos tokens ou os destinatários dos aportes.
As duas suítes anteriores continuam cobrindo pools comuns e liquidez pública.

## Lançamento inicial POVIX (v23)

Após compilar o Core em Release:

```sh
mkdir -p work
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -r:System.Numerics -r:System.Runtime.Serialization -r:System.Web.Extensions -out:work/InitialLaunchRegression.exe \
  Tests/PrivateCoinInitialLaunchRegression.cs Tests/LegacyConsensusFixture.cs \
  Tests/ValidationBatchFixture.cs PrivateCoin.Desktop/WalletBalanceSummary.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/InitialLaunchRegression.exe work/initial-launch-swap.json
node Tests/PovixInitialLaunchWalletRegression.js work/initial-launch-swap.json
```

Verifica a distribuição de 600 POVIX, emissão única da reserva, limite de um milhão USDC em créditos de 100 por registro
e emissão de um milhão USDT, criação automática na 1ª carteira, compra única por identidade assinada,
bloqueio de gasto e negociação antecipados, catálogo e saldo da carteira,
restauração completa e contabilização. A fronteira 29.949 → 29.950 usa estado
UTXO de teste, validado pelo mesmo `Apply` do consenso, para conferir abertura,
liberação dos compradores, cotas principais permanentes, trocas com taxas e
retirada integral da liquidez adicional. A fixture evita minerar 29.950 compras;
não substitui teste de carga de rede ou uma simulação econômica.

Para verificar descoberta automática no DEX sem registros locais de pools, compile
os Models/Services do DEX e execute `Tests/PovixInitialLaunchCatalogueRegression.cs`
com `work/initial-launch-swap.json.chain.json` gerado pela suíte anterior.
A regressão usa a consulta real de catálogo, sem iniciar um servidor HTTP/TCP.


## Registro no Desktop 1.0.1

```sh
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core \
  -r:System.Runtime.Serialization -out:work/DesktopRegistration.exe \
  Tests/PrivateCoinDesktopRegistrationRegression.cs \
  PrivateCoin.Desktop/WalletRegistration.cs PrivateCoin.Desktop/WalletBalanceSummary.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/DesktopRegistration.exe
```

Verifica a primeira carteira local, preparação e confirmação dos 600 POVIX,
registro pendente sem crédito antecipado, reserva da pool fora do saldo,
os dois tokens locais no catálogo, carteiras posteriores com zero, repetição
bloqueada e restauração dos registros após serialização. A suíte testa a lógica
usada pelos dois botões; não faz automação visual do Windows Forms.


## Distribuição de USDC por registro (v23)

Após compilar o Core em Release:

```sh
mcs -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core -r:System.Runtime.Serialization -out:work/UsdcDistribution.exe Tests/PrivateCoinUsdcDistributionRegression.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/UsdcDistribution.exe
```

Aplica os 10.000 créditos pelo validador real de consenso e verifica o milhão
acumulado, duplicidade, crédito pendente indisponível, última recompensa,
rejeição de valores adulterados e registro sem crédito após esgotar o limite.
O teste completo de blocos do lançamento cobre restauração e negociação.


## Compra inicial pela tela Liquidity/Market

Com Core e Models/Services do DEX compilados:

```sh
mcs -out:work/LaunchMarketFixture.exe -r:work/Povix.Dex.Services.dll -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core -r:System.Runtime.Serialization -r:System.Web.Extensions -r:System.ComponentModel.DataAnnotations Tests/PovixInitialLaunchMarketFixture.cs
MONO_PATH=PrivateCoin.Core/bin/Release:work mono work/LaunchMarketFixture.exe work/launch-market
node Tests/PovixInitialLaunchMarketWalletRegression.js work/launch-market
node Tests/PovixDexBuySellUiRegression.js
```

Verifica preparação real de compra fixa, rejeição de venda/quantidade incorreta,
payloads reais da primeira e última compra, identidade, bloqueio, reservas e
transição da interface da captação para compra/venda. A fixture simula apenas
a prontidão do nó para preparar, sem abrir sockets, enviar ou minerar transações.


## Taxa descontada no lançamento (v24)

A compra inicial consome apenas USDC do comprador. A reserva reduz 6 POVIX brutos;
a saída bloqueada recebe `6 POVIX - Fee`, e a taxa segue a distribuição normal
da rede. O caso de carteira com zero POVIX é coberto pela fixture do mercado e
pela suíte de consenso do lançamento, incluindo rejeição de crédito bruto sem
desconto e abertura com as mesmas reservas finais. Após a meta, o AMM continua
cobrando a taxa separadamente. A alteração muda o genesis e requer rede v24;
não migra automaticamente a cadeia anterior.


## Compra de lançamento sem aprovação de validadores (v25)

A suíte do lançamento inclui compra confirmada com AddBlock sem
TransactionApproval, restauração da cadeia, verificações de assinatura e
limites preservadas, além da rejeição de compras v25 em blocos v24. A fixture
do mercado também minera a compra aceita sem aprovações e verifica seu recibo
confirmado. Transferências e negociações AMM mantêm aprovação dos validadores.


## POVIX do lançamento como garantia automática (v26)

A suíte `PrivateCoinInitialLaunchRegression.cs` cobre a elegibilidade automática
após confirmação da compra, aprovação de uma transferência real, assinatura de
bloco PoS e replay, preservação do bloqueio após recompensas, rejeição de
StakeUnlock antecipado, união com staking manual sem duplicar identidade e saída
da garantia automática quando a meta é atingida. Use os comandos da seção
Lançamento inicial POVIX com o Core v26 compilado pelo `.csproj` (incluindo os
recursos de compromisso dos fontes). Consulte `docs/povix-initial-launch.md`
para a mudança de identidade da rede.


## Primeira recompensa em garantia até a meta (v27)

A suíte `PrivateCoinInitialLaunchRegression.cs` agora inicializa os validadores
com a primeira recompensa bloqueada e compras confirmadas, sem gastar os 600 POVIX.
Confere bloqueio, garantia automática, recompensas de bloco para primeira carteira
e compradores, aprovação por chave correspondente, rejeição de uso por outra chave,
proibição de autoaprovação, replay e liberação dos dois tipos de saldo na meta.
Use os comandos da seção Lançamento inicial POVIX com Core v27. Os blocos de
bootstrap na fixture remuneram os validadores para financiar operações posteriores
sem desbloquear a recompensa inicial.

## Validadores com recibos bloqueados do lançamento (v28)

```sh
mcs -out:work/LaunchValidators.exe -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core -r:System.Runtime.Serialization Tests/PrivateCoinLaunchValidatorsRegression.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/LaunchValidators.exe
```

Verifica bootstrap sem validador, elegibilidade de recibo confirmado, aprovação
assinada, bloco com um criador, recompensa integral mais taxa, subconjunto de
detentores disponíveis, replay, rejeição de assinatura/peso adulterado e preservação
da garantia automática das operações comuns durante a captação. Uma fixture UTXO próxima da meta valida aprovação
e voto do último bloco, bloqueio permanente principal e fim dos direitos do
lançamento na ativação. Não minera 29.950 compras nem substitui teste de rede.


## Acesso a pools após meta principal (v29)

A suíte `PrivateCoinInitialLaunchRegression.cs` também verifica os três tipos
bloqueados (LiquidityPool, LaunchCreate e LiquidityAdd), antes da criação principal,
durante a captação e depois da compra que ativa a pool. A fixture
`PovixInitialLaunchMarketFixture.cs` verifica os bloqueios nos serviços DEX e que
compras da pool principal continuam disponíveis. `PovixDexOwnedPoolsRegression.js`
verifica botões bloqueados e liberação após a meta.


## Recompensa de bloco no registro Desktop (v30)

`PrivateCoinDesktopRegistrationRegression.cs` confirma um novo registro com
apenas a garantia automática dos 600 POVIX e verifica recompensa disponível,
garantia preservada, ausência de crédito pendente, rejeição de pagamento repetido
e replay. `PrivateCoinInitialLaunchRegression.cs` conserva a cobertura de vários
validadores. `PrivateCoinLaunchValidatorsRegression.cs` rejeita a exceção de
um único validador para registros após a meta.


## Fundador valida compras da pool principal (v31)

`PrivateCoinDesktopRegistrationRegression.cs` reproduz duas carteiras: uma recebe
os 600, valida o registro da outra e depois assina sua primeira compra. Confere
aprovação pela saída inicial 4, recompensa mais taxa, bloqueios preservados,
compras seguintes com apenas o fundador, rejeição de autoaprovação e replay.
`PrivateCoinLaunchValidatorsRegression.cs` verifica soma das garantias automáticas
sem duplicar identidade e preserva a cobertura do bootstrap sem aprovação,
da fronteira da meta e das regras normais após ativação.


## Registros consecutivos e concorrência sem chave (v32)

`PrivateCoinRegistrationRewardPropagationRegression.cs` simula dois nós com a
mesma cadeia, mas apenas um possui a chave da garantia dos 600 POVIX. Confere os
registros 2, 3 e 4 consecutivos: o nó sem chave aguarda, a API recusa o fallback,
um bloco concorrente minerado com prova de trabalho real é rejeitado no replay,
e o bloco assinado remunera o fundador e sincroniza corretamente em ambos.
Também cobre mistura de operações, bloqueios e teto total preservados.

```sh
mcs -out:work/RegistrationPropagation.exe -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core -r:System.Numerics -r:System.Runtime.Serialization Tests/PrivateCoinRegistrationRewardPropagationRegression.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/RegistrationPropagation.exe
```


## Registro sem recompensa de validador (v33)

`PrivateCoinRegistrationNoRewardRegression.cs` cobre um e dois validadores,
registros em lote, emissão e extrato iguais a zero, preservação dos 100 USDC,
rejeição de recompensa forjada mesmo com assinaturas e prova de trabalho válidas,
separação de registros e trocas, remuneração das compras e replay completo.

```sh
mcs -out:work/RegistrationNoReward.exe -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core -r:System.Numerics -r:System.Runtime.Serialization Tests/PrivateCoinRegistrationNoRewardRegression.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/RegistrationNoReward.exe
```

As suítes Desktop e de propagação agora esperam zero pagamento em registros.
A fixture de lançamento financia seus validadores por atividades remuneradas
reais, mantendo os registros assinados sem recompensa.


## Dificuldade dinâmica (v34)

```sh
mcs -out:work/DynamicProofOfWork.exe -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll -r:System.Core -r:System.Numerics -r:System.Runtime.Serialization Tests/PrivateCoinDynamicProofOfWorkRegression.cs Tests/LegacyConsensusFixture.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/DynamicProofOfWork.exe
```

Verifica médias de 2, 10 e 30 segundos, limites de ajuste, piso, fronteiras da
janela, alvo errado mesmo com PoW válido, codificação, horários regressivos e
futuros, relógio local, assinatura vinculada ao horário, manifesto, trabalho
acumulado, confirmações e replay de blocos reais. As janelas sintéticas testam
somente a aritmética dos cabeçalhos; não representam cadeias com transações válidas.
