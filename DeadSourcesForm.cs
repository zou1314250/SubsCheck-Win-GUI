using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace subs_check.win.gui
{
    /// <summary>
    /// 失效订阅源列表窗口：查看被自动剔除的地址，并一键恢复到订阅列表。
    /// </summary>
    public class DeadSourcesForm : Form
    {
        private readonly SubscriptionHealthStore _store;
        private ListView listView;
        private Button restoreSelectedButton;
        private Button restoreAllButton;
        private Button removeButton;
        private Button closeButton;
        private Label infoLabel;

        /// <summary>用户选择要恢复的地址，供调用方回填订阅框。</summary>
        public List<string> RestoredUrls { get; private set; }

        public DeadSourcesForm(SubscriptionHealthStore store)
        {
            _store = store ?? SubscriptionHealthStore.Load();
            RestoredUrls = new List<string>();
            InitializeUi();
            ReloadList();
        }

        private void InitializeUi()
        {
            this.Text = "失效订阅源";
            this.ClientSize = new Size(720, 440);
            this.MinimumSize = new Size(560, 320);
            this.StartPosition = FormStartPosition.CenterParent;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            infoLabel = new Label();
            infoLabel.Location = new Point(12, 10);
            infoLabel.Size = new Size(696, 32);
            infoLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            infoLabel.Text = "以下订阅源已被自动判定为失效并剔除。选中后可恢复到订阅列表。";
            this.Controls.Add(infoLabel);

            listView = new ListView();
            listView.Location = new Point(12, 46);
            listView.Size = new Size(696, 340);
            listView.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            listView.View = View.Details;
            listView.FullRowSelect = true;
            listView.GridLines = true;
            listView.MultiSelect = true;
            listView.HideSelection = false;
            listView.Columns.Add("订阅地址", 360);
            listView.Columns.Add("剔除原因", 200);
            listView.Columns.Add("剔除时间", 130);
            listView.DoubleClick += (s, e) => RestoreSelected();
            this.Controls.Add(listView);

            restoreSelectedButton = new Button();
            restoreSelectedButton.Text = "恢复选中";
            restoreSelectedButton.Location = new Point(12, 398);
            restoreSelectedButton.Size = new Size(90, 26);
            restoreSelectedButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            restoreSelectedButton.Click += (s, e) => RestoreSelected();
            this.Controls.Add(restoreSelectedButton);

            restoreAllButton = new Button();
            restoreAllButton.Text = "全部恢复";
            restoreAllButton.Location = new Point(110, 398);
            restoreAllButton.Size = new Size(90, 26);
            restoreAllButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            restoreAllButton.Click += (s, e) => RestoreAll();
            this.Controls.Add(restoreAllButton);

            removeButton = new Button();
            removeButton.Text = "永久删除";
            removeButton.Location = new Point(208, 398);
            removeButton.Size = new Size(90, 26);
            removeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            removeButton.Click += (s, e) => DeleteSelected();
            this.Controls.Add(removeButton);

            closeButton = new Button();
            closeButton.Text = "关闭";
            closeButton.Location = new Point(618, 398);
            closeButton.Size = new Size(90, 26);
            closeButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            closeButton.Click += (s, e) => this.Close();
            this.Controls.Add(closeButton);

            this.CancelButton = closeButton;
        }

        private void ReloadList()
        {
            listView.BeginUpdate();
            listView.Items.Clear();

            foreach (var r in _store.Removed.OrderByDescending(x => x.RemovedAt))
            {
                var item = new ListViewItem(r.Url);
                item.SubItems.Add(r.Reason ?? "");
                item.SubItems.Add(r.RemovedAt.ToString("yyyy-MM-dd HH:mm"));
                item.Tag = r;
                listView.Items.Add(item);
            }

            listView.EndUpdate();

            int n = _store.Removed.Count;
            infoLabel.Text = n == 0
                ? "当前没有失效订阅源记录。"
                : $"共 {n} 个失效订阅源。选中后可恢复到订阅列表；「永久删除」会清除记录。";

            bool has = n > 0;
            restoreSelectedButton.Enabled = has;
            restoreAllButton.Enabled = has;
            removeButton.Enabled = has;
        }

        private List<RemovedRecord> SelectedRecords()
        {
            var list = new List<RemovedRecord>();
            foreach (ListViewItem item in listView.SelectedItems)
            {
                var r = item.Tag as RemovedRecord;
                if (r != null) list.Add(r);
            }
            return list;
        }

        private void RestoreSelected()
        {
            var sel = SelectedRecords();
            if (sel.Count == 0)
            {
                MessageBox.Show("请先选中要恢复的订阅地址。", "失效源",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Restore(sel);
        }

        private void RestoreAll()
        {
            if (_store.Removed.Count == 0) return;
            Restore(new List<RemovedRecord>(_store.Removed));
        }

        private void Restore(List<RemovedRecord> records)
        {
            var confirm = MessageBox.Show(
                $"确定恢复这 {records.Count} 个订阅地址？\r\n\r\n它们会重新加入订阅列表，并在下次测速时重新接受检查。",
                "恢复订阅源", MessageBoxButtons.YesNo, MessageBoxIcon.Question);

            if (confirm != DialogResult.Yes) return;

            foreach (var r in records)
            {
                RemovedRecord taken;
                if (_store.TakeRemoved(r.Url, out taken))
                    RestoredUrls.Add(r.Url);
            }

            _store.Save();
            ReloadList();
        }

        private void DeleteSelected()
        {
            var sel = SelectedRecords();
            if (sel.Count == 0)
            {
                MessageBox.Show("请先选中要删除的记录。", "失效源",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var confirm = MessageBox.Show(
                $"确定永久删除这 {sel.Count} 条记录？\r\n\r\n删除后将不再出现在失效列表中（不影响订阅框）。",
                "永久删除", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (confirm != DialogResult.Yes) return;

            foreach (var r in sel)
            {
                var found = _store.Removed.FirstOrDefault(x =>
                    string.Equals(x.Url, r.Url, StringComparison.OrdinalIgnoreCase));
                if (found != null)
                {
                    _store.Removed.Remove(found);
                    _store.Records.Remove(found.Url);
                }
            }

            _store.Save();
            ReloadList();
        }
    }
}
