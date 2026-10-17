# Proposta: emissão inicial e lançamento POVIX

Status: especificação de produto; ainda não implementada no consenso.
Este documento descreve o modelo solicitado e não altera o comportamento atual.

## Distribuição e reservas

| Destino inicial | POVIX |
| --- | ---: |
| Distribuição gratuita: 100 carteiras × 6 POVIX | 600 |
| Reserva do lançamento | 359.400 |
| Total destinado a esta etapa inicial | 360.000 |

O total desta etapa não redefine a emissão futura da rede.

## Participação

Após a distribuição gratuita, o lançamento aceita uma participação por carteira.
Cada participação custa exatamente 1 USDC ou 1 USDT e reserva 6 POVIX para
essa carteira. Esses 6 POVIX saem da reserva inicial de 359.400; não são
criados outros 6 POVIX para a pool a cada compra.

Os POVIX adquiridos devem aparecer como saldo bloqueado na carteira e não
podem ser transferidos, vendidos ou usados antes da abertura. Antes da abertura,
a reserva disponível para novas participações é reduzida em 6 por participação;
os tokens comprometidos permanecem contabilizados separadamente, sem dupla contagem.

## Abertura

A negociação permanece desativada até a reserva disponível atingir exatamente
179.700 POVIX. Isso corresponde a:

- 179.700 POVIX comprometidos com os compradores;
- 29.950 participações de 6 POVIX;
- 29.950 unidades arrecadadas em stablecoins;
- 179.700 POVIX restantes para a liquidez principal.

A última participação deve encerrar a captação, liberar os saldos bloqueados e
habilitar as negociações de forma atômica. Não aceitar participações além da meta.
A meta anterior de 180.000 POVIX é substituída por este critério.

Conservação: 600 + 179.700 + 179.700 = 360.000 POVIX.

## Liquidez e taxas

A proposta destina 100% da captação à liquidez principal, junto dos 179.700 POVIX
restantes. Essa liquidez fica bloqueada permanentemente: nem o criador nem
administradores podem removê-la ou encerrar a pool.

Após a abertura, terceiros podem adicionar liquidez e retirar somente sua
participação, sem consumir a participação principal bloqueada.
Os provedores recebem uma parcela das taxas de negociação conforme sua
participação e a regra de remuneração a definir. Bloqueio do principal e
direito a taxas devem ser tratados separadamente.

## Decisões necessárias para a implementação

- Definir a rede, o identificador e a autenticidade do USDC/USDT aceito.
  Não aceitar um ativo apenas pelo nome ou símbolo.
- Escolher um único ativo por par ou especificar reservas/pools separadas.
  A soma de USDC e USDT é uma contagem nominal; não são o mesmo ativo.
- Definir a identidade de carteira que impede participação repetida, considerando
  que a rede utiliza endereços descartáveis. Uma carteira não equivale a uma pessoa.
- Definir se as 100 carteiras da distribuição gratuita também podem comprar.
- Definir o destino das taxas correspondentes à liquidez principal e o percentual
  cobrado e distribuído.
- Definir se existe prazo ou devolução caso a meta nunca seja atingida.
  Nenhum prazo ou desbloqueio antecipado foi especificado.

## Diferenças em relação ao código atual

O README descreve recompensa promocional de até 180.000 POVIX.
TokenLaunch limita contribuintes a 100, permite aumentar aportes, exige prazo
e trabalha com um token de venda (AssetId não nulo). POVIX é o ativo nativo;
o lançamento genérico existente não implementa automaticamente este modelo.

A implementação precisa cobrir consenso, emissão/distribuição, identidade dos
participantes, custódia dos saldos bloqueados, liquidez principal permanente,
liquidez removível de terceiros, ativos de captação e interface.
Mudanças nas regras canônicas exigem avaliar a identidade da rede e
compatibilidade das cadeias existentes.

## Critérios de aceitação da futura implementação

- Distribuição gratuita para exatamente 100 carteiras, sem exceder 600 POVIX.
- Rejeitar segundo aporte da mesma identidade de carteira, valores diferentes
  de 1 e ativos de captação não autorizados.
- Não permitir gastar os 6 POVIX reservados antes da meta.
- Após 29.949 participações: 179.706 POVIX disponíveis e negociação bloqueada.
- Após 29.950 participações: 179.700 POVIX disponíveis, saldos liberados e
  negociação aberta; rejeitar a participação seguinte.
- Conservar saldos em cada etapa, usando unidades atômicas inteiras.
- Impedir retirada do principal e encerramento inclusive por operações diretas
  de consenso; permitir retirada apenas da liquidez adicional pertencente ao provedor.
- Verificar contabilização das taxas sem permitir retirada do principal bloqueado.
