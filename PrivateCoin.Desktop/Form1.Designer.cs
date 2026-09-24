namespace PrivateCoin.Desktop
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;
        private System.Windows.Forms.TextBox listenPortTextBox;
        private System.Windows.Forms.Button startNodeButton;
        private System.Windows.Forms.Label nodeStatusLabel;
        private System.Windows.Forms.TextBox peerHostTextBox;
        private System.Windows.Forms.TextBox peerPortTextBox;
        private System.Windows.Forms.Button connectButton;
        private System.Windows.Forms.TextBox receiveAddressTextBox;
        private System.Windows.Forms.ComboBox walletComboBox;
        private System.Windows.Forms.TextBox walletNameTextBox;
        private System.Windows.Forms.Button createWalletButton;
        private System.Windows.Forms.Label balanceLabel;
        private System.Windows.Forms.Button newAddressButton;
        private System.Windows.Forms.TextBox destinationTextBox;
        private System.Windows.Forms.TextBox amountTextBox;
        private System.Windows.Forms.Button createTransactionButton;
        private System.Windows.Forms.Label miningStatusLabel;
        private System.Windows.Forms.Label chainStatusLabel;
        private System.Windows.Forms.ListView validationListView;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.listenPortTextBox = new System.Windows.Forms.TextBox();
            this.startNodeButton = new System.Windows.Forms.Button();
            this.nodeStatusLabel = new System.Windows.Forms.Label();
            this.peerHostTextBox = new System.Windows.Forms.TextBox();
            this.peerPortTextBox = new System.Windows.Forms.TextBox();
            this.connectButton = new System.Windows.Forms.Button();
            this.receiveAddressTextBox = new System.Windows.Forms.TextBox();
            this.walletComboBox = new System.Windows.Forms.ComboBox();
            this.walletNameTextBox = new System.Windows.Forms.TextBox();
            this.createWalletButton = new System.Windows.Forms.Button();
            this.balanceLabel = new System.Windows.Forms.Label();
            this.newAddressButton = new System.Windows.Forms.Button();
            this.destinationTextBox = new System.Windows.Forms.TextBox();
            this.amountTextBox = new System.Windows.Forms.TextBox();
            this.createTransactionButton = new System.Windows.Forms.Button();
            this.miningStatusLabel = new System.Windows.Forms.Label();
            this.chainStatusLabel = new System.Windows.Forms.Label();
            this.validationListView = new System.Windows.Forms.ListView();
            this.SuspendLayout();
            // node
            AddLabel("NÓ P2P", 24, 20, 110, true);
            AddLabel("Porta local", 24, 57, 80, false);
            this.listenPortTextBox.SetBounds(108, 53, 72, 23); this.listenPortTextBox.Text = "4777";
            this.startNodeButton.SetBounds(190, 51, 110, 28); this.startNodeButton.Text = "Iniciar nó"; this.startNodeButton.Click += new System.EventHandler(this.StartNodeButtonClick);
            this.nodeStatusLabel.SetBounds(315, 56, 430, 20); this.nodeStatusLabel.Text = "Nó parado";
            AddLabel("Conectar a", 24, 96, 80, false);
            this.peerHostTextBox.SetBounds(108, 92, 160, 23); this.peerHostTextBox.Text = "127.0.0.1";
            this.peerPortTextBox.SetBounds(278, 92, 72, 23); this.peerPortTextBox.Text = "4778";
            this.connectButton.SetBounds(360, 90, 110, 28); this.connectButton.Text = "Conectar"; this.connectButton.Enabled = false; this.connectButton.Click += new System.EventHandler(this.ConnectButtonClick);
            // wallet
            AddLabel("CARTEIRAS", 24, 145, 220, true);
            AddLabel("Carteira", 24, 182, 80, false);
            this.walletComboBox.SetBounds(108, 178, 260, 24); this.walletComboBox.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList; this.walletComboBox.SelectedIndexChanged += new System.EventHandler(this.WalletComboBoxSelectedIndexChanged);
            this.balanceLabel.SetBounds(390, 181, 356, 22); this.balanceLabel.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Bold); this.balanceLabel.TextAlign = System.Drawing.ContentAlignment.TopRight;
            AddLabel("Nova", 24, 219, 80, false);
            this.walletNameTextBox.SetBounds(108, 215, 260, 23);
            this.createWalletButton.SetBounds(378, 213, 150, 28); this.createWalletButton.Text = "Criar carteira"; this.createWalletButton.Click += new System.EventHandler(this.CreateWalletButtonClick);
            AddLabel("Receber em", 24, 256, 80, false);
            this.receiveAddressTextBox.SetBounds(108, 252, 500, 23); this.receiveAddressTextBox.ReadOnly = true;
            this.newAddressButton.SetBounds(618, 250, 128, 28); this.newAddressButton.Text = "Novo endereço"; this.newAddressButton.Click += new System.EventHandler(this.NewAddressButtonClick);
            AddLabel("Destino", 24, 293, 80, false);
            this.destinationTextBox.SetBounds(108, 289, 500, 23);
            AddLabel("Valor", 24, 330, 80, false);
            this.amountTextBox.SetBounds(108, 326, 120, 23); this.amountTextBox.Text = "1,00";
            this.createTransactionButton.SetBounds(238, 324, 190, 28); this.createTransactionButton.Text = "Transferir e propagar"; this.createTransactionButton.Click += new System.EventHandler(this.CreateTransactionButtonClick);
            // mining
            AddLabel("MINERAÇÃO", 24, 380, 110, true);
            this.miningStatusLabel.SetBounds(24, 419, 500, 20); this.miningStatusLabel.Text = "Aguardando o bloco atingir 2 MiB";
            this.chainStatusLabel.SetBounds(24, 463, 722, 24); this.chainStatusLabel.Font = new System.Drawing.Font("Segoe UI", 9F, System.Drawing.FontStyle.Bold);
            // validation log
            AddLabel("VALIDAÇÕES DA REDE", 24, 504, 220, true);
            this.validationListView.SetBounds(24, 539, 722, 190); this.validationListView.View = System.Windows.Forms.View.Details; this.validationListView.FullRowSelect = true; this.validationListView.GridLines = true;
            this.validationListView.Columns.Add("Hora", 70); this.validationListView.Columns.Add("Resultado", 90); this.validationListView.Columns.Add("Validação", 535);
            // form
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.BackColor = System.Drawing.Color.WhiteSmoke;
            this.ClientSize = new System.Drawing.Size(770, 754);
            this.Controls.AddRange(new System.Windows.Forms.Control[] { this.listenPortTextBox, this.startNodeButton, this.nodeStatusLabel, this.peerHostTextBox, this.peerPortTextBox, this.connectButton, this.walletComboBox, this.walletNameTextBox, this.createWalletButton, this.balanceLabel, this.receiveAddressTextBox, this.newAddressButton, this.destinationTextBox, this.amountTextBox, this.createTransactionButton, this.miningStatusLabel, this.chainStatusLabel, this.validationListView });
            this.Font = new System.Drawing.Font("Segoe UI", 9F);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "PrivateCoin — Carteiras, transferências e nó";
            this.ResumeLayout(false);
            this.PerformLayout();
        }

        private void AddLabel(string text, int x, int y, int width, bool heading)
        {
            var label = new System.Windows.Forms.Label();
            label.SetBounds(x, y, width, 22);
            label.Text = text;
            if (heading) label.Font = new System.Drawing.Font("Segoe UI", 10F, System.Drawing.FontStyle.Bold);
            this.Controls.Add(label);
        }
    }
}
