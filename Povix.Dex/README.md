# Cadastro e movimentação de tokens no Povix.Dex

A tela `/tokens/criar` cadastra tokens nativos de quantidade fixa na **blockchain
POVIX existente**, com as regras `TokenCreate` do `PrivateCoin.Core`. A página
inicial redireciona para esse cadastro. Não há emissão de POVIX, mineração nem
criação de uma rede separada pelo DEX.

Cada usuário importa e desbloqueia a própria carteira no navegador. A senha e
as chaves privadas não são enviadas ao servidor. O Core prepara a transação com
chaves públicas e UTXOs confirmados; o navegador confere o conteúdo apresentado,
assina com Web Crypto RSA/SHA-256 e envia apenas as assinaturas. O nó valida a
transação novamente, salva a fila e a propaga por `PeerNode.BroadcastAsync`.
O comprovante `/tokens/registro/{id}` diferencia pendente, confirmado e retirado
da fila. Só um bloco recebido e validado pode confirmar o registro.

## Configurar a rede

1. Execute o projeto ASP.NET MVC 5 em IIS/IIS Express com .NET Framework 4.8 e
   HTTPS. Restaure os pacotes NuGet de `packages.config` antes de compilar.
2. Configure `PeerSeeds` em `Web.config` com pares **reais** da rede existente,
   separados por vírgula, ou use `POVIX_PEERS`. Os pares devem usar o consenso 3
   e o gênese fixado no Core. Não são fornecidos endpoints fictícios.
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

## Abrir o arquivo de carteiras

Na tela, escolha `wallet.dat` ou `wallets.dat`, clique em **Ler carteiras do
arquivo**, selecione uma das carteiras listadas e clique em **Abrir carteira
selecionada**. A seleção é explícita, mesmo quando o arquivo contém uma única
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
Defina uma senha para guardar a cópia cifrada no navegador. O original permanece
inalterado. Se o navegador solicitar permissão de acesso à rede local, permita
a conexão com o auxiliar.

Para escolher o arquivo correto, clique em **Pasta da carteira** no Desktop:
o Explorador destaca o `wallets.dat` efetivamente usado, inclusive em instalações
com uma pasta antiga. Se a leitura falhar, o DEX mostra um código específico.
Use o diagnóstico local `Povix.WalletBridge.exe --check-wallet "C:\caminho\wallets.dat"`
para verificar a proteção do Windows e o formato antes da criação da cópia cifrada.
Consulte os códigos e as instruções no README do auxiliar.

Também é possível listar as carteiras da cópia cifrada já salva no navegador:
deixe o seletor de arquivo vazio e informe a senha existente. Backups antigos
contendo uma única carteira continuam compatíveis.

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
backup cifrado em armazenamento local. Desbloquear mantém o material privado
em memória até bloquear, sair ou recarregar a página. Não há scripts externos
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

O botão de gerar endereço de recebimento ainda cria uma chave local, salva
cifrada **antes** da assinatura. O backup atualizado preserva todas as
carteiras da coleção e modifica apenas a selecionada. A revisão pede seu
download. Esse backup inclui as chaves novas de recebimento que não existem no
`wallets.dat` original nem podem ser recuperadas apenas pela frase antiga.
Use o backup atualizado para restaurar a carteira no DEX em outro navegador.
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

## Movimentação dos tokens pelo criador

Acesse **Movimentar tokens** no menu ou **Movimentar este token** no
comprovante da criação. Abra o `wallets.dat` pelo auxiliar local Windows ou
o `.povixwallet` atualizado e selecione a carteira que criou o token. A tela
consulta as criações confirmadas na blockchain, reconhece o criador pela
chave que assinou a primeira entrada da criação (da qual deriva o
identificador do token) e mostra o saldo confirmado e o disponível.

Escolha o token, a quantidade, o endereço de destino e a prioridade da taxa.
O botão de revisão fica disponível quando a carteira está aberta e a rede
está pronta. Ao clicar, a tela valida a seleção do token, os campos e os dois saldos,
mostrando a causa precisa se faltar quantidade, destino, taxa ou saldo livre.
O saldo em POVIX paga a taxa; ele não substitui o saldo do token. Se o token
aparece sem saldo nas chaves abertas e seu endereço foi gerado no navegador,
abra o `.povixwallet` atualizado daquela criação. Tokens reservados por envios
pendentes aguardam a confirmação. Alterações dos campos e da taxa atualizam
a revisão pelos eventos `input` e `change`.
O envio só usa tokens e POVIX confirmados disponíveis nas chaves dessa
carteira. Se a criação enviou o token a outra carteira, o criador não pode
gastar o saldo dela. Se o endereço de recebimento foi gerado pelo DEX, use
o backup atualizado que contém essa chave.

A revisão mostra quantidade, taxa em POVIX, troco do token, troco em POVIX e
seus endereços. Ambos os trocos voltam ao primeiro endereço já existente da
carteira aberta; a preparação não gera chaves novas. Baixe o backup, confira
os dados e confirme antes de assinar. O navegador reconstrói o payload
nativo `TokenTransfer` e assina cada entrada localmente. Também assina uma
autorização separada, vinculada à rede, ao token, ao identificador da
preparação e ao payload completo. O servidor valida essa autorização com a
chave do criador registrada na blockchain antes de aceitar ou retransmitir.
Conhecer a chave pública, receber tokens ou assinar entradas de outra carteira
não concede essa permissão. Chaves privadas e senha continuam no dispositivo.

A exclusividade do criador é uma regra **dessa tela e dos endpoints do DEX**.
O consenso da rede e as transferências feitas por proprietários fora dela
mantêm suas regras atuais. A assinatura do criador não permite gastar tokens
ou POVIX de terceiros sem as assinaturas das entradas correspondentes.

O comprovante começa pendente e confirma somente após um bloco validado,
mostrando hash, altura e confirmações. O histórico lista os últimos 20 envios
desse token aceitos por esta instalação do DEX, incluindo pendentes e
rejeitados após mudança de cadeia; ele não é um histórico global da rede.
Preparações expiram em 15 minutos, exigem a mesma sessão e são descartadas ao
reiniciar. Envios já aceitos e seus comprovantes persistem; repetir o mesmo
envio recupera o comprovante. Falhas temporárias preservam a revisão para
uma nova tentativa, sem criar outra transferência.

## Verificação

Veja `../Tests/README.md` para executar a regressão do DEX. Ela usa carteiras e
nós locais descartáveis; não publica tokens na rede configurada pelo operador.
O navegador exige HTTPS (ou localhost para desenvolvimento) e suporte a Web
Crypto, BigInt e armazenamento local. A hospedagem de produção permanece no
ambiente IIS do projeto.
