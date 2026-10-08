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
