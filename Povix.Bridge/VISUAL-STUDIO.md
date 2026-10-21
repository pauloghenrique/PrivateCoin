# Povix.Bridge em C# — Visual Studio

O cliente de operação é um aplicativo de console C# para **.NET Framework 4.8**, sem Node.js. Usa Nethereum para BNB/EIP-712/keystore EVM e o adaptador C# existente para validar o consenso POVIX em processo. Os contratos na BNB continuam em Solidity; mudar a linguagem do cliente não muda o contrato publicado ou o lastro.

O único aplicativo da ponte é `Povix.Bridge`: a validação nativa está compilada no próprio `Povix.Bridge.exe`, sem projeto ou executável `Povix.Bridge.Native` separado. `PrivateCoin.Core` é a biblioteca compartilhada do protocolo e `Povix.Bridge.Tests` contém apenas testes.

## Abrir e compilar

1. Instale Visual Studio 2022 com **Desenvolvimento para desktop com .NET** e o Developer/Targeting Pack do **.NET Framework 4.8**.
2. Abra `Povix.Bridge/Povix.Bridge.sln`. Defina `Povix.Bridge` como projeto de inicialização.
3. Restaure os pacotes NuGet e compile em **Release / Any CPU**. `NuGet.Config` na raiz mantém os pacotes em `packages`, compartilhada com os demais projetos.
4. Execute `Povix.Bridge/bin/CSharp/Release/Povix.Bridge.exe --help`. Para depurar com F5, informe o comando na aba **Depurar → Argumentos de linha de comando** das propriedades do projeto.
5. Distribua a pasta de saída inteira, incluindo DLLs, `.exe.config` e subpasta `contracts`. Não copie apenas o EXE.

Também há integração na solução principal `PrivateCoin.slnx`; a `.sln` separada não exige suporte ao formato `.slnx`.

Em um Developer Command Prompt com NuGet CLI:

```text
nuget restore Povix.Bridge/Povix.Bridge.sln -NonInteractive
msbuild Povix.Bridge/Povix.Bridge.sln /p:Configuration=Release
```

O build C# não precisa de npm, Node.js ou solc. ABI e bytecode pré-compilados com Solidity 0.8.30 estão em `contracts/artifacts`; o manifesto SHA-256 é conferido ao carregar. Alterações nos contratos exigem recompilação e revisão de artefatos/manifesto juntos; compilar C# não recompila Solidity.

## Configuração e operadores

Copie `production.config.example.json` para `production.local.json`. Os placeholders são inválidos de propósito. Use caminhos absolutos nos snapshots. Leia [PRODUCTION.md](PRODUCTION.md): continuam **dois operadores e duas assinaturas**, fontes independentes, 64 confirmações BNB/120 POVIX em produção, limites e espera mínima de 24 horas para resgates. Há compatibilidade com comitês maiores já aceitos pelo contrato.

Cada operador gera suas chaves na própria máquina:

```text
Povix.Bridge.exe create-native-operator operador-povix.local.json
Povix.Bridge.exe create-operator operador-bnb.local.json
Povix.Bridge.exe network
```

As senhas vêm de `NATIVE_KEYSTORE_PASSWORD` e `BRIDGE_KEYSTORE_PASSWORD`, no ambiente local; a criação exige pelo menos 16 caracteres. Não passe senhas como argumentos, não salve em scripts/Git e não envie chaves privadas ao coordenador. Cadastre somente os endereços e chaves públicas em `operators`, `nativeAdministrators` e `nativeSigningPublicKeys`.

Os keystores novos usam scrypt N=131072/r=8/p=1. O nativo preserva o formato anterior RSA 3072/PKCS#8/AES-256-GCM; o EVM usa JSON V3/AES-128-CTR. Arquivos são criados sem sobrescrever, com ACL exclusiva da conta Windows atual ou permissão 600 em Linux/Mono. A leitura recusa acesso compartilhado. No Windows, ajuste a ACL de keystores antigos antes do uso; não há conversão automática. Em Linux, a conferência de permissões usa `stat`, sem shell ou Node.js.

Perder uma das duas chaves pode bloquear fundos permanentemente; não foi adicionada rotação/migração de operadores.

## Operação

Substitua `node src/cli.mjs` por `Povix.Bridge.exe` nos comandos de produção. Saída JSON vai para stdout; erros e hashes enviados vão para stderr. Se houver interrupção após envio, confira o hash antes de tentar novamente.

```text
Povix.Bridge.exe audit production.local.json
Povix.Bridge.exe propose-native production.local.json mint referencia.json coordenador.journal
Povix.Bridge.exe sign-native production.local.json proposta.json operador-povix.local.json operador.journal
Povix.Bridge.exe complete-native production.local.json proposta.json assinaturas.json
Povix.Bridge.exe sign-release production.local.json queima.json operador-bnb.local.json operador.journal PRAZO_UNIX
Povix.Bridge.exe release production.local.json queima.json autorizacoes.json relay-bnb.local.json
Povix.Bridge.exe execute-release production.local.json ID_DA_QUEIMA relay-bnb.local.json
```

Salve a proposta e reúna as duas assinaturas públicas em um array JSON. Emissão/queima nativa retorna a transação para submissão ao nó POVIX; não há novo relayer automático. `release` agenda e `execute-release` transfere depois da espera e das verificações do contrato. Qualquer operador pode pausar/cancelar filas; a retomada exige ambos.

Inclui publicação de cofre/factory, depósito com aprovação exata, configuração fundadora, mint/register/pause/resume, atualização de proposta expirada, assinatura, queima/autorização pelo titular, resgate, auditoria e aprovação/configuração de outros ativos. Os comandos legados de teste que reuniam chaves para emitir não são oferecidos pelo cliente C#; use propostas com assinaturas independentes.

## Journal e recuperação

O cliente usa **`.journal`** por rede/cofre/token e operador, com trava exclusiva, registros encadeados por hash e flush durável **antes** de devolver assinatura. Recusa conflito, concorrência, corrupção e registro final incompleto. O hash detecta corrupção acidental; não substitui acesso restrito, backups e monitoramento contra quem pode alterar o disco.

Esse formato **não é o SQLite da versão JavaScript**. Não renomeie SQLite nem inicie journal vazio em uma ponte já operando sem reconciliar independentemente o histórico e as decisões. Não há importação automática: interrompa assinaturas, preserve os bancos anteriores e conclua reconciliação/revisão antes da troca de cliente. O código recusa extensões antigas e não apaga ou conserta registros automaticamente.

Claims no consenso continuam impedindo mint repetido após perda do journal, mas isso não autoriza ignorar decisões anteriores. Em corrupção ou perda, pare e siga o procedimento de incidentes. Não trunque registros para forçar uma assinatura. A leitura é limitada a 64 MiB; arquivamento/compactação exige um procedimento revisado que preserve as decisões anteriores.

## Validação e situação de produção

Execute `bin/Tests/Release/CSharpRegression.exe self-test` para rodar as regressões locais sem Node.js ou RPCs. `Povix.Bridge.Tests.csproj` contém regressões C# de quorum, durabilidade, concorrência, escopo e precisão Int64. `test/csharp-production.integration.mjs` usa Node/Ganache **apenas como infraestrutura de teste**, invocando os EXEs C#: não é dependência do cliente em operação. As ferramentas de teste ficam em `bin/Tests`, separadas do pacote do operador em `bin/CSharp`; não distribua a pasta de testes. Testa keystores entre implementações, assinaturas C# verificadas pelo contrato/Ethers, depósito, ledger POVIX, claims, pausa e resgate com espera simulada. A infraestrutura JavaScript anterior permanece para comparação e manutenção.

É uma migração candidata, sem publicação de contratos ou chaves reais. Limitações federadas, reorganizações, ausência de rotação e requisitos de revisão independente de PRODUCTION.md continuam válidos. Dependências .NET e nova persistência também precisam dessa revisão; build aprovado não significa liberação para receber fundos reais.

## Inspeção experimental de provas

`Povix.Bridge.exe inspect-receipt-proof prova.json` verifica apenas inclusão de recibo em uma raiz fornecida. Não autentica consenso nem autoriza emissão ou saque. A ponte continua federada. Veja [RECEIPT-INCLUSION.md](docs/RECEIPT-INCLUSION.md).
