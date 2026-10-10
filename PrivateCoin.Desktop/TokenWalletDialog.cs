using PrivateCoin.Core;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PrivateCoin.Desktop
{
    internal sealed class TokenWalletDialog : Form
    {
        private readonly Func<Blockchain> chain;
        private readonly Func<Transaction[]> pendingTransactions;
        private readonly Func<bool> canTransfer;
        private readonly Func<string> connectionStatus;
        private readonly Func<NamedWallet, string, string, long, long, Task<string>> sendTransfer;
        private readonly ComboBox walletChoices = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly DataGridView tokens = new DataGridView();
        private readonly Label status = new Label();
        private readonly Label connection = new Label();
        private readonly Label selection = new Label();
        private readonly Label feeBalance = new Label();
        private readonly CheckBox onlyOwned = new CheckBox { Checked = true };
        private readonly TextBox destination = new TextBox();
        private readonly TextBox amount = new TextBox();
        private readonly ComboBox fees = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly AccentButton send = new AccentButton { Text = "Revisar e enviar", Primary = true };
        private readonly AccentButton close = new AccentButton { Text = "Fechar", Primary = false, DialogResult = DialogResult.Cancel };
        private readonly Timer timer = new Timer { Interval = 5000 };
        private string lastTip, lastPending, lastAddresses;
        private NamedWallet lastWallet;
        private int lastPendingCount = -1;
        private bool refreshing, sending;

        private NamedWallet SelectedWallet => walletChoices.SelectedItem as NamedWallet;
        private TokenBalance SelectedToken => tokens.CurrentRow?.Tag as TokenBalance;

        private sealed class FeeChoice
        {
            public FeeChoice(string name, long amount) { Name = name; Amount = amount; }
            public string Name { get; }
            public long Amount { get; }
            public override string ToString() => Name + " — " + TokenAmount.Format(Amount, 8) + " POVIX";
        }

        public TokenWalletDialog(Func<Blockchain> chain, IEnumerable<NamedWallet> wallets, NamedWallet selectedWallet,
            Func<Transaction[]> pendingTransactions, Func<bool> canTransfer, Func<string> connectionStatus,
            Func<NamedWallet, string, string, long, long, Task<string>> sendTransfer)
        {
            this.chain = chain; this.pendingTransactions = pendingTransactions;
            this.canTransfer = canTransfer; this.connectionStatus = connectionStatus; this.sendTransfer = sendTransfer;
            Text = "POVIX — Tokens da carteira";
            Font = new Font("Segoe UI", 9F); BackColor = UiTheme.Background;
            ClientSize = new Size(990, 655); AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            var title = new Label { Text = "Tokens da carteira", Font = new Font("Segoe UI", 18F, FontStyle.Bold), ForeColor = UiTheme.Ink };
            title.SetBounds(24, 18, 440, 38);
            var walletLabel = new Label { Text = "Carteira:", ForeColor = UiTheme.PrimaryDark };
            walletLabel.SetBounds(24, 64, 70, 24);
            walletChoices.SetBounds(96, 60, 510, 27);
            walletChoices.Items.AddRange(wallets.Cast<object>().ToArray());
            if (selectedWallet != null) walletChoices.SelectedItem = selectedWallet;
            if (walletChoices.SelectedIndex < 0 && walletChoices.Items.Count > 0) walletChoices.SelectedIndex = 0;
            connection.SetBounds(620, 60, 346, 30); connection.ForeColor = UiTheme.Muted;
            connection.TextAlign = ContentAlignment.MiddleRight;
            onlyOwned.Text = "Somente tokens com saldo"; onlyOwned.SetBounds(24, 94, 260, 26);
            var refresh = new AccentButton { Text = "Atualizar", Primary = false }; refresh.SetBounds(851, 89, 115, 32);
            tokens.SetBounds(24, 134, 942, 260);
            tokens.ReadOnly = true; tokens.AllowUserToAddRows = false; tokens.AllowUserToDeleteRows = false;
            tokens.AllowUserToResizeRows = false; tokens.RowHeadersVisible = false;
            tokens.MultiSelect = false; tokens.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            tokens.BackgroundColor = UiTheme.SurfaceSoft; tokens.BorderStyle = BorderStyle.None;
            tokens.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            tokens.Columns.Add("Name", "Nome"); tokens.Columns[0].Width = 170;
            tokens.Columns.Add("Symbol", "Símbolo"); tokens.Columns[1].Width = 70;
            tokens.Columns.Add("Amount", "Saldo em blocos"); tokens.Columns[2].Width = 205;
            tokens.Columns.Add("Available", "Disponível para enviar"); tokens.Columns[3].Width = 205;
            tokens.Columns.Add("Supply", "Quantidade total"); tokens.Columns[4].Width = 190;
            tokens.Columns.Add("Decimals", "Casas decimais"); tokens.Columns[5].Width = 95;
            tokens.Columns.Add("Id", "Identificador do token"); tokens.Columns[6].Width = 470;
            tokens.Columns.Add("Height", "Bloco de criação"); tokens.Columns[7].Width = 105;
            tokens.Columns.Add("Confirmations", "Validações"); tokens.Columns[8].Width = 95;
            foreach (DataGridViewColumn column in tokens.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            status.SetBounds(24, 402, 942, 38); status.ForeColor = UiTheme.Muted;
            selection.SetBounds(24, 445, 942, 34); selection.ForeColor = UiTheme.PrimaryDark;
            var destinationLabel = new Label { Text = "Endereço de destino", ForeColor = UiTheme.Muted };
            destinationLabel.SetBounds(24, 485, 490, 20); destination.SetBounds(24, 508, 490, 27);
            var amountLabel = new Label { Text = "Quantidade do token", ForeColor = UiTheme.Muted };
            amountLabel.SetBounds(530, 485, 184, 20); amount.SetBounds(530, 508, 184, 27);
            var feeLabel = new Label { Text = "Taxa em POVIX", ForeColor = UiTheme.Muted };
            feeLabel.SetBounds(730, 485, 236, 20); fees.SetBounds(730, 508, 236, 27);
            feeBalance.SetBounds(24, 544, 942, 25); feeBalance.ForeColor = UiTheme.Muted;
            var help = new Label { Text = "O saldo disponível inclui somente tokens confirmados em bloco. O troco permanece na carteira que envia.", ForeColor = UiTheme.Muted };
            help.SetBounds(24, 616, 942, 28);
            var copy = new AccentButton { Text = "Copiar identificador", Primary = false }; copy.SetBounds(488, 576, 194, 32);
            send.SetBounds(694, 576, 156, 32); close.SetBounds(862, 576, 104, 32);
            Controls.AddRange(new Control[] { title, walletLabel, walletChoices, connection, onlyOwned, refresh, tokens, status,
                selection, destinationLabel, destination, amountLabel, amount, feeLabel, fees, feeBalance, help, copy, send, close });
            CancelButton = close;
            refresh.Click += (sender, args) => RefreshTokens(true);
            onlyOwned.CheckedChanged += (sender, args) => RefreshTokens(true);
            walletChoices.SelectedIndexChanged += (sender, args) => { amount.Clear(); RefreshTokens(true); };
            tokens.SelectionChanged += (sender, args) => { if (!refreshing) { amount.Clear(); UpdateSelection(); } };
            copy.Click += (sender, args) => {
                if (SelectedToken == null) return;
                try { Clipboard.SetText(SelectedToken.Id); }
                catch (Exception error) { MessageBox.Show(this, "Não foi possível copiar. " + error.Message, "POVIX", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            send.Click += SendClick;
            timer.Tick += (sender, args) => RefreshTokens(false);
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); RefreshTokens(true); timer.Start(); }

        private void RefreshTokens(bool force)
        {
            if (sending) return;
            try { LoadTokens(force); }
            catch (InvalidOperationException)
            {
                // Mining can update the chain before the UI removes its confirmed transactions from the queue.
                lastTip = null;
                send.Enabled = false;
                status.Text = "Aguardando atualização dos saldos e das transações pendentes. A lista será atualizada automaticamente.";
            }
        }

        private void LoadTokens(bool force)
        {
            NamedWallet wallet = SelectedWallet;
            Blockchain blockchain = chain();
            Transaction[] pending = pendingTransactions();
            string tip = blockchain.Blocks.Last().Hash;
            string pendingIds = string.Join("|", pending.Select(tx => tx.Id + ":" + tx.TransactionApproval?.Proof?.Signature));
            string addresses = string.Join("|", wallet?.Wallet.OwnedOneTimeAddresses ?? new string[0]);
            connection.Text = connectionStatus();
            UpdateFees(pending.Length);
            feeBalance.Text = "POVIX disponíveis nesta carteira para taxas: " + TokenAmount.Format(
                wallet == null ? 0 : blockchain.GetSpendableBalance(wallet.Wallet.OwnedOneTimeAddresses, pending), 8);
            if (!force && tip == lastTip && pendingIds == lastPending && addresses == lastAddresses && ReferenceEquals(wallet, lastWallet))
            {
                UpdateSelection();
                return;
            }
            string selectedId = ReferenceEquals(wallet, lastWallet) ? SelectedToken?.Id : null;
            var entries = TokenWalletOperations.GetBalances(blockchain, wallet?.Wallet, pending, onlyOwned.Checked)
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id).ToArray();
            refreshing = true;
            try
            {
                tokens.Rows.Clear();
                foreach (TokenBalance item in entries)
                {
                    long available = item.Amount == 0 ? 0 : TokenWalletOperations.GetAvailableBalance(blockchain, wallet?.Wallet, pending, item.Id);
                    long inBlocks = wallet == null ? 0 : blockchain.GetTokenBalance(wallet.Wallet.OwnedOneTimeAddresses, item.Id);
                    int row = tokens.Rows.Add(item.Name, item.Symbol, TokenAmount.Format(inBlocks, item.Decimals),
                        TokenAmount.Format(available, item.Decimals), TokenAmount.Format(item.Supply, item.Decimals),
                        item.Decimals, item.Id, item.CreationHeight.HasValue ? (object)item.CreationHeight.Value : "Sem bloco", item.ValidationCount);
                    tokens.Rows[row].Tag = item;
                    tokens.Rows[row].Cells["Available"].Tag = available;
                    if (item.Id == selectedId) tokens.CurrentCell = tokens.Rows[row].Cells[0];
                }
            }
            finally { refreshing = false; }
            if (SelectedToken?.Id != selectedId) amount.Clear();
            status.Text = wallet == null ? "Selecione uma carteira."
                : entries.Length == 0 ? (onlyOwned.Checked
                    ? "Nenhum token com saldo disponível nesta carteira. Confira os envios pendentes, o destino no DEX e a sincronização."
                    : "Nenhum token confirmado em bloco. Confira a conexão com a rede do DEX e aguarde a sincronização.")
                : tokens.Rows.Count + " token(s) exibido(s) para esta carteira. Saldos atualizados automaticamente após validações, blocos e envios.";
            lastTip = tip; lastWallet = wallet; lastPending = pendingIds; lastAddresses = addresses;
            UpdateSelection();
        }

        private void UpdateFees(int pendingCount)
        {
            if (pendingCount == lastPendingCount) return;
            int selectedIndex = fees.SelectedIndex < 0 ? 1 : fees.SelectedIndex;
            fees.Items.Clear();
            fees.Items.Add(new FeeChoice("Econômica", Blockchain.CalculateAutomaticFee(pendingCount, 1)));
            fees.Items.Add(new FeeChoice("Normal", Blockchain.CalculateAutomaticFee(pendingCount, 2)));
            fees.Items.Add(new FeeChoice("Prioritária", Blockchain.CalculateAutomaticFee(pendingCount, 4)));
            fees.SelectedIndex = selectedIndex;
            lastPendingCount = pendingCount;
        }

        private void UpdateSelection()
        {
            TokenBalance token = SelectedToken;
            selection.Text = token == null ? "Selecione um token na lista para enviar."
                : "Enviar " + token.Name + " (" + token.Symbol + ") | Identificador: " + token.Id;
            bool owned = token != null && token.Amount > 0 && SelectedWallet != null;
            destination.Enabled = amount.Enabled = fees.Enabled = owned && !sending;
            send.Enabled = owned && !sending && canTransfer() &&
                ((long?)tokens.CurrentRow?.Cells["Available"].Tag ?? 0) > 0;
        }

        private async void SendClick(object sender, EventArgs e)
        {
            if (sending) return;
            timer.Stop();
            try
            {
                NamedWallet wallet = SelectedWallet;
                TokenBalance token = SelectedToken;
                FeeChoice fee = fees.SelectedItem as FeeChoice;
                if (!canTransfer()) throw new InvalidOperationException("Aguarde a conexão e a sincronização com a rede e verifique o acesso aos arquivos da carteira.");
                if (token == null || fee == null) throw new InvalidOperationException("Selecione um token e uma taxa.");
                long quantity = TokenAmount.Parse(amount.Text, token.Decimals);
                string address = destination.Text.Trim();
                TokenWalletOperations.ValidateTransfer(chain(), wallet?.Wallet, pendingTransactions(), token.Id, address, quantity, fee.Amount);
                string review = "Carteira: " + wallet.Name + "\nToken: " + token.Name + " (" + token.Symbol + ")\nIdentificador: " + token.Id +
                    "\nQuantidade: " + TokenAmount.Format(quantity, token.Decimals) + "\nDestino: " + address +
                    "\nTaxa: " + TokenAmount.Format(fee.Amount, 8) + " POVIX\n\nO troco do token e de POVIX permanece nesta carteira. Confirmar envio?";
                if (MessageBox.Show(this, review, "Revisar transferência de token", MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                sending = true;
                walletChoices.Enabled = tokens.Enabled = onlyOwned.Enabled = close.Enabled = false;
                UpdateSelection();
                string result = await sendTransfer(wallet, token.Id, address, quantity, fee.Amount);
                amount.Clear();
                MessageBox.Show(this, result, "Transferência de token", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "Não foi possível enviar o token", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                sending = false;
                walletChoices.Enabled = tokens.Enabled = onlyOwned.Enabled = close.Enabled = true;
                RefreshTokens(true);
                timer.Start();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (sending) e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }
}
