using PrivateCoin.Core;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PrivateCoin.Desktop
{
    public partial class Form1 : Form
    {
        private readonly object pendingSync = new object();
        private readonly List<Transaction> pendingTransactions = new List<Transaction>();
        private readonly HashSet<string> pendingIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<NamedWallet> wallets = new List<NamedWallet>();
        private readonly WalletStore walletStore = new WalletStore();
        private Blockchain blockchain;
        private PeerNode peerNode;
        private bool persistenceAvailable = true;
        private bool miningInProgress;

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
                        if (!walletStore.WalletExists)
                            AddInitialWalletReward(blockchain, wallets[0].Wallet);
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
                    "PrivateCoin", MessageBoxButtons.OK, MessageBoxIcon.Error);
                var recoveryWallet = new NamedWallet("Carteira temporária", new Wallet());
                wallets.Add(recoveryWallet);
                blockchain = CreateBlockchainForWallet(recoveryWallet.Wallet);
            }
            RefreshWalletList(0);
            UpdateChainSummary();
        }

        private static Blockchain CreateBlockchainForWallet(Wallet wallet)
        {
            var chain = new Blockchain();
            AddInitialWalletReward(chain, wallet);
            return chain;
        }

        private static void AddInitialWalletReward(Blockchain chain, Wallet wallet)
        {
            Block rewardBlock;
            if (!chain.TryAddWalletCreationReward(wallet.CreateReceiveAddress(), out rewardBlock))
                throw new InvalidOperationException("Não foi possível distribuir a recompensa da nova carteira.");
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
            var namedWallet = new NamedWallet(name, new Wallet());
            string recoveryPhrase = RecoveryPhraseGenerator.Generate();
            bool walletAdded = false;
            try
            {
                string rewardAddress = namedWallet.Wallet.CreateReceiveAddress();
                Block rewardBlock = null;
                bool rewarded = await Task.Run(() => blockchain.TryAddWalletCreationReward(rewardAddress, out rewardBlock));

                wallets.Add(namedWallet);
                walletAdded = true;
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

                if (rewarded)
                {
                    decimal reward = (decimal)Blockchain.WalletCreationReward / Blockchain.OneCoin;
                    Log("Carteira “" + name + "” criada com recompensa de " +
                        reward.ToString("N8", CultureInfo.CurrentCulture) + " PRIVATE no bloco #" +
                        rewardBlock.Height.ToString(CultureInfo.InvariantCulture) + ".", true);
                    if (peerNode != null) await peerNode.BroadcastChainAsync(blockchain.Blocks);
                }
                else
                    Log("Carteira “" + name + "” criada e salva. Os 180.000 PRIVATE reservados para novas carteiras já foram distribuídos.", true);

                UpdateChainSummary();
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

        private void WalletComboBoxSelectedIndexChanged(object sender, EventArgs e)
        {
            NamedWallet selected = SelectedWallet;
            if (selected == null) return;
            receiveAddressTextBox.Text = selected.Wallet.OwnedOneTimeAddresses.LastOrDefault() ?? string.Empty;
            UpdateWalletSummary();
        }

        private void StartNodeButtonClick(object sender, EventArgs e)
        {
            int port;
            if (!TryReadPort(listenPortTextBox.Text, out port)) return;

            try
            {
                peerNode = new PeerNode(port);
                peerNode.TransactionReceived += PeerNodeTransactionReceived;
                peerNode.ChainReceived += PeerNodeChainReceived;
                peerNode.SynchronizationRequested += PeerNodeSynchronizationRequested;
                peerNode.PeerCountChanged += PeerNodePeerCountChanged;
                peerNode.Start(GetBootstrapPeers());
                startNodeButton.Enabled = false;
                connectButton.Enabled = true;
                UpdatePeerStatus();
                Log("Nó P2P iniciado. Descoberta automática de pares ativada.", true);
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
            string environment = Environment.GetEnvironmentVariable("PRIVATECOIN_PEERS") ?? string.Empty;
            return (configured + "," + environment).Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private void PeerNodePeerCountChanged(object sender, EventArgs e)
        {
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
                await peerNode.BroadcastChainAsync(blockchain.Blocks);
                await peerNode.RequestSynchronizationAsync();
                Log("Conectado ao par " + peerHostTextBox.Text.Trim() + ":" + port.ToString(CultureInfo.InvariantCulture) + ".", true);
            }
            catch (Exception error)
            {
                Log("Falha ao conectar ao par: " + error.Message, false);
            }
            finally
            {
                connectButton.Enabled = peerNode != null;
            }
        }

        private void PeerNodeTransactionReceived(object sender, TransactionReceivedEventArgs e)
        {
            BeginInvoke(new Action(() => ValidateAndQueue(e.Transaction, "Rede P2P")));
        }

        private void PeerNodeSynchronizationRequested(object sender, EventArgs e)
        {
            PeerNode node = peerNode;
            if (node != null) node.BroadcastChainAsync(blockchain.Blocks);
        }

        private void PeerNodeChainReceived(object sender, ChainReceivedEventArgs e)
        {
            BeginInvoke(new Action(async () =>
            {
                try
                {
                    if (blockchain.TryReplaceChain(e.Blocks))
                    {
                        RemoveInvalidPendingTransactions();
                        SaveState();
                        UpdateChainSummary();
                        Log("Blockchain sincronizada pela rede (" + e.Blocks.Length.ToString(CultureInfo.InvariantCulture) + " blocos).", true);
                        PeerNode node = peerNode;
                        if (node != null) await node.BroadcastChainAsync(blockchain.Blocks);
                    }
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
                foreach (Transaction transaction in pendingTransactions)
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

        private void ValidateAndQueue(Transaction transaction, string source)
        {
            lock (pendingSync)
            {
                if (transaction != null && !string.IsNullOrEmpty(transaction.Id) && pendingIds.Contains(transaction.Id))
                {
                    Log(source + ": transação duplicada ignorada (" + ShortId(transaction.Id) + ").", false);
                    return;
                }
            }

            try
            {
                blockchain.ValidatePendingTransactions(SnapshotPending().Concat(new[] { transaction }));
                lock (pendingSync)
                {
                    if (!pendingIds.Add(transaction.Id))
                    {
                        Log(source + ": transação duplicada ignorada (" + ShortId(transaction.Id) + ").", false);
                        return;
                    }
                    pendingTransactions.Add(transaction);
                }
                Log(source + ": assinatura, propriedade, saldo e gasto duplo validados (" + ShortId(transaction.Id) + ").", true);
                SaveState();
                UpdateChainSummary();
                BeginInvoke(new Action(StartAutomaticMining));
            }
            catch (Exception error)
            {
                Log(source + ": transação rejeitada — " + error.Message, false);
            }
        }

        private async void StartAutomaticMining()
        {
            if (miningInProgress) return;
            miningInProgress = true;
            try
            {
                while (true)
                {
                    Transaction[] batch = SnapshotPending();
                    if (batch.Length == 0) break;

                    ValidatorStake[] activeValidators = EligibleValidators(batch);
                    if (activeValidators.Length < 2)
                    {
                        miningStatusLabel.Text = "Aguardando 2 validadores sem participação na transferência";
                        Log("O bloco aguarda dois validadores elegíveis: remetentes e destinatários não podem criar nem confirmar o bloco.", false);
                        break;
                    }
                    miningStatusLabel.Text = "Criando e validando " + batch.Length.ToString(CultureInfo.InvariantCulture) + " transação(ões)...";
                    Log("Consenso proof-of-stake iniciado após a validação de " +
                        batch.Length.ToString(CultureInfo.InvariantCulture) + " transação(ões) pendente(s).", true);

                    Block block = await Task.Run(() => blockchain.AddProofOfStakeBlock(batch, activeValidators));
                    lock (pendingSync)
                    {
                        foreach (Transaction transaction in batch)
                        {
                            pendingTransactions.RemoveAll(item => item.Id == transaction.Id);
                            pendingIds.Remove(transaction.Id);
                        }
                    }
                    BlockValidator creator = block.Validators.Single(item => item.IsCreator);
                    decimal reward = (decimal)ProofOfStake.GetBlockReward(block.Height) / Blockchain.OneCoin;
                    Log("Bloco #" + block.Height.ToString(CultureInfo.InvariantCulture) + " criado por “" + creator.ValidatorId +
                        "”, confirmado por " + (block.Validators.Count - 1).ToString(CultureInfo.InvariantCulture) +
                        " validador(es) e recompensado com " + reward.ToString("N8", CultureInfo.CurrentCulture) + " PRIVATE: " + ShortId(block.Hash) + ".", true);
                    SaveState();
                    if (peerNode != null) await peerNode.BroadcastChainAsync(blockchain.Blocks);
                    UpdateChainSummary();
                }
            }
            catch (Exception error)
            {
                Log("Mineração cancelada: o lote não passou na validação — " + error.Message, false);
                RemoveInvalidPendingTransactions();
            }
            finally
            {
                miningInProgress = false;
                Transaction[] remaining = SnapshotPending();
                miningStatusLabel.Text = remaining.Length > 0 && EligibleValidators(remaining).Length < 2
                    ? "Aguardando 2 validadores sem participação na transferência"
                    : "Aguardando uma transferência válida";
                UpdateChainSummary();
                if (!IsDisposed && remaining.Length > 0 && EligibleValidators(remaining).Length >= 2)
                    BeginInvoke(new Action(StartAutomaticMining));
            }
        }

        private ValidatorStake[] EligibleValidators(IEnumerable<Transaction> transactions)
        {
            Transaction[] batch = transactions.ToArray();
            return wallets.Where(item => item.IsValidator && !batch.Any(item.Wallet.IsParticipant))
                .Select(item => item.Validator).ToArray();
        }

        private async void CreateTransactionButtonClick(object sender, EventArgs e)
        {
            decimal coins;
            if (!decimal.TryParse(amountTextBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out coins) || coins <= 0)
            {
                Log("Informe um valor PRIVATE maior que zero.", false);
                return;
            }
            if (string.IsNullOrWhiteSpace(destinationTextBox.Text))
            {
                Log("Informe um endereço descartável de destino.", false);
                return;
            }

            try
            {
                long amount = checked((long)(coins * Blockchain.OneCoin));
                NamedWallet selected = SelectedWallet;
                if (selected == null) throw new InvalidOperationException("Selecione uma carteira.");
                long balance = blockchain.GetBalance(selected.Wallet.OwnedOneTimeAddresses, SnapshotPending());
                if (amount > balance - selected.LockedStake)
                    throw new InvalidOperationException("Saldo disponível insuficiente. Os tokens bloqueados como garantia não podem ser transferidos.");
                Transaction transaction = selected.Wallet.CreateTransaction(blockchain, SnapshotPending(), destinationTextBox.Text.Trim(), amount);
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
                Log("Informe uma garantia PRIVATE maior que zero.", false);
                return;
            }

            try
            {
                long amount = checked((long)(coins * Blockchain.OneCoin));
                if (coins * Blockchain.OneCoin != amount)
                    throw new InvalidOperationException("A garantia aceita no máximo 8 casas decimais.");

                NamedWallet selected = SelectedWallet;
                if (selected == null) throw new InvalidOperationException("Selecione uma carteira.");
                if (selected.IsValidator) throw new InvalidOperationException("Esta carteira já está ativa como validadora.");
                long balance = blockchain.GetBalance(selected.Wallet.OwnedOneTimeAddresses, SnapshotPending());
                if (amount > balance) throw new InvalidOperationException("Saldo insuficiente para bloquear essa garantia.");

                string rewardAddress = selected.Wallet.CreateReceiveAddress();
                selected.ActivateValidator(amount, rewardAddress);
                SaveState();
                UpdateWalletSummary();
                Log("Validador ativado com " + coins.ToString("N8", CultureInfo.CurrentCulture) +
                    " PRIVATE bloqueados como garantia para validação e criação de blocos.", true);
                if (wallets.Count(item => item.IsValidator) >= 2 && SnapshotPending().Length > 0)
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
            NamedWallet selected = SelectedWallet;
            long totalBalance = selected == null ? 0 : blockchain.GetBalance(selected.Wallet.OwnedOneTimeAddresses, SnapshotPending());
            long lockedStake = selected == null ? 0 : selected.LockedStake;
            long availableBalance = Math.Max(0, totalBalance - lockedStake);
            balanceLabel.Text = "Saldo disponível: " + ((decimal)availableBalance / Blockchain.OneCoin).ToString("N8", CultureInfo.CurrentCulture) + " PRIVATE";
            validatorStatusLabel.Text = lockedStake == 0
                ? "Validador inativo"
                : "Ativo  |  Bloqueado: " + ((decimal)lockedStake / Blockchain.OneCoin).ToString("N8", CultureInfo.CurrentCulture) + " PRIVATE";
            stakeAmountTextBox.Enabled = selected != null && !selected.IsValidator;
            activateValidatorButton.Enabled = selected != null && !selected.IsValidator;
        }

        private void SaveState()
        {
            if (!persistenceAvailable) return;
            try { walletStore.Save(wallets, blockchain, SnapshotPending()); }
            catch (Exception error) { Log("Não foi possível salvar os arquivos da carteira e da rede: " + error.Message, false); }
        }

        private Transaction[] SnapshotPending()
        {
            lock (pendingSync) return pendingTransactions.ToArray();
        }

        private void UpdateChainSummary()
        {
            int pendingCount = SnapshotPending().Length;
            chainStatusLabel.Text = "Blocos: " + blockchain.Blocks.Count.ToString(CultureInfo.InvariantCulture) +
                "   |   Pendentes: " + pendingCount.ToString(CultureInfo.InvariantCulture) +
                "   |   Cadeia: " + (blockchain.IsValid() ? "válida" : "inválida");
            UpdateWalletSummary();
        }

        private void Log(string message, bool accepted)
        {
            ListViewItem item = validationListView.Items.Insert(0, DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture));
            item.SubItems.Add(accepted ? "APROVADO" : "ATENÇÃO");
            item.SubItems.Add(message);
            item.ForeColor = accepted ? System.Drawing.Color.DarkGreen : System.Drawing.Color.DarkRed;
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
