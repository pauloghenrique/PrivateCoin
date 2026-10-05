using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PrivateCoin.Desktop
{
    internal static class UiTheme
    {
        internal static readonly Color Background = Color.FromArgb(244, 247, 251);
        internal static readonly Color Surface = Color.White;
        internal static readonly Color Primary = Color.FromArgb(88, 80, 236);
        internal static readonly Color PrimaryDark = Color.FromArgb(65, 57, 204);
        internal static readonly Color Ink = Color.FromArgb(30, 41, 59);
        internal static readonly Color Muted = Color.FromArgb(100, 116, 139);
        internal static readonly Color Border = Color.FromArgb(226, 232, 240);
        internal static readonly Color Success = Color.FromArgb(5, 150, 105);
    }

    internal sealed class CardPanel : Panel
    {
        public CardPanel()
        {
            BackColor = UiTheme.Surface;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(UiTheme.Border))
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    internal sealed class AccentButton : Button
    {
        private bool primary;

        public bool Primary
        {
            get { return primary; }
            set { primary = value; ApplyStyle(); }
        }

        public AccentButton()
        {
            FlatStyle = FlatStyle.Flat;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
            FlatAppearance.BorderSize = 1;
            ApplyStyle();
        }

        private void ApplyStyle()
        {
            BackColor = primary ? UiTheme.Primary : UiTheme.Surface;
            ForeColor = primary ? Color.White : UiTheme.Ink;
            FlatAppearance.BorderColor = primary ? UiTheme.Primary : UiTheme.Border;
            FlatAppearance.MouseOverBackColor = primary ? UiTheme.PrimaryDark : Color.FromArgb(248, 250, 252);
            FlatAppearance.MouseDownBackColor = primary ? UiTheme.PrimaryDark : Color.FromArgb(241, 245, 249);
        }
    }
}
