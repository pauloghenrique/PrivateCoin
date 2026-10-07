# DEXPovix

Aplicação ASP.NET MVC 5 / .NET Framework 4.8 para listar e criar tokens nativos
de quantidade fixa na rede PrivateCoin, consenso 3. O protocolo não oferece
execução de código de contratos inteligentes; os tokens usam `TokenCreate` do
Core e recebem um ID derivado da primeira entrada de financiamento.

## Executar

1. Restaure os pacotes NuGet da solução, compile `DEXPovix.csproj` e execute em
   IIS/IIS Express com HTTPS (Web Crypto também funciona em localhost).
2. Inicie um nó PrivateCoin atualizado. Em `Web.config`, configure `PeerSeeds`
   com seus endereços TCP, separados por vírgula. O padrão conecta ao Desktop
   em `127.0.0.1:4777`; `ListenPort=4781` evita conflito com ele e o explorer.
   Abra as portas necessárias no host. O serviço usa o handshake de rede e
   consenso do Core e valida integralmente cada cadeia recebida.
3. Conceda escrita à identidade do IIS somente em `App_Data` para o cache
   público `DEXBlockchain.json` e `peers.dat`. Cadeia e fila são restauradas e
   revalidadas após reinício. Nenhum arquivo de carteira Desktop é acessado.
4. Abra `/Tokens`, crie uma carteira ou importe seu backup **DEXPovix** e
   informe a senha de pelo menos 12 caracteres. Os backups são cifrados com
   AES-GCM-256 e PBKDF2-SHA256 (310.000 iterações); chaves RSA de 2048 bits
   permanecem no navegador. O formato não é o `wallets.dat` do Desktop.
5. Guarde backup e senha e envie POVIX da carteira Desktop para o endereço
   mostrado. Aguarde o saldo confirmado. A aplicação não concede recompensas
   nem cria saldo para financiar a emissão.
6. Informe nome, símbolo A–Z, casas decimais (0–8) e quantidade total. Use
   vírgula ou ponto para decimais, sem separador de milhares. A quantidade
   atômica deve caber em um inteiro positivo de 64 bits.
7. Use **Preparar token**, guarde o novo backup contendo as chaves dos endereços
   de tokens e troco, confira quantidade e taxa e use **Backup salvo: assinar
   e enviar** dentro de 5 minutos. É necessário baixar o backup atualizado
   antes de enviar. O servidor recebe somente chaves públicas e assinaturas.

O envio valida assinaturas, entradas, saldo, taxa e quantidade no Core, salva
a fila e propaga a transação via P2P. A lista atualiza a cada 15 segundos e
mantém `Pendente` até a inclusão em um bloco confirmado por pelo menos dois
validadores elegíveis. Sem pares, o servidor recusa preparação e envio. Ele
não mina, aprova blocos sozinho ou afirma que uma transação enviada já foi
confirmada. Repetir o envio do mesmo rascunho não duplica a transação.

Esta interface oferece criação e consulta; transferência de tokens não está
incluída. Guarde os backups para preservar o acesso aos tokens. Uma carteira
aberta existe apenas na memória da página e pode conter até 100 endereços.

Veja `Tests/README.md` para as regressões do Core, assinatura Web Crypto,
criptografia de backup e sincronização entre nós locais.
