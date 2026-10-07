# Wallet startup regression

Run from the repository root with Mono installed:

```sh
xbuild PrivateCoin.Desktop/PrivateCoin.Desktop.csproj /p:Configuration=Release /verbosity:minimal
mkdir -p work/wallet-startup
mcs -out:work/wallet-startup/WalletStartupRegression.exe \
  -r:PrivateCoin.Core/bin/Release/PrivateCoin.Core.dll \
  -r:System.Core -r:System.Security -r:System.Runtime.Serialization -r:System.Web.Extensions \
  Tests/WalletStartupRegression.cs PrivateCoin.Desktop/WalletStore.cs \
  PrivateCoin.Desktop/RecoveryPhraseGenerator.cs
MONO_PATH=PrivateCoin.Core/bin/Release mono work/wallet-startup/WalletStartupRegression.exe
```

The standalone regression uses disposable fixtures in a temporary directory. It
checks imported keys, deterministic address continuation, transaction signing,
mixed wallet persistence, confirmed/projected balances, schema 3 compatibility,
and rejection of incorrect addresses, checksums, and public balances. It prints
the load time for 41 wallets and an estimate of the previous RSA regeneration
cost based on one wallet. Timing is diagnostic, not a pass/fail threshold.

## Povix Swap

Run the swap regression with Node.js, without installing dependencies:

```sh
node Tests/PovixSwapRegression.js
```

It covers decimal input, fees, minimum receipts, atomic rounding, invalid pairs,
insufficient balances, confirmation and demo reset. The swap uses fixed example
prices and never submits blockchain transactions.

## Tesouraria de liquidez própria

```sh
python3 Tests/PovixLiquidityRegression.py
```

Usa apenas a biblioteca padrão do Python e fixtures locais. Confere reservas
concorrentes, precisão, persistência, idempotência, proteção de códigos, alocação
exclusiva de endereços, provas não reutilizáveis, depósito/pagamento verificados,
contrato/rede incorretos, reorgs, confirmações, autenticação administrativa e
origem HTTP. Não move fundos nem substitui validação integrada no IIS e nas
redes de produção. Veja `PrivateCoin.Swap/README.md`.
