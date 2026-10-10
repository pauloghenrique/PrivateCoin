using PrivateCoin.Core;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PrivateCoin.Desktop
{
    public partial class Form1 : Form
    {
        private readonly object pendingSync = new object();
        private readonly NetworkReadiness networkReadiness = new NetworkReadiness();
        private readonly List<Transaction> pendingTransactions = new List<Transaction>();
        private readonly HashSet<string> pendingIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<NamedWallet> wallets = new List<NamedWallet>();
        private readonly WalletStore walletStore = new WalletStore();
        private Blockchain blockchain;
        private PeerNode peerNode;
        private bool persistenceAvailable = true;
        private bool miningInProgress;
        private bool updateCheckInProgress;

        private sealed class FeeChoice
        {
            public FeeChoice(string name, long amount) { Name = name; Amount = amount; }
            public string Name { get; }
            public long Amount { get; }
            public override string ToString() => Name + " — " + FormatFee(Amount);
        }

        private NamedWallet SelectedWallet => walletComboBox.SelectedItem as NamedWallet;

        public Form1()
        {
            InitializeComponent();
            try
            {
                List<NamedWallet> legacyWallets;
                Blockchain legacyBlockchain;
                if (walletStore.TryLoadLegacy(out legacyWallets, out legacyBlockchain))
                {
                    wallets.AddRange(legacyWallets);
                    blockchain = legacyBlockchain;
                    SaveState();
                    Log("Dados antigos migrados para os arquivos separados da carteira e da rede.", true);
                }
                else
                {
                    if (walletStore.WalletExists)
                        wallets.AddRange(walletStore.LoadWallets());
                    else
                        wallets.Add(new NamedWallet("Carteira principal", new Wallet()));

                    if (walletStore.NetworkExists)
                    {
                        List<Transaction> restoredPendingTransactions;
                        blockchain = walletStore.LoadNetwork(out restoredPendingTransactions);
                        pendingTransactions.AddRange(restoredPendingTransactions);
                        foreach (Transaction transaction in restoredPendingTransactions) pendingIds.Add(transaction.Id);
                        // Losing wallets.dat must not mutate the public chain or consume a
                        // new-wallet reward.  The temporary local wallet only keeps the UI
                        // usable until the user restores the real wallet from its phrase.
                    }
                    else
                        blockchain = CreateBlockchainForWallet(wallets[0].Wallet);

                    if (!walletStore.WalletExists || !walletStore.NetworkExists || walletStore.NetworkNeedsUpgrade)
                    {
                        SaveState();
                        Log("Arquivos separados da carteira local e da rede foram criados ou atualizados com SHA-256.", true);
                    }
                    else Log("Carteira local e dados da rede restaurados com segurança.", true);
                }
            }
            catch (Exception error)
            {
                persistenceAvailable = false;
                MessageBox.Show("Não foi possível abrir os dados salvos. Os arquivos não foram alterados.\n\n" + error.Message,
                    "POVIX", MessageBoxButtons.OK, MessageBoxIcon.Error);
                var recoveryWallet = new NamedWallet("Carteira temporária", new Wallet());
                wallets.Add(recoveryWallet);
                blockchain = CreateBlockchainForWallet(recoveryWallet.Wallet);
            }
            if (persistenceAvailable && RecoverWalletCreationReceipts()) SaveState();
            RefreshWalletList(0);
            UpdateChainSummary();
        }

        private static Blockchain CreateBlockchainForWallet(Wallet wallet)
        {
            // Opening a wallet must not create an isolated chain or issue coins.
            return new Blockchain();
        }

        private bool IsNetworkConnected() => peerNode != null && peerNode.ConnectedPeerCount > 0;

        private T ExecuteNetworkOperation<T>(Func<T> operation)
        {
            return networkReadiness.Execute(IsNetworkConnected, operation);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (SnapshotPending().Length > 0) BeginInvoke(new Action(StartAutomaticMining));
            string configuredPort = ConfigurationManager.AppSettings["ListenPort"];
            if (!string.IsNullOrWhiteSpace(configuredPort)) listenPortTextBox.Text = configuredPort.Trim();
            bool autoStart;
            if (!bool.TryParse(ConfigurationManager.AppSettings["AutoStartNode"], out autoStart) || autoStart)
                StartNodeButtonClick(this, EventArgs.Empty);
            if (!string.IsNullOrWhiteSpace(ConfigurationManager.AppSettings["UpdateManifestUrl"]))
                BeginInvoke(new Action(() => CheckForUpdatesAsync(false)));
        }

        private async void UpdateButtonClick(object sender, EventArgs e)
        {
            await CheckForUpdatesAsync(true);
        }

        private async Task CheckForUpdatesAsync(bool interactive)
        {
            if (updateCheckInProgress) return;
            string manifestUrl = ConfigurationManager.AppSettings["UpdateManifestUrl"];
            if (string.IsNullOrWhiteSpace(manifestUrl))
            {
                if (interactive) MessageBox.Show("O canal de atualização ainda não foi configurado neste build.", "Atualização do POVIX",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            updateCheckInProgress = true;
            updateButton.Enabled = false;
            updateButton.Text = "Verificando...";
            try
            {
                var updater = new DesktopUpdater(manifestUrl.Trim());
                UpdateManifest update = await updater.CheckAsync();
                if (update == null)
                {
                    if (interactive) MessageBox.Show("Você já está usando a versão mais recente.", "Atualização do POVIX",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string notes = string.IsNullOrWhiteSpace(update.ReleaseNotes) ? string.Empty : "\n\n" + update.ReleaseNotes.Trim();
                if (MessageBox.Show("A versão " + update.Version + " está disponível." + notes +
                    "\n\nBaixar, instalar e reiniciar agora? Seus arquivos de carteira e blockchain serão preservados.",
                    "Atualização do POVIX", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;

                updateButton.Text = "Baixando 0%";
                var progress = new Progress<int>(value => updateButton.Text = "Baixando " + value + "%");
                string payload = await updater.DownloadAsync(update, progress);
                SaveState();
                DesktopUpdater.LaunchInstaller(payload);
                Application.Exit();
            }
            catch (Exception error)
            {
                if (interactive) MessageBox.Show("Não foi possível verificar ou instalar a atualização.\n\n" + error.Message,
                    "Atualização do POVIX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else Log("A verificação automática de atualização falhou: " + error.Message, false);
            }
            finally
            {
                updateCheckInProgress = false;
                updateButton.Enabled = true;
                updateButton.Text = "Buscar atualização";
            }
        }

        private async void CreateWalletButtonClick(object sender, EventArgs e)
        {
            string name = walletNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                Log("Informe um nome para a nova carteira.", false);
                return;
            }
            if (wallets.Any(item => string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                Log("Já existe uma carteira com esse nome.", false);
                return;
            }

            createWalletButton.Enabled = false;
            string recoveryPhrase = RecoveryPhraseGenerator.Generate();
            var namedWallet = new NamedWallet(name, Wallet.FromSeed(RecoveryPhraseGenerator.ToSeed(recoveryPhrase), 0), 0, null, recoveryPhrase, true);
            bool walletAdded = false;
            try
            {
                ExecuteNetworkOperation(() => true);
                string rewardAddress = namedWallet.Wallet.CreateReceiveAddress();
                Transaction registration = await Task.Run(() => ExecuteNetworkOperation(() => blockchain.CreateWalletCreationTransaction(rewardAddress, SnapshotPending())));

                wallets.Add(namedWallet);
                walletAdded = true;
                lock (pendingSync)
                {
                    blockchain.ValidatePendingTransactions(pendingTransactions.Concat(new[] { registration }));
                    pendingIds.Add(registration.Id);
                    pendingTransactions.Add(registration);
                }
                walletNameTextBox.Clear();
                RefreshWalletList(wallets.Count - 1);
                SaveState();

                MessageBox.Show(
                    "Estas são as 12 palavras em inglês para recuperar sua carteira:\n\n" +
                    recoveryPhrase +
                    "\n\nAnote-as na ordem exibida e guarde-as em um local seguro. " +
                    "Elas não serão mostradas novamente e não devem ser compartilhadas.",
                    "Frase de recuperação — " + name,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                Log("Carteira “" + name + "” criada e salva. O registro será confirmado em um bloco próprio, sem aprovação por tokens bloqueados. " +
                    (registration.Outputs[0].Amount > 0 ? "Os 6 POVIX estarão disponíveis após a confirmação em bloco." : "A distribuição promocional terminou."), true);
                if (peerNode != null)
                {
                    await peerNode.BroadcastAsync(registration);
                }

                UpdateChainSummary();
                BeginInvoke(new Action(StartAutomaticMining));
            }
            catch (Exception error)
            {
                if (!walletAdded)
                {
                    namedWallet.Dispose();
                    Log("Não foi possível criar a carteira: " + error.Message, false);
                }
                else
                    Log("A carteira foi criada, mas não foi possível propagar sua recompensa: " + error.Message, false);
            }
            finally
            {
                createWalletButton.Enabled = true;
            }
        }

        private async void RecoverWalletButtonClick(object sender, EventArgs e)
        {
            string name;
            string phrase;
            if (!RecoveryWalletDialog.Prompt(this, out name, out phrase)) return;
            if (wallets.Any(item => string.Equals(item.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            {
                Log("Já existe uma carteira com esse nome.", false);
                return;
            }

            recoverWalletButton.Enabled = false;
            Log("Recuperando a carteira. A reconstrução das chaves pode levar alguns instantes...", true);
            try
            {
                // Rebuilding deterministic RSA keys is deliberately expensive. Running it
                // away from the UI thread prevents Windows from reporting the application
                // as unresponsive while the recovery gap is scanned.
                NamedWallet recovered = await Task.Run(() => walletStore.Recover(phrase, name, blockchain));
                if (recovered.Wallet.OwnedOneTimeAddresses.Any(address =>
                    wallets.Any(item => item.Wallet.OwnedOneTimeAddresses.Contains(address))))
                {
                    recovered.Dispose();
                    throw new InvalidOperationException("Essa carteira já está aberta.");
                }
                wallets.Add(recovered);
                RefreshWalletList(wallets.Count - 1);
                SaveState();
                UpdateChainSummary();
                Log("Carteira “" + recovered.Name + "” recuperada diretamente pela frase.", true);
            }
            catch (Exception error)
            {
                Log("Não foi possível recuperar a carteira: " + error.Message, false);
            }
            finally
            {
                recoverWalletButton.Enabled = true;
            }
        }

        private void WalletComboBoxSelectedIndexChanged(object sender, EventArgs e)
        {
            NamedWallet selected = SelectedWallet;
            if (selected == null) return;
            receiveAddressTextBox.Text = selected.Wallet.OwnedOneTimeAddresses.LastOrDefault() ?? string.Empty;
            UpdateWalletSummary();
        }

        private void CopyAddressButtonClick(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(receiveAddressTextBox.Text))
            {
                Log("Gere um endereço antes de copiá-lo.", false);
                return;
            }

            Clipboard.SetText(receiveAddressTextBox.Text);
            Log("Endereço de recebimento copiado para a área de transferência.", true);
        }

        private void TokensButtonClick(object sender, EventArgs e)
        {
            using (var dialog = new TokenWalletDialog(() => blockchain, wallets, SelectedWallet, SnapshotPending,
                () => persistenceAvailable && networkReadiness.IsReady && IsNetworkConnected(),
                () => peerNode == null ? "Nó parado; inicie-o para sincronizar."
                    : networkReadiness.IsReady && IsNetworkConnected() ? "Sincronizado | " + peerNode.ConnectedPeerCount + " par(es)."
                    : "Aguardando conexão e sincronização.", SendTokenTransferAsync))
                dialog.ShowDialog(this);
        }

        private async Task<string> SendTokenTransferAsync(NamedWallet wallet, string tokenId, string destination, long amount, long fee)
        {
            if (!persistenceAvailable) throw new InvalidOperationException("Restaure o acesso aos arquivos da carteira antes de enviar tokens.");
            if (!wallets.Contains(wallet)) throw new InvalidOperationException("Selecione uma carteira local.");
            Transaction transaction = ExecuteNetworkOperation(() => {
                lock (pendingSync)
                {
                    Transaction[] pending = SnapshotPending();
                    Transaction created = TokenWalletOperations.CreateTransfer(blockchain, wallet.Wallet, pending, tokenId, destination, amount, fee);
                    // Persist the new change keys and the signed transaction before accepting or broadcasting it.
                    walletStore.SaveTokenTransfer(wallets, blockchain, pending.Concat(new[] { created }));
                    pendingIds.Add(created.Id);
                    pendingTransactions.Add(created);
                    return created;
                }
            });
            Log("Carteira " + wallet.Name + ": transferência de token validada e salva (" + ShortId(transaction.Id) + ").", true);
            UpdateChainSummary();
            BeginInvoke(new Action(StartAutomaticMining));
            string result = "Transação: " + transaction.Id + "\n\nTransferência salva na fila local. Aguarde a aprovação de um validador; ela será confirmada em um bloco próprio.";
            try
            {
                PeerNode node = peerNode;
                if (node != null && node.ConnectedPeerCount > 0)
                {
                    await node.BroadcastAsync(transaction);
                    if (node.ConnectedPeerCount == 0) return result + "\nReconecte o nó para propagá-la à rede; não repita o envio.";
                }
                else return result + "\nReconecte o nó para propagá-la à rede.";
                Log("Transferência de token propagada aos pares conectados.", true);
                return result + "\nEnviada aos pares conectados.";
            }
            catch (Exception error)
            {
                Log("Transferência de token salva; falha na propagação: " + error.Message, false);
                return result + "\nFalha na propagação. Reconecte o nó para tentar novamente; não repita o envio.";
            }
        }

        private void WalletFolderButtonClick(object sender, EventArgs e)
        {
            try
            {
                if (!walletStore.WalletExists) throw new FileNotFoundException("O arquivo local de carteiras ainda não foi salvo.");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                    "/select,\"" + walletStore.WalletFilePath + "\"") { UseShellExecute = true });
            }
            catch (Exception error)
            {
                MessageBox.Show(this, "Não foi possível abrir a pasta.\n\nArquivo em uso: " + walletStore.WalletFilePath + "\n\n" + error.Message,
                    "POVIX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void StartNodeButtonClick(object sender, EventArgs e)
        {
            int port;
            if (!TryReadPort(listenPortTextBox.Text, out port)) return;

            try
            {
                bool enableNatTraversal;
                if (!bool.TryParse(ConfigurationManager.AppSettings["EnableNatTraversal"], out enableNatTraversal))
                    enableNatTraversal = true;
                string peerCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "peers.dat");
                networkReadiness.Disconnect();
                peerNode = new PeerNode(port, enableNatTraversal, peerCachePath);
                peerNode.TransactionReceived += PeerNodeTransactionReceived;
                peerNode.ChainReceived += PeerNodeChainReceived;
                peerNode.SynchronizationRequested += PeerNodeSynchronizationRequested;
                peerNode.PeerCountChanged += PeerNodePeerCountChanged;
                peerNode.NatTraversalStatusChanged += PeerNodePeerCountChanged;
                string[] bootstrapPeers = GetBootstrapPeers().ToArray();
                peerNode.Start(bootstrapPeers);
                startNodeButton.Enabled = false;
                connectButton.Enabled = true;
                showPeersButton.Enabled = true;
                UpdatePeerStatus();
                Log("Nó P2P iniciado em IPv4/IPv6. Descoberta automática de pares ativada.", true);
                if (peerNode.KnownPeers.Length == 0)
                    Log("Nenhum par salvo ou seed configurado. Abrir a porta não descobre nós: configure PeerSeeds para o primeiro contato.", false);
            }
            catch (Exception error)
            {
                if (peerNode != null) peerNode.Dispose();
                peerNode = null;
                Log("Não foi possível iniciar o nó: " + error.Message, false);
            }
        }

        private IEnumerable<string> GetBootstrapPeers()
        {
            string configured = ConfigurationManager.AppSettings["PeerSeeds"] ?? string.Empty;
            string environment = Environment.GetEnvironmentVariable("POVIX_PEERS")
                ?? Environment.GetEnvironmentVariable("PONEX_PEERS")
                ?? Environment.GetEnvironmentVariable("NOX_PEERS")
                ?? Environment.GetEnvironmentVariable("PRIVATECOIN_PEERS")
                ?? string.Empty;
            return (configured + "," + environment).Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private void PeerNodePeerCountChanged(object sender, EventArgs e)
        {
            if (!IsNetworkConnected()) networkReadiness.Disconnect();
            if (!IsHandleCreated || IsDisposed) return;
            BeginInvoke(new Action(UpdatePeerStatus));
        }

        private void UpdatePeerStatus()
        {
            PeerNode node = peerNode;
            if (node == null) return;
            nodeStatusLabel.Text = "Nó ativo na porta " + listenPortTextBox.Text.Trim() +
                "   |   Pares: " + node.ConnectedPeerCount.ToString(CultureInfo.InvariantCulture) +
                "   |   Conhecidos: " + node.KnownPeers.Length.ToString(CultureInfo.InvariantCulture);
            nodeStatusLabel.Text += "   |   " + (networkReadiness.IsReady && IsNetworkConnected() ? "Sincronizado" : "Aguardando sincronização") + "   |   NAT: " + node.NatTraversalStatus;
        }

        private async void ConnectButtonClick(object sender, EventArgs e)
        {
            int port;
            if (!TryReadPort(peerPortTextBox.Text, out port)) return;
            if (string.IsNullOrWhiteSpace(peerHostTextBox.Text))
            {
                Log("Informe o host do par.", false);
                return;
            }

            connectButton.Enabled = false;
            try
            {
                await peerNode.ConnectAsync(peerHostTextBox.Text.Trim(), port);
                await peerNode.RequestSynchronizationAsync();
                Log("Conectado ao par " + peerHostTextBox.Text.Trim() + ":" + port.ToString(CultureInfo.InvariantCulture) + ".", true);
            }
            catch (Exception error)
            {
                Log("Falha ao conectar ao par: " + error.Message +
                    " Verifique o Firewall do Windows, o IP público/CGNAT e se o teste não está usando o IP externo dentro da mesma rede.", false);
            }
            finally
            {
                connectButton.Enabled = peerNode != null;
            }
        }

        private void ShowPeersButtonClick(object sender, EventArgs e)
        {
            PeerNode node = peerNode;
            if (node == null) return;

            string[] connected = node.ConnectedPeers;
            var connectedSet = new HashSet<string>(connected, StringComparer.OrdinalIgnoreCase);
            string[] endpoints = node.KnownPeers.Concat(connected).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();

            using (var dialog = new Form())
            using (var list = new ListView())
            {
                dialog.Text = "Pares da rede POVIX";
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.ClientSize = new System.Drawing.Size(620, 380);
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                list.Dock = DockStyle.Fill;
                list.View = View.Details;
                list.FullRowSelect = true;
                list.GridLines = true;
                list.Columns.Add("Estado", 100);
                list.Columns.Add("Endereço", 490);
                foreach (string endpoint in endpoints)
                {
                    var item = new ListViewItem(connectedSet.Contains(endpoint) ? "Conectado" : "Conhecido");
                    item.SubItems.Add(endpoint);
                    list.Items.Add(item);
                }
                if (list.Items.Count == 0)
                {
                    var item = new ListViewItem("Aguardando");
                    item.SubItems.Add("Nenhum endereço foi descoberto ainda.");
                    list.Items.Add(item);
                }
                dialog.Controls.Add(list);
                dialog.ShowDialog(this);
            }
        }

        private void PeerNodeTransactionReceived(object sender, TransactionReceivedEventArgs e)
        {
            long epoch = networkReadiness.Epoch;
            BeginInvoke(new Action(() =>
            {
                if (!ReferenceEquals(sender, peerNode) || epoch != networkReadiness.Epoch || !IsNetworkConnected()) return;
                if (networkReadiness.IsReady) ValidateAndQueue(e.Transaction, "Rede P2P");
                else networkReadiness.Defer(epoch, e.Transaction);
            }));
        }

        private async void PeerNodeSynchronizationRequested(object sender, EventArgs e)
        {
            PeerNode node = peerNode;
            if (node == null) return;
            try
            {
                await node.BroadcastChainAsync(blockchain.Blocks);
                foreach (Transaction transaction in SnapshotPending()) await node.BroadcastAsync(transaction);
            }
            catch (Exception error) when (error is IOException || error is System.Net.Sockets.SocketException ||
                error is ObjectDisposedException || error is OperationCanceledException)
            { System.Diagnostics.Trace.TraceWarning("Propagação será repetida na próxima sincronização: {0}", error.GetType().Name); }
        }

        private void PeerNodeChainReceived(object sender, ChainReceivedEventArgs e)
        {
            long epoch = networkReadiness.Epoch;
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    bool changed = false;
                    bool accepted = networkReadiness.Accept(epoch,
                        () => ReferenceEquals(sender, peerNode) && IsNetworkConnected(), () =>
                        {
                            var orphaned = blockchain.Blocks.SelectMany(block => block.Transactions)
                                .Where(tx => tx.Inputs.Count > 0 || tx.Kind == TransactionKind.WalletCreate).ToArray();
                            if (!blockchain.TrySynchronizeChain(e.Blocks, out changed)) return false;
                            if (changed)
                            {
                                lock (pendingSync)
                                    foreach (Transaction tx in orphaned)
                                        if (pendingIds.Add(tx.Id)) pendingTransactions.Add(tx);
                                RemoveInvalidPendingTransactions();
                                RecoverWalletCreationReceipts();
                                RefreshValidatorState();
                                SaveState();
                            }
                            return true;
                        });
                    if (!accepted) return;
                    foreach (Transaction transaction in Blockchain.OrderByFeePriority(networkReadiness.TakeDeferred(epoch)))
                        ValidateAndQueue(transaction, "Rede P2P após sincronização");
                    UpdatePeerStatus();
                    UpdateChainSummary();
                    if (changed)
                    {
                        Log("Blockchain sincronizada pela rede (" + e.Blocks.Length.ToString(CultureInfo.InvariantCulture) + " blocos).", true);
                        PeerNode node = peerNode;
                        if (node != null) await node.BroadcastChainAsync(blockchain.Blocks);
                    }
                    if (peerNode != null)
                        foreach (Transaction transaction in SnapshotPending()) await peerNode.BroadcastAsync(transaction);
                    if (SnapshotPending().Length > 0) StartAutomaticMining();
                }
                catch (Exception error)
                {
                    Log("Blockchain recebida foi rejeitada — " + error.Message, false);
                }
            }));
        }

        private void RemoveInvalidPendingTransactions()
        {
            lock (pendingSync)
            {
                var valid = new List<Transaction>();
                foreach (Transaction transaction in Blockchain.OrderByFeePriority(pendingTransactions))
                {
                    try
                    {
                        blockchain.ValidatePendingTransactions(valid.Concat(new[] { transaction }));
                        valid.Add(transaction);
                    }
                    catch (InvalidOperationException) { pendingIds.Remove(transaction.Id); }
                }
                pendingTransactions.Clear();
                pendingTransactions.AddRange(valid);
            }
        }

        private bool ValidateAndQueue(Transaction transaction, string source)
        {
            Transaction[] snapshot = SnapshotPending();
            Transaction existing = snapshot.FirstOrDefault(tx => tx.Id == transaction?.Id);
            if (existing != null)
            {
                if (blockchain.HasValidTransactionApproval(existing, snapshot) ||
                    !blockchain.HasValidTransactionApproval(transaction, snapshot)) return false;
                lock (pendingSync)
                {
                    int index = pendingTransactions.FindIndex(tx => tx.Id == transaction.Id);
                    if (index < 0) return false;
                    pendingTransactions[index] = transaction;
                }
                SaveState();
                Log(source + ": movimentação aprovada por carteira com tokens bloqueados; saldo e taxa aguardam confirmação em bloco.", true);
                BeginInvoke(new Action(StartAutomaticMining));
                return true;
            }
            if (transaction?.TransactionApproval != null && !blockchain.HasValidTransactionApproval(transaction, snapshot))
            {
                Log(source + ": comprovante de validação da movimentação rejeitado.", false);
                return false;
            }

            try
            {
                blockchain.ValidatePendingTransactions(SnapshotPending().Concat(new[] { transaction }));
                lock (pendingSync)
                {
                    if (!pendingIds.Add(transaction.Id))
                    {
                        Log(source + ": transação duplicada ignorada (" + ShortId(transaction.Id) + ").", false);
                        return false;
                    }
                    pendingTransactions.Add(transaction);
                }
                Log(source + ": assinatura, propriedade, saldo e gasto duplo validados (" + ShortId(transaction.Id) + ").", true);
                SaveState();
                UpdateChainSummary();
                BeginInvoke(new Action(StartAutomaticMining));
                return true;
            }
            catch (Exception error)
            {
                Log(source + ": transação rejeitada — " + error.Message, false);
                return false;
            }
        }

        private async void StartAutomaticMining()
        {
            if (miningInProgress || !networkReadiness.IsReady || !IsNetworkConnected()) return;
            miningInProgress = true;
            try
            {
                RecoverWalletCreationReceipts();
                while (true)
                {
                    await ApprovePendingTransactions();
                    Transaction[] batch = blockchain.SelectApprovedValidationBatch(SnapshotPending()).ToArray();
                    if (batch.Length == 0) break;

                    ValidatorStake[] activeValidators = EligibleValidators(batch);
                    bool selfValidatedOnly = batch.All(Blockchain.IsSelfValidatedOperation);
                    if (!selfValidatedOnly && activeValidators.Length < 2)
                    {
                        miningStatusLabel.Text = "Aguardando 2 validadores sem participação na transferência";
                        Log("O bloco aguarda dois validadores elegíveis: remetentes e destinatários não podem criar nem confirmar o bloco.", false);
                        break;
                    }
                    miningStatusLabel.Text = "Criando e validando " + batch.Length.ToString(CultureInfo.InvariantCulture) + " transação(ões)...";
                    Log("Criação de bloco iniciada para a operação " + ShortId(batch[0].Id) + ".", true);

                    Block block = await Task.Run(() => ExecuteNetworkOperation(() => selfValidatedOnly && activeValidators.Length < 2 ? blockchain.AddBlock(batch) : blockchain.AddProofOfStakeBlock(batch, activeValidators)));
                    lock (pendingSync)
                    {
                        foreach (Transaction transaction in batch)
                        {
                            pendingTransactions.RemoveAll(item => item.Id == transaction.Id);
                            pendingIds.Remove(transaction.Id);
                        }
                    }
                    if (block.Validators != null && block.Validators.Count > 0)
                    {
                        BlockValidator creator = block.Validators.Single(item => item.IsCreator);
                        decimal reward = (decimal)ProofOfStake.GetBlockReward(block.Height) / Blockchain.OneCoin;
                        Log("Bloco #" + block.Height.ToString(CultureInfo.InvariantCulture) + " criado por “" + creator.ValidatorId +
                            "”, confirmado por " + (block.Validators.Count - 1).ToString(CultureInfo.InvariantCulture) +
                            " validador(es) e recompensado com " + reward.ToString("N8", CultureInfo.CurrentCulture) + " POVIX: " + ShortId(block.Hash) + ".", true);
                    }
                    else Log("Bloco #" + block.Height.ToString(CultureInfo.InvariantCulture) + " criado com uma operação validada: " + ShortId(block.Hash) + ".", true);
                    SaveState();
                    if (peerNode != null) await peerNode.BroadcastChainAsync(blockchain.Blocks);
                    UpdateChainSummary();
                }
            }
            catch (Exception error)
            {
                Log("Mineração cancelada: a operação não passou na validação — " + error.Message, false);
                RemoveInvalidPendingTransactions();
            }
            finally
            {
                miningInProgress = false;
                Transaction[] remaining = SnapshotPending();
                Transaction[] nextBatch = blockchain.SelectApprovedValidationBatch(remaining).ToArray();
                miningStatusLabel.Text = nextBatch.Length > 0 && !nextBatch.All(Blockchain.IsSelfValidatedOperation) && EligibleValidators(nextBatch).Length < 2
                    ? "Aguardando 2 validadores sem participação na transferência"
                    : "Operações pendentes: " + remaining.Length.ToString(CultureInfo.InvariantCulture) + "  |  Uma operação por bloco";
                UpdateChainSummary();
                if (!IsDisposed && nextBatch.Length > 0 && (nextBatch.All(Blockchain.IsSelfValidatedOperation) || EligibleValidators(nextBatch).Length >= 2))
                    BeginInvoke(new Action(StartAutomaticMining));
            }
        }

        private async Task ApprovePendingTransactions()
        {
            foreach (Transaction transaction in SnapshotPending().Where(Blockchain.RequiresLockedTokenApproval))
            {
                Transaction[] pending = SnapshotPending();
                if (blockchain.HasValidTransactionApproval(transaction, pending)) continue;
                ValidatorStake[] active = blockchain.GetActiveValidators(pending).ToArray();
                ValidatorStake[] owned = wallets.Where(wallet => !wallet.Wallet.IsParticipant(transaction))
                    .SelectMany(wallet => active.Where(stake => wallet.Wallet.OwnedOneTimeAddresses.Contains(stake.RewardAddress))
                        .Select(stake => wallet.Wallet.CreateValidatorStake(stake.RewardAddress, stake.LockedAmount))).ToArray();
                TransactionApproval approval = ExecuteNetworkOperation(() => blockchain.CreateTransactionApproval(transaction, pending, owned));
                if (approval == null) continue;
                transaction.TransactionApproval = approval;
                SaveState();
                Log("Movimentação validada; saldo e taxa aguardam o bloco: " + ShortId(transaction.Id) + ".", true);
                if (peerNode != null) await peerNode.BroadcastAsync(transaction);
            }
        }

        private ValidatorStake[] EligibleValidators(IEnumerable<Transaction> transactions)
        {
            Transaction[] batch = transactions.ToArray();
            ValidatorStake[] eligible = blockchain.GetActiveValidators(batch).ToArray();
            return wallets.Where(item => eligible.Any(stake => item.Wallet.OwnedOneTimeAddresses.Contains(stake.RewardAddress)) &&
                    !batch.Any(item.Wallet.IsParticipant))
                .Select(item =>
                {
                    ValidatorStake stake = eligible.Single(value => item.Wallet.OwnedOneTimeAddresses.Contains(value.RewardAddress));
                    return item.Wallet.CreateValidatorStake(stake.RewardAddress, stake.LockedAmount);
                }).ToArray();
        }

        private async void CreateTransactionButtonClick(object sender, EventArgs e)
        {
            decimal coins;
            if (!decimal.TryParse(amountTextBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out coins) || coins <= 0)
            {
                Log("Informe um valor POVIX maior que zero.", false);
                return;
            }
            if (string.IsNullOrWhiteSpace(destinationTextBox.Text))
            {
                Log("Informe um endereço descartável de destino.", false);
                return;
            }

            try
            {
                ExecuteNetworkOperation(() => true);
                long amount = checked((long)(coins * Blockchain.OneCoin));
                if (coins * Blockchain.OneCoin != amount)
                    throw new InvalidOperationException("O valor aceita no máximo 8 casas decimais.");
                Transaction[] pending = SnapshotPending();
                FeeChoice feeChoice = feeComboBox.SelectedItem as FeeChoice;
                if (feeChoice == null) throw new InvalidOperationException("Selecione uma das opções de taxa.");
                long fee = feeChoice.Amount;
                NamedWallet selected = SelectedWallet;
                if (selected == null) throw new InvalidOperationException("Selecione uma carteira.");
                long balance = blockchain.GetSpendableBalance(selected.Wallet.OwnedOneTimeAddresses, SnapshotPending());
                if (checked(amount + fee) > balance)
                    throw new InvalidOperationException("Saldo disponível insuficiente. Os tokens bloqueados como garantia não podem ser transferidos.");
                Transaction transaction = selected.Wallet.CreateTransaction(blockchain, pending, destinationTextBox.Text.Trim(), amount, fee);
                ValidateAndQueue(transaction, "Carteira local");
                if (peerNode != null)
                {
                    await peerNode.BroadcastAsync(transaction);
                    Log("Transação propagada aos pares conectados.", true);
                }
                else Log("Transação criada localmente; inicie o nó para propagá-la.", false);
            }
            catch (Exception error)
            {
                Log("Não foi possível criar a transação: " + error.Message, false);
            }
        }

        private void ActivateValidatorButtonClick(object sender, EventArgs e)
        {
            decimal coins;
            if (!decimal.TryParse(stakeAmountTextBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out coins) || coins <= 0)
            {
                Log("Informe uma garantia POVIX maior que zero.", false);
                return;
            }

            try
            {
                ExecuteNetworkOperation(() => true);
                long amount = checked((long)(coins * Blockchain.OneCoin));
                if (coins * Blockchain.OneCoin != amount)
                    throw new InvalidOperationException("A garantia aceita no máximo 8 casas decimais.");

                NamedWallet selected = SelectedWallet;
                if (selected == null) throw new InvalidOperationException("Selecione uma carteira.");
                if (selected.IsValidator) throw new InvalidOperationException("Esta carteira já está ativa como validadora.");
                long balance = blockchain.GetSpendableBalance(selected.Wallet.OwnedOneTimeAddresses, SnapshotPending());
                long fee = 0;
                if (checked(amount + fee) > balance) throw new InvalidOperationException("Saldo insuficiente para bloquear essa garantia.");

                string rewardAddress = selected.Wallet.CreateReceiveAddress();
                Transaction lockTransaction = selected.Wallet.CreateStakeLockTransaction(blockchain, SnapshotPending(), rewardAddress, amount, fee);
                if (!ValidateAndQueue(lockTransaction, "Bloqueio de garantia")) return;
                RefreshValidatorState();
                SaveState();
                if (peerNode != null) _ = peerNode.BroadcastAsync(lockTransaction);
                UpdateWalletSummary();
                Log("Bloqueio de " + coins.ToString("N8", CultureInfo.CurrentCulture) +
                    " POVIX aguarda seu próprio bloco. O validador será ativado após a confirmação em bloco.", true);
                BeginInvoke(new Action(StartAutomaticMining));
            }
            catch (Exception error)
            {
                Log("Não foi possível ativar o validador: " + error.Message, false);
            }
        }

        private void NewAddressButtonClick(object sender, EventArgs e)
        {
            NamedWallet selected = SelectedWallet;
            if (selected == null) return;
            receiveAddressTextBox.Text = selected.Wallet.CreateReceiveAddress();
            SaveState();
            Log("Novo endereço descartável criado para recebimento.", true);
        }

        private void UnlockStakeButtonClick(object sender, EventArgs e)
        {
            try
            {
                NamedWallet selected = SelectedWallet;
                if (selected == null) throw new InvalidOperationException("Selecione uma carteira.");

                ExecuteNetworkOperation(() => true);
                decimal unlockedCoins = (decimal)selected.LockedStake / Blockchain.OneCoin;
                long fee = 0;
                Transaction unlockTransaction = selected.Wallet.CreateStakeUnlockTransaction(blockchain, SnapshotPending(), selected.ValidatorRewardAddress, fee);
                if (!ValidateAndQueue(unlockTransaction, "Desbloqueio de garantia")) return;
                RefreshValidatorState();
                SaveState();
                if (peerNode != null) _ = peerNode.BroadcastAsync(unlockTransaction);
                UpdateWalletSummary();
                Log(unlockedCoins.ToString("N8", CultureInfo.CurrentCulture) +
                    " POVIX aguardam desbloqueio em bloco. A operação terá seu próprio bloco.", true);
            }
            catch (Exception error)
            {
                Log("Não foi possível desbloquear a garantia: " + error.Message, false);
            }
        }

        private void RefreshWalletList(int selectedIndex)
        {
            walletComboBox.BeginUpdate();
            walletComboBox.Items.Clear();
            walletComboBox.Items.AddRange(wallets.Cast<object>().ToArray());
            walletComboBox.EndUpdate();
            if (walletComboBox.Items.Count > 0)
                walletComboBox.SelectedIndex = Math.Max(0, Math.Min(selectedIndex, walletComboBox.Items.Count - 1));
        }

        private void UpdateWalletSummary()
        {
            RefreshValidatorState();
            NamedWallet selected = SelectedWallet;
            WalletBalanceSummary balance = WalletBalanceSummary.Read(blockchain, selected?.Wallet, SnapshotPending());
            long lockedStake = selected == null ? 0 : selected.LockedStake;
            balanceLabel.Text = ((decimal)balance.Total / Blockchain.OneCoin).ToString("N8", CultureInfo.CurrentCulture) + " POVIX";
            toolTip.SetToolTip(balanceLabel, "Disponível para enviar: " +
                ((decimal)balance.Available / Blockchain.OneCoin).ToString("N8", CultureInfo.CurrentCulture) + " POVIX" +
                (balance.PendingChange == 0 ? string.Empty : "\nTroco aguardando confirmação em bloco: " +
                    ((decimal)balance.PendingChange / Blockchain.OneCoin).ToString("N8", CultureInfo.CurrentCulture) + " POVIX"));
            validatorStatusLabel.Text = lockedStake == 0
                ? "Validador inativo"
                : "Ativo  |  Bloqueado: " + ((decimal)lockedStake / Blockchain.OneCoin).ToString("N8", CultureInfo.CurrentCulture) + " POVIX";
            validatorStatusLabel.ForeColor = lockedStake == 0
                ? UiTheme.Muted
                : UiTheme.Success;
            bool collateralPending = selected != null && SnapshotPending().Any(transaction =>
                (transaction.Kind == TransactionKind.StakeLock || transaction.Kind == TransactionKind.StakeUnlock) &&
                selected.Wallet.IsParticipant(transaction));
            if (collateralPending) validatorStatusLabel.Text += "  |  Alteração aguardando bloco";
            stakeAmountTextBox.Enabled = selected != null && !selected.IsValidator && !collateralPending;
            activateValidatorButton.Enabled = selected != null && !selected.IsValidator && !collateralPending;
            unlockStakeButton.Enabled = selected != null && selected.IsValidator && !collateralPending;
        }

        private void RefreshValidatorState()
        {
            ValidatorStake[] active = blockchain.GetActiveValidators(SnapshotPending()).ToArray();
            foreach (NamedWallet wallet in wallets)
            {
                ValidatorStake stake = active.FirstOrDefault(item => wallet.Wallet.OwnedOneTimeAddresses.Contains(item.RewardAddress));
                if (stake == null)
                {
                    if (wallet.IsValidator) wallet.DeactivateValidator();
                }
                else if (wallet.LockedStake != stake.LockedAmount || wallet.ValidatorRewardAddress != stake.RewardAddress)
                {
                    if (wallet.IsValidator) wallet.DeactivateValidator();
                    wallet.ActivateValidator(stake.LockedAmount, stake.RewardAddress);
                }
            }
        }

        private void SaveState()
        {
            if (!persistenceAvailable) return;
            try { walletStore.Save(wallets, blockchain, SnapshotPending()); }
            catch (Exception error) { Log("Não foi possível salvar os arquivos da carteira e da rede: " + error.Message, false); }
        }

        private bool RecoverWalletCreationReceipts()
        {
            bool changed = false;
            lock (pendingSync)
                foreach (Transaction receipt in blockchain.GetUncountedWalletCreations())
                    if (pendingIds.Add(receipt.Id)) { pendingTransactions.Add(receipt); changed = true; }
            return changed;
        }

        private Transaction[] SnapshotPending()
        {
            lock (pendingSync) return Blockchain.OrderByFeePriority(pendingTransactions).ToArray();
        }

        private void UpdateChainSummary()
        {
            Transaction[] pending = SnapshotPending();
            int pendingCount = pending.Length;
            chainStatusLabel.Text = "Blocos: " + blockchain.Blocks.Count.ToString(CultureInfo.InvariantCulture) +
                "   |   Trabalho: " + blockchain.ChainWork.ToString(CultureInfo.InvariantCulture) +
                "   |   Pendentes: " + pendingCount.ToString(CultureInfo.InvariantCulture) +
                "   |   Cadeia: " + (blockchain.IsValid() ? "válida" : "inválida");
            int selectedFeeIndex = feeComboBox.SelectedIndex < 0 ? 1 : feeComboBox.SelectedIndex;
            feeComboBox.BeginUpdate();
            feeComboBox.Items.Clear();
            feeComboBox.Items.Add(new FeeChoice("Econômica", Blockchain.CalculateAutomaticFee(pendingCount, 1)));
            feeComboBox.Items.Add(new FeeChoice("Normal", Blockchain.CalculateAutomaticFee(pendingCount, 2)));
            feeComboBox.Items.Add(new FeeChoice("Prioritária", Blockchain.CalculateAutomaticFee(pendingCount, 4)));
            feeComboBox.EndUpdate();
            feeComboBox.SelectedIndex = Math.Min(selectedFeeIndex, feeComboBox.Items.Count - 1);
            feePolicyLabel.Text = "Opções calculadas para " + pendingCount.ToString(CultureInfo.InvariantCulture) +
                " transação(ões) na fila; taxas maiores recebem prioridade.";
            UpdateWalletSummary();
        }

        private static string FormatFee(long amount)
        {
            return ((decimal)amount / Blockchain.OneCoin).ToString("N8", CultureInfo.CurrentCulture) + " POVIX";
        }

        private void Log(string message, bool accepted)
        {
            ListViewItem item = validationListView.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture));
            item.SubItems.Add(accepted ? "APROVADO" : "ATENÇÃO");
            item.SubItems.Add(message);
            item.ForeColor = accepted ? UiTheme.PrimaryDark : UiTheme.Danger;
            item.BackColor = validationListView.Items.Count % 2 == 0
                ? System.Drawing.Color.White
                : UiTheme.SurfaceSoft;
        }

        private bool TryReadPort(string value, out int port)
        {
            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port > 0 && port <= 65535) return true;
            Log("A porta deve ser um número entre 1 e 65535.", false);
            return false;
        }

        private static string ShortId(string value)
        {
            if (string.IsNullOrEmpty(value)) return "sem id";
            return value.Length > 14 ? value.Substring(0, 14) + "…" : value;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (peerNode != null) peerNode.Dispose();
            SaveState();
            foreach (NamedWallet wallet in wallets) wallet.Dispose();
            base.OnFormClosed(e);
        }
    }
}
