using System;
using System.Drawing;
using System.Windows.Forms;

namespace QQViewerPin
{
    internal delegate bool HotkeyApplyHandler(bool enabled, uint modifiers, uint key, out string error);

    internal sealed class HotkeyForm : Form
    {
        private readonly CheckBox _enabled;
        private readonly TextBox _box;
        private readonly Label _hint;
        private readonly Button _ok;
        private readonly Button _cancel;
        private readonly HotkeyApplyHandler _apply;
        private uint _modifiers;
        private uint _key;

        public HotkeyForm(bool enabled, uint modifiers, uint key, HotkeyApplyHandler apply)
            : this(enabled, modifiers, key, apply, "快速隐藏")
        {
        }

        public HotkeyForm(bool enabled, uint modifiers, uint key, HotkeyApplyHandler apply, string subject)
        {
            _apply = apply;
            _modifiers = modifiers;
            _key = key;

            Text = "设置" + subject + "快捷键";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(400, 178);
            Font = new Font("Microsoft YaHei UI", 9F);

            Label title = new Label();
            title.Text = "按下想要的组合键（至少包含 Ctrl / Alt / Shift 之一）：";
            title.AutoSize = true;
            title.Location = new Point(16, 14);
            Controls.Add(title);

            _box = new TextBox();
            _box.Location = new Point(16, 44);
            _box.Width = 368;
            _box.Height = 28;
            _box.ReadOnly = true;
            _box.TextAlign = HorizontalAlignment.Center;
            _box.Font = new Font("Consolas", 11F);
            Controls.Add(_box);

            _hint = new Label();
            _hint.Text = "示例：Ctrl+`、Alt+Q、Ctrl+Alt+Q";
            _hint.AutoSize = true;
            _hint.ForeColor = Color.Gray;
            _hint.Location = new Point(16, 78);
            Controls.Add(_hint);

            _enabled = new CheckBox();
            _enabled.Text = "启用" + subject + "快捷键";
            _enabled.AutoSize = true;
            _enabled.Location = new Point(16, 104);
            _enabled.Checked = enabled;
            _enabled.CheckedChanged += delegate { UpdateEnabled(); };
            Controls.Add(_enabled);

            _ok = new Button();
            _ok.Text = "确定";
            _ok.Location = new Point(216, 138);
            _ok.Size = new Size(80, 28);
            _ok.Click += OkClick;
            Controls.Add(_ok);

            _cancel = new Button();
            _cancel.Text = "取消";
            _cancel.Location = new Point(304, 138);
            _cancel.Size = new Size(80, 28);
            _cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(_cancel);

            AcceptButton = _ok;
            CancelButton = _cancel;

            UpdateBox();
            UpdateEnabled();

            Shown += delegate { if (_enabled.Checked) _box.Focus(); else _ok.Focus(); };
        }

        public bool EnabledHotkey { get { return _enabled.Checked; } }
        public uint Modifiers { get { return _modifiers; } }
        public uint Key { get { return _key; } }

        private void UpdateEnabled()
        {
            _box.Enabled = _enabled.Checked;
            if (!_enabled.Checked)
            {
                _box.Text = "（已禁用）";
            }
            else
            {
                UpdateBox();
            }
        }

        private void UpdateBox()
        {
            if (_modifiers != 0 && _key != 0)
            {
                _box.Text = HotkeyUtil.Format(_modifiers, _key);
            }
            else
            {
                _box.Text = "请按下组合键";
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!_enabled.Checked) return base.ProcessCmdKey(ref msg, keyData);
            if (!_box.Focused) return base.ProcessCmdKey(ref msg, keyData);

            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Escape || key == Keys.Tab || key == Keys.Enter)
            {
                return base.ProcessCmdKey(ref msg, keyData);
            }
            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu ||
                key == Keys.LWin || key == Keys.RWin || key == Keys.None)
            {
                return true;
            }

            Keys mods = keyData & Keys.Modifiers;
            uint m = 0;
            if ((mods & Keys.Control) != 0) m |= Native.MOD_CONTROL;
            if ((mods & Keys.Alt) != 0) m |= Native.MOD_ALT;
            if ((mods & Keys.Shift) != 0) m |= Native.MOD_SHIFT;
            if ((Native.GetAsyncKeyState(Native.VK_LWIN) & 0x8000) != 0 ||
                (Native.GetAsyncKeyState(Native.VK_RWIN) & 0x8000) != 0)
            {
                m |= Native.MOD_WIN;
            }

            if (m == 0)
            {
                _hint.Text = "必须至少包含 Ctrl、Alt 或 Shift 之一。";
                _hint.ForeColor = Color.Firebrick;
                return true;
            }

            _modifiers = m;
            _key = (uint)key;
            _hint.Text = "示例：Ctrl+`、Alt+Q、Ctrl+Alt+Q";
            _hint.ForeColor = Color.Gray;
            UpdateBox();
            return true;
        }

        private void OkClick(object sender, EventArgs e)
        {
            if (_enabled.Checked && (_modifiers == 0 || _key == 0))
            {
                MessageBox.Show(this, "请先按下一个组合键。", "设置快捷键",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                _box.Focus();
                return;
            }

            string error;
            bool ok = _apply(_enabled.Checked, _enabled.Checked ? _modifiers : 0,
                _enabled.Checked ? _key : 0, out error);
            if (!ok)
            {
                MessageBox.Show(this, error, "快捷键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                if (_enabled.Checked) _box.Focus();
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
