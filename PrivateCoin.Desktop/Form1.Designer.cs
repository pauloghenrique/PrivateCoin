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
        private System.Windows.Forms.Button showPeersButton;
        private System.Windows.Forms.TextBox receiveAddressTextBox;
        private System.Windows.Forms.ComboBox walletComboBox;
        private System.Windows.Forms.TextBox walletNameTextBox;
        private System.Windows.Forms.Button createWalletButton;
        private System.Windows.Forms.Button recoverWalletButton;
        private System.Windows.Forms.Label balanceLabel;
        private System.Windows.Forms.Button newAddressButton;
        private System.Windows.Forms.Button copyAddressButton;
        private System.Windows.Forms.TextBox destinationTextBox;
        private System.Windows.Forms.TextBox amountTextBox;
        private System.Windows.Forms.ComboBox feeComboBox;
        private System.Windows.Forms.Label feePolicyLabel;
        private System.Windows.Forms.Button createTransactionButton;
        private System.Windows.Forms.TextBox stakeAmountTextBox;
        private System.Windows.Forms.Button activateValidatorButton;
        private System.Windows.Forms.Button unlockStakeButton;
        private System.Windows.Forms.Label validatorStatusLabel;
        private System.Windows.Forms.Label miningStatusLabel;
        private System.Windows.Forms.Label chainStatusLabel;
        private System.Windows.Forms.ListView validationListView;
        private System.Windows.Forms.ToolTip toolTip;
        private System.Windows.Forms.Button updateButton;
        private System.Windows.Forms.Button tokensButton;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            this.toolTip = new System.Windows.Forms.ToolTip(this.components);
            this.listenPortTextBox = CreateTextBox("4778");
            this.startNodeButton = CreateButton("Iniciar nó", true);
            this.nodeStatusLabel = CreateValueLabel("●  Nó parado");
            this.peerHostTextBox = CreateTextBox("127.0.0.1");
            this.peerPortTextBox = CreateTextBox("4778");
            this.connectButton = CreateButton("Conectar", true);
            this.showPeersButton = CreateButton("Ver pares", false);
            this.receiveAddressTextBox = CreateTextBox("");
            this.walletComboBox = CreateComboBox();
            this.walletNameTextBox = CreateTextBox("");
            this.createWalletButton = CreateButton("Criar carteira", true);
            this.recoverWalletButton = CreateButton("Recuperar", false);
            this.balanceLabel = CreateValueLabel("0,00000000 POVIX");
            this.newAddressButton = CreateButton("Novo endereço", false);
            this.copyAddressButton = CreateButton("Copiar", false);
            this.destinationTextBox = CreateTextBox("");
            this.amountTextBox = CreateTextBox("1,00");
            this.feeComboBox = CreateComboBox();
            this.feePolicyLabel = CreateMutedLabel("Taxas calculadas conforme o tamanho da fila.");
            this.createTransactionButton = CreateButton("Enviar POVIX", true);
            this.stakeAmountTextBox = CreateTextBox("1,00");
            this.activateValidatorButton = CreateButton("Bloquear e ativar", true);
            this.unlockStakeButton = CreateButton("Desbloquear", false);
            this.validatorStatusLabel = CreateMutedLabel("Validador inativo");
            this.miningStatusLabel = CreateValueLabel("Aguardando uma transferência válida");
            this.chainStatusLabel = CreateMutedLabel("");
            this.validationListView = new System.Windows.Forms.ListView();
            this.updateButton = CreateButton("Buscar atualização", false);
            this.tokensButton = CreateButton("Ver tokens", false);

            var header = new HeroPanel();
            header.SetBounds(0, 0, 1040, 96);
            var brandMark = new BrandMark();
            brandMark.SetBounds(25, 25, 42, 42);
            var brand = CreateHeaderLabel("POVIX", 19F, UiTheme.HeaderText);
            brand.SetBounds(78, 20, 180, 33);
            var subtitle = CreateHeaderLabel("PRIVACIDADE PARA PERTENCER", 8F, UiTheme.HeaderTextSecondary);
            subtitle.SetBounds(80, 54, 260, 20);
            var security = CreateHeaderLabel("CARTEIRA LOCAL   •   REDE P2P   •   SUAS CHAVES", 8.5F, UiTheme.HeaderText);
            security.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            security.SetBounds(430, 35, 385, 24);
            updateButton.SetBounds(842, 31, 168, 32);
            header.Controls.AddRange(new System.Windows.Forms.Control[] { brandMark, brand, subtitle, security, updateButton });

            var walletCard = CreateCard(24, 112, 640, 358, "Carteira", "Gerencie seus fundos e faça transferências");
            tokensButton.SetBounds(492, 14, 120, 28);
            AddFieldLabel(walletCard, "CARTEIRA ATIVA", 24, 72, 220);
            walletComboBox.SetBounds(24, 94, 292, 28);
            balanceLabel.Font = new System.Drawing.Font("Segoe UI", 16F, System.Drawing.FontStyle.Bold);
            balanceLabel.ForeColor = UiTheme.PrimaryDark;
            balanceLabel.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            balanceLabel.SetBounds(326, 87, 286, 40);
            AddFieldLabel(walletCard, "CRIAR OU RECUPERAR", 24, 137, 220);
            walletNameTextBox.SetBounds(24, 159, 292, 27);
            walletNameTextBox.PlaceholderTextCompat("Nome da nova carteira");
            createWalletButton.SetBounds(326, 157, 136, 31);
            recoverWalletButton.SetBounds(472, 157, 140, 31);
            AddFieldLabel(walletCard, "ENDEREÇO PARA RECEBER", 24, 201, 220);
            receiveAddressTextBox.SetBounds(24, 223, 398, 27); receiveAddressTextBox.ReadOnly = true;
            copyAddressButton.SetBounds(432, 221, 82, 31);
            newAddressButton.SetBounds(524, 221, 88, 31);
            AddFieldLabel(walletCard, "ENVIAR PARA", 24, 265, 120);
            destinationTextBox.SetBounds(24, 287, 292, 27);
            AddFieldLabel(walletCard, "VALOR", 326, 265, 70);
            amountTextBox.SetBounds(326, 287, 102, 27);
            AddFieldLabel(walletCard, "TAXA", 438, 265, 70);
            feeComboBox.SetBounds(438, 287, 174, 28);
            feePolicyLabel.SetBounds(24, 323, 398, 22);
            createTransactionButton.SetBounds(438, 320, 174, 32);
            walletCard.Controls.AddRange(new System.Windows.Forms.Control[] { walletComboBox, balanceLabel, walletNameTextBox,
                createWalletButton, recoverWalletButton, receiveAddressTextBox, copyAddressButton, newAddressButton,
                destinationTextBox, amountTextBox, feeComboBox, feePolicyLabel, createTransactionButton, tokensButton });
            tokensButton.BringToFront();

            var validatorCard = CreateCard(684, 112, 332, 122, "Validador", "Ajude a proteger a rede");
            stakeAmountTextBox.SetBounds(20, 75, 72, 27);
            activateValidatorButton.SetBounds(102, 73, 130, 31);
            unlockStakeButton.SetBounds(242, 73, 70, 31);
            validatorStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            validatorStatusLabel.SetBounds(151, 15, 161, 42);
            validatorCard.Controls.AddRange(new System.Windows.Forms.Control[] { stakeAmountTextBox,
                activateValidatorButton, unlockStakeButton, validatorStatusLabel });

            var activityCard = CreateCard(24, 490, 992, 246, "Atividade da rede", "Acompanhe sincronização, mineração e validações");
            miningStatusLabel.SetBounds(24, 67, 570, 24);
            chainStatusLabel.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            chainStatusLabel.SetBounds(600, 67, 368, 24);
            validationListView.SetBounds(24, 101, 944, 125);
            validationListView.View = System.Windows.Forms.View.Details;
            validationListView.FullRowSelect = true;
            validationListView.GridLines = false;
            validationListView.BorderStyle = System.Windows.Forms.BorderStyle.None;
            validationListView.BackColor = UiTheme.SurfaceSoft;
            validationListView.ForeColor = UiTheme.Ink;
            validationListView.HeaderStyle = System.Windows.Forms.ColumnHeaderStyle.Nonclickable;
            validationListView.Columns.Add("Hora", 80);
            validationListView.Columns.Add("Status", 100);
            validationListView.Columns.Add("Detalhes da validação", 740);
            activityCard.Controls.AddRange(new System.Windows.Forms.Control[] { miningStatusLabel, chainStatusLabel, validationListView });

            startNodeButton.Click += new System.EventHandler(this.StartNodeButtonClick);
            connectButton.Click += new System.EventHandler(this.ConnectButtonClick);
            showPeersButton.Click += new System.EventHandler(this.ShowPeersButtonClick);
            walletComboBox.SelectedIndexChanged += new System.EventHandler(this.WalletComboBoxSelectedIndexChanged);
            createWalletButton.Click += new System.EventHandler(this.CreateWalletButtonClick);
            recoverWalletButton.Click += new System.EventHandler(this.RecoverWalletButtonClick);
            newAddressButton.Click += new System.EventHandler(this.NewAddressButtonClick);
            copyAddressButton.Click += new System.EventHandler(this.CopyAddressButtonClick);
            createTransactionButton.Click += new System.EventHandler(this.CreateTransactionButtonClick);
            activateValidatorButton.Click += new System.EventHandler(this.ActivateValidatorButtonClick);
            unlockStakeButton.Click += new System.EventHandler(this.UnlockStakeButtonClick);
            updateButton.Click += new System.EventHandler(this.UpdateButtonClick);
            tokensButton.Click += new System.EventHandler(this.TokensButtonClick);
            connectButton.Enabled = false;
            showPeersButton.Enabled = false;
            toolTip.SetToolTip(copyAddressButton, "Copiar endereço para a área de transferência");
            toolTip.SetToolTip(newAddressButton, "Gerar um novo endereço descartável");
            toolTip.SetToolTip(tokensButton, "Ver os tokens confirmados da blockchain e o saldo da carteira selecionada");

            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Dpi;
            this.BackColor = UiTheme.Background;
            this.ClientSize = new System.Drawing.Size(1040, 760);
            this.Controls.AddRange(new System.Windows.Forms.Control[] { walletCard, validatorCard, activityCard, header });
            this.Font = new System.Drawing.Font("Segoe UI", 9F);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "POVIX — Carteira privada";
        }

        private static CardPanel CreateCard(int x, int y, int width, int height, string title, string subtitle)
        {
            var card = new CardPanel();
            card.SetBounds(x, y, width, height);
            var titleLabel = CreateLabel(title, 14F, System.Drawing.FontStyle.Bold, UiTheme.Ink);
            titleLabel.SetBounds(20, 14, width - 40, 28);
            var subtitleLabel = CreateLabel(subtitle, 9F, System.Drawing.FontStyle.Regular, UiTheme.Muted);
            subtitleLabel.SetBounds(21, 42, width - 42, 22);
            var accent = new System.Windows.Forms.Panel { BackColor = UiTheme.PrimaryDark };
            accent.SetBounds(0, 0, 5, height);
            card.Controls.AddRange(new System.Windows.Forms.Control[] { accent, titleLabel, subtitleLabel });
            return card;
        }

        private static System.Windows.Forms.Label CreateLabel(string text, float size, System.Drawing.FontStyle style, System.Drawing.Color color)
        {
            return new System.Windows.Forms.Label { Text = text, AutoSize = false,
                Font = new System.Drawing.Font("Segoe UI", size, style), ForeColor = color };
        }

        private static System.Windows.Forms.Label CreateValueLabel(string text)
        {
            return CreateLabel(text, 9.5F, System.Drawing.FontStyle.Bold, UiTheme.Ink);
        }

        private static System.Windows.Forms.Label CreateHeaderLabel(string text, float size, System.Drawing.Color color)
        {
            var label = CreateLabel(text, size, System.Drawing.FontStyle.Bold, color);
            // Header labels must not inherit the form's light background. Making
            // transparency explicit preserves the contrast over HeroPanel's gradient.
            label.BackColor = System.Drawing.Color.Transparent;
            label.UseCompatibleTextRendering = true;
            return label;
        }

        private static System.Windows.Forms.Label CreateMutedLabel(string text)
        {
            return CreateLabel(text, 8.5F, System.Drawing.FontStyle.Regular, UiTheme.Muted);
        }

        private static void AddFieldLabel(System.Windows.Forms.Control parent, string text, int x, int y, int width)
        {
            var label = CreateLabel(text, 7.5F, System.Drawing.FontStyle.Bold, UiTheme.Muted);
            label.SetBounds(x, y, width, 18);
            parent.Controls.Add(label);
        }

        private static System.Windows.Forms.TextBox CreateTextBox(string text)
        {
            return new System.Windows.Forms.TextBox { Text = text, BorderStyle = System.Windows.Forms.BorderStyle.FixedSingle,
                Font = new System.Drawing.Font("Segoe UI", 9.5F), ForeColor = UiTheme.Ink, BackColor = System.Drawing.Color.White };
        }

        private static System.Windows.Forms.ComboBox CreateComboBox()
        {
            return new System.Windows.Forms.ComboBox { DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList,
                FlatStyle = System.Windows.Forms.FlatStyle.Flat, Font = new System.Drawing.Font("Segoe UI", 9.5F),
                ForeColor = UiTheme.Ink, BackColor = System.Drawing.Color.White };
        }

        private static AccentButton CreateButton(string text, bool primary)
        {
            return new AccentButton { Text = text, Primary = primary };
        }
    }

    internal static class TextBoxCompatibilityExtensions
    {
        // .NET Framework 4.8 has no native placeholder property. The tooltip-like
        // cue banner is intentionally omitted on older Windows versions.
        internal static void PlaceholderTextCompat(this System.Windows.Forms.TextBox textBox, string placeholder)
        {
            textBox.AccessibleDescription = placeholder;
        }
    }
}
