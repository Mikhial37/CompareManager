using CompareManager.Classes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CompareManager
{
    /// <summary>
    /// The duplicate scripts in one folder: each object that more than one script defines,
    /// and each data script saved more than once, with its copies listed underneath.
    ///
    /// A copy can be compared with the newest (or two copies with each other) in the diff
    /// viewer, opened, or shown in Explorer — deciding which to keep is left to the person.
    /// Nothing here moves or deletes a file.
    /// </summary>
    public sealed class DuplicatesForm : Form
    {
        private readonly string               _folder;
        private readonly List<DuplicateGroup> _groups;
        private readonly CompareOptions       _options;

        private readonly ListView _groupList = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
            MultiSelect = false, BorderStyle = BorderStyle.FixedSingle, BackColor = Ui.Card
        };

        private readonly ListView _copyList = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Ui.Card, ShowItemToolTips = true
        };

        private readonly Button _compareNewest = Ui.Secondary("Compare with newest");
        private readonly Button _compareTwo    = Ui.Secondary("Compare the two selected");
        private readonly Button _open          = Ui.Secondary("Open");
        private readonly Button _reveal        = Ui.Secondary("Show in folder");

        public DuplicatesForm(string folder, List<DuplicateGroup> groups, CompareOptions options)
        {
            _folder  = folder;
            _groups  = groups;
            _options = options;

            Text          = "Duplicate scripts — Compare Manager";
            Icon          = Ui.AppIcon();
            Font          = Ui.Body;
            BackColor     = Ui.Background;
            StartPosition = FormStartPosition.CenterParent;
            Size          = new Size(1100, 720);
            MinimumSize   = new Size(760, 480);

            var title = Ui.Label("Duplicate scripts", Ui.Title);
            var where = Ui.Label(folder, Ui.Body, Ui.Muted);
            var summary = Ui.Label(Summary(), Ui.Body, groups.Any(g => !g.AllIdentical) ? Ui.DifferentFore : Ui.Text);
            summary.Margin = new Padding(3, 8, 3, 0);

            var header = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown,
                WrapContents = false, Padding = new Padding(16, 12, 16, 10)
            };
            header.Controls.AddRange(new Control[] { title, where, summary });

            _groupList.Columns.Add("Object or script", 360);
            _groupList.Columns.Add("Type", 130);
            _groupList.Columns.Add("Copies", 70);
            _groupList.Columns.Add("Versions", 160);
            _groupList.Columns.Add("Newest copy", 300);
            _groupList.SelectedIndexChanged += (s, e) => FillCopies();

            _copyList.Columns.Add("Script", 520);
            _copyList.Columns.Add("Changed", 150);
            _copyList.Columns.Add("Size", 80, HorizontalAlignment.Right);
            _copyList.Columns.Add("Against the newest", 200);
            _copyList.SelectedIndexChanged += (s, e) => UpdateButtons();
            _copyList.DoubleClick += (s, e) => CompareWithNewest();

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, BackColor = Ui.Background };
            split.Panel1.Padding = new Padding(16, 0, 16, 6);
            split.Panel2.Padding = new Padding(16, 6, 16, 0);
            split.Panel1.Controls.Add(_groupList);
            split.Panel1.Controls.Add(Caption("Each object defined by more than one script, and each script saved more than once"));
            split.Panel2.Controls.Add(_copyList);
            split.Panel2.Controls.Add(Caption("Its copies — newest first; that is the one a compare uses"));
            Shown += (s, e) => split.SplitterDistance = (int)(split.Height * 0.45);

            _compareNewest.Click += (s, e) => CompareWithNewest();
            _compareTwo.Click    += (s, e) => CompareTwo();
            _open.Click          += (s, e) => OpenSelected();
            _reveal.Click        += (s, e) => RevealSelected();

            var close = Ui.Secondary("Close");
            close.DialogResult = DialogResult.Cancel;
            CancelButton = close;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(16, 8, 16, 12) };
            buttons.Controls.AddRange(new Control[] { _compareNewest, _compareTwo, _open, _reveal, close });

            Controls.Add(split);
            Controls.Add(header);
            Controls.Add(buttons);

            FillGroups();
        }

        private static Label Caption(string text)
        {
            var label = Ui.Label(text, Ui.Caption, Ui.Muted);
            label.Dock = DockStyle.Top;
            label.Padding = new Padding(0, 0, 0, 4);
            return label;
        }

        private string Summary()
        {
            if (_groups.Count == 0) return "No duplicates: every object is defined by one script, and no script is saved twice.";

            int objects  = _groups.Count(g => g.Reason == DuplicateReason.SameObject);
            int drifted  = _groups.Count(g => g.Reason == DuplicateReason.SameObject && !g.AllIdentical);
            int sameText = _groups.Count(g => g.Reason == DuplicateReason.SameContent);

            var parts = new List<string>();
            if (objects > 0)
                parts.Add($"{objects} object{(objects == 1 ? " is" : "s are")} defined by more than one script" +
                          (drifted > 0 ? $" — {drifted} with copies that differ" : ", all identical copies"));
            if (sameText > 0)
                parts.Add($"{sameText} other script{(sameText == 1 ? " is" : "s are")} saved more than once under another name");
            return string.Join(";  ", parts) + ".";
        }

        private void FillGroups()
        {
            _groupList.BeginUpdate();
            foreach (var group in _groups)
            {
                var item = new ListViewItem(new[]
                {
                    group.Title,
                    group.Reason == DuplicateReason.SameObject ? group.Newest.KindLabel : "Script (same text)",
                    group.Copies.Count.ToString(),
                    group.VersionsLabel,
                    group.Newest.RelativePath
                })
                { Tag = group };

                if (!group.AllIdentical) { item.BackColor = Ui.DifferentBack; item.ForeColor = Ui.DifferentFore; }
                _groupList.Items.Add(item);
            }
            _groupList.EndUpdate();

            if (_groupList.Items.Count > 0) _groupList.Items[0].Selected = true;
            UpdateButtons();
        }

        private DuplicateGroup SelectedGroup => _groupList.SelectedItems.Count == 0 ? null : (DuplicateGroup)_groupList.SelectedItems[0].Tag;

        private List<SqlObject> SelectedCopies => _copyList.SelectedItems.Cast<ListViewItem>().Select(i => (SqlObject)i.Tag).ToList();

        private void FillCopies()
        {
            _copyList.BeginUpdate();
            _copyList.Items.Clear();

            var group = SelectedGroup;
            if (group != null)
            {
                for (int i = 0; i < group.Copies.Count; i++)
                {
                    var copy = group.Copies[i];
                    string against = i == 0 ? "Newest" : group.SameAsNewest[i] ? "Same as the newest" : "Differs from the newest";

                    var item = new ListViewItem(new[]
                    {
                        copy.RelativePath,
                        copy.Modified?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                        FileSize(copy.Origin),
                        against
                    })
                    { Tag = copy, ToolTipText = copy.Origin };

                    if (i == 0) item.Font = Ui.Bold;
                    else if (!group.SameAsNewest[i]) { item.BackColor = Ui.DifferentBack; item.ForeColor = Ui.DifferentFore; }
                    _copyList.Items.Add(item);
                }
            }

            _copyList.EndUpdate();
            UpdateButtons();
        }

        private static string FileSize(string path)
        {
            try
            {
                long bytes = new FileInfo(path).Length;
                return bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.#} KB";
            }
            catch
            {
                return "";
            }
        }

        private void UpdateButtons()
        {
            var selected = SelectedCopies;
            var group    = SelectedGroup;
            _compareNewest.Enabled = selected.Count == 1 && group != null && selected[0] != group.Newest;
            _compareTwo.Enabled    = selected.Count == 2;
            _open.Enabled          = selected.Count >= 1;
            _reveal.Enabled        = selected.Count == 1;
        }

        // ── Acting on a copy ──────────────────────────────────────────────────

        private void CompareWithNewest()
        {
            var group = SelectedGroup;
            var copy  = SelectedCopies.FirstOrDefault();
            if (group == null || copy == null || copy == group.Newest) return;
            ShowDiff(copy, group.Newest);
        }

        private void CompareTwo()
        {
            var two = SelectedCopies;
            if (two.Count != 2) return;

            // The older on the left, so the right side reads as what it became.
            var ordered = two.OrderBy(c => c.Modified ?? DateTime.MinValue).ToList();
            ShowDiff(ordered[0], ordered[1]);
        }

        private void ShowDiff(SqlObject left, SqlObject right)
        {
            var result = ObjectComparer.Compare(new CatalogEntry { Key = left.Key, Left = left, Right = right }, _options);
            using var form = new DiffForm(result, left.RelativePath, right.RelativePath, _options);
            form.ShowDialog(this);
        }

        private void OpenSelected()
        {
            foreach (var copy in SelectedCopies)
            {
                try { Process.Start(new ProcessStartInfo(copy.Origin) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            }
        }

        private void RevealSelected()
        {
            var copy = SelectedCopies.FirstOrDefault();
            if (copy == null) return;
            try { Process.Start("explorer.exe", $"/select,\"{copy.Origin}\""); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }
    }
}
