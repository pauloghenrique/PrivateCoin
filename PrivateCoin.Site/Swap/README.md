# POVIX Swap — primeira integração

A página `/Home/Swap` consulta rotas de BTC nativo, ETH nativo e BNB na BNB Smart Chain pelo THORChain. O cliente conecta sua carteira; o site não recebe frases de recuperação, chaves privadas nem depósitos em uma conta própria. A moeda da blockchain POVIX ainda não está listada: sua integração requer um projeto separado.

## Preparar os arquivos do site

```sh
cd PrivateCoin.Site/Swap
npm ci
npm test
npm run build
```

O build produz `Views/Home/Swap.cshtml`, `Scripts/povix-swap.js` e uma versão independente em `Swap/dist`. O bundle e a view estão versionados para que a publicação normal do projeto ASP.NET MVC não dependa de Node no servidor. Depois de editar `src` ou `markup.html`, execute o build novamente e inclua os arquivos gerados na alteração.

Para visualizar a página independente:

```sh
python3 -m http.server 8080 --directory dist
```

Abra `http://localhost:8080` num navegador com a carteira instalada. A prévia independente sempre mantém o envio desativado. ETH e BNB usam uma carteira EIP-1193, como MetaMask ou Vultisig; Bitcoin usa o adaptador `window.vultisig.bitcoin`. Para recebimento BTC, esta versão aceita apenas endereços mainnet SegWit/Taproot (`bc1`). As moedas EVM precisam estar na respectiva rede original. Uma conexão EVM não habilita Bitcoin.

## Situação da entrega

- Cotações usam valores retornados pelo serviço; erros de rede nunca são substituídos por preços fictícios.
- Quantidades são calculadas com inteiros; o THORChain usa precisão de 8 casas. Nesta versão, quantidades inferiores a essa precisão não são aceitas, mesmo em ETH/BNB.
- O envio é **desativado por padrão** (`PovixSwapExecutionEnabled=false`). Não houve transação real nem validação com uma extensão de carteira real durante o desenvolvimento.
- A rede do ambiente de desenvolvimento bloqueou tanto `gateway.liquify.com` (o gateway usado pela aplicação) quanto `thornode.ninerealms.com` com HTTP 403. A integração HTTP real, disponibilidade do gateway, CORS e latência precisam ser verificadas no ambiente de hospedagem. Os testes locais usam respostas controladas e carteiras substitutas.
- O build ASP.NET/.NET Framework 4.8 precisa ser executado no ambiente Windows/IIS ou no pipeline do repositório; esse runtime não está instalado no ambiente desta entrega.
- A preparação de envio EVM usa o Router `depositWithExpiry`. O adaptador Bitcoin delega construção/assinatura à extensão Vultisig, com memo UTF-8. A ordenação de saídas, troco e reembolso Bitcoin precisa ser conferida na versão de carteira instalada antes de ativar envio.

## Integração e habilitação de envio

A aplicação recebe pools, vaults e cotações de `https://gateway.liquify.com/chain/thorchain_api/thorchain`. Antes de assinar, verifica pools disponíveis, pausas nas duas redes, recebimento mínimo, validade da cotação e se os endereços atuais de vault/Router coincidem. Depois da revisão do usuário, renova a cotação e rejeita uma redução do mínimo a receber. Uma conta ou rede alterada invalida a revisão. Nenhuma taxa de afiliado está configurada.

Quando o envio for habilitado no aplicativo ASP.NET, verifica-se também o código do endereço EVM de destino pelos RPCs `https://ethereum-rpc.publicnode.com` e `https://bsc-rpc.publicnode.com`. Esta edição limita o destino EVM a uma carteira sem contrato. O endereço de origem precisa ter saldo para o valor e o gas exigido pela carteira.

Antes de mudar a configuração para `true`, valide no ambiente de implantação: acesso/CORS dos endpoints, extensões suportadas, os seis sentidos de troca, endereços de origem e destino, mínimo a receber, reembolso, pausas de rede, expiração e tratamento de recusas. O valor da configuração sozinho não comprova prontidão para operar. A interface acompanha as etapas informadas pelo THORChain e não chama uma transação assinada de recebimento confirmado.

O funcionamento da interface depende do protocolo e das redes externos. Conectar uma carteira não torna a operação isenta de requisitos legais; o enquadramento da atividade precisa ser definido conforme o país e o modelo de negócio.

## Referências da integração

- [THORChain: cotação, validade e preço mínimo](https://dev.thorchain.org/swap-guide/quickstart-guide.html)
- [THORChain: envio EVM e estrutura das transações Bitcoin](https://dev.thorchain.org/concepts/sending-transactions.html)
- [Vultisig: interface de extensão](https://docs.vultisig.com/developer-docs/vultisig-extension-integration-guide)
- [Vultisig: implementação do adaptador UTXO](https://github.com/vultisig/vulticonnect/blob/main/src/utils/transaction-provider/utxo/index.ts)
