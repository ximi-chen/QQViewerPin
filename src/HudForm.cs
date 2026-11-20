using System;
using System.Drawing;
using System.Windows.Forms;

namespace QQViewerPin
{
    internal sealed class HudForm : Form
    {
        private readonly Label _label;
        private readonly Timer _timer;

        public HudForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(150, 40);
            BackColor = Color.FromArgb(28, 28, 28);
            Opacity = 0.92;
            Padding = new Padding(1);

            _label = new Label();
            _label.Dock = DockStyle.Fill;
            _label.TextAlign = ContentAlignment.MiddleCenter;
            _label.ForeColor = Color.White;
            _label.BackColor = Color.Transparent;
            try
            {
                _label.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular);
            }
            catch
            {
                _label.Font = SystemFonts.DefaultFont;
            }
            Controls.Add(_label);

            _timer = new Timer();
            _timer.Interval = 1100;
            _timer.Tick += TimerTick;
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        public void ShowText(string text, Rectangle anchor)
        {
            if (IsDisposed) return;

            _label.Text = text;

            Screen screen = Screen.FromRectangle(anchor);
            Rectangle work = screen.WorkingArea;

            int x = anchor.Left + 12;
            int y = anchor.Top + 12;
            if (x + Width > work.Right) x = work.Right - Width - 8;
            if (y + Height > work.Bottom) y = work.Bottom - Height - 8;
            if (x < work.Left) x = work.Left;
            if (y < work.Top) y = work.Top;

            Location = new Point(x, y);

            if (!Visible)
            {
                Show();
            }

            _timer.Stop();
            _timer.Start();
        }

        private void TimerTick(object sender, EventArgs e)
        {
            _timer.Stop();
            Hide();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Stop();
                _timer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
