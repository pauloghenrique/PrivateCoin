# Instalador do PrivateCoin.Desktop

Gera um `.exe` para Windows 10/11 x86 e x64. O aplicativo e o Core são
compilados em Release, sem dependências NuGet para o Desktop. O instalador
inclui os dois binários, a configuração, instruções, atalhos e desinstalador.
O runtime exigido é o **.NET Framework 4.8**, não o .NET 8/9/10.

## Gerar no Windows

Instale Visual Studio Build Tools (MSBuild e targeting pack do .NET Framework
4.8) e NSIS 3. Execute na raiz do repositório:

```powershell
# Inclui o redistribuível do .NET para instalação offline.
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Offline

# Pacote menor: baixa o .NET da Microsoft somente se faltar no computador.
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build-installer.ps1
```

Os parâmetros `-MSBuildPath` e `-MakensisPath` permitem indicar os executáveis.

## Gerar no Linux

Requisitos: Mono com referências 4.8/xbuild, NSIS 3, Python 3 e curl.

```bash
bash installer/build-installer.sh --offline
bash installer/build-installer.sh
```

Os resultados ficam em `artifacts/installer/`, com nome versionado e SHA-256.
O .NET fica em `artifacts/prerequisites/`. São diretórios já ignorados pelo Git.
O build Linux usa referências Mono 4.8; valide o resultado em Windows antes de
distribuir. Não inclua Mono no instalador Windows.

## Dependências e instalação

- Detecta o valor `Release >= 528040` no registro, aceitando .NET 4.8 e 4.8.1.
- Se faltar o .NET, usa o redistribuível offline ou o baixa por HTTPS do CDN
  oficial da Microsoft. Confere o SHA-256 fixado e a assinatura Authenticode
  da Microsoft antes de executar. A versão em inglês do redistribuível não
  altera o idioma em português do aplicativo.
- A instalação do .NET exige autorização de administrador. Cancelamento ou
  erro interrompe a instalação do aplicativo. Códigos 3010/1641 indicam que
  será necessário reiniciar; o instalador não abre o aplicativo automaticamente.
- Instala o aplicativo para o usuário atual em
  `%LOCALAPPDATA%\Programs\PrivateCoin`, permitindo o atualizador existente.
  Não precisa de Visual Studio, Mono, banco de dados ou servidor web no destino.
- Preserva a configuração existente em atualizações. Não inclui nem apaga
  `wallets.dat`, `Blockchain.json`, `recovery.dat`, `peers.dat`, `node-id.dat`,
  `finality-votes.dat`, seus arquivos de lock ou `.privatecoin`.
  Não abre portas públicas nem altera automaticamente o firewall.
- O instalador e desinstalador aceitam `/S`. Se faltar o .NET, a elevação UAC
  continua necessária. Códigos de sucesso: 0; 3010 significa reinicialização.
- A conexão à rede pública depende dos pares configurados em `App.config`.
  O instalador não garante disponibilidade do seed ou conectividade P2P.

O redistribuível é obtido da Microsoft; o SHA-256 também é registrado na
[referência dotnet48 do Winetricks](https://github.com/Winetricks/winetricks/blob/master/src/winetricks).
Não substitua o hash ou desative verificações para contornar um download inválido.
Para distribuir publicamente, assine o instalador final com seu certificado
Authenticode e gere novamente seu arquivo `.sha256` após a assinatura.

## Validação no Windows

Neste ambiente Linux, Core e Desktop compilaram em Release. Passaram nove
checks funcionais locais (incluindo assinaturas RSA, rejeição de gasto duplo,
serialização e sincronização entre dois nós). As duas edições do instalador
foram geradas; a extração confirmou os binários, a configuração, o script de
pré-requisito e o SHA-256 do redistribuível, sem dados de carteira. Também foram
verificadas a sintaxe PowerShell e, com a leitura do registro simulada, as
rotas de .NET já instalado e rejeição de um redistribuível com hash inválido.

A checagem adicional de Authenticode pelo `osslsigncode` no Linux não pôde
validar a cadeia: o trust store Debian não contém as raízes Microsoft usadas
na assinatura. O redistribuível veio do CDN oficial por HTTPS e teve o hash
fixado conferido. O helper Windows mantém a exigência de uma assinatura
Microsoft válida; ela não foi desabilitada. O instalador final da aplicação
ainda não está assinado, e os fluxos reais de Windows/UAC não foram executados.

Use VMs descartáveis para estes cenários antes de publicar o pacote:

1. Windows com .NET 4.8/4.8.1: instalar, abrir o aplicativo e criar uma carteira.
2. Windows sem .NET 4.8: testar o download na edição Online e a edição Offline
   sem internet; verificar UAC, cancelamento, falha de download e reinicialização.
3. Reinstalar após alterar os seeds; verificar que a configuração e a carteira
   continuam disponíveis. Tentar instalar com o aplicativo aberto e confirmar
   que a instalação é interrompida.
4. Desinstalar pelo Windows, confirmar a preservação da carteira e reinstalar.
5. Conferir instalação/desinstalação silenciosa, incluindo código 3010.

Esses cenários dependem de Windows e não são validados apenas pela compilação
ou extração do instalador no ambiente Linux.
