using CompareManager.Classes;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CompareManager
{
    /// <summary>
    /// Chooses one side of a compare: a folder of scripts, or a SQL Server database.
    ///
    /// For a database, the connections saved in Issue Manager and the ones used here lately
    /// are offered at the top, so picking one fills in the rest — password included.
    /// </summary>
    public sealed class SourceForm : Form
    {
        private readonly AppSettings _settings;

        private readonly RadioButton _folderChoice   = new RadioButton { Text = "Folder of scripts", AutoSize = true };
        private readonly RadioButton _databaseChoice = new RadioButton { Text = "SQL Server database", AutoSize = true };

        private readonly ComboBox _folder   = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
        private readonly ComboBox _saved    = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly TextBox  _server   = new TextBox { Dock = DockStyle.Fill };
        private readonly ComboBox _auth     = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly TextBox  _login    = new TextBox { Dock = DockStyle.Fill };
        private readonly TextBox  _password = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        private readonly ComboBox _database = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
        private readonly Label    _status   = Ui.Label("", Ui.Small, Ui.Muted);

        private readonly Panel _folderPanel   = new Panel { Dock = DockStyle.Top, AutoSize = true };
        private readonly Panel _databasePanel = new Panel { Dock = DockStyle.Top, AutoSize = true };

        /// <summary>The side as chosen; set when the window closes with OK.</summary>
        public CompareSource Source { get; private set; }

        public SourceForm(string sideName, CompareSource current, AppSettings settings)
        {
            _settings = settings;

            Text            = $"Choose the {sideName.ToLowerInvariant()} side";
            Icon            = Ui.AppIcon();
            Font            = Ui.Body;
            BackColor       = Ui.Background;
            ForeColor       = Ui.Text;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            ClientSize      = new Size(620, 440);
            Padding         = new Padding(18, 14, 18, 14);

            BuildFolderPanel();
            BuildDatabasePanel();

            var choices = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 8) };
            choices.Controls.Add(_folderChoice);
            choices.Controls.Add(_databaseChoice);

            var heading = Ui.Label($"{sideName.ToUpperInvariant()} SIDE", Ui.Caption, Ui.Muted);
            heading.Dock = DockStyle.Top;

            var ok     = Ui.Primary("Use this");
            var cancel = Ui.Secondary("Cancel");
            cancel.DialogResult = DialogResult.Cancel;
            ok.Click += (s, e) => Accept();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);

            Controls.Add(_databasePanel);
            Controls.Add(_folderPanel);
            Controls.Add(choices);
            Controls.Add(heading);
            Controls.Add(buttons);

            AcceptButton = ok;
            CancelButton = cancel;

            _folderChoice.CheckedChanged   += (s, e) => ShowKind();
            _databaseChoice.CheckedChanged += (s, e) => ShowKind();

            Fill(current ?? new CompareSource());
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private static TableLayoutPanel Grid()
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return grid;
        }

        private static void AddRow(TableLayoutPanel grid, string caption, Control field, Control extra = null)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var label = Ui.Label(caption, Ui.Body, Ui.Muted);
            label.Anchor = AnchorStyles.Left;
            label.Margin = new Padding(0, 6, 8, 6);
            field.Margin = new Padding(0, 4, 6, 4);

            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(field, 1, row);
            if (extra != null) { extra.Margin = new Padding(0, 2, 0, 2); grid.Controls.Add(extra, 2, row); }
        }

        private void BuildFolderPanel()
        {
            var grid   = Grid();
            var browse = Ui.Secondary("Browse…");
            browse.Click += (s, e) => Browse();

            _folder.Items.AddRange(_settings.RecentFolders.Cast<object>().ToArray());
            AddRow(grid, "Folder", _folder, browse);

            var hint = Ui.Label("Every .sql file below this folder is read. A script that creates or alters a procedure,\n" +
                                "function, view, trigger or table is matched on that object; any other script on its path.",
                                Ui.Small, Ui.Muted);
            hint.Margin = new Padding(0, 6, 0, 0);
            grid.SetColumnSpan(hint, 3);
            grid.Controls.Add(hint, 0, grid.RowCount++);

            _folderPanel.Controls.Add(grid);
        }

        private void BuildDatabasePanel()
        {
            var grid = Grid();

            var saved = new List<object>();
            saved.AddRange(_settings.RecentConnections.Select(c => (object)new RecentChoice(c)));
            saved.AddRange(IssueManagerConnections.Load().Select(c => (object)c));
            _saved.Items.Add("— pick a saved connection —");
            _saved.Items.AddRange(saved.ToArray());
            _saved.SelectedIndex = 0;
            _saved.SelectedIndexChanged += (s, e) => PickSaved();
            if (saved.Count > 0) AddRow(grid, "Saved", _saved);

            _auth.Items.AddRange(new object[] { "SQL Server login", "Windows authentication" });
            _auth.SelectedIndexChanged += (s, e) => _login.Enabled = _password.Enabled = _auth.SelectedIndex == 0;

            var list = Ui.Secondary("List");
            list.Click += async (s, e) => await ListDatabasesAsync();

            var test = Ui.Secondary("Test connection");
            test.Click += async (s, e) => await TestAsync();

            AddRow(grid, "Server",         _server);
            AddRow(grid, "Authentication", _auth);
            AddRow(grid, "Login",          _login);
            AddRow(grid, "Password",       _password);
            AddRow(grid, "Database",       _database, list);

            var testRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
            _status.Margin = new Padding(8, 8, 0, 0);
            testRow.Controls.Add(test);
            testRow.Controls.Add(_status);
            grid.SetColumnSpan(testRow, 2);
            grid.Controls.Add(testRow, 1, grid.RowCount++);

            _databasePanel.Controls.Add(grid);
        }

        private void ShowKind()
        {
            _folderPanel.Visible   = _folderChoice.Checked;
            _databasePanel.Visible = _databaseChoice.Checked;
        }

        // ── Values ────────────────────────────────────────────────────────────

        private void Fill(CompareSource source)
        {
            _folderChoice.Checked   = source.Kind == SourceKind.Folder;
            _databaseChoice.Checked = source.Kind == SourceKind.Database;
            ShowKind();

            _folder.Text = source.Folder ?? "";
            FillConnection(source);
        }

        private void FillConnection(CompareSource source)
        {
            _server.Text       = source.Server ?? "";
            _auth.SelectedIndex = source.WindowsAuth ? 1 : 0;
            _login.Text        = source.Login ?? "";
            _password.Text     = source.Password;
            _database.Text     = source.Database ?? "";
        }

        private CompareSource Read() => new CompareSource
        {
            Kind        = _folderChoice.Checked ? SourceKind.Folder : SourceKind.Database,
            Folder      = _folder.Text.Trim(),
            Server      = _server.Text.Trim(),
            WindowsAuth = _auth.SelectedIndex == 1,
            Login       = _login.Text.Trim(),
            Password    = _password.Text,
            Database    = _database.Text.Trim()
        };

        private void PickSaved()
        {
            var source = _saved.SelectedItem switch
            {
                RecentChoice recent                  => recent.Source,
                IssueManagerConnections.Entry entry => entry.ToSource(),
                _                                    => null
            };
            if (source == null) return;

            FillConnection(source);
            _status.Text = source.WindowsAuth || !string.IsNullOrEmpty(source.Password)
                ? "" : "No password was saved with that connection — type it in.";
        }

        private void Browse()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description            = "The folder of SQL scripts to compare",
                UseDescriptionForTitle = true,
                SelectedPath           = _folder.Text
            };
            if (dialog.ShowDialog(this) == DialogResult.OK) _folder.Text = dialog.SelectedPath;
        }

        private async Task ListDatabasesAsync()
        {
            var source = Read();
            if (string.IsNullOrWhiteSpace(source.Server)) { _status.Text = "Type the server first."; return; }

            _status.Text = "Listing databases…";
            try
            {
                var names = await Task.Run(() => DatabaseSource.ListDatabases(source));
                string typed = _database.Text;
                _database.Items.Clear();
                _database.Items.AddRange(names.Cast<object>().ToArray());
                _database.Text = typed;
                _status.Text = $"{names.Count} databases — pick one from the list.";
                _database.DroppedDown = names.Count > 0;
            }
            catch (Exception ex)
            {
                _status.Text = ex.Message;
            }
        }

        private async Task TestAsync()
        {
            var source = Read();
            source.Kind = SourceKind.Database;
            if (!source.IsComplete) { _status.Text = "Fill in the server, database and login first."; return; }

            _status.ForeColor = Ui.Muted;
            _status.Text      = "Connecting…";
            try
            {
                await Task.Run(() => DatabaseSource.Test(source));
                _status.ForeColor = Ui.RightOnlyFore;
                _status.Text      = "Connected.";
            }
            catch (Exception ex)
            {
                _status.ForeColor = Ui.LeftOnlyFore;
                _status.Text      = ex.Message;
            }
        }

        private void Accept()
        {
            var source = Read();
            if (!source.IsComplete)
            {
                MessageBox.Show(this,
                    source.Kind == SourceKind.Folder
                        ? "Choose the folder of scripts."
                        : "Fill in the server, the database and — for a SQL Server login — the login.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (source.Kind == SourceKind.Folder && !System.IO.Directory.Exists(source.Folder))
            {
                MessageBox.Show(this, $"The folder \"{source.Folder}\" does not exist or cannot be reached.",
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Source       = source;
            DialogResult = DialogResult.OK;
        }

        /// <summary>A recent connection in the Saved list, labelled as one.</summary>
        private sealed class RecentChoice
        {
            public CompareSource Source { get; }
            public RecentChoice(CompareSource source) => Source = source;
            public override string ToString() => "Recent — " + Source.Describe();
        }
    }
}
