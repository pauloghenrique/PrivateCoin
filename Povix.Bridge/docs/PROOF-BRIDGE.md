# Ponte com verificação por provas — desenho, não implementação

Status: proposta técnica. O cliente e o cofre atuais continuam federados. Este documento não habilita depósitos, remove operadores ou autoriza implantação.

## Evidência no código atual

- `PrivateCoin.Core/NativeBridge.cs`: claims impedem reutilização de depósitos, mas a autenticidade externa é autorizada pelo quorum; não há light client BNB.
- `PrivateCoin.Core/Models.cs`, `Block.CalculateHash`: o hash compromete uma sequência de IDs e provas, sem raiz Merkle de transações. Uma prova Merkle nova não seria verificável contra esses blocos.
- `PrivateCoin.Core/Blockchain.cs`: escolha de cadeia por trabalho acumulado e validação de blocos/transações/participação. Contar cabeçalhos ou verificar apenas votos não reproduz essas regras.
- `Povix.Bridge/contracts/ProductionVault.sol`: implantação e resgates exigem operadores. Não existe interface para retirar usando prova nativa. O cofre atual não pode ser convertido apenas editando JSON.

## Modelo de confiança pretendido

Qualquer relay pode transmitir provas e pagar gas; nenhuma assinatura do relay autoriza emissão ou retirada. Os verificadores devem reconhecer a cadeia válida e a operação exata. Um recibo, hash ou resposta de RPC sem prova de consenso não basta. A configuração inicial fixa a rede e o ponto de partida do verificador; checkpoints posteriores assinados por um comitê reintroduziriam confiança nesse comitê.

## BNB para POVIX

1. Implementar light client BNB determinístico no consenso POVIX, com especificação fixada das versões e forks BNB suportados, assinaturas, conjunto de validadores e suas transições, regras de finalidade e codificação dos cabeçalhos.
2. Provar inclusão do recibo contra receiptsRoot de bloco autenticado pelo light client: validação estrita de RLP/Merkle Patricia, índice da transação e posição do log.
3. Exigir recibo bem-sucedido, emissor igual ao cofre cadastrado, assinatura do evento correta, token/rede/cofre vinculados, destinatário e quantidade válidos e escala exata.
4. Consumir atomicamente o identificador do depósito e emitir a representação, respeitando limites e pausas. A mesma prova não pode ser usada em outro cofre, ativo ou rede.
5. Dados de consenso externos precisam estar nas transações/blocos: validadores POVIX não devem consultar HTTP durante replay ou validação.

## POVIX para BNB

É necessário especificar e implementar um verificador da cadeia POVIX, não apenas um verificador de inclusão. Antes de escolher a implementação, avaliar custo EVM de validação integral versus provas de validade com circuito/programa fixado. Nenhuma dessas opções está disponível hoje.

O verificador precisa cobrir genesis/NetworkId, regras de dificuldade e trabalho, escolha de cadeia, validade das transações e do estado, participação e autorizações aplicáveis e política de reorganização. Uma sequência privada de cabeçalhos válidos não demonstra por si só qual é a cadeia pública canônica. A segurança econômica da POVIX e resistência a eclipses/reorganizações devem limitar a reserva permitida.

Uma nova versão de consenso poderá introduzir compromissos Merkle de transações e estado, com serialização canônica, separação de domínio, índices, tamanhos e regras de árvores explicitamente definidos. Esses compromissos precisam ser validados pelos nós e incluídos no hash do bloco. Não há compatibilidade retroativa automática com blocos atuais.

A prova da queima deve vincular rede, token, cofre BNB, chainId BNB, destinatário, quantidade e identificador único. O contrato só agenda/libera após prova de consenso e validade da queima, com consumo atômico do identificador e proteções de reserva/reentrância/precisão. A espera de resgate não substitui prova de consenso.

## Sequência de entrega

1. Fixar especificações de consenso e finalidade de ambas as redes e produzir vetores independentes, inclusive transições de validadores BNB.
2. Escolher a arquitetura do verificador POVIX e demonstrar seu custo e modelo de segurança; especificar alteração de consenso se necessária.
3. Implementar e testar cada verificador isoladamente antes de conectá-lo à emissão ou custódia.
4. Criar nova política nativa e novo cofre com domínio/versão próprios, sem fallback para assinaturas ou RPCs. Não reutilizar o cofre federado como se suportasse provas.
5. Implementar relay C# reiniciável e permissionless, geração/submissão de provas e acompanhamento público do estado.
6. Ensaio ponta a ponta em redes de teste, revisão independente e plano explícito de lançamento/migração. Não apagar snapshots nem transferir reservas existentes automaticamente.

## Critérios obrigatórios de aceitação

- Rejeitar provas alteradas, truncadas, excessivas, duplicadas, não canônicas e de outra rede/ativo/cofre/destino/quantidade.
- Rejeitar eventos de contratos falsos e recibos de transações revertidas; provar o índice correto do evento.
- Rejeitar assinatura BNB inválida, quorum insuficiente, transição de validadores incorreta, fork não suportado e cadeia sem finalidade exigida.
- Rejeitar queima POVIX com inputs/estado inválidos e trabalho/dificuldade manipulados; exercitar reorganizações e forks concorrentes.
- Reproduzir o mesmo estado sem rede HTTP; impor limites de tamanho, memória, processamento e gas antes de alocação não limitada.
- Provar prevenção de replay após reinício e revalidação, atomicidade da emissão/pagamento e solvência após depósitos e resgates concorrentes.
- Documentar comportamento quando relay para, fontes divergem ou verificadores ficam desatualizados. Ausência de prova mantém a operação bloqueada.

Não há estimativa de prontidão de produção antes da validação desses verificadores. A ponte atual continua exigindo dois operadores.
