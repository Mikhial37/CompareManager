using CompareManager.Classes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CompareManager
{
    /// <summary>
    /// Runs a folder's scripts on a SQL Server database (<see cref="ScriptRunner"/>).
    ///
    /// The folder is read the moment it is chosen: every script below it, in the order it
    /// runs, each with the database it will land on. Check scripts compiles all of them with
    /// <c>SET NOEXEC ON</c> and runs nothing; Run ticked scripts applies them, after a
    /// confirmation that names the server and database — and, for a database Issue Manager
    /// does not know, after its name is typed. A database Issue Manager marks Read only is
    /// refused outright (<see cref="RunPermission"/>).
    /// </summary>
    public sealed class ScriptRunnerForm : Form
    {
        private readonly AppSettings _settings;
        private List<RunItem> _items = new List<RunItem>();
        private CancellationTokenSource _cancel;
        private bool _running;

        private readonly ComboBox _folder     = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
        private readonly Label    _target     = new Label { AutoEllipsis = true, Dock = DockStyle.Fill, Font = Ui.Bold, TextAlign = ContentAlignment.MiddleLeft };
        private readonly Label    _permission = new Label { AutoSize = true, Font = Ui.Body, MaximumSize = new Size(900, 0) };
        private readonly CheckBox _aim        = new CheckBox { Text = "Aim each script at this database (its opening USE line, or a --USE [] placeholder)", AutoSize = true };
        private readonly CheckBox _stop       = new CheckBox { Text = "Stop at the first script that fails", AutoSize = true };

        private readonly ListView _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Ui.Card, ShowItemToolTips = true
        };
        private readonly TextBox _messages = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Font = Ui.Code, BackColor = Ui.Card, BorderStyle = BorderStyle.FixedSingle
        };

        private readonly Button _check  = Ui.Primary("Check scripts (nothing runs)");
        private readonly Button _run    = Ui.Destructive("Run ticked scripts");
        private readonly Button _stopBt = Ui.Secondary("Stop");
        private readonly Label  _status = Ui.Label("", Ui.Body, Ui.Muted);
        private bool _filling;

        public ScriptRunnerForm(AppSettings settings, string suggestedFolder, CompareSource suggestedTarget)
        {
            _settings = settings;

            Text          = "Run scripts — Compare Manager";
            Icon          = Ui.AppIcon();
            Font          = Ui.Body;
            BackColor     = Ui.Background;
            StartPosition = FormStartPosition.CenterParent;
            Size          = new Size(1200, 820);
            MinimumSize   = new Size(860, 560);

            // ── Header ──
            var header = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(16, 12, 16, 4) };
            header.Controls.Add(Ui.Label("Run scripts", Ui.Title));
            header.Controls.Add(Ui.Label("Applies a folder's scripts to a SQL Server database, in the order the folder lists them. " +
                                         "Check scripts first: it compiles every script and runs none of them.", Ui.Body, Ui.Muted));

            // ── Folder and target ──
            var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(16, 6, 16, 6) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var browse = Ui.Secondary("Browse…");
            var reload = Ui.Secondary("Reload");
            var change = Ui.Secondary("Change…");
            browse.Click += (s, e) => BrowseFolder();
            reload.Click += (s, e) => LoadScripts();
            change.Click += (s, e) => ChooseTarget();

            var folderButtons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            folderButtons.Controls.Add(browse);
            folderButtons.Controls.Add(reload);

            AddRow(grid, "Folder", _folder, folderButtons);
            AddRow(grid, "Run on", _target, change);
            AddRow(grid, "", _permission, null);

            var choices = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
            choices.Controls.Add(_aim);
            choices.Controls.Add(_stop);
            AddRow(grid, "", choices, null);

            // ── Scripts and their messages ──
            _list.Columns.Add("#", 44, HorizontalAlignment.Right);
            _list.Columns.Add("Script", 520);
            _list.Columns.Add("Database", 260);
            _list.Columns.Add("Status", 110);
            _list.Columns.Add("Time", 70, HorizontalAlignment.Right);
            _list.Columns.Add("Rows", 70, HorizontalAlignment.Right);
            _list.ItemChecked += (s, e) => { if (!_filling && e.Item.Tag is RunItem item) { item.Ticked = e.Item.Checked; UpdateStatus(); } };
            _list.SelectedIndexChanged += (s, e) => ShowMessages();
            _list.DoubleClick += (s, e) => OpenSelected();

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Ui.Background };
            split.Panel1.Padding = new Padding(16, 0, 16, 4);
            split.Panel2.Padding = new Padding(16, 4, 16, 0);
            split.Panel1.Controls.Add(_list);
            split.Panel2.Controls.Add(_messages);
            var messagesCaption = Ui.Label("Messages for the selected script — PRINT output, errors with their script line", Ui.Caption, Ui.Muted);
            messagesCaption.Dock = DockStyle.Top;
            split.Panel2.Controls.Add(messagesCaption);
            Shown += (s, e) => split.SplitterDistance = (int)(split.Height * 0.68);

            // ── Buttons ──
            var tickAll  = Ui.Secondary("Tick all");
            var tickNone = Ui.Secondary("Tick none");
            var logs     = Ui.Secondary("Open log folder");
            var close    = Ui.Secondary("Close");
            tickAll.Click  += (s, e) => TickAll(true);
            tickNone.Click += (s, e) => TickAll(false);
            logs.Click     += (s, e) => OpenLogFolder();
            close.Click    += (s, e) => Close();
            _check.Click   += async (s, e) => await RunAsync(checkOnly: true);
            _run.Click     += async (s, e) => await RunAsync(checkOnly: false);
            _stopBt.Click  += (s, e) => { _cancel?.Cancel(); _status.Text = "Stopping after the statement in hand…"; };
            _stopBt.Enabled = false;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(16, 8, 16, 12) };
            buttons.Controls.AddRange(new Control[] { tickAll, tickNone, _check, _run, _stopBt, logs, close, _status });
            _status.Margin = new Padding(10, 8, 0, 0);

            Controls.Add(split);
            Controls.Add(grid);
            Controls.Add(header);
            Controls.Add(buttons);

            FormClosing += (s, e) =>
            {
                if (!_running) { SaveChoices(); return; }
                e.Cancel = true;
                MessageBox.Show(this, "Scripts are running. Stop them first — the one in hand finishes its current statement.",
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            };

            // ── Starting values ──
            _filling = true;
            _aim.Checked  = settings.RunnerAim;
            _stop.Checked = settings.RunnerStopOnError;
            _filling = false;
            _aim.CheckedChanged += (s, e) => { if (!_filling) LoadScripts(); };

            _folder.Items.AddRange(settings.RecentFolders.Cast<object>().ToArray());
            _folder.Text = !string.IsNullOrWhiteSpace(suggestedFolder) ? suggestedFolder : settings.RunnerFolder ?? "";
            _folder.SelectionChangeCommitted += (s, e) => BeginInvoke(new Action(LoadScripts));
            _folder.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { LoadScripts(); e.SuppressKeyPress = true; } };

            Target = suggestedTarget ?? settings.RunnerTarget;
            ShowTarget();
            if (!string.IsNullOrWhiteSpace(_folder.Text)) Shown += (s, e) => LoadScripts();
        }

        private CompareSource Target { get; set; }

        private static void AddRow(TableLayoutPanel grid, string caption, Control field, Control extra)
        {
            int row = grid.RowCount++;
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var label = Ui.Label(caption, Ui.Body, Ui.Muted);
            label.Anchor = AnchorStyles.Left;
            field.Margin = new Padding(0, 4, 6, 4);
            grid.Controls.Add(label, 0, row);
            grid.Controls.Add(field, 1, row);
            if (extra != null) { extra.Margin = new Padding(0, 2, 0, 2); grid.Controls.Add(extra, 2, row); }
        }

        // ── Folder ────────────────────────────────────────────────────────────

        private void BrowseFolder()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "The folder of scripts to run", UseDescriptionForTitle = true, SelectedPath = _folder.Text
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            _folder.Text = dialog.SelectedPath;
            LoadScripts();
        }

        private void LoadScripts()
        {
            if (_running) return;
            string folder = _folder.Text.Trim();
            if (folder.Length == 0) return;

            try
            {
                _items = ScriptRunner.ListScripts(folder, _aim.Checked ? Target?.Database : null);
            }
            catch (Exception ex)
            {
                _items = new List<RunItem>();
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            _settings.RememberFolder(folder);
            FillList();
        }

        private void FillList()
        {
            _filling = true;
            _list.BeginUpdate();
            _list.Items.Clear();
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                var row = new ListViewItem(new[] { (i + 1).ToString(), item.RelativePath, "", "", "", "" })
                { Tag = item, Checked = item.Ticked, UseItemStyleForSubItems = false };
                _list.Items.Add(row);
                UpdateRow(row);
            }
            _list.EndUpdate();
            _filling = false;
            UpdateStatus();
        }

        /// <summary>
        /// Where a script will land: the chosen database once aimed (with what its USE said
        /// before), what its own USE says when not aimed — red when that is not the chosen
        /// database — and any USE further down.
        /// </summary>
        private string DatabaseText(RunItem item, out bool warn)
        {
            warn = false;
            var p = item.Prepared;
            if (p == null) { warn = true; return "could not read the script"; }

            string target = Target?.Database?.Trim() ?? "";
            string text;
            if (p.Aimed)
                text = p.OriginalUse == null || p.OriginalUse.Equals(target, StringComparison.OrdinalIgnoreCase)
                    ? target
                    : $"{target} (was {p.OriginalUse})";
            else if (p.OriginalUse != null)
            {
                text = "USE " + p.OriginalUse;
                warn = !p.OriginalUse.Equals(target, StringComparison.OrdinalIgnoreCase);
            }
            else text = target.Length > 0 ? target : "—";

            if (p.LaterUses.Count > 0)
            {
                text += "  · also " + string.Join(", ", p.LaterUses.Select(u => $"USE {u.Database} (line {u.Line})"));
                warn |= p.LaterUses.Any(u => !u.Database.Equals(target, StringComparison.OrdinalIgnoreCase));
            }
            return text;
        }

        private void UpdateRow(ListViewItem row)
        {
            var item = (RunItem)row.Tag;
            row.SubItems[2].Text = DatabaseText(item, out bool warn);
            row.SubItems[2].ForeColor = warn ? Ui.LeftOnlyFore : Ui.Text;
            row.SubItems[3].Text = item.StateLabel;
            row.SubItems[4].Text = item.State is RunState.Waiting or RunState.Running ? "" : $"{item.Duration.TotalSeconds:0.0}s";
            row.SubItems[5].Text = item.Rows > 0 ? item.Rows.ToString("N0") : "";
            row.ToolTipText = item.Path;

            Color back = item.State switch
            {
                RunState.Succeeded or RunState.CheckedOk => Ui.RightOnlyBack,
                RunState.Failed                          => Ui.LeftOnlyBack,
                RunState.Running                         => Ui.DateBack,
                RunState.Skipped or RunState.NotRun or RunState.Cancelled => Ui.DifferentBack,
                _                                        => Ui.Card
            };
            foreach (ListViewItem.ListViewSubItem sub in row.SubItems) sub.BackColor = back;
        }

        private void TickAll(bool tick)
        {
            if (_running) return;
            _filling = true;
            foreach (ListViewItem row in _list.Items) { row.Checked = tick; ((RunItem)row.Tag).Ticked = tick; }
            _filling = false;
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (_running) return;
            int ticked = _items.Count(i => i.Ticked);
            _status.Text = _items.Count == 0 ? "No scripts yet — choose a folder." : $"{ticked:N0} of {_items.Count:N0} scripts ticked.";
        }

        private void ShowMessages()
        {
            var item = _list.SelectedItems.Count == 0 ? null : (RunItem)_list.SelectedItems[0].Tag;
            if (item == null) { _messages.Text = ""; return; }

            string[] messages;
            lock (item.Messages) messages = item.Messages.ToArray();
            _messages.Text = item.Path + Environment.NewLine + Environment.NewLine +
                             (messages.Length == 0 ? "(no messages)" : string.Join(Environment.NewLine, messages));
        }

        private void OpenSelected()
        {
            var item = _list.SelectedItems.Count == 0 ? null : (RunItem)_list.SelectedItems[0].Tag;
            if (item == null) return;
            try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        // ── Target ────────────────────────────────────────────────────────────

        private void ChooseTarget()
        {
            if (_running) return;
            using var form = new SourceForm("Run on", Target ?? new CompareSource { Kind = SourceKind.Database }, _settings, databaseOnly: true);
            if (form.ShowDialog(this) != DialogResult.OK) return;

            Target = form.Source;
            _settings.RememberConnection(Target);
            ShowTarget();
            LoadScripts();
        }

        private void ShowTarget()
        {
            bool chosen = Target != null && Target.Kind == SourceKind.Database && Target.IsComplete;
            _target.Text = chosen ? Target.Describe() : "Choose the SQL Server database to run the scripts on";
            _target.ForeColor = chosen ? Ui.Text : Ui.Muted;

            if (!chosen) { _permission.Text = ""; return; }

            var (kind, reason) = RunPermission.For(Target, IssueManagerConnections.Load());
            _permission.Text = reason;
            _permission.ForeColor = kind switch
            {
                RunPermissionKind.Allowed => Ui.RightOnlyFore,
                RunPermissionKind.Refused => Ui.LeftOnlyFore,
                _                         => Ui.DifferentFore
            };
            _run.Enabled = _check.Enabled = kind != RunPermissionKind.Refused;
        }

        // ── Running ───────────────────────────────────────────────────────────

        private async Task RunAsync(bool checkOnly)
        {
            if (_running) return;
            if (Target == null || Target.Kind != SourceKind.Database || !Target.IsComplete)
            {
                MessageBox.Show(this, "Choose the database to run on first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var ticked = _items.Where(i => i.Ticked).ToList();
            if (ticked.Count == 0)
            {
                MessageBox.Show(this, "Tick at least one script.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var (kind, reason) = RunPermission.For(Target, IssueManagerConnections.Load());
            if (kind == RunPermissionKind.Refused)
            {
                MessageBox.Show(this, reason, Text, MessageBoxButtons.OK, MessageBoxIcon.Stop);
                return;
            }

            if (!checkOnly)
            {
                var warnings = new List<string>();
                int elsewhere = ticked.Count(i => { DatabaseText(i, out bool w); return w; });
                if (elsewhere > 0)
                    warnings.Add($"{elsewhere} of them name another database in a USE line (shown in red) — those statements run there, not on {Target.Database}.");
                if (!_aim.Checked)
                    warnings.Add("Opening USE lines are not being aimed at the chosen database.");

                if (!ConfirmRunForm.Confirm(this, Target, ticked.Count, kind == RunPermissionKind.ConfirmByTyping, warnings))
                    return;
            }

            var options = new RunOptions { CheckOnly = checkOnly, AimAtDatabase = _aim.Checked, StopOnError = _stop.Checked };
            SetRunning(true, checkOnly);
            _cancel = new CancellationTokenSource();

            var rows = _list.Items.Cast<ListViewItem>().ToDictionary(r => (RunItem)r.Tag);
            void Changed(RunItem item) => BeginInvoke(new Action(() =>
            {
                if (rows.TryGetValue(item, out var row)) { UpdateRow(row); if (item.State == RunState.Running) row.EnsureVisible(); }
                if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag == item) ShowMessages();
            }));

            string log = null;
            try
            {
                var target = Target;
                log = await Task.Run(() => ScriptRunner.Run(_items, target, options, Changed, _cancel.Token));
            }
            finally
            {
                SetRunning(false, checkOnly);
                _cancel.Dispose();
                _cancel = null;
            }

            foreach (ListViewItem row in _list.Items) UpdateRow(row);
            SaveChoices();

            int ok     = ticked.Count(i => i.State is RunState.Succeeded or RunState.CheckedOk);
            int failed = ticked.Count(i => i.State == RunState.Failed);
            int other  = ticked.Count - ok - failed;
            _status.Text = $"{(checkOnly ? "Checked" : "Ran")}: {ok} OK, {failed} failed" + (other > 0 ? $", {other} not run" : "") +
                           (log != null ? "  ·  log saved" : "");
            _status.ForeColor = failed > 0 ? Ui.LeftOnlyFore : Ui.RightOnlyFore;

            var firstFailed = _list.Items.Cast<ListViewItem>().FirstOrDefault(r => ((RunItem)r.Tag).State == RunState.Failed);
            if (firstFailed != null) { firstFailed.Selected = true; firstFailed.EnsureVisible(); }
        }

        private void SetRunning(bool running, bool checkOnly)
        {
            _running = running;
            _check.Enabled = _run.Enabled = !running;
            _stopBt.Enabled = running;
            _folder.Enabled = _aim.Enabled = _stop.Enabled = !running;
            _list.CheckBoxes = true;
            UseWaitCursor = false;
            if (running) { _status.ForeColor = Ui.Muted; _status.Text = checkOnly ? "Checking — nothing is executed…" : "Running scripts…"; }
            else ShowTarget();
        }

        private void SaveChoices()
        {
            _settings.RunnerFolder      = _folder.Text.Trim();
            _settings.RunnerTarget      = Target;
            _settings.RunnerAim         = _aim.Checked;
            _settings.RunnerStopOnError = _stop.Checked;
            _settings.Save();
        }

        private void OpenLogFolder()
        {
            try
            {
                System.IO.Directory.CreateDirectory(ScriptRunner.LogFolder);
                Process.Start("explorer.exe", $"\"{ScriptRunner.LogFolder}\"");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        /// <summary>
        /// The last word before scripts change a database: what will run, where, and what to
        /// watch for. For a database Issue Manager does not know, Run stays off until its
        /// name is typed.
        /// </summary>
        private sealed class ConfirmRunForm : Form
        {
            public static bool Confirm(IWin32Window owner, CompareSource target, int count, bool mustType, IReadOnlyList<string> warnings)
            {
                using var form = new ConfirmRunForm(target, count, mustType, warnings);
                return form.ShowDialog(owner) == DialogResult.OK;
            }

            private ConfirmRunForm(CompareSource target, int count, bool mustType, IReadOnlyList<string> warnings)
            {
                Text            = "Run scripts?";
                Icon            = Ui.AppIcon();
                Font            = Ui.Body;
                BackColor       = Ui.Background;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = MinimizeBox = false;
                StartPosition   = FormStartPosition.CenterParent;
                AutoSize        = true;
                AutoSizeMode    = AutoSizeMode.GrowAndShrink;
                Padding         = new Padding(18, 14, 18, 14);

                var body = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
                body.Controls.Add(Ui.Label($"Run {count} script{(count == 1 ? "" : "s")} on", Ui.Body));
                body.Controls.Add(Ui.Label(target.Describe(), Ui.Title, Ui.Danger));
                body.Controls.Add(Ui.Label("This changes the database. It cannot be undone from here.", Ui.Body, Ui.Muted));

                foreach (string warning in warnings)
                {
                    var label = Ui.Label("⚠ " + warning, Ui.Body, Ui.DifferentFore);
                    label.MaximumSize = new Size(560, 0);
                    label.Margin = new Padding(3, 8, 3, 0);
                    body.Controls.Add(label);
                }

                var run    = Ui.Destructive($"Run {count} script{(count == 1 ? "" : "s")}");
                var cancel = Ui.Secondary("Cancel");
                run.DialogResult    = DialogResult.OK;
                cancel.DialogResult = DialogResult.Cancel;

                if (mustType)
                {
                    var prompt = Ui.Label($"It is not in Issue Manager's SQL connections. Type the database name — {target.Database.Trim()} — to confirm:", Ui.Body);
                    prompt.MaximumSize = new Size(560, 0);
                    prompt.Margin = new Padding(3, 12, 3, 4);
                    var typed = new TextBox { Width = 360 };
                    run.Enabled = false;
                    typed.TextChanged += (s, e) =>
                        run.Enabled = string.Equals(typed.Text.Trim(), target.Database.Trim(), StringComparison.OrdinalIgnoreCase);
                    body.Controls.Add(prompt);
                    body.Controls.Add(typed);
                }

                var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
                buttons.Controls.Add(run);
                buttons.Controls.Add(cancel);
                body.Controls.Add(buttons);

                Controls.Add(body);
                CancelButton = cancel;   // Escape cancels; Enter does nothing — Run is clicked on purpose
            }
        }
    }
}
