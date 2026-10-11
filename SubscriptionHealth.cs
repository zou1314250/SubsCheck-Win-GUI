using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace subs_check.win.gui
{
    /// <summary>
    /// 单个订阅源的健康状态记录。
    /// </summary>
    public class SubscriptionRecord
    {
        /// <summary>原始订阅地址（未经 GitHub 代理改写的用户原始输入）。</summary>
        public string Url { get; set; }

        /// <summary>连续失败轮数。成功一轮即清零。</summary>
        public int ConsecutiveFailures { get; set; }

        /// <summary>最近一次失败原因（内核日志中的 err 字段）。</summary>
        public string LastError { get; set; }

        /// <summary>最近一次判定时间。</summary>
        public DateTime LastChecked { get; set; }

        /// <summary>累计失败轮数（仅统计用）。</summary>
        public int TotalFailures { get; set; }

        /// <summary>累计成功轮数（仅统计用）。</summary>
        public int TotalSuccess { get; set; }
    }

    /// <summary>
    /// 已被自动剔除的订阅源，保留用于一键恢复。
    /// </summary>
    public class RemovedRecord
    {
        public string Url { get; set; }
        public string Reason { get; set; }
        public DateTime RemovedAt { get; set; }
    }

    /// <summary>
    /// 订阅源健康状态的持久化容器。
    /// 存放于可执行文件目录的 config/subscription-health.json。
    /// </summary>
    public class SubscriptionHealthStore
    {
        public Dictionary<string, SubscriptionRecord> Records { get; set; }
        public List<RemovedRecord> Removed { get; set; }

        public SubscriptionHealthStore()
        {
            Records = new Dictionary<string, SubscriptionRecord>(StringComparer.OrdinalIgnoreCase);
            Removed = new List<RemovedRecord>();
        }

        [JsonIgnore]
        public string FilePath { get; private set; }

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateTimeZoneHandling = DateTimeZoneHandling.Local
        };

        /// <summary>默认存储路径：&lt;exe目录&gt;\config\subscription-health.json。</summary>
        public static string GetDefaultPath()
        {
            string exeDir = Path.GetDirectoryName(System.Windows.Forms.Application.ExecutablePath);
            return Path.Combine(exeDir, "config", "subscription-health.json");
        }

        /// <summary>
        /// 从磁盘加载；文件不存在或损坏时返回空的实例（不会抛异常，避免影响主流程）。
        /// </summary>
        public static SubscriptionHealthStore Load(string path = null)
        {
            path = path ?? GetDefaultPath();
            try
            {
                if (File.Exists(path))
                {
                    string json = File.ReadAllText(path, Encoding.UTF8);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        var loaded = JsonConvert.DeserializeObject<SubscriptionHealthStore>(json, JsonSettings);
                        if (loaded != null)
                        {
                            if (loaded.Records == null)
                                loaded.Records = new Dictionary<string, SubscriptionRecord>(StringComparer.OrdinalIgnoreCase);
                            if (loaded.Removed == null)
                                loaded.Removed = new List<RemovedRecord>();
                            loaded.FilePath = path;
                            return loaded;
                        }
                    }
                }
            }
            catch
            {
                // 配置损坏时退回空状态，绝不因为读健康记录失败而中断测速
            }

            var store = new SubscriptionHealthStore();
            store.FilePath = path;
            return store;
        }

        /// <summary>保存到磁盘；目录不存在时自动创建。</summary>
        public void Save(string path = null)
        {
            path = path ?? FilePath ?? GetDefaultPath();
            FilePath = path;

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonConvert.SerializeObject(this, JsonSettings), Encoding.UTF8);
        }

        /// <summary>取得（或创建）某个订阅地址的记录。</summary>
        public SubscriptionRecord GetOrCreate(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();

            SubscriptionRecord rec;
            if (!Records.TryGetValue(url, out rec))
            {
                rec = new SubscriptionRecord { Url = url };
                Records[url] = rec;
            }
            return rec;
        }

        /// <summary>标记某个订阅地址本轮失败。</summary>
        public SubscriptionRecord MarkFailure(string url, string error, DateTime now)
        {
            var rec = GetOrCreate(url);
            if (rec == null) return null;

            rec.ConsecutiveFailures++;
            rec.TotalFailures++;
            rec.LastError = error;
            rec.LastChecked = now;
            return rec;
        }

        /// <summary>标记某个订阅地址本轮成功，清零连续失败计数。</summary>
        public void MarkSuccess(string url, DateTime now)
        {
            var rec = GetOrCreate(url);
            if (rec == null) return;

            rec.ConsecutiveFailures = 0;
            rec.LastError = null;
            rec.LastChecked = now;
            rec.TotalSuccess++;
        }

        /// <summary>把订阅地址移入失效列表（去重，保留最早剔除时间）。</summary>
        public void AddRemoved(string url, string reason, DateTime now)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            url = url.Trim();

            if (Removed.Any(r => string.Equals(r.Url, url, StringComparison.OrdinalIgnoreCase)))
                return;

            Removed.Add(new RemovedRecord { Url = url, Reason = reason, RemovedAt = now });
        }

        /// <summary>从失效列表取回一个地址（恢复用），返回是否取到。</summary>
        public bool TakeRemoved(string url, out RemovedRecord removed)
        {
            removed = null;
            if (string.IsNullOrWhiteSpace(url)) return false;

            removed = Removed.FirstOrDefault(r => string.Equals(r.Url, url.Trim(), StringComparison.OrdinalIgnoreCase));
            if (removed == null) return false;

            Removed.Remove(removed);
            Records.Remove(removed.Url);
            return true;
        }

        /// <summary>
        /// 清理不再被跟踪的记录：既不在当前订阅列表中，也不在失效列表中。
        /// 返回被清理的数量。
        /// </summary>
        public int PruneRecords(IEnumerable<string> currentUrls)
        {
            var keep = new HashSet<string>(
                (currentUrls ?? Enumerable.Empty<string>()).Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()),
                StringComparer.OrdinalIgnoreCase);

            foreach (var r in Removed)
                if (!string.IsNullOrWhiteSpace(r.Url)) keep.Add(r.Url.Trim());

            var stale = Records.Keys.Where(k => !keep.Contains(k)).ToList();
            foreach (var k in stale)
                Records.Remove(k);
            return stale.Count;
        }
    }
}
