using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using PrivateCoin.Core;

namespace PrivateCoin.Desktop
{
    internal sealed class CheckpointDialog : Form
    {
        private readonly Func<Blockchain> currentChain;
        private readonly Func<bool> ready;
        private readonly Func<string, bool> protectedPath;
        private readonly NumericUpDown height;
        private readonly TextBox details;
        private readonly Label status;
        private readonly Button save;
        private readonly Button copy;
        private FinalityCheckpoint candidate;

        internal CheckpointDialog(Func<Blockchain> currentChain, Func<bool> ready, Func<string, bool> protectedPath)
        {
            this.currentChain = currentChain; this.ready = ready; this.protectedPath = protectedPath;
            Text = "Checkpoint de ativação"; StartPosition = FormStartPosition.CenterParent;
            ClientSize = new System.Drawing.Size(760, 525); BackColor = UiTheme.Background;
            Font = new System.Drawing.Font("Segoe UI", 9F);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            var explanation = new Label { Left = 20, Top = 18, Width = 720, Height = 54,
                Text = "Escolha um bloco existente e compartilhe o candidato para conferência.\nTodos os operadores devem acordar a mesma altura e o mesmo hash antes de ativar a votação." };
            var caption = new Label { Left = 20, Top = 81, Width = 240, Height = 24, Text = "Altura do bloco de referência:" };
            Blockchain chain = currentChain();
            height = new NumericUpDown { Left = 265, Top = 78, Width = 100, Minimum = 1,
                Maximum = Math.Max(1, chain.Blocks.Count - 1), Value = Math.Max(1, chain.Finality?.AnchorHeight ?? chain.Blocks.Count - 1),
                Enabled = chain.Finality == null };
            var generate = new Button { Left = 385, Top = 76, Width = 150, Height = 30, Text = "Gerar candidato" };
            var load = new Button { Left = 550, Top = 76, Width = 190, Height = 30, Text = "Conferir arquivo recebido" };
            details = new TextBox { Left = 20, Top = 120, Width = 720, Height = 282, Multiline = true,
                ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, BackColor = UiTheme.Surface };
            status = new Label { Left = 20, Top = 414, Width = 720, Height = 44, ForeColor = UiTheme.Muted,
                Text = "Nenhum candidato gerado. A votação não será ativada por esta janela." };
            save = new Button { Left = 20, Top = 475, Width = 150, Height = 30, Text = "Salvar candidato", Enabled = false };
            copy = new Button { Left = 185, Top = 475, Width = 175, Height = 30, Text = "Copiar configuração", Enabled = false };
            var close = new Button { Left = 630, Top = 475, Width = 110, Height = 30, Text = "Fechar", DialogResult = DialogResult.Cancel };
            generate.Click += (sender, args) => Attempt(() => Select(FinalityCheckpoint.Create(RequireChain(), (int)height.Value)));
            load.Click += LoadCandidate;
            save.Click += SaveCandidate;
            copy.Click += (sender, args) => Attempt(() =>
            {
                candidate.ValidateAgainst(RequireChain());
                Clipboard.SetText(candidate.ConfigurationSnippet());
                status.Text = "Configuração copiada. Aplique somente após o acordo entre os operadores; a votação continua inalterada.";
            });
            height.ValueChanged += (sender, args) => { candidate = null; save.Enabled = copy.Enabled = false; details.Clear(); status.Text = "Altura alterada. Gere um novo candidato para esse bloco."; };
            Controls.AddRange(new Control[] { explanation, caption, height, generate, load, details, status, save, copy, close });
            CancelButton = close;
        }

        private Blockchain RequireChain()
        {
            if (!ready()) throw new InvalidOperationException("Aguarde a conexão e a sincronização antes de gerar ou conferir um checkpoint.");
            return currentChain();
        }

        private void Attempt(Action action)
        {
            try { action(); status.ForeColor = UiTheme.PrimaryDark; }
            catch (Exception error)
            {
                candidate = null; save.Enabled = copy.Enabled = false; details.Clear();
                status.ForeColor = UiTheme.Danger; status.Text = error.Message;
            }
        }

        private void Select(FinalityCheckpoint checkpoint)
        {
            checkpoint.ValidateAgainst(RequireChain());
            height.Maximum = Math.Max(height.Maximum, checkpoint.Height);
            height.Value = checkpoint.Height;
            candidate = checkpoint;
            details.Text = "CANDIDATO — aguarda acordo dos operadores\r\n\r\n" +
                "Altura: " + checkpoint.Height.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Hash: " + checkpoint.BlockHash + "\r\nPolítica: " + checkpoint.PolicyId + "\r\n\r\n" +
                string.Join("\r\n", checkpoint.Committee.Select(v => "Validador " + v.ValidatorId + " | stake: " + v.LockedAmountAtomic + " unidades atômicas")) +
                "\r\n\r\n" + checkpoint.ConfigurationSnippet().Replace("\n", "\r\n");
            status.Text = "O bloco e os stakes correspondem à cadeia local. Isso não comprova o acordo dos demais validadores.";
            save.Enabled = copy.Enabled = true;
        }

        private void LoadCandidate(object sender, EventArgs args)
        {
            using (var dialog = new OpenFileDialog { Filter = "Checkpoint JSON (*.json)|*.json", CheckFileExists = true })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    Attempt(() =>
                    {
                        if (new FileInfo(dialog.FileName).Length > 1024 * 1024) throw new InvalidDataException("Checkpoint excede o limite de 1 MiB.");
                        Select(FinalityCheckpoint.FromJson(File.ReadAllBytes(dialog.FileName)));
                    });
        }

        private void SaveCandidate(object sender, EventArgs args)
        {
            using (var dialog = new SaveFileDialog { Filter = "Checkpoint JSON (*.json)|*.json", DefaultExt = "json",
                AddExtension = true, OverwritePrompt = true, FileName = "PrivateCoin-checkpoint-" + candidate.Height.ToString(CultureInfo.InvariantCulture) + ".json" })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    Attempt(() =>
                    {
                        candidate.ValidateAgainst(RequireChain());
                        if (protectedPath(dialog.FileName)) throw new InvalidOperationException("Escolha outro arquivo: os dados da carteira e da rede não podem ser substituídos por um checkpoint.");
                        WriteCandidate(dialog.FileName, candidate.ToJson());
                        status.Text = "Candidato salvo com dados públicos. Compartilhe para conferência; nenhuma configuração foi alterada.";
                    });
        }

        private static void WriteCandidate(string path, byte[] bytes)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
