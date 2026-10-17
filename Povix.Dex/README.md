# Tokens, pools de liquidez e trocas no Povix.Dex

A tela `/tokens/criar`, também acessível por `/liquidez/criar?tipo=token`, cadastra
tokens de usuários com suas próprias políticas no `PrivateCoin.Core`. O separador
**Criar pool** mantém a operação de liquidez em `/liquidez/criar`. A página
inicial (`/`) exibe o mercado com os ativos confirmados do catálogo da rede,
busca por nome/símbolo/ID e filtro por pool disponível. A consulta não exige
carteira; os links abrem a negociação existente. Contagens usam dados reais,
sem preços ou volumes simulados. O botão Atualizar refaz a consulta e informa
falhas sem descartar o último catálogo. Não há emissão de POVIX, mineração nem
criação de uma rede separada pelo DEX.

## Políticas de tokens de usuários — implementação v20 em rascunho

### Queima automática nas transferências diretas

Na criação, escolha **Desabilitada**, **Taxa percentual** ou **Taxa fixa em tokens**.
A regra é permanente, assinada na política do token e independente da permissão
de queima manual. Percentuais vão de 0,01% a 100%, com duas casas; o cálculo usa
unidades atômicas e arredonda para baixo. A taxa fixa respeita a precisão do token.
O remetente paga o valor recebido pelo destinatário **mais** a queima: enviar 100
com 1% consome 101 tokens, entrega 100 e destrói 1. Não há carteira de coleta.
Valores pequenos cujo percentual é inferior a uma unidade atômica queimam zero.
O valor fixo é cobrado mesmo em envios pequenos; saldo insuficiente para envio e
queima impede a preparação. A revisão do Desktop mostra o débito e a queima.

Somente `TokenTransfer` é tributada. Criação, emissão adicional, queima manual e
operações de liquidez (incluindo swaps) não sofrem essa cobrança. Taxas de pool e
taxa de rede em POVIX continuam separadas. O Core exige termos de transferência
assinados, valor exato da queima e troco em endereços dos signatários; não aceita
omitir a taxa nem redirecionar troco para um terceiro. A oferta só diminui após
confirmação em bloco e é reconstruída no reinício e na reorganização da cadeia.
O teto de emissão acumulada não é reaberto pela queima automática.

A API de preparação retorna `automaticBurnEnabled` e `automaticBurnAtomic`.
Clientes Web Crypto devem conferir a cotação e fornecer `AutomaticBurnAtomic`
à verificação local antes de assinar. A antiga tela de transferências do DEX
permanece removida; esta alteração adiciona a configuração na criação, a regra
no Core e a revisão no Desktop.

Os campos de criação incluem descrição, URL HTTPS de logo, site e rede social,
oferta fixa ou emissão adicional com teto, queima pelo titular, pausa administrativa,
até 16 endereços administradores com limiar de assinaturas e distribuição inicial
em até 48 parcelas por altura de bloco. As regras e a apresentação original são
registradas no token e comprometidas pela assinatura. As URLs são dados públicos;
o servidor não baixa o logo nem visita esses links.

Em **Administrar token** (`/Tokens/Manage`), emissão, queima, pausa e retomada
passam por revisão e assinatura local. Emissão e pausa exigem o limiar de
administradores; queima exige a posse das entradas gastas. O teto limita a emissão
acumulada: queimar não abre espaço para emitir novamente. Pausa bloqueia emissão,
queima, transferências e operações de pools envolvendo esse token. Parcelas ainda
bloqueadas não podem financiar operações; a liberação usa altura de bloco e não
o relógio informado pelo remetente. A política se aplica à distribuição inicial;
emissões adicionais não herdam esse cronograma.

As transações `TokenManage` incluem revisão do estado e assinaturas administrativas
no identificador. Oferta, emissão acumulada, pausa e revisão são reconstruídas a
partir da cadeia, inclusive depois de reorganizações. Uma preparação é vinculada
à sessão, expira em 15 minutos e aceita nova tentativa idempotente. Aprovação de
validador e confirmação em bloco permanecem etapas diferentes.

**Limites pendentes:** o navegador reúne assinaturas de chaves presentes na
carteira selecionada; ainda não há troca de propostas entre carteiras independentes.
Privacidade confidencial de valores e endereços **não está implementada**. A opção
é desabilitada no formulário e rejeitada pelo Core; dados públicos não são
apresentados como confidenciais. É necessário integrar um protocolo de provas
criptográficas antes de habilitá-la.

**Compatibilidade de rede:** esta implementação não é uma atualização pronta para
uma rede em produção. `GenesisRules` compromete os fontes do Core no gênese;
alterá-los, junto com o consenso v20, muda `GenesisHash`, `RulesHash` e `NetworkId`.
Não foi implementada uma migração que preserve a identidade e os dados da rede
existente. Não substituir caches ou carteiras para contornar essa incompatibilidade.
O PR permanece em rascunho. As constantes de oferta, distribuição, recompensas e
taxas do ativo nativo POVIX não foram alteradas; emissão e queima configuráveis
referem-se somente aos tokens de usuários.

Cada usuário importa e desbloqueia a própria carteira no navegador. A senha e
as chaves privadas não são enviadas ao servidor. O Core prepara a transação com
chaves públicas e UTXOs confirmados; o navegador confere o conteúdo apresentado,
assina com Web Crypto RSA/SHA-256 e envia apenas as assinaturas. O nó valida a
transação novamente, salva a fila e a propaga por `PeerNode.BroadcastAsync`.
O comprovante `/tokens/registro/{id}` diferencia pendente, aprovado aguardando
bloco, confirmado e retirado da fila. A aprovação da operação aparece separada
das confirmações em bloco. Só um bloco recebido e validado confirma o registro
e efetiva os saldos e a taxa. Com validadores elegíveis, o Desktop produz um
bloco ao reunir 20 operações aprovadas ou após 10 segundos de espera.

## Configurar a rede

1. Execute o projeto ASP.NET MVC 5 em IIS/IIS Express com .NET Framework 4.8 e
   HTTPS. Restaure os pacotes NuGet de `packages.config` antes de compilar.
2. Configure `PeerSeeds` em `Web.config` com pares **reais** da rede existente,
   separados por vírgula, ou use `POVIX_PEERS`. Para esta versão em rascunho, os pares devem usar o consenso 20
   e o mesmo gênese compilado no Core; veja o bloqueio de migração acima. Não são fornecidos endpoints fictícios.
3. `DexListenPort` usa 4780 por padrão. Use uma porta livre; o Desktop e o
   explorer podem continuar em suas portas próprias. A criação aguarda ao menos
   um par conectado e uma cadeia recebida e validada.
4. Dê à identidade do processo permissão de escrita em `App_Data`. Use **um
   processo de trabalho** por instalação, sem web garden e sem reciclagem
   sobreposta: o cache e a fila têm um único escritor. Guarde esse diretório
   entre publicações. O arquivo `dex-network.json` contém somente a cadeia,
   a fila e os comprovantes públicos; `dex-peers.dat` guarda pares conhecidos.
   Cache inválido ou de outra rede interrompe o serviço, sem substituição
   silenciosa por uma cadeia vazia.

Sem pares configurados o formulário continua disponível, mas não transmite
criações. Os validadores existentes precisam incluir a operação em um bloco;
o DEX não confirma suas próprias transações.

Ao adotar uma cadeia mais recente, o DEX devolve essa cadeia aos pares antes
de reenviar as operações pendentes. Assim, um Desktop que recusou o cache
inicial atrasado consegue concluir a sincronização e liberar sua fila de
validação. Cadeias idênticas não provocam outra resposta de cadeia, evitando
reenvios contínuos entre nós DEX.

## Abrir o arquivo de carteiras

No menu **Conectar carteira**, escolha `wallet.dat` ou `wallets.dat`, clique em **Ler carteiras do
arquivo**, selecione uma das carteiras listadas e clique em **Conectar carteira**. A seleção é explícita, mesmo quando o arquivo contém uma única
carteira. Somente a carteira escolhida fornece as chaves e os endereços para
consultar o saldo e assinar.

Carteiras sem endereços, como a carteira inicial do Desktop, são preservadas
no arquivo e no backup. Elas aparecem como **sem endereços**, com a seleção
desabilitada, sem impedir a abertura das outras carteiras. Para usar uma delas,
selecione-a no Desktop, clique em **Novo endereço** e leia novamente o arquivo
no DEX. A carteira escolhida precisa ter POVIX disponível para pagar a taxa.

O arquivo original do Desktop é protegido pelo usuário do Windows. Para abri-lo
diretamente, execute o [Povix.WalletBridge](../Povix.WalletBridge/README.md) no
seu Windows com a origem HTTPS do DEX autorizada. A leitura passa exclusivamente
por `127.0.0.1:4781`; o arquivo e a senha não são enviados ao servidor do DEX.
A cópia local é cifrada com uma senha aleatória gerada pelo navegador, sem pedir
que você crie uma senha para conectar. O original permanece inalterado. Se o navegador solicitar permissão de acesso à rede local, permita
a conexão com o auxiliar.

Para escolher o arquivo correto, clique em **Pasta da carteira** no Desktop:
o Explorador destaca o `wallets.dat` efetivamente usado, inclusive em instalações
com uma pasta antiga. Se a leitura falhar, o DEX mostra um código específico.
Use o diagnóstico local `Povix.WalletBridge.exe --check-wallet "C:\caminho\wallets.dat"`
para verificar a proteção do Windows e o formato antes da criação da cópia cifrada.
Consulte os códigos e as instruções no README do auxiliar.

A carteira selecionada vale para criar, movimentar e editar tokens, pools,
trocas e comprovantes na mesma aba. Navegar, recarregar ou enviar operações
não exige outra conexão. Use **Desconectar** no menu para encerrar a sessão.
Backups `.povixwallet` já cifrados precisam da senha original somente na abertura;
backups antigos contendo uma única carteira continuam compatíveis.
Ao exportar uma carteira conectada sem senha, defina uma senha para proteger
o arquivo de backup. Essa senha não é exigida para navegar entre as telas.

### Exportação alternativa sem auxiliar em execução

No Windows, execute a exportação **localmente**, com o mesmo usuário que possui
o `wallets.dat`. O script lê a proteção DPAPI já usada pelo Desktop e grava
somente um backup cifrado, sem modificar a carteira original:

```powershell
.\Povix.Dex\Tools\ExportDexWallet.ps1 -OutputFile ".\minhas-carteiras.povixwallet"
```

O exportador inclui todas as carteiras por padrão. Para incluir somente uma,
informe `-WalletName "Minha carteira"`. Se a instalação usa a pasta antiga,
informe também `-WalletFile` com o caminho
correto. Escolha uma senha de pelo menos 10 caracteres; a senha é solicitada
localmente. Importe o `.povixwallet` na tela do DEX e informe a mesma senha.
A carteira precisa ter POVIX confirmado e disponível para pagar a taxa.
Garantias de validador e UTXOs reservados não ficam disponíveis para essa taxa.

O backup usa PBKDF2-SHA256 com 210.000 iterações, AES-256-CBC e HMAC-SHA256
(encrypt-then-MAC com chaves independentes). O navegador guarda somente o
backup cifrado em armazenamento local. A sessão da aba guarda a cópia cifrada,
a credencial de abertura e o índice da carteira em `sessionStorage`, permitindo
reimportar as chaves ao navegar sem enviá-las ao servidor. **Desconectar** remove
essa sessão; uma nova aba independente não herda a conexão. As chaves importadas
são descartadas da memória ao sair da página. Não há scripts externos
na tela; a política CSP permite scripts da mesma origem e conexões ao servidor
do DEX e ao auxiliar de loopback fixo.

O troco em POVIX da taxa retorna ao primeiro endereço já presente na carteira
selecionada. Ao abrir uma carteira do Desktop, sua chave continua no
`wallets.dat` original, permitindo ao Desktop reconhecer o troco confirmado.
Essa escolha reutiliza um endereço da carteira. A criação desconta somente a
taxa em POVIX; a quantidade do token emitido não é descontada do saldo nativo.
Por exemplo, 6 POVIX com uma taxa de 0,00000002 POVIX deixam 5,99999998 POVIX
após a confirmação. Enquanto a transação está pendente, o UTXO inteiro pode
estar reservado: a tela separa disponível, confirmado, reservado e valores a
receber. O troco pendente não pode ser gasto até sua confirmação.

Ao abrir a carteira, um destino vazio é preenchido com seu primeiro endereço
já salvo. O botão **Usar endereço da minha carteira** usa esse mesmo endereço,
sem gerar uma chave exclusiva do navegador. Assim, o `wallets.dat` original
reconhece tanto os tokens emitidos quanto o troco em POVIX após a confirmação.
Um destino informado explicitamente é preservado. Se pertencer a outra
carteira, a revisão avisa que a movimentação pelo DEX exige também a chave
desse destino, além da autorização do criador.

A exportação opcional no menu da carteira preserva todas as carteiras da coleção.
A revisão não exige baixar nem selecionar um arquivo: clique em **Confirmar transação**
para verificar, assinar localmente e enviar usando a carteira já conectada. Use-o para restaurar a carteira no DEX em outro navegador.
Não apague o armazenamento local antes de guardá-lo. Evite usar a mesma
carteira simultaneamente em instalações diferentes: uma transação concorrente
pode consumir seu UTXO.

Versões anteriores do DEX também criavam uma chave nova para o troco. Se uma
criação anterior deixou o saldo zerado no arquivo original do Desktop, abra
o `.povixwallet` atualizado baixado antes daquela assinatura e selecione a
mesma carteira no DEX. Ele preserva a chave desse troco. O comprovante mostra
os valores e endereços das saídas em POVIX da transação para conferência.
Esta correção não modifica transações já confirmadas nem importa as chaves
do backup para o Desktop automaticamente. Guarde esse backup; o arquivo
original do Desktop e sua frase antiga não contêm a chave gerada pelo DEX.

Nome: até 64 caracteres; símbolo: 1–10 letras A–Z; precisão: 0–8 casas; quantidade:
positiva, limitada a `Int64.MaxValue` em unidades atômicas. A conversão não usa
ponto flutuante nem arredondamento. Símbolos podem se repetir: o identificador
de 64 caracteres distingue os tokens. Taxas seguem as opções econômica, normal
e prioritária do Core.

Preparações expiram em 15 minutos e ficam vinculadas à sessão. Reiniciar o
processo descarta preparações ainda não enviadas; as transações já aceitas
permanecem no cache e são retransmitidas na sincronização. Repetir o envio da
mesma preparação já aceita retorna o mesmo comprovante.

O DEX mantém a sessão da preparação ativa para que o ASP.NET preserve seu
cookie entre a revisão e o envio. Use a mesma janela e permita os cookies
do site. As falhas de envio mostram uma mensagem específica e um código:

| Código | Ação |
| --- | --- |
| `draft_session_changed` | A sessão mudou; permita os cookies e prepare novamente na mesma janela. |
| `draft_missing` | A preparação não está mais em memória, por exemplo após reiniciar o servidor; prepare novamente. |
| `draft_expired` | Passaram os 15 minutos de validade; prepare novamente. |
| `funding_unavailable` | Os UTXOs escolhidos para a taxa foram usados, reservados ou bloqueados; atualize o saldo e prepare novamente. |
| `signatures_invalid`, `signature_invalid` | O envio ou a assinatura não corresponde à preparação; atualize o DEX, abra a carteira novamente e prepare outra criação. |
| `network_not_ready`, `network_unavailable`, `pending_limit` | Aguarde a rede ou a fila e tente enviar a mesma preparação enquanto ela for válida. |
| `persistence_unavailable` | O servidor não conseguiu salvar; tente o mesmo envio novamente. |
| `transaction_invalid`, `submission_invalid` | A validação falhou; confira as versões do DEX/Core e prepare novamente. |

Falhas temporárias retornam HTTP 503 e mantêm a revisão para nova tentativa.
Rejeições que exigem outra preparação retornam HTTP 400. Os erros não mostram
os dados da transação nem o conteúdo das exceções do servidor. Uma falha no
envio não equivale a uma confirmação; consulte sempre o comprovante.

## Comprovantes de movimentação

A tela **Movimentar tokens** foi removida do DEX, incluindo os atalhos do menu
e do comprovante de criação. Os endpoints de movimentação e os comprovantes
já registrados continuam disponíveis. O link de retorno dos comprovantes
leva ao mercado.

## Verificação

As telas de criação e edição carregam a carteira e seu script em
um único bundle com URL versionada pelo conteúdo, inclusive em modo Debug.
Isso evita reutilizar uma carteira antiga do cache após atualizar o DEX.

Veja `../Tests/README.md` para executar a regressão do DEX. Ela usa carteiras e
nós locais descartáveis; não publica tokens na rede configurada pelo operador.
O navegador exige HTTPS (ou localhost para desenvolvimento) e suporte a Web
Crypto, BigInt e armazenamento local. A hospedagem de produção permanece no
ambiente IIS do projeto.

## Edição do perfil e renúncia

Acesse **Editar tokens** (`/tokens/editar`) e abra a carteira do criador.
Somente criações confirmadas dessa carteira são listadas. Nome e símbolo de
exibição, descrição e site HTTPS podem ser alterados no perfil local do DEX.
A revisão exige confirmação explícita; o navegador reconstrói a autorização
e assina com a chave do criador. O servidor verifica a assinatura, a sessão,
a validade de 15 minutos, a criação atual e a revisão do perfil.

**Renunciar** bloqueia permanentemente novas alterações deste perfil e o
encerramento das pools deste token administradas pelo criador nesta instalação,
inclusive preparações anteriores ainda não enviadas. A regra consulta os dois
ativos da pool e vincula a renúncia à criação confirmada e à chave do criador.
As reservas continuam excluídas do saldo livre, e as trocas permanecem disponíveis.
Pools antigas ainda podem ter suas reservas bloqueadas; depois, seu encerramento
também é impedido. Saldos livres e pools de outros provedores mantêm suas operações.
A renúncia não altera o consenso nem os dados originais do token na blockchain.
Não há taxa de rede para esta operação local.

`App_Data/dex-token-profiles.json` persiste os perfis e renúncias com suas
autorizações assinadas. Preserve esse arquivo entre publicações e backups,
junto do cache da rede. A assinatura é verificada ao iniciar; arquivo inválido
interrompe o serviço. Como o estado é local, outras instalações não recebem
a alteração, e restaurar um backup antigo pode restaurar permissões antigas.

## Pools e troca de tokens

Abra **Pools e trocas** no menu (`/liquidez`). A página usa a mesma carteira
local das telas de tokens. Escolha dois ativos distintos e as quantidades
iniciais, revise as reservas, os trocos, os endereços e a taxa em POVIX,
clique no botão de confirmação para assinar e enviar a criação. São aceitos dois tokens nativos
confirmados ou um token nativo com POVIX. ETH, BTC e tokens de outras redes
não são ativos dessa blockchain.

As pools desta versão permitem **trocas sem aprovação do criador**, validadas
pelas regras nativas do consenso 18. O comprador assina suas entradas de saldo
livre; as duas reservas bloqueadas são consumidas pela regra da pool e recriadas
com os mesmos ativos e provedor, seguindo a cotação de 0,30%. Criação, migração
e encerramento continuam exigindo as assinaturas do dono. Cada pool pertence a
um único provedor; não há emissão de cotas LP ou depósito de outros provedores.
O servidor não guarda chaves privadas.
As reservas são bloqueadas **na blockchain**, inclusive contra transferências
pelo Desktop ou por outro programa com as mesmas chaves. O Core rejeita
movimentos comuns, criação de tokens, taxas, garantia de validador e novas
pools que tentem gastar essas entradas. Os seletores de saldo livre também
excluem ambas as reservas; o troco continua disponível após confirmação.

A criação usa `LiquidityPool` (tipo 7), com as duas reservas nas primeiras
saídas. `LiquiditySwap` (tipo 8) consome exatamente as duas reservas da mesma
pool e recria ambas bloqueadas, com os mesmos ativos e provedor. A cotação de
0,30% e a quantidade entregue constam no payload assinado e são verificadas
por todos os validadores. As taxas da rede vêm do saldo livre, nunca das
reservas. Somente a confirmação em bloco torna a pool disponível.

O identificador da pool é o da transação de criação. O cadastro da pool fica
no cache público desta instalação do DEX; as novas trocas e o encerramento
confirmados são seguidos diretamente na cadeia. Guarde `App_Data/dex-network.json`: outro DEX não descobre
automaticamente esse cadastro local apenas ao sincronizar a blockchain.

A lista e o comprovante mostram o estado atual da pool separadamente do estado
da criação. Uma criação pode estar confirmada e ter reservas movimentadas
posteriormente. A criação pendente informa que aguarda validação; a validada
informa que aguarda um bloco. A pool fica disponível somente quando as duas
reservas estão confirmadas e não são entradas de outra operação pendente.
O comprovante identifica a transação que ocupa ou gastou uma reserva.
Ao voltar dele para a tela de liquidez, a mesma pool fica selecionada e é
habilitada automaticamente quando a confirmação chega, sem reabrir a carteira
já selecionada nessa tela.

Para liberar os valores, abra a carteira provedora e use **Revisar encerramento
da pool** na lista. Confira as quantidades e os endereços, confirme a operação e
assine. `LiquidityClose` (tipo 9) exige as assinaturas das duas reservas e
retorna ambos os valores intactos ao provedor, sem novo bloqueio. A taxa é
paga separadamente em POVIX livre. Até a confirmação, a pool fica ocupada;
após confirmar, aparece como **Encerrada** e os valores voltam ao saldo livre.
O encerramento fica bloqueado neste DEX quando o provedor é o criador de um
dos tokens da pool e salvou sua renúncia. Uma revisão preparada antes da
renúncia também é rejeitada no envio. As trocas continuam disponíveis.
Não há liberação por timeout, reinício ou simples perda do cadastro local.

Pools criadas antes do consenso 17 mantêm seus UTXOs e assinaturas originais;
não recebem um bloqueio retroativo. A lista as identifica como **Reservas antigas
sem bloqueio** e desabilita trocas até o provedor usar **Bloquear reservas desta
pool**, revisar e assinar a migração. Ambos os valores são preservados. Se as
reservas antigas já foram gastas, essa migração não recupera os valores.

**Ativação:** as trocas automáticas exigem consenso 18 no DEX e nos nós da
rede. O handshake rejeita versões diferentes. Atualize também os clientes e
validadores antes de produzir blocos v18. Atualizar só a interface não ativa
esse fluxo numa rede v17. O Core preserva a validação original dos blocos v17:
reservas em trocas históricas ainda precisam das assinaturas do provedor. Os
caches v17 e anteriores são revalidados ao restaurar. Pools v17 já bloqueadas
podem receber trocas automáticas após a ativação; reservas antigas sem bloqueio
continuam exigindo a migração assinada pelo dono.

Para trocar, use a **carteira já selecionada**, escolha uma pool disponível, o ativo
que entrega, sua quantidade e o **mínimo a receber**. A cotação é calculada
com inteiros, sem arredondamento para cima, pela fórmula:

```text
recebimento = reservaSaida × quantidade × 9970
              / (reservaEntrada × 10000 + quantidade × 9970)
```

A divisão descarta a fração atômica. A taxa de 0,30% permanece nas reservas,
e a taxa da rede é cobrada separadamente em POVIX do comprador. A revisão
mostra o recebimento, o mínimo, os novos valores das reservas e os trocos.
O comprador assina e envia a troca diretamente à rede, inclusive quando a
pool pertence a outra carteira. Não há solicitação ou aprovação do criador.
O navegador reconstrói o payload e verifica quantidades, taxa, endereços,
reservas e fórmula antes de assinar. O servidor verifica as assinaturas do
comprador, a sessão, as reservas atuais e os UTXOs antes de persistir e
retransmitir a transação. O Core dispensa assinaturas somente nas duas entradas
de reservas de uma `LiquiditySwap` válida a partir de v18. Todas as entradas de
saldo livre e os encerramentos continuam exigindo assinaturas. Os ativos são
conservados independentemente; não há pagamentos parciais.

Preparações expiram em 15 minutos e ficam em memória; reiniciar descarta as
que ainda não foram aceitas. Uma troca aceita ocupa as reservas até confirmar;
preparações concorrentes com as mesmas entradas devem ser refeitas. Envios
aceitos, comprovantes e identificação das pools persistem. Repetir as mesmas
assinaturas recupera o comprovante sem criar outra operação.

O comprovante `/liquidez/operacao/{id}` distingue pendente, validado,
confirmado e rejeitado. Uma aprovação sem bloco não muda saldos ou reservas.
Após a confirmação, a pool segue as duas primeiras saídas da troca como novas
reservas. Mudanças de cadeia removem confirmações antigas e impedem o uso de
reservas órfãs. Os endpoints de criação/movimentação de tokens e a exigência
do criador na tela de movimentação continuam com suas regras existentes.

## Candles e livro caixa da pool

Na lista de pools, **Ver candles e livro caixa** abre
`/Liquidity/Market?poolId=<id>`. A tela consulta `MarketData` a cada cinco
segundos, sem abrir carteira, e pausa as consultas quando a aba está oculta.
Os intervalos disponíveis são 1, 5 e 15 minutos, 1 hora e 1 dia.

Os candles representam o preço A/B das reservas após trocas confirmadas,
agrupadas pelo horário do bloco. Não são candles de uma bolsa externa nem
um livro de ofertas. Períodos sem trocas não produzem candles artificiais.
A resposta inclui até 200 candles e as 100 movimentações mais recentes.
O livro caixa mostra criação, troca, bloqueio e encerramento, com entradas e
saídas dos dois ativos em precisão atômica. Operações pendentes, validadas e
rejeitadas ficam identificadas e não participam dos candles. A cadeia atual
é relida em cada consulta para refletir reorganizações; o catálogo abrange
as pools registradas nesta instalação do DEX.

Validação da interface: `node Tests/PovixDexPoolMarketUiRegression.js`.

A tela de mercado também integra a carteira, a cotação, o mínimo a receber,
a taxa e a revisão/assinatura da troca da pool aberta. O envio permanece na
tela e mostra seu status automaticamente a cada cinco segundos: pendente,
validado, confirmado ou não confirmado. O painel mostra recebimento, taxa,
confirmações, altura e hash do bloco, com acesso ao comprovante completo.
A última transação da pool é preservada na sessão do navegador para continuar
a consulta após recarregar, sem reabrir a carteira. Consultas falhas preservam
o último status; uma reorganização limpa confirmações e hashes anteriores. As informações dos dois ativos mostram
nome, símbolo, identificador, precisão, oferta total dos tokens e descrição
do perfil local assinado, quando vinculada à criação confirmada.

O preço e os candles usam sempre Reserva A ÷ Reserva B, ajustando as casas
decimais de cada ativo, independentemente do sentido selecionado na troca.
