using CompareManager.Classes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CompareManager
{
    /// <summary>
    /// Compare Manager's one window, read top to bottom as the steps it takes:
    ///
    /// <list type="number">
    /// <item>Choose the two sides — each a folder of scripts or a SQL Server database.</item>
    /// <item>Load objects: both sides are read, and every object found on either is listed.</item>
    /// <item>Tick the objects and scripts to compare. Against a folder, its own are ticked to
    /// start with; that is nearly always the question — "are my scripts what the server has?"</item>
    /// <item>Compare: the ticked objects whose text differs, or whose change dates differ, are
    /// listed, with those on one side only. Double-click one to see the difference.</item>
    /// </list>
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly AppSettings _settings = AppSettings.Load();

        private List<CatalogEntry>  _catalog = new List<CatalogEntry>();
        private List<CompareResult> _results = new List<CompareResult>();
        private readonly HashSet<string> _ticked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _filling;

        // ── Sources ──
        private readonly SourceCard _leftCard  = new SourceCard("LEFT · PRIMARY, EVERY OBJECT");
        private readonly SourceCard _rightCard = new SourceCard("RIGHT · CHECKED AGAINST THE LEFT");
        private readonly Button     _load      = Ui.Primary("Load objects");
        private readonly Button     _duplicates = Ui.Secondary("Find duplicate scripts");
        private readonly Label      _loadNote  = Ui.Label("", Ui.Body, Ui.Muted);

        // ── Objects ──
        private readonly TextBox  _search      = new TextBox { PlaceholderText = "Search objects…", Width = 220 };
        private readonly ComboBox _kindFilter  = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly ComboBox _whereFilter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
        private readonly ListView _objects     = NewList(checkBoxes: true);
        private readonly Label    _objectNote  = Ui.Label("", Ui.Body, Ui.Muted);

        // ── Results ──
        private readonly CheckBox _ignoreWhitespace = Option("Ignore whitespace");
        private readonly CheckBox _ignoreCase       = Option("Ignore case");
        private readonly CheckBox _ignoreComments   = Option("Ignore comments");
        private readonly CheckBox _compareScriptDates = Option("Script header date");
        private readonly CheckBox _compareDates     = Option("Last changed");
        private readonly CheckBox _compareCreated   = Option("Created");
        private readonly CheckBox _showDifferent    = Option("Different");
        private readonly CheckBox _showDateDiffers  = Option("Dates differ");
        private readonly CheckBox _showOnlyLeft     = Option("Only in left");
        private readonly CheckBox _showOnlyRight    = Option("Only in right");
        private readonly CheckBox _showIdentical    = Option("Identical");
        private readonly Button   _compare          = Ui.Primary("Compare ticked");
        private readonly ListView _resultList       = NewList(checkBoxes: false);
        private readonly Label    _resultNote       = Ui.Label("Load objects, tick what to compare, then Compare.", Ui.Body, Ui.Muted);
        private readonly Button   _viewDiff         = Ui.Secondary("View differences");
        private readonly Button   _winMerge         = Ui.Secondary("Open in WinMerge");
        private readonly Button   _export           = Ui.Secondary("Export CSV…");

        private readonly ToolStripStatusLabel _status = new ToolStripStatusLabel("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };

        private int  _sortColumn = -1;
        private bool _sortAscending = true;

        public MainForm()
        {
            Text          = "Compare Manager";
            Icon          = Ui.AppIcon();
            Font          = Ui.Body;
            BackColor     = Ui.Background;
            ForeColor     = Ui.Text;
            StartPosition = FormStartPosition.CenterScreen;
            Size          = new Size(1360, 860);
            MinimumSize   = new Size(980, 620);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill, BackColor = Ui.Border, SplitterWidth = 6,
                FixedPanel = FixedPanel.Panel1
            };
            split.Panel1.BackColor = Ui.Background;
            split.Panel2.BackColor = Ui.Background;
            split.Panel1.Controls.Add(BuildObjectsPane());
            split.Panel2.Controls.Add(BuildResultsPane());

            var statusStrip = new StatusStrip { BackColor = Ui.Card, SizingGrip = true };
            statusStrip.Items.Add(_status);

            Controls.Add(split);
            Controls.Add(BuildSources());
            Controls.Add(BuildHeader());
            Controls.Add(statusStrip);

            // Sizes only hold once the window has its real size; set earlier, they throw.
            Shown += (s, e) =>
            {
                split.Panel1MinSize    = 300;
                split.Panel2MinSize    = 420;
                split.SplitterDistance = (int)(split.Width * 0.42);
            };
            FormClosing += (s, e) => SaveSettings();

            ApplySettings();
        }

        // ── Building the window ───────────────────────────────────────────────

        private Control BuildHeader()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 68, BackColor = Ui.Header, Padding = new Padding(18, 0, 18, 0) };

            var logo = new PictureBox
            {
                Image = Ui.Logo(44), SizeMode = PictureBoxSizeMode.CenterImage,
                Size = new Size(48, 48), Location = new Point(16, 10), BackColor = Color.Transparent
            };
            var title = Ui.Label("Compare Manager", Ui.Title, Color.White);
            title.Location = new Point(72, 9);
            var sub = Ui.Label("Compare SQL objects and scripts between folders and databases — what differs, and what changed when.",
                               Ui.Body, Color.FromArgb(199, 210, 254));
            sub.Location = new Point(74, 39);

            header.Controls.Add(logo);
            header.Controls.Add(title);
            header.Controls.Add(sub);
            return header;
        }

        private Control BuildSources()
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Top, Height = 150, ColumnCount = 3, RowCount = 2,
                Padding = new Padding(16, 14, 16, 6), BackColor = Ui.Background
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var swap = Ui.Secondary("⇄");
            swap.AutoSize = false;
            swap.Size     = new Size(40, 36);
            swap.Font     = new Font("Segoe UI", 12f);
            swap.Anchor   = AnchorStyles.None;
            swap.Click   += (s, e) => Swap();
            new ToolTip().SetToolTip(swap, "Swap the left and right sides");

            _leftCard.Dock  = DockStyle.Fill;
            _rightCard.Dock = DockStyle.Fill;
            _leftCard.Choose  += (s, e) => ChooseSource(left: true);
            _rightCard.Choose += (s, e) => ChooseSource(left: false);

            grid.Controls.Add(_leftCard, 0, 0);
            grid.Controls.Add(swap, 1, 0);
            grid.Controls.Add(_rightCard, 2, 0);

            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
            _loadNote.Margin = new Padding(10, 8, 0, 0);
            row.Controls.Add(_load);
            row.Controls.Add(_duplicates);
            row.Controls.Add(_loadNote);
            new ToolTip().SetToolTip(_duplicates,
                "Lists the objects a folder defines in more than one script — and which copy runs last — and the scripts saved twice. " +
                "With two folders, pick the left, the right or both.");
            _duplicates.Click += (s, e) => ChooseDuplicateSides();
            grid.SetColumnSpan(row, 3);
            grid.Controls.Add(row, 0, 1);

            _load.Click += async (s, e) => await LoadObjectsAsync();
            return grid;
        }

        private Control BuildObjectsPane()
        {
            var pane = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 6, 8, 12) };

            var title = Ui.Label("Objects to compare", Ui.Bold);
            title.Dock = DockStyle.Top;
            title.Padding = new Padding(0, 0, 0, 6);

            _kindFilter.Items.AddRange(new object[] { "All types", "Stored procedures", "Functions", "Views", "Triggers", "Tables", "Other scripts" });
            _kindFilter.SelectedIndex = 0;
            _whereFilter.Items.AddRange(new object[] { "Either side", "Both sides", "Left only", "Right only" });
            _whereFilter.SelectedIndex = 0;

            var filters = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 6) };
            filters.Controls.AddRange(new Control[] { _search, _kindFilter, _whereFilter });

            var tickAll   = Ui.Secondary("Tick all");
            var tickNone  = Ui.Secondary("Tick none");
            var tickLeft  = Ui.Secondary("Tick left's");
            var tickRight = Ui.Secondary("Tick right's");
            tickAll.Click   += (s, e) => TickShown(_ => true);
            tickNone.Click  += (s, e) => TickShown(_ => false);
            tickLeft.Click  += (s, e) => TickShown(entry => entry.Left != null);
            tickRight.Click += (s, e) => TickShown(entry => entry.Right != null);

            var tip = new ToolTip();
            tip.SetToolTip(tickAll,   "Tick every object in the list as it is filtered now");
            tip.SetToolTip(tickNone,  "Untick every object in the list as it is filtered now");
            tip.SetToolTip(tickLeft,  "Tick only the objects the left side has (of those listed)");
            tip.SetToolTip(tickRight, "Tick only the objects the right side has (of those listed)");

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            buttons.Controls.AddRange(new Control[] { tickAll, tickNone, tickLeft, tickRight });

            _objectNote.Dock = DockStyle.Bottom;
            _objectNote.Padding = new Padding(0, 6, 0, 0);

            _objects.Columns.Add("Object", 280);
            _objects.Columns.Add("Type", 120);
            _objects.Columns.Add("Found in", 80);
            _objects.ItemChecked += (s, e) =>
            {
                if (_filling) return;
                var entry = (CatalogEntry)e.Item.Tag;
                if (e.Item.Checked) _ticked.Add(entry.Key); else _ticked.Remove(entry.Key);
                UpdateObjectNote();
            };

            var debounce = new Timer { Interval = 250 };
            debounce.Tick += (s, e) => { debounce.Stop(); FillObjects(); };
            _search.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
            _kindFilter.SelectedIndexChanged  += (s, e) => FillObjects();
            _whereFilter.SelectedIndexChanged += (s, e) => FillObjects();

            pane.Controls.Add(_objects);
            pane.Controls.Add(filters);
            pane.Controls.Add(title);
            pane.Controls.Add(_objectNote);
            pane.Controls.Add(buttons);
            return pane;
        }

        private Control BuildResultsPane()
        {
            var pane = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 6, 16, 12) };

            var title = Ui.Label("Differences", Ui.Bold);
            title.Dock = DockStyle.Top;
            title.Padding = new Padding(0, 0, 0, 6);

            var options = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 0, 0, 4) };
            options.Controls.AddRange(new Control[] { _compare, _ignoreWhitespace, _ignoreCase, _ignoreComments });
            _compare.Margin = new Padding(0, 0, 12, 0);

            Label RowLabel(string text)
            {
                var label = Ui.Label(text, Ui.Bold, Ui.Muted);
                label.AutoSize = false;
                label.Size     = new Size(84, 22);
                label.Margin   = new Padding(0, 6, 4, 0);
                return label;
            }

            var dates = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 2, 0, 0) };
            dates.Controls.AddRange(new Control[] { RowLabel("Check dates:"), _compareScriptDates, _compareDates, _compareCreated });

            var show = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(0, 2, 0, 0) };
            show.Controls.AddRange(new Control[] { RowLabel("Show:"), _showDifferent, _showDateDiffers, _showOnlyRight, _showOnlyLeft, _showIdentical });

            var tip = new ToolTip();
            tip.SetToolTip(_ignoreWhitespace,   "Indentation, spacing and blank lines do not count as differences");
            tip.SetToolTip(_ignoreCase,         "SELECT and select are the same");
            tip.SetToolTip(_ignoreComments,     "Only the code that runs is compared, not the comments");
            tip.SetToolTip(_compareScriptDates, "The latest date written in each script's comments — the Change Control header, the modification history. Compared to the day.");
            tip.SetToolTip(_compareDates,       "Last changed: the server's modify_date, or the file's last modified time");
            tip.SetToolTip(_compareCreated,     "Created: the server's create_date, or the file's created time");
            tip.SetToolTip(_showDifferent,    "Objects whose code differs between the two sides");
            tip.SetToolTip(_showDateDiffers,  "Objects with the same code but a different date (of those being checked)");
            tip.SetToolTip(_showOnlyRight,    "Ticked objects the right side has and the left does not — new ones");
            tip.SetToolTip(_showOnlyLeft,     "Ticked objects the left side has and the right does not");
            tip.SetToolTip(_showIdentical,    "Objects that are the same on both sides");

            _resultNote.Dock = DockStyle.Top;
            _resultNote.Padding = new Padding(0, 4, 0, 8);

            foreach (var column in ResultColumns) _resultList.Columns.Add(column.Title, column.Width);
            _resultList.DoubleClick += (s, e) => ViewDiff();
            _resultList.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) ViewDiff(); };
            _resultList.ColumnClick += (s, e) => SortResults(e.Column);
            _resultList.SelectedIndexChanged += (s, e) => UpdateResultButtons();
            _resultList.ContextMenuStrip = ResultMenu();

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
            buttons.Controls.AddRange(new Control[] { _viewDiff, _winMerge, _export });

            _compare.Click  += async (s, e) => await CompareAsync();
            _viewDiff.Click += (s, e) => ViewDiff();
            _winMerge.Click += (s, e) => OpenWinMerge();
            _export.Click   += (s, e) => Export();

            foreach (var box in new[] { _ignoreWhitespace, _ignoreCase, _ignoreComments, _compareScriptDates, _compareDates, _compareCreated })
                box.CheckedChanged += async (s, e) => { if (!_filling && _results.Count > 0) await CompareAsync(); };
            foreach (var box in new[] { _showDifferent, _showDateDiffers, _showOnlyLeft, _showOnlyRight, _showIdentical })
                box.CheckedChanged += (s, e) => { if (!_filling) { SaveSettings(); FillResults(); } };

            pane.Controls.Add(_resultList);
            pane.Controls.Add(_resultNote);
            pane.Controls.Add(show);
            pane.Controls.Add(dates);
            pane.Controls.Add(options);
            pane.Controls.Add(title);
            pane.Controls.Add(buttons);

            UpdateResultButtons();
            return pane;
        }

        private ContextMenuStrip ResultMenu()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("View differences", null, (s, e) => ViewDiff());
            menu.Items.Add("Open in WinMerge", null, (s, e) => OpenWinMerge());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Copy name", null, (s, e) =>
            {
                var names = SelectedResults().Select(r => r.Any.DisplayName).ToList();
                if (names.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, names));
            });
            var openLeft  = menu.Items.Add("Open left script",  null, (s, e) => OpenScript(SelectedResult()?.Left));
            var openRight = menu.Items.Add("Open right script", null, (s, e) => OpenScript(SelectedResult()?.Right));
            menu.Opening += (s, e) =>
            {
                var r = SelectedResult();
                if (r == null) { e.Cancel = true; return; }
                openLeft.Visible  = r.Left?.IsFile  == true;
                openRight.Visible = r.Right?.IsFile == true;
            };
            return menu;
        }

        private static ListView NewList(bool checkBoxes)
        {
            var list = new ListView
            {
                Dock          = DockStyle.Fill,
                View          = View.Details,
                FullRowSelect = true,
                CheckBoxes    = checkBoxes,
                HideSelection = false,
                BorderStyle   = BorderStyle.FixedSingle,
                BackColor     = Ui.Card,
                Font          = Ui.Body,
                ShowItemToolTips = true
            };

            // The Object column (the second on the results, the first on the objects) takes
            // whatever width the others leave, so nothing runs off the right edge.
            list.Resize += (s, e) =>
            {
                if (list.Columns.Count < 2) return;
                int fill  = checkBoxes ? 0 : 1;
                int other = list.Columns.Cast<ColumnHeader>().Where(c => c.Index != fill).Sum(c => c.Width);
                list.Columns[fill].Width = Math.Max(160, list.ClientSize.Width - other - 4);
            };
            return list;
        }

        private static CheckBox Option(string text) => new CheckBox
        {
            Text = text, AutoSize = true, Margin = new Padding(0, 6, 14, 0), ForeColor = Ui.Text
        };

        // ── Settings ──────────────────────────────────────────────────────────

        private void ApplySettings()
        {
            _filling = true;
            _ignoreWhitespace.Checked = _settings.Options.IgnoreWhitespace;
            _ignoreCase.Checked       = _settings.Options.IgnoreCase;
            _ignoreComments.Checked   = _settings.Options.IgnoreComments;
            _compareDates.Checked     = _settings.Options.CompareDates;
            _compareCreated.Checked   = _settings.Options.CompareCreated;
            _compareScriptDates.Checked = _settings.Options.CompareScriptDates;
            _showDifferent.Checked    = _settings.ShowDifferent;
            _showDateDiffers.Checked  = _settings.ShowDateDiffers;
            _showOnlyLeft.Checked     = _settings.ShowOnlyLeft;
            _showOnlyRight.Checked    = _settings.ShowOnlyRight;
            _showIdentical.Checked    = _settings.ShowIdentical;
            _filling = false;

            ShowSources();
        }

        private CompareOptions ReadOptions() => new CompareOptions
        {
            IgnoreWhitespace = _ignoreWhitespace.Checked,
            IgnoreCase       = _ignoreCase.Checked,
            IgnoreComments   = _ignoreComments.Checked,
            CompareDates       = _compareDates.Checked,
            CompareCreated     = _compareCreated.Checked,
            CompareScriptDates = _compareScriptDates.Checked
        };

        private void SaveSettings()
        {
            _settings.Options       = ReadOptions();
            _settings.ShowDifferent   = _showDifferent.Checked;
            _settings.ShowDateDiffers = _showDateDiffers.Checked;
            _settings.ShowOnlyLeft    = _showOnlyLeft.Checked;
            _settings.ShowOnlyRight   = _showOnlyRight.Checked;
            _settings.ShowIdentical   = _showIdentical.Checked;
            _settings.Save();
        }

        // ── Sources ───────────────────────────────────────────────────────────

        private void ShowSources()
        {
            _leftCard.Show(_settings.Left);
            _rightCard.Show(_settings.Right);
        }

        private void ChooseSource(bool left)
        {
            var current = left ? _settings.Left : _settings.Right;
            using var form = new SourceForm(left ? "Left" : "Right", current, _settings);
            if (form.ShowDialog(this) != DialogResult.OK) return;

            if (left) _settings.Left = form.Source; else _settings.Right = form.Source;
            if (form.Source.Kind == SourceKind.Folder) _settings.RememberFolder(form.Source.Folder);
            else _settings.RememberConnection(form.Source);
            SaveSettings();
            ShowSources();

            // What was loaded no longer matches the sides; nothing stale is left on screen.
            ClearLoaded("Sides changed — Load objects to read them.");
        }

        /// <summary>Swaps the sides — and what was loaded and compared with them, so nothing has to be read again.</summary>
        private void Swap()
        {
            (_settings.Left, _settings.Right) = (_settings.Right, _settings.Left);
            foreach (var entry in _catalog) (entry.Left, entry.Right) = (entry.Right, entry.Left);
            foreach (var result in _results) (result.Left, result.Right) = (result.Right, result.Left);
            SaveSettings();
            ShowSources();
            FillObjects();
            FillResults();
        }

        private void ClearLoaded(string note)
        {
            _catalog = new List<CatalogEntry>();
            _results = new List<CompareResult>();
            _ticked.Clear();
            FillObjects();
            FillResults();
            _loadNote.Text = note;
        }

        // ── Loading ───────────────────────────────────────────────────────────

        private async Task LoadObjectsAsync()
        {
            var left  = _settings.Left;
            var right = _settings.Right;
            if (!left.IsComplete || !right.IsComplete)
            {
                MessageBox.Show(this, "Choose both sides first — click a side to pick a folder or a database.",
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            SetBusy(true, "Reading both sides…");
            var watch = Stopwatch.StartNew();

            var leftTask  = Task.Run(() => left.Load());
            var rightTask = Task.Run(() => right.Load());
            try { await Task.WhenAll(leftTask, rightTask); }
            catch { /* each side's error is reported below, by side */ }

            SetBusy(false, "Ready");

            var errors = new List<string>();
            if (leftTask.IsFaulted)  errors.Add("Left side (" + left.Describe() + "):\n" + leftTask.Exception.GetBaseException().Message);
            if (rightTask.IsFaulted) errors.Add("Right side (" + right.Describe() + "):\n" + rightTask.Exception.GetBaseException().Message);
            if (errors.Count > 0)
            {
                MessageBox.Show(this, "Could not read:\n\n" + string.Join("\n\n", errors), Text,
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                ClearLoaded("Loading failed — see the message.");
                return;
            }

            _catalog = ObjectComparer.Catalog(leftTask.Result, rightTask.Result);
            _results = new List<CompareResult>();
            _ticked.Clear();

            // The left is the primary set — everything — and the right is what is checked
            // against it, so the right side's objects start ticked. Comparing all 27,000 of the
            // left's would only bury the answer under objects the right never had.
            Func<CatalogEntry, bool> startTicked = rightTask.Result.Count > 0 ? e => e.Right != null : e => true;
            foreach (var entry in _catalog.Where(startTicked)) _ticked.Add(entry.Key);

            _loadNote.Text = $"{leftTask.Result.Count:N0} on the left, {rightTask.Result.Count:N0} on the right, " +
                             $"{_catalog.Count(e => e.Left != null && e.Right != null):N0} on both — read in {watch.Elapsed.TotalSeconds:0.0}s.";
            _status.Text = "Loaded. Tick the objects to compare, then Compare ticked.";

            // Only the newest of several scripts for one object is compared, so say when
            // that happened rather than let it pass unseen.
            int leftDuplicated  = leftTask.Result.Count(o => o.OtherCopies.Count > 0);
            int rightDuplicated = rightTask.Result.Count(o => o.OtherCopies.Count > 0);
            if (leftDuplicated + rightDuplicated > 0)
            {
                var sides = new List<string>();
                if (leftDuplicated  > 0) sides.Add($"left {leftDuplicated:N0}");
                if (rightDuplicated > 0) sides.Add($"right {rightDuplicated:N0}");
                _loadNote.Text += $"  Objects with more than one script: {string.Join(", ", sides)} — Find duplicate scripts lists them.";
                _loadNote.ForeColor = Ui.DifferentFore;
            }
            else _loadNote.ForeColor = Ui.Muted;

            FillObjects();
            FillResults();
        }

        private void SetBusy(bool busy, string status)
        {
            UseWaitCursor     = busy;
            _load.Enabled       = !busy;
            _duplicates.Enabled = !busy;
            _compare.Enabled    = !busy;
            _leftCard.Enabled   = _rightCard.Enabled = !busy;
            _status.Text        = status;
        }

        // ── Duplicates ────────────────────────────────────────────────────────

        /// <summary>
        /// Which folders to look in. With one folder side there is nothing to ask; with two,
        /// a menu under the button offers the left, the right or both.
        /// </summary>
        private void ChooseDuplicateSides()
        {
            bool leftIsFolder  = _settings.Left.Kind  == SourceKind.Folder && _settings.Left.IsComplete;
            bool rightIsFolder = _settings.Right.Kind == SourceKind.Folder && _settings.Right.IsComplete;

            if (!leftIsFolder && !rightIsFolder)
            {
                MessageBox.Show(this, "Duplicates are looked for in a folder of scripts, and neither side is one. " +
                                      "Choose a folder for one of the sides first.",
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (leftIsFolder != rightIsFolder)
            {
                _ = FindDuplicatesAsync(left: leftIsFolder, right: rightIsFolder);
                return;
            }

            var menu = new ContextMenuStrip { Font = Ui.Body };
            menu.Items.Add($"Left folder — {_settings.Left.Folder.Trim()}",   null, async (s, e) => await FindDuplicatesAsync(left: true,  right: false));
            menu.Items.Add($"Right folder — {_settings.Right.Folder.Trim()}", null, async (s, e) => await FindDuplicatesAsync(left: false, right: true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Both folders", null, async (s, e) => await FindDuplicatesAsync(left: true, right: true));
            menu.Closed += (s, e) => BeginInvoke(new Action(menu.Dispose));
            menu.Show(_duplicates, new Point(0, _duplicates.Height));
        }

        /// <summary>
        /// Looks for duplicate scripts in the folders asked for, reading them afresh — so it
        /// works before anything is loaded and needs no database.
        /// </summary>
        private async Task FindDuplicatesAsync(bool left, bool right)
        {
            var sides = new List<(string Side, string Folder)>();
            if (left)  sides.Add(("Left",  _settings.Left.Folder.Trim()));
            if (right) sides.Add(("Right", _settings.Right.Folder.Trim()));

            var options = ReadOptions();
            SetBusy(true, "Looking for duplicate scripts in " + string.Join(" and ", sides.Select(s => s.Folder)) + "…");

            List<DuplicateGroup> groups;
            try
            {
                var found = await Task.WhenAll(sides.Select(side =>
                    Task.Run(() => Duplicates.Find(FolderSource.ReadAll(side.Folder), options, side.Side, side.Folder))));
                groups = found.SelectMany(g => g).ToList();
            }
            catch (Exception ex)
            {
                SetBusy(false, "Ready");
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SetBusy(false, groups.Count == 0 ? "No duplicate scripts found." : $"{groups.Count:N0} sets of duplicate scripts found.");

            using var form = new DuplicatesForm(sides, groups, options);
            form.ShowDialog(this);
        }

        // ── The object list ───────────────────────────────────────────────────

        private IEnumerable<CatalogEntry> Listed()
        {
            string search = _search.Text.Trim();
            ObjectKind? kind = _kindFilter.SelectedIndex switch
            {
                1 => ObjectKind.Procedure,
                2 => ObjectKind.Function,
                3 => ObjectKind.View,
                4 => ObjectKind.Trigger,
                5 => ObjectKind.Table,
                6 => ObjectKind.Script,
                _ => null
            };

            return _catalog.Where(e =>
                (kind == null || e.Any.Kind == kind) &&
                (_whereFilter.SelectedIndex switch
                {
                    1 => e.Left != null && e.Right != null,
                    2 => e.Right == null,
                    3 => e.Left == null,
                    _ => true
                }) &&
                (search.Length == 0 || e.Any.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        private void FillObjects()
        {
            _filling = true;
            _objects.BeginUpdate();
            try
            {
                _objects.Items.Clear();
                var items = Listed().Select(entry =>
                {
                    var item = new ListViewItem(new[] { entry.Any.DisplayName, entry.Any.KindLabel, entry.Where })
                    {
                        Tag         = entry,
                        Checked     = _ticked.Contains(entry.Key),
                        ToolTipText = Origins(entry)
                    };
                    if (entry.Left == null || entry.Right == null) item.ForeColor = Ui.Muted;
                    return item;
                }).ToArray();
                _objects.Items.AddRange(items);
            }
            finally
            {
                _objects.EndUpdate();
                _filling = false;
            }
            UpdateObjectNote();
        }

        private static string Origins(CatalogEntry entry)
        {
            var text = new StringBuilder();
            if (entry.Left  != null) text.AppendLine("Left: "  + entry.Left.Origin);
            if (entry.Right != null) text.AppendLine("Right: " + entry.Right.Origin);
            foreach (var obj in new[] { entry.Left, entry.Right }.Where(o => o != null && o.OtherCopies.Count > 0))
                text.AppendLine($"Also defined in {obj.OtherCopies.Count} older script(s); the newest is compared.");
            return text.ToString().TrimEnd();
        }

        private void TickShown(Func<CatalogEntry, bool> tick)
        {
            foreach (var entry in Listed())
                if (tick(entry)) _ticked.Add(entry.Key); else _ticked.Remove(entry.Key);
            FillObjects();
        }

        private void UpdateObjectNote()
        {
            _objectNote.Text = _catalog.Count == 0
                ? "Nothing loaded yet."
                : $"{_ticked.Count:N0} of {_catalog.Count:N0} ticked · {_objects.Items.Count:N0} shown";
            _compare.Text = _ticked.Count > 0 ? $"Compare ticked ({_ticked.Count:N0})" : "Compare ticked";
        }

        // ── Comparing ─────────────────────────────────────────────────────────

        private async Task CompareAsync()
        {
            var entries = _catalog.Where(e => _ticked.Contains(e.Key)).ToList();
            if (entries.Count == 0)
            {
                MessageBox.Show(this, _catalog.Count == 0
                                    ? "Load objects first, then tick the ones to compare."
                                    : "Tick at least one object to compare.",
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var options = ReadOptions();
            SetBusy(true, $"Comparing {entries.Count:N0} objects…");
            try
            {
                _results = await Task.Run(() => ObjectComparer.Compare(entries, options));
            }
            finally
            {
                SetBusy(false, "Compared.");
            }

            SaveSettings();
            FillResults();
        }

        private void FillResults()
        {
            var shown = Sorted(_results.Where(r => Shows(r.Status)).ToList());

            _resultList.BeginUpdate();
            try
            {
                _resultList.Items.Clear();
                _resultList.Items.AddRange(shown.Select(ResultItem).ToArray());
            }
            finally
            {
                _resultList.EndUpdate();
            }

            if (_results.Count == 0)
                _resultNote.Text = _catalog.Count == 0 ? "Load objects, tick what to compare, then Compare." : "Tick what to compare, then Compare.";
            else
            {
                // Every kind is counted, shown or not, so a hidden one is never a silent one.
                string Count(CompareStatus s, string label)
                {
                    int n = _results.Count(r => r.Status == s);
                    return $"{n:N0} {label}" + (n > 0 && !Shows(s) ? " (hidden)" : "");
                }
                _resultNote.Text = string.Join("  ·  ",
                    Count(CompareStatus.Different,   "different"),
                    Count(CompareStatus.DateDiffers, "dates differ"),
                    Count(CompareStatus.OnlyRight,   "only in right"),
                    Count(CompareStatus.OnlyLeft,    "only in left"),
                    Count(CompareStatus.Identical,   "identical"));
            }
            UpdateResultButtons();
        }

        private bool Shows(CompareStatus status) => status switch
        {
            CompareStatus.Different   => _showDifferent.Checked,
            CompareStatus.DateDiffers => _showDateDiffers.Checked,
            CompareStatus.OnlyLeft    => _showOnlyLeft.Checked,
            CompareStatus.OnlyRight   => _showOnlyRight.Checked,
            _                         => _showIdentical.Checked
        };

        /// <summary>
        /// The results' columns, in order: what each shows and what it sorts by. The list,
        /// the sorting and the CSV export all read this one table.
        /// </summary>
        private static readonly (string Title, int Width, Func<CompareResult, string> Text, Func<CompareResult, object> Sort)[] ResultColumns =
        {
            ("Status",            260, r => r.StatusLabel,             r => r.Status),
            ("Object",            240, r => r.Any.DisplayName,         r => r.Any.DisplayName.ToLowerInvariant()),
            ("Type",              115, r => r.Any.KindLabel,           r => r.Any.KindLabel),
            ("Left script date",  105, r => Day(r.Left?.ScriptDate),   r => r.Left?.ScriptDate  ?? DateTime.MinValue),
            ("Right script date", 105, r => Day(r.Right?.ScriptDate),  r => r.Right?.ScriptDate ?? DateTime.MinValue),
            ("Left changed",      130, r => Stamp(r.Left?.Modified),   r => r.Left?.Modified    ?? DateTime.MinValue),
            ("Right changed",     130, r => Stamp(r.Right?.Modified),  r => r.Right?.Modified   ?? DateTime.MinValue),
            ("Left created",      130, r => Stamp(r.Left?.Created),    r => r.Left?.Created     ?? DateTime.MinValue),
            ("Right created",     130, r => Stamp(r.Right?.Created),   r => r.Right?.Created    ?? DateTime.MinValue),
            ("Newer",              55, r => r.Newer,                   r => r.Newer)
        };

        private static ListViewItem ResultItem(CompareResult r)
        {
            var item = new ListViewItem(ResultColumns.Select(c => c.Text(r)).ToArray())
            { Tag = r, UseItemStyleForSubItems = true };

            (item.BackColor, item.ForeColor) = r.Status switch
            {
                CompareStatus.Different   => (Ui.DifferentBack, Ui.DifferentFore),
                CompareStatus.DateDiffers => (Ui.DateBack,      Ui.DateFore),
                CompareStatus.OnlyLeft    => (Ui.LeftOnlyBack,  Ui.LeftOnlyFore),
                CompareStatus.OnlyRight   => (Ui.RightOnlyBack, Ui.RightOnlyFore),
                _                         => (Ui.Card,          Ui.SameFore)
            };
            return item;
        }

        private static string Stamp(DateTime? when) => when?.ToString("yyyy-MM-dd HH:mm:ss") ?? "";

        private static string Day(DateTime? when) => when?.ToString("yyyy-MM-dd") ?? "";

        private void SortResults(int column)
        {
            _sortAscending = column == _sortColumn ? !_sortAscending : true;
            _sortColumn    = column;
            FillResults();
        }

        private List<CompareResult> Sorted(List<CompareResult> list)
        {
            if (_sortColumn < 0) return list.OrderBy(r => r.Status).ThenBy(r => r.Any.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            var key    = ResultColumns[Math.Min(_sortColumn, ResultColumns.Length - 1)].Sort;
            var sorted = _sortAscending ? list.OrderBy(key) : list.OrderByDescending(key);
            return sorted.ThenBy(r => r.Any.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        }

        // ── Acting on a result ────────────────────────────────────────────────

        private CompareResult SelectedResult() => SelectedResults().FirstOrDefault();

        private List<CompareResult> SelectedResults() =>
            _resultList.SelectedItems.Cast<ListViewItem>().Select(i => (CompareResult)i.Tag).ToList();

        private void UpdateResultButtons()
        {
            bool one = _resultList.SelectedItems.Count > 0;
            _viewDiff.Enabled = one;
            _winMerge.Enabled = one;
            _export.Enabled   = _results.Count > 0;
        }

        private void ViewDiff()
        {
            var result = SelectedResult();
            if (result == null) return;
            using var form = new DiffForm(result, _settings.Left.ShortLabel(), _settings.Right.ShortLabel(), ReadOptions());
            form.ShowDialog(this);
        }

        private void OpenWinMerge()
        {
            var result = SelectedResult();
            if (result == null) return;
            try
            {
                WinMerge.Open(result, _settings.Left.ShortLabel(), _settings.Right.ShortLabel());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message + "\n\nView differences shows the same comparison here.",
                                Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void OpenScript(SqlObject obj)
        {
            if (obj == null || !obj.IsFile) return;
            try { Process.Start(new ProcessStartInfo(obj.Origin) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        private void Export()
        {
            if (_results.Count == 0) return;

            using var dialog = new SaveFileDialog
            {
                Filter   = "CSV file (*.csv)|*.csv",
                FileName = $"Compare {DateTime.Now:yyyy-MM-dd HHmm}.csv"
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;

            static string Cell(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

            var csv = new StringBuilder();
            csv.AppendLine(string.Join(",", ResultColumns.Select(c => c.Title).Concat(new[] { "Left", "Right" }).Select(Cell)));
            foreach (var r in Sorted(_results))
                csv.AppendLine(string.Join(",", ResultColumns.Select(c => c.Text(r))
                                                             .Concat(new[] { r.Left?.Origin, r.Right?.Origin })
                                                             .Select(Cell)));

            try
            {
                File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                _status.Text = "Exported to " + dialog.FileName;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // ── A side ────────────────────────────────────────────────────────────

        /// <summary>One side's card: which side, what kind of source, where it is. Clicking anywhere on it changes it.</summary>
        private sealed class SourceCard : Panel
        {
            private readonly Label  _kind  = Ui.Label("", Ui.Caption, Ui.Accent);
            private readonly Label  _where = new Label { AutoEllipsis = true, Font = Ui.Bold, ForeColor = Ui.Text };
            private readonly Button _change = Ui.Secondary("Change…");

            public event EventHandler Choose;

            public SourceCard(string side)
            {
                BackColor = Ui.Card;
                Padding   = new Padding(14, 10, 14, 10);
                Cursor    = Cursors.Hand;
                Margin    = new Padding(0);

                _caption = Ui.Label(side, Ui.Caption, Ui.Muted);
                _caption.Location = new Point(14, 10);
                _kind.Location    = new Point(64, 10);
                _where.Location   = new Point(14, 34);
                _where.Height     = 24;

                _change.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                Controls.Add(_caption);
                Controls.Add(_kind);
                Controls.Add(_where);
                Controls.Add(_change);

                foreach (Control c in new Control[] { this, _caption, _kind, _where, _change })
                    c.Click += (s, e) => Choose?.Invoke(this, EventArgs.Empty);
            }

            private readonly Label _caption;

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);

                // Setting Padding in the constructor lays the card out before the caption is
                // made, so the first pass has none to measure.
                if (_caption == null) return;
                _kind.Left       = _caption.Right + 12;
                _change.Location = new Point(Width - _change.Width - 14, (Height - _change.Height) / 2);
                _where.Width     = Math.Max(50, _change.Left - 14 - 12);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using var pen = new Pen(Ui.Border);
                e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }

            public void Show(CompareSource source)
            {
                _kind.Text  = !source.IsComplete ? "" : source.Kind == SourceKind.Folder ? "FOLDER OF SCRIPTS" : "SQL SERVER DATABASE";
                _where.Text = source.IsComplete ? source.Describe() : "Click to choose a folder or a database";
                _where.ForeColor = source.IsComplete ? Ui.Text : Ui.Muted;
                Invalidate();
            }
        }
    }
}
