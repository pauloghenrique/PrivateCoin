# Abertura local de wallet.dat / wallets.dat

O `Povix.WalletBridge` permite escolher diretamente o arquivo de carteiras do
Desktop na tela do DEX e selecionar uma carteira salva nele. O arquivo do
Desktop usa DPAPI `CurrentUser`, com a mesma entropia `PrivateCoin` de
`PrivateCoin.Desktop/WalletStore.cs`. Essa proteção só pode ser aberta no
ambiente do usuário Windows que possui a carteira, e não pelo site remoto.

## Iniciar no Visual Studio

No Windows, instale a carga **Desenvolvimento para desktop com .NET** e o
Developer Pack/Targeting Pack do **.NET Framework 4.8** no Visual Studio.
Abra a solução ou diretamente `Povix.WalletBridge.csproj`.

1. No Gerenciador de Soluções, clique com o botão direito em
   **Povix.WalletBridge** e escolha **Definir como Projeto de Inicialização**.
2. Selecione **Debug** e **Any CPU** na barra de ferramentas e compile o projeto.
3. Pressione **F5** (com depuração) ou **Ctrl+F5** (sem depuração).
4. Na janela, informe o endereço do DEX aberto no navegador, como
   `https://localhost:44355`, e pressione Enter. Mantenha a janela aberta enquanto
   usa o arquivo no DEX.

O projeto declara as configurações **Debug** e **Release** e gera o executável
em `bin\Debug\` ou `bin\Release\`. Após atualizar uma versão anterior,
recarregue o projeto ou feche e reabra a solução para o Visual Studio reconhecer
as configurações. Abrir o `.csproj` diretamente também permite iniciar o auxiliar
sem depender da versão de Visual Studio necessária para abrir uma solução `.slnx`.

## Iniciar o executável

No computador Windows que possui o arquivo, abra o executável compilado. Sem
argumentos, ele solicita o endereço do DEX na própria janela. Também é possível
informar a origem exata na linha de comando:

```powershell
.\Povix.WalletBridge\bin\Release\Povix.WalletBridge.exe --origin https://localhost:44355
```

Para outra instalação, substitua o endereço pela origem HTTPS real do DEX.
O processo usa somente `127.0.0.1:4781`; não requer uma porta pública nem
alterações no roteador. Mantenha a janela aberta durante a leitura do arquivo.
Se a porta 4781 estiver ocupada por outra instância, feche a janela anterior
antes de iniciar novamente. Erros de endereço ou de porta são mostrados na
janela, que aguarda Enter antes de fechar quando iniciada interativamente.

No Developer Command Prompt do Visual Studio, o projeto pode ser compilado
separadamente, sem restaurar pacotes dos outros projetos:

```cmd
msbuild Povix.WalletBridge\Povix.WalletBridge.csproj /t:Rebuild /p:Configuration=Debug /p:Platform=AnyCPU
Povix.WalletBridge\bin\Debug\Povix.WalletBridge.exe
```

## Abrir a carteira no DEX

Na página `/tokens/criar`:

1. Escolha `wallet.dat` ou `wallets.dat`. O Desktop usa normalmente
   `%LOCALAPPDATA%\PrivateCoin\.privatecoin\wallets.dat`.
2. Defina uma senha de pelo menos 10 caracteres para guardar a cópia cifrada
   no navegador e clique em **Ler carteiras do arquivo**. Essa senha protege
   a cópia local do DEX; a abertura do original depende do seu usuário Windows.
3. Escolha uma carteira na lista e clique em **Abrir carteira selecionada**.

Se o navegador solicitar acesso à rede local, permita a conexão com o auxiliar.
O site deve usar HTTPS ou localhost. O auxiliar só aceita a origem informada no
início; reinicie-o com a origem correta se mudar o domínio ou a porta do DEX.
Ele pode ser encerrado após a leitura: a assinatura continua local no navegador.

O auxiliar recebe o arquivo protegido apenas pelo endereço de loopback, abre
o conteúdo em memória com DPAPI e devolve uma cópia da coleção completa cifrada
com PBKDF2/AES/HMAC. Não recebe caminhos, não altera o arquivo original, não
salva material privado em disco e não faz conexões externas. Requisições de
outras origens ou com outro `Host` são rejeitadas. Há limites de tamanho,
tempo de leitura e duas requisições simultâneas.

O original pode ter até 6 MB; os backups cifrados aceitos pelo navegador podem
ter até 9 MB. A coleção aceita até 100 carteiras, com até 1.000 chaves por
carteira. Somente as chaves da carteira selecionada são usadas para assinar.
O backup atualizado preserva as demais carteiras da coleção e acrescenta os
novos endereços da carteira selecionada.

Sem o auxiliar, também é possível exportar uma coleção cifrada localmente com
`Povix.Dex/Tools/ExportDexWallet.ps1` e abri-la no mesmo seletor. Backups antigos
do DEX contendo uma única carteira continuam aceitos.

Os testes automatizados do conversor substituem somente a abertura DPAPI no
limite do Windows, pois o ambiente de testes é Linux. A leitura DPAPI real e
a permissão de rede local no navegador precisam ser verificadas no Windows.
