using PrivateCoin.Core;
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace PrivateCoin.Desktop
{
    internal sealed class TokenWalletDialog : Form
    {
        private readonly Func<Blockchain> chain;
        private readonly Func<NamedWallet> selectedWallet;
        private readonly Func<string> connectionStatus;
        private readonly DataGridView tokens = new DataGridView();
        private readonly Label walletLabel = new Label();
        private readonly Label status = new Label();
        private readonly Label connection = new Label();
        private readonly CheckBox onlyOwned = new CheckBox();
        private readonly Timer timer = new Timer { Interval = 5000 };
        private string lastTip;
        private NamedWallet lastWallet;

        public TokenWalletDialog(Func<Blockchain> chain, Func<NamedWallet> selectedWallet, Func<string> connectionStatus)
        {
            this.chain = chain; this.selectedWallet = selectedWallet;
            this.connectionStatus = connectionStatus;
            Text = "POVIX — Tokens da blockchain";
            Font = new Font("Segoe UI", 9F); BackColor = UiTheme.Background;
            ClientSize = new Size(990, 555); AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            var title = new Label { Text = "Tokens registrados", Font = new Font("Segoe UI", 18F, FontStyle.Bold), ForeColor = UiTheme.Ink };
            title.SetBounds(24, 18, 440, 38);
            walletLabel.SetBounds(24, 60, 600, 24); walletLabel.ForeColor = UiTheme.PrimaryDark;
            connection.SetBounds(645, 60, 321, 24); connection.ForeColor = UiTheme.Muted;
            connection.TextAlign = ContentAlignment.MiddleRight;
            onlyOwned.Text = "Somente tokens com saldo"; onlyOwned.SetBounds(24, 94, 260, 26);
            var refresh = new AccentButton { Text = "Atualizar", Primary = false }; refresh.SetBounds(851, 89, 115, 32);
            var help = new Label { Text = "O saldo pertence à carteira que controla o endereço de destino informado no DEX.", ForeColor = UiTheme.Muted };
            help.SetBounds(24, 510, 610, 34);
            tokens.SetBounds(24, 134, 942, 325);
            tokens.ReadOnly = true; tokens.AllowUserToAddRows = false; tokens.AllowUserToDeleteRows = false;
            tokens.AllowUserToResizeRows = false; tokens.RowHeadersVisible = false;
            tokens.MultiSelect = false; tokens.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            tokens.BackgroundColor = UiTheme.SurfaceSoft; tokens.BorderStyle = BorderStyle.None;
            tokens.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
            tokens.Columns.Add("Name", "Nome"); tokens.Columns[0].Width = 180;
            tokens.Columns.Add("Symbol", "Símbolo"); tokens.Columns[1].Width = 70;
            tokens.Columns.Add("Supply", "Quantidade total"); tokens.Columns[2].Width = 190;
            tokens.Columns.Add("Amount", "Seu saldo confirmado"); tokens.Columns[3].Width = 190;
            tokens.Columns.Add("Decimals", "Casas decimais"); tokens.Columns[4].Width = 95;
            tokens.Columns.Add("Id", "Identificador do token"); tokens.Columns[5].Width = 470;
            tokens.Columns.Add("Height", "Bloco de criação"); tokens.Columns[6].Width = 105;
            tokens.Columns.Add("Confirmations", "Confirmações"); tokens.Columns[7].Width = 95;
            foreach (DataGridViewColumn column in tokens.Columns) column.SortMode = DataGridViewColumnSortMode.NotSortable;
            status.SetBounds(24, 469, 942, 34); status.ForeColor = UiTheme.Muted;
            var copy = new AccentButton { Text = "Copiar identificador", Primary = false }; copy.SetBounds(652, 510, 194, 32);
            var close = new AccentButton { Text = "Fechar", Primary = true, DialogResult = DialogResult.Cancel }; close.SetBounds(856, 510, 110, 32);
            Controls.AddRange(new Control[] { title, walletLabel, connection, onlyOwned, refresh, tokens, status, help, copy, close });
            CancelButton = close;
            refresh.Click += (sender, args) => RefreshTokens(true);
            onlyOwned.CheckedChanged += (sender, args) => RefreshTokens(true);
            copy.Click += (sender, args) => {
                if (tokens.CurrentRow == null) return;
                try { Clipboard.SetText((string)tokens.CurrentRow.Cells[5].Value); }
                catch (Exception error) { MessageBox.Show(this, "Não foi possível copiar. " + error.Message, "POVIX", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };
            timer.Tick += (sender, args) => RefreshTokens(false);
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); RefreshTokens(true); timer.Start(); }

        private void RefreshTokens(bool force)
        {
            NamedWallet wallet = selectedWallet();
            Blockchain blockchain = chain();
            string tip = blockchain.Blocks.Last().Hash;
            walletLabel.Text = "Carteira selecionada: " + (wallet?.Name ?? "Nenhuma");
            connection.Text = connectionStatus();
            if (!force && tip == lastTip && ReferenceEquals(wallet, lastWallet)) return;
            string selectedId = tokens.CurrentRow?.Cells[5].Value as string;
            var entries = blockchain.GetTokenBalances(wallet?.Wallet.OwnedOneTimeAddresses)
                .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(item => item.Id).ToArray();
            tokens.Rows.Clear();
            foreach (TokenBalance item in entries.Where(item => !onlyOwned.Checked || item.Amount > 0))
            {
                int row = tokens.Rows.Add(item.Name, item.Symbol, TokenAmount.Format(item.Supply, item.Decimals),
                    TokenAmount.Format(item.Amount, item.Decimals), item.Decimals, item.Id, item.CreationHeight, item.Confirmations);
                if (item.Id == selectedId) tokens.CurrentCell = tokens.Rows[row].Cells[0];
            }
            status.Text = entries.Length == 0 ? "Nenhum token confirmado nesta blockchain. Conecte o nó à mesma rede do DEX e aguarde a sincronização."
                : tokens.Rows.Count == 0 ? "A carteira selecionada ainda não possui saldo em tokens. Confira o endereço de destino informado no DEX."
                : entries.Length + " token(s) confirmado(s) na blockchain local. A lista acompanha automaticamente os novos blocos.";
            lastTip = tip; lastWallet = wallet;
        }

        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
    }
}
