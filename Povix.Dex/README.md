# Cadastro de tokens no Povix.Dex

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

O arquivo original do Desktop é protegido pelo usuário do Windows. Para abri-lo
diretamente, execute o [Povix.WalletBridge](../Povix.WalletBridge/README.md) no
seu Windows com a origem HTTPS do DEX autorizada. A leitura passa exclusivamente
por `127.0.0.1:4781`; o arquivo e a senha não são enviados ao servidor do DEX.
Defina uma senha para guardar a cópia cifrada no navegador. O original permanece
inalterado. Se o navegador solicitar permissão de acesso à rede local, permita
a conexão com o auxiliar.

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

Endereços novos de recebimento e troco são gerados localmente e salvos cifrados
**antes** da assinatura. O backup atualizado preserva todas as carteiras da
coleção e modifica apenas a selecionada. A revisão pede seu download. Esse
backup inclui as chaves novas que não existem no `wallets.dat` original nem
podem ser recuperadas apenas pela frase antiga. Use o backup atualizado para
restaurar a carteira no DEX em outro navegador. Não apague o armazenamento
local antes de guardá-lo. Evite usar a mesma carteira simultaneamente em
instalações diferentes: uma transação concorrente pode consumir seu UTXO.

Nome: até 64 caracteres; símbolo: 1–10 letras A–Z; precisão: 0–8 casas; quantidade:
positiva, limitada a `Int64.MaxValue` em unidades atômicas. A conversão não usa
ponto flutuante nem arredondamento. Símbolos podem se repetir: o identificador
de 64 caracteres distingue os tokens. Taxas seguem as opções econômica, normal
e prioritária do Core.

Preparações expiram em 15 minutos e ficam vinculadas à sessão. Reiniciar o
processo descarta preparações ainda não enviadas; as transações já aceitas
permanecem no cache e são retransmitidas na sincronização. Repetir o envio da
mesma preparação já aceita retorna o mesmo comprovante.

## Verificação

Veja `../Tests/README.md` para executar a regressão do DEX. Ela usa carteiras e
nós locais descartáveis; não publica tokens na rede configurada pelo operador.
O navegador exige HTTPS (ou localhost para desenvolvimento) e suporte a Web
Crypto, BigInt e armazenamento local. A hospedagem de produção permanece no
ambiente IIS do projeto.
