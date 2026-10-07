# Tesouraria Povix Swap

Este serviço implementa ordens de troca **POVIX nativo ↔ USDT EVM com 6 casas decimais**, lastreadas nas carteiras próprias da tesouraria. O preço é definido pelo operador (`povix_usdt`); não é apresentado como cotação de mercado. O serviço está **desativado por padrão**, sem carteiras, contrato ou preço reais configurados.

A implementação é uma primeira versão com **pagamento manual verificado**: o operador envia a moeda pela carteira externa e informa o identificador da transação. O serviço não assina transações, guarda chaves ou promete liquidação automática. Não há bridge ou token POVIX ERC-20 implícito.

## Fluxo

1. O cliente informa quantidade e endereço de destino. A API calcula preço e taxa em inteiros, verifica as reservas confirmadas e fornece uma cotação por 120 segundos.
2. A confirmação cria uma ordem persistente, reserva o valor de saída e aloca um endereço exclusivo. A API revalida as reservas dentro de uma transação SQLite serializada, evitando reservas concorrentes acima do saldo.
3. O cliente envia exatamente o valor indicado na rede indicada e informa o hash do depósito. A API confere a rede, o contrato, o destino, o valor, a inclusão canônica e as confirmações. Em POVIX, recompensas de emissão e operações de stake não são aceitas como depósitos.
4. O operador consulta a fila, confere novamente o depósito e paga pela carteira da tesouraria.
5. O operador informa o hash do pagamento. A API verifica o pagamento e o depósito novamente. Somente então a ordem se torna `completed` e sua reserva é liberada. Uma transação não pode liquidar duas ordens.

As reservas vêm de consultas às blockchains; não existe endpoint para fabricar saldo. Endereços de depósito devem ser novos, vazios, controlados pela tesouraria e nunca reutilizados. A lista de depósitos é um subconjunto da lista de endereços da tesouraria. Em POVIX, inclua também os novos endereços de troco do Desktop na lista da tesouraria, para que as reservas permaneçam visíveis.

## Instalação

Requisitos: Python 3.10+, ASP.NET MVC/.NET Framework 4.8 no IIS, nó POVIX sincronizado com peers, RPC confiável da rede EVM e carteira externa de cada ativo. O Python usa somente a biblioteca padrão.

Copie `config.example.json` para um arquivo de configuração privado do operador. Mantenha `enabled: false` durante a configuração. Não coloque arquivos SQLite, backups, configurações reais ou tokens na raiz pública do IIS.

Configure:

- `public_origin`: origem HTTPS exata do site.
- `povix_usdt`: preço próprio em string decimal, com até oito casas. O preço não pode ser zero e não é inferido do simulador.
- `fee_bps`: taxa em pontos base. O exemplo usa 30 (= 0,30%); confirme a taxa operacional antes de ativar.
- `minimum`, `maximum`: limites por moeda; `reserve_buffer_atomic`: reserva para despesas/margem. Taxas do envio do depósito ficam por conta do cliente; a tesouraria deve pagar o gás de saída adicionalmente ao valor cotado.
- `assets.POVIX`: API interna `/SwapNetwork`, identidade de rede e gênese existentes, confirmações e listas de endereços.
- `assets.USDT`: nome da rede, `chain_id`, RPC, **contrato oficial do USDT nessa rede**, confirmações e endereços. O serviço recusa contrato cujo `decimals()` não seja 6. Contratos com 18 casas, incluindo algumas implementações na BNB Chain, não são suportados por esta versão.

Financie as carteiras de saída com POVIX e USDT reais, além da moeda nativa para pagar gás. Os endereços do pool de depósito devem permanecer vazios. Não envie fundos ao endereço de recebimento de um cliente para financiar a tesouraria.

Defina `POVIX_SWAP_NETWORK_TOKEN`, com pelo menos 32 caracteres aleatórios, no ambiente do processo Python e do application pool do IIS; ele protege a API de leitura do nó. Defina outro segredo independente `POVIX_SWAP_ADMIN_TOKEN` para a API administrativa e a ferramenta local do operador. Nunca envie chaves privadas ou frases de recuperação a este serviço. Tokens são fornecidos pelo ambiente, não por argumentos de comando nem por arquivos versionados.

Execute, usando caminhos fora da raiz pública:

```sh
python3 PrivateCoin.Swap/server.py --config /caminho/privado/config.json --database /caminho/privado/orders.sqlite --port 8787
```

No Windows, use `python` e caminhos equivalentes. Configure o processo como serviço supervisionado com reinício, usuário dedicado e acesso restrito ao banco, configurações e backups. O servidor HTTP da biblioteca padrão atende apenas em `127.0.0.1`; TLS, limites de requisições e timeouts ficam a cargo do proxy.

### Integração no IIS

Publique o projeto `PrivateCoin.Site` atualizado no IIS e habilite ARR/URL Rewrite para encaminhar `/swap-api/*` a `http://127.0.0.1:8787/swap-api/*`, antes da rota MVC. Preserve método, JSON e `Idempotency-Key`.

Bloqueie `/swap-api/admin/*` na entrada pública; o operador usa loopback. Sobrescreva `X-Real-IP` no proxy com o IP real do cliente, bloqueie cabeçalhos fornecidos pelo cliente e aplique limites de criação de ordens. Não publique a API interna `/SwapNetwork/*` como API de clientes. As rotas internas continuam exigindo o token mesmo em loopback.

`/swap` passa a usar a API real; `/Home/SwapDemo` mantém a demonstração identificada. Sem serviço ou sem ativação, a página real informa indisponibilidade e não fornece endereço para depósito.

### Operação

```sh
python3 PrivateCoin.Swap/operator.py queue
python3 PrivateCoin.Swap/operator.py settle --order ID_DA_ORDEM --tx HASH_DO_PAGAMENTO
```

Antes de enviar, confira o depósito na carteira/nó, a rede, o destino e o valor da fila. Pague uma única ordem por transação. Para POVIX, use o endereço descartável fornecido pelo cliente e não reutilize esse endereço. Registre o hash imediatamente e, em caso de falha da API, consulte a cadeia e registre novamente o mesmo hash; **não envie um segundo pagamento**. O serviço não pode impedir gasto duplo operacional pela carteira externa e não oferece assinatura automática/idempotente do pagamento.

Acompanhe a ordem com o identificador e seu código de acesso. O código é armazenado como hash no banco; o navegador preserva a criação pendente na sessão para repetir a mesma requisição em caso de falha. O cliente deve guardar os dados antes de fechar o navegador.

## Limites da primeira versão

- Não há cancelamento automático, expiração de ordens já criadas ou liberação de suas reservas: endereços já entregues podem receber depósitos posteriormente. Cotações não utilizadas expiram em 120 segundos, mas não reservam fundos. Ordens abertas mantêm a reserva até serem pagas; por isso, limites de acesso/criação no proxy e atendimento do operador são necessários antes de exposição pública. Existe limite global de ordens abertas (`max_open_orders`, exemplo: 100).
- Depósitos parciais, em excesso ou na rede errada e solicitações de cancelamento/reembolso exigem atendimento manual. Esta versão não inclui reembolsos nem fechamento dessas ordens. Defina o procedimento operacional e implemente o fluxo de exceções antes de oferecer operação pública em escala.
- O serviço verifica provas quando o depósito é informado e novamente na liquidação; não executa monitoramento contínuo de reorgs nem recupera automaticamente depósitos sem hash informado. Confirmações reduzem, mas não eliminam, risco de reorganização. Use nó/RPC confiável; peers conectados e gênese correto não provam que o nó está na ponta global.
- A identidade das redes fica vinculada ao banco de ordens, evitando que uma troca de rede/contrato reaproveite provas antigas. Para outra rede, use uma instalação/banco separados. Atualizações de preço invalidam cotações ainda não confirmadas; ordens existentes conservam seus valores.
- Não há painel administrativo web, autenticação de clientes, assinatura automatizada, integração de preço externo ou suporte a BNB/ETH/BTC. O atendimento manual permite manter as chaves fora do servidor, mas limita a capacidade operacional.
- Faça backup consistente do SQLite (API de backup ou snapshot coordenado, incluindo WAL); não copie somente o arquivo principal enquanto o serviço grava. Proteja também a carteira externa e seus endereços futuros.

## Verificação

```sh
python3 Tests/PovixLiquidityRegression.py
node Tests/PovixSwapRegression.js
node --check PrivateCoin.Site/Scripts/povix-swap-live.js
```

Os testes usam fixtures locais, sem movimentar fundos. Exercitam concorrência, reservas, precisão, persistência, idempotência, endereço exclusivo, reutilização de provas, contrato/rede incorretos e confirmações/reorgs. Eles não substituem compilação do projeto ASP.NET, validação no IIS e uma operação controlada nas redes selecionadas, com carteiras da tesouraria financiadas e supervisão do operador.
