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
            Text = "Recuperar carteira";
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(550, 205);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;

            var help = new Label { Text = "Informe as 12 palavras na ordem original. Nenhum arquivo de carteira é necessário.", AutoSize = false };
            help.SetBounds(18, 16, 514, 38);
            var nameLabel = new Label { Text = "Nome" }; nameLabel.SetBounds(18, 65, 75, 22);
            nameTextBox.SetBounds(100, 62, 432, 23);
            var phraseLabel = new Label { Text = "Frase" }; phraseLabel.SetBounds(18, 102, 75, 22);
            phraseTextBox.SetBounds(100, 99, 432, 46); phraseTextBox.Multiline = true;
            var cancel = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel }; cancel.SetBounds(342, 162, 90, 28);
            var recover = new Button { Text = "Recuperar", DialogResult = DialogResult.OK }; recover.SetBounds(442, 162, 90, 28);
            Controls.AddRange(new Control[] { help, nameLabel, nameTextBox, phraseLabel, phraseTextBox, cancel, recover });
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
                    MessageBox.Show(owner, "Informe um nome para a carteira recuperada.", "NOX", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                return accepted;
            }
        }
    }
}
