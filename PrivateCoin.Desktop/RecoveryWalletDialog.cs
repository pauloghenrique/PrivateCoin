using System;
using System.Drawing;
using System.Windows.Forms;

namespace PrivateCoin.Desktop
{
    internal sealed class RecoveryWalletDialog : Form
    {
        private readonly TextBox nameTextBox = new TextBox();
        private readonly TextBox phraseTextBox = new TextBox();

        private RecoveryWalletDialog()
        {
            Text = "POVIX — Recuperar carteira";
            Font = new Font("Segoe UI", 9F);
            BackColor = UiTheme.Background;
            ClientSize = new Size(570, 260);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var title = new Label { Text = "Recupere sua carteira", Font = new Font("Segoe UI", 16F, FontStyle.Bold), ForeColor = UiTheme.Ink };
            title.SetBounds(22, 16, 526, 34);
            var help = new Label { Text = "Use as 12 palavras na ordem original. Seus arquivos antigos não são necessários.", ForeColor = UiTheme.Muted, AutoSize = false };
            help.SetBounds(24, 51, 522, 34);
            var nameLabel = new Label { Text = "NOME DA CARTEIRA", Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = UiTheme.Muted };
            nameLabel.SetBounds(24, 92, 180, 18);
            nameTextBox.SetBounds(24, 111, 522, 27);
            nameTextBox.BorderStyle = BorderStyle.FixedSingle;
            var phraseLabel = new Label { Text = "FRASE DE RECUPERAÇÃO", Font = new Font("Segoe UI", 8F, FontStyle.Bold), ForeColor = UiTheme.Muted };
            phraseLabel.SetBounds(24, 149, 200, 18);
            phraseTextBox.SetBounds(24, 168, 522, 42); phraseTextBox.Multiline = true; phraseTextBox.BorderStyle = BorderStyle.FixedSingle;
            var cancel = new AccentButton { Text = "Cancelar", DialogResult = DialogResult.Cancel, Primary = false }; cancel.SetBounds(298, 222, 104, 31);
            var recover = new AccentButton { Text = "Recuperar carteira", DialogResult = DialogResult.OK, Primary = true }; recover.SetBounds(412, 222, 134, 31);
            Controls.AddRange(new Control[] { title, help, nameLabel, nameTextBox, phraseLabel, phraseTextBox, cancel, recover });
            AcceptButton = recover;
            CancelButton = cancel;
        }

        public static bool Prompt(IWin32Window owner, out string name, out string phrase)
        {
            using (var dialog = new RecoveryWalletDialog())
            {
                bool accepted = dialog.ShowDialog(owner) == DialogResult.OK;
                name = dialog.nameTextBox.Text.Trim();
                phrase = dialog.phraseTextBox.Text;
                if (accepted && string.IsNullOrWhiteSpace(name))
                {
                    MessageBox.Show(owner, "Informe um nome para a carteira recuperada.", "POVIX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                return accepted;
            }
        }
    }
}
