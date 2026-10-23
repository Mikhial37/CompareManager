using CompareManager.Classes;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CompareManager
{
    /// <summary>
    /// Shows one object's two definitions side by side, line for line, with the lines that
    /// differ tinted — so a difference can be read without leaving the app. One grid holds
    /// both sides, which is what keeps them scrolling together.
    ///
    /// Open in WinMerge is here too, for anyone who wants to edit or merge.
    /// </summary>
    public sealed class DiffForm : Form
    {
        private readonly CompareResult _result;
        private readonly string        _leftLabel;
        private readonly string        _rightLabel;
        private readonly List<DiffRow> _rows;
        private readonly DataGridView  _grid;
        private readonly Label         _summary;

        private static readonly Color ChangedBack = Color.FromArgb(254, 249, 195);
        private static readonly Color RemovedBack = Color.FromArgb(255, 228, 230);
        private static readonly Color AddedBack   = Color.FromArgb(220, 252, 231);
        private static readonly Color EmptyBack   = Color.FromArgb(241, 245, 249);

        public DiffForm(CompareResult result, string leftLabel, string rightLabel, CompareOptions options)
        {
            _result     = result;
            _leftLabel  = leftLabel;
            _rightLabel = rightLabel;

            Func<string, string> key = line =>
            {
                string k = options.IgnoreWhitespace ? string.Join(" ", line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)) : line.TrimEnd();
                return options.IgnoreCase ? k.ToUpperInvariant() : k;
            };

            // A missing side is shown as nothing at all, so every line reads as removed or added.
            _rows = result.Left == null
                ? LineDiff.Lines(result.Right.Definition).Select((l, i) => new DiffRow { Kind = DiffKind.Added, RightNumber = i + 1, RightText = l }).ToList()
                : result.Right == null
                    ? LineDiff.Lines(result.Left.Definition).Select((l, i) => new DiffRow { Kind = DiffKind.Removed, LeftNumber = i + 1, LeftText = l }).ToList()
                    : LineDiff.Compare(result.Left.Definition, result.Right.Definition, key);

            Text          = $"{result.Any.DisplayName} — Compare Manager";
            Icon          = Ui.AppIcon();
            Font          = Ui.Body;
            BackColor     = Ui.Background;
            StartPosition = FormStartPosition.CenterParent;
            Size          = new Size(1280, 800);
            MinimumSize   = new Size(700, 400);

            // ── Header ──
            var title = Ui.Label(result.Any.DisplayName, Ui.Title);
            var sub   = Ui.Label($"{result.Any.KindLabel}  ·  {result.StatusLabel}", Ui.Body, Ui.Muted);

            var previous = Ui.Secondary("▲ Previous difference");
            var next     = Ui.Secondary("▼ Next difference");
            var winMerge = Ui.Secondary("Open in WinMerge");
            previous.Click += (s, e) => Jump(-1);
            next.Click     += (s, e) => Jump(+1);
            winMerge.Click += (s, e) => OpenWinMerge();
            winMerge.Enabled = WinMerge.IsInstalled;

            _summary = Ui.Label(Summary(), Ui.Body, Ui.Muted);
            _summary.Margin = new Padding(12, 8, 0, 0);

            var tools = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 8, 0, 8) };
            tools.Controls.AddRange(new Control[] { previous, next, winMerge, _summary });

            var header = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown,
                Padding = new Padding(16, 12, 16, 0), WrapContents = false
            };
            header.Controls.Add(title);
            header.Controls.Add(sub);

            // ── Side captions ──
            var captions = new TableLayoutPanel { Dock = DockStyle.Top, Height = 28, ColumnCount = 2, Padding = new Padding(16, 0, 16, 0) };
            captions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            captions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            captions.Controls.Add(SideCaption("LEFT", leftLabel, result.Left), 0, 0);
            captions.Controls.Add(SideCaption("RIGHT", rightLabel, result.Right), 1, 0);

            _grid = BuildGrid();
            var gridHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 16) };
            gridHost.Controls.Add(_grid);

            Controls.Add(gridHost);
            Controls.Add(captions);
            Controls.Add(tools);
            Controls.Add(header);
            tools.Padding = new Padding(16, 8, 16, 8);

            KeyPreview = true;
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) Close();
                else if (e.KeyCode == Keys.F8 || (e.KeyCode == Keys.Down && e.Alt)) { Jump(+1); e.Handled = true; }
                else if (e.KeyCode == Keys.F7 || (e.KeyCode == Keys.Up && e.Alt))   { Jump(-1); e.Handled = true; }
            };

            // The grid puts its current cell on the first row; from there the first jump
            // would step over a difference that starts on line 1.
            Shown += (s, e) => { _grid.CurrentCell = null; Jump(+1); };
        }

        private static Label SideCaption(string side, string source, SqlObject obj)
        {
            string when = obj?.Modified is DateTime d ? $"  ·  changed {d:yyyy-MM-dd HH:mm:ss}" : "";
            string what = obj == null ? "  ·  not here" : when;
            return new Label
            {
                Text = $"{side}  {source}{what}", AutoEllipsis = true, Dock = DockStyle.Fill,
                Font = Ui.Caption, ForeColor = Ui.Muted, TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private DataGridView BuildGrid()
        {
            var grid = new DataGridView
            {
                Dock                     = DockStyle.Fill,
                ReadOnly                 = true,
                VirtualMode              = true,
                AllowUserToAddRows       = false,
                AllowUserToDeleteRows    = false,
                AllowUserToResizeRows    = false,
                RowHeadersVisible        = false,
                ColumnHeadersVisible     = false,
                SelectionMode            = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor          = Ui.Card,
                BorderStyle              = BorderStyle.FixedSingle,
                CellBorderStyle          = DataGridViewCellBorderStyle.None,
                GridColor                = Ui.Border,
                Font                     = Ui.Code,
                AutoSizeColumnsMode      = DataGridViewAutoSizeColumnsMode.None,
                ScrollBars               = ScrollBars.Both
            };
            grid.RowTemplate.Height = 20;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(199, 210, 254);
            grid.DefaultCellStyle.SelectionForeColor = Ui.Text;
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;

            grid.Columns.Add(NumberColumn());
            grid.Columns.Add(TextColumn());
            grid.Columns.Add(NumberColumn());
            grid.Columns.Add(TextColumn());

            grid.RowCount = _rows.Count;
            grid.CellValueNeeded += (s, e) =>
            {
                var row = _rows[e.RowIndex];
                e.Value = e.ColumnIndex switch
                {
                    0 => row.LeftNumber?.ToString(),
                    1 => row.LeftText.Replace("\t", "    "),
                    2 => row.RightNumber?.ToString(),
                    _ => row.RightText.Replace("\t", "    ")
                };
            };
            grid.CellFormatting += (s, e) =>
            {
                var  row  = _rows[e.RowIndex];
                bool left = e.ColumnIndex < 2;
                bool number = e.ColumnIndex == 0 || e.ColumnIndex == 2;

                Color back = row.Kind switch
                {
                    DiffKind.Changed => ChangedBack,
                    DiffKind.Removed => left ? RemovedBack : EmptyBack,
                    DiffKind.Added   => left ? EmptyBack : AddedBack,
                    _                => Ui.Card
                };
                e.CellStyle.BackColor = back;
                e.CellStyle.ForeColor = number ? Ui.Muted : Ui.Text;
            };
            grid.Resize += (s, e) => SizeColumns(grid);
            SizeColumns(grid);
            return grid;
        }

        private static DataGridViewTextBoxColumn NumberColumn() => new DataGridViewTextBoxColumn
        {
            Width = 52, SortMode = DataGridViewColumnSortMode.NotSortable,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleRight, Padding = new Padding(0, 0, 8, 0) }
        };

        private static DataGridViewTextBoxColumn TextColumn() => new DataGridViewTextBoxColumn
        {
            SortMode = DataGridViewColumnSortMode.NotSortable, Width = 400
        };

        private static void SizeColumns(DataGridView grid)
        {
            int text = Math.Max(200, (grid.ClientSize.Width - 2 * 52 - SystemInformation.VerticalScrollBarWidth) / 2);
            grid.Columns[1].Width = text;
            grid.Columns[3].Width = text;
        }

        private string Summary()
        {
            int changed = _rows.Count(r => r.Kind == DiffKind.Changed);
            int removed = _rows.Count(r => r.Kind == DiffKind.Removed);
            int added   = _rows.Count(r => r.Kind == DiffKind.Added);
            if (changed + removed + added == 0)
                return _result.TextDiffers
                    ? "The lines match once whitespace and case are set aside as the options say; the difference is in spacing or line breaks."
                    : "No line differs.";
            return $"{changed} changed, {removed} only on the left, {added} only on the right";
        }

        /// <summary>Moves to the start of the next (or previous) block of differing lines.</summary>
        private void Jump(int direction)
        {
            if (_rows.Count == 0) return;
            int current = _grid.CurrentCell?.RowIndex ?? -1;

            bool IsDiff(int i) => i >= 0 && i < _rows.Count && _rows[i].Kind != DiffKind.Same;

            int i = current + direction;
            // Step off the block we are in, then find the next one's first line.
            while (IsDiff(i) && IsDiff(i - direction)) i += direction;
            while (i >= 0 && i < _rows.Count && !IsDiff(i)) i += direction;
            if (i < 0 || i >= _rows.Count) return;
            while (direction < 0 && IsDiff(i - 1)) i--;

            _grid.CurrentCell = _grid.Rows[i].Cells[1];
            _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, i - 5);
        }

        private void OpenWinMerge()
        {
            try { WinMerge.Open(_result, _leftLabel, _rightLabel); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
