using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PrivateCoin.Desktop
{
    /// <summary>
    /// Desktop interpretation of the palette used by PrivateCoin.Site.
    /// Keeping the colors in one place makes the wallet and recovery screens
    /// feel like two parts of the same PONIX product.
    /// </summary>
    internal static class UiTheme
    {
        internal static readonly Color Background = Color.FromArgb(242, 240, 232); // --cream
        internal static readonly Color Surface = Color.White;
        internal static readonly Color SurfaceSoft = Color.FromArgb(244, 248, 247); // --pale
        internal static readonly Color Forest = Color.FromArgb(7, 31, 25); // --forest
        internal static readonly Color ForestLight = Color.FromArgb(11, 45, 35); // --forest-2
        internal static readonly Color Primary = Color.FromArgb(98, 228, 173); // --mint
        internal static readonly Color PrimarySoft = Color.FromArgb(185, 248, 220); // --mint-soft
        internal static readonly Color PrimaryDark = Color.FromArgb(8, 127, 106); // --green
        internal static readonly Color Ink = Color.FromArgb(16, 42, 46); // --ink
        internal static readonly Color Muted = Color.FromArgb(96, 118, 122); // --muted
        internal static readonly Color Border = Color.FromArgb(216, 229, 228); // --line
        internal static readonly Color Success = Color.FromArgb(32, 168, 121);
        internal static readonly Color Danger = Color.FromArgb(173, 53, 67);

        internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class HeroPanel : Panel
    {
        public HeroPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var brush = new LinearGradientBrush(ClientRectangle, UiTheme.Forest,
                UiTheme.ForestLight, LinearGradientMode.Horizontal))
                e.Graphics.FillRectangle(brush, ClientRectangle);

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.FromArgb(24, UiTheme.Primary), 1F))
            {
                e.Graphics.DrawEllipse(pen, Width - 260, -190, 360, 360);
                e.Graphics.DrawEllipse(pen, Width - 175, -105, 220, 220);
            }
        }
    }

    internal sealed class BrandMark : Control
    {
        public BrandMark()
        {
            Size = new Size(42, 42);
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(UiTheme.Primary, 1.6F))
            {
                e.Graphics.DrawEllipse(pen, 2, 2, Width - 5, Height - 5);
                e.Graphics.DrawLine(pen, 12, 14, 29, 10);
                e.Graphics.DrawLine(pen, 12, 20, 29, 26);
                e.Graphics.DrawLine(pen, 12, 27, 29, 20);
            }
        }
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
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = UiTheme.RoundedRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 12))
            using (var pen = new Pen(UiTheme.Border))
                e.Graphics.DrawPath(pen, path);
        }

        protected override void OnResize(System.EventArgs eventargs)
        {
            base.OnResize(eventargs);
            if (Width < 2 || Height < 2) return;
            using (var path = UiTheme.RoundedRectangle(new Rectangle(0, 0, Width, Height), 12))
                Region = new Region(path);
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
            ForeColor = primary ? UiTheme.Forest : UiTheme.Ink;
            FlatAppearance.BorderColor = primary ? UiTheme.Primary : UiTheme.Border;
            FlatAppearance.MouseOverBackColor = primary ? UiTheme.PrimarySoft : UiTheme.SurfaceSoft;
            FlatAppearance.MouseDownBackColor = primary ? UiTheme.Success : UiTheme.Border;
        }
    }
}
