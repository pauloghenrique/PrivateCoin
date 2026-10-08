using System;
using System.IO;
using System.Windows.Forms;
using PrivateCoin.Core;

namespace PrivateCoin.Desktop
{
    // This setup window never instantiates WalletStore or PeerNode.
    internal sealed class CheckpointSetupForm : Form
    {
        internal CheckpointSetupForm(string problem)
        {
            Text = "POVIX — checkpoint necessário";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new System.Drawing.Size(690, 300);
            Font = new System.Drawing.Font("Segoe UI", 9F);
            BackColor = UiTheme.Background;
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            var message = new Label { Left = 20, Top = 20, Width = 650, Height = 100, ForeColor = UiTheme.Danger,
                Text = problem + "\n\nO nó não está conectado à rede. A votação é obrigatória e não há escolha pela cadeia mais longa." };
            var instructions = new Label { Left = 20, Top = 125, Width = 650, Height = 80,
                Text = "Você pode abrir somente o arquivo público Blockchain.json para gerar um candidato.\nConfira a mesma altura e o mesmo hash com os demais operadores, configure esses valores em todos os nós e reinicie. Nenhuma configuração será alterada nesta janela." };
            var open = new Button { Left = 20, Top = 232, Width = 335, Height = 36, Text = "Abrir Blockchain.json e preparar checkpoint" };
            var close = new Button { Left = 550, Top = 232, Width = 120, Height = 36, Text = "Fechar" };
            open.Click += (sender, args) =>
            {
                using (var dialog = new OpenFileDialog { Filter = "Arquivo público Blockchain.json (*.json)|*.json", CheckFileExists = true })
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        Blockchain snapshot = PublicBlockchainSnapshot.Read(dialog.FileName, FinalityPolicy.ReadCheckpointConfigurationForInspection());
                        string source = Path.GetFullPath(dialog.FileName);
                        using (var review = new CheckpointDialog(() => snapshot, () => true,
                            path => string.Equals(Path.GetFullPath(path), source, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(Path.GetFileName(path), "wallets.dat", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(Path.GetFileName(path), "recovery.dat", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(Path.GetFileName(path), "finality-votes.journal", StringComparison.OrdinalIgnoreCase), false))
                            review.ShowDialog(this);
                    }
                    catch (Exception error)
                    { MessageBox.Show(this, error.Message, "Não foi possível conferir o arquivo público", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                }
            };
            close.Click += (sender, args) => Close();
            Controls.AddRange(new Control[] { message, instructions, open, close });
        }
    }
}
