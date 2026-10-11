using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace subs_check.win.gui
{
    /// <summary>一个「本轮异常但暂不剔除」的订阅源。</summary>
    public class PendingSource
    {
        public string Url;
        public string Note;
    }

    /// <summary>一次去失效处理的结果，用于汇报日志。</summary>
    public class DeadSourceSweepResult
    {
        public int Checked;
        public List<RemovedRecord> Removed = new List<RemovedRecord>();
        public List<string> Recovered = new List<string>();          // 本轮成功、失败计数被清零的地址
        public List<PendingSource> Pending = new List<PendingSource>(); // 异常但还没达到剔除阈值
        public List<string> Unmatched = new List<string>();           // 日志里出现但不在订阅框中的地址

        public bool HasChanges { get { return Removed.Count > 0; } }
    }

    /// <summary>
    /// 「自动去失效网站」核心逻辑：把内核日志里的订阅失败映射回用户订阅列表，
    /// 按确定性/网络类分别计数，剔除失效源并备份以便恢复。
    /// </summary>
    public class DeadSourceSweeper
    {
        private readonly SubscriptionHealthStore _store;

        public DeadSourceSweeper(SubscriptionHealthStore store)
        {
            _store = store ?? SubscriptionHealthStore.Load();
        }

        public SubscriptionHealthStore Store { get { return _store; } }

        /// <summary>
        /// 处理一轮测速的日志内容。
        /// </summary>
        /// <param name="currentUrls">订阅框中当前的所有地址（用户原始输入）。</param>
        /// <param name="logLines">本轮内核输出的日志行。</param>
        /// <param name="now">判定时间。</param>
        /// <param name="allowRemove">
        /// 是否允许真正剔除。为 false 时只更新健康状态（用于开关关闭时仍保留统计），
        /// 不写入失效列表，避免出现「没被剔除却出现在失效列表」的假记录。
        /// </param>
        public DeadSourceSweepResult Sweep(IEnumerable<string> currentUrls, IEnumerable<string> logLines,
            DateTime now, bool allowRemove = true)
        {
            var result = new DeadSourceSweepResult();

            var urls = (currentUrls ?? Enumerable.Empty<string>())
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Select(u => u.Trim())
                .ToList();

            result.Checked = urls.Count;

            // 解析本轮日志中的失败
            var failures = SubscriptionFailureParser.ParseAll(logLines);

            // 把日志中的 URL 映射回订阅列表中的原始地址
            var matched = new Dictionary<string, SubscriptionFailure>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in failures)
            {
                string original = MatchToOriginal(f.Url, urls);
                if (original == null)
                {
                    result.Unmatched.Add(f.Url);
                    continue;
                }

                SubscriptionFailure exist;
                if (!matched.TryGetValue(original, out exist) || (exist.IsDefinitive && !f.IsDefinitive))
                    matched[original] = f;
            }

            // 逐个订阅地址更新状态
            foreach (var url in urls)
            {
                SubscriptionFailure f;
                if (matched.TryGetValue(url, out f))
                {
                    var rec = _store.MarkFailure(url, f.Error, now);

                    if (SubscriptionFailureParser.ShouldRemove(f, rec))
                    {
                        string reason = SubscriptionFailureParser.DescribeReason(f, rec);

                        if (allowRemove)
                        {
                            _store.AddRemoved(url, reason, now);
                            result.Removed.Add(new RemovedRecord { Url = url, Reason = reason, RemovedAt = now });
                        }
                        else
                        {
                            // 未开启自动剔除：仅登记为「待处理」，供界面提示
                            result.Pending.Add(new PendingSource { Url = url, Note = "已判定失效，未开启自动剔除" });
                        }
                    }
                    else
                    {
                        int left = SubscriptionFailureParser.RequiredConsecutiveFailures - rec.ConsecutiveFailures;
                        result.Pending.Add(new PendingSource { Url = url, Note = $"再失败 {left} 轮即剔除" });
                    }
                }
                else
                {
                    // 本轮没报错 = 拉取成功，清零连续失败计数
                    var existing = _store.GetOrCreate(url);
                    if (existing != null && existing.ConsecutiveFailures > 0)
                        result.Recovered.Add(url);

                    _store.MarkSuccess(url, now);
                }
            }

            // 清理不再跟踪的记录
            _store.PruneRecords(urls.Where(u => !result.Removed.Any(r => string.Equals(r.Url, u, StringComparison.OrdinalIgnoreCase))));

            _store.Save();

            return result;
        }

        /// <summary>
        /// 把内核日志里的地址对应回用户订阅列表中的原始地址。
        /// 内核会用 utils.WarpUrl 规范化地址，GUI 也可能把 GitHub raw 链接改写成代理链接，
        /// 因此这里做多级容错匹配。
        /// </summary>
        public static string MatchToOriginal(string loggedUrl, IList<string> urls)
        {
            if (string.IsNullOrWhiteSpace(loggedUrl) || urls == null || urls.Count == 0)
                return null;

            loggedUrl = loggedUrl.Trim();

            // 1) 完全相等
            foreach (var u in urls)
                if (string.Equals(u, loggedUrl, StringComparison.OrdinalIgnoreCase))
                    return u;

            // 2) 忽略大小写与首尾斜杠
            string normLogged = NormalizeForCompare(loggedUrl);
            foreach (var u in urls)
                if (string.Equals(NormalizeForCompare(u), normLogged, StringComparison.OrdinalIgnoreCase))
                    return u;

            // 3) 包一层 GitHub 代理的情形：日志里是原始 raw 链接，订阅框里是代理前缀 + 原链接
            foreach (var u in urls)
            {
                if (u.IndexOf(loggedUrl, StringComparison.OrdinalIgnoreCase) >= 0)
                    return u;
                if (loggedUrl.IndexOf(u, StringComparison.OrdinalIgnoreCase) >= 0)
                    return u;
            }

            // 4) 去掉 /om/ 之类修复痕迹后再比一次
            string stripped = loggedUrl.Replace("/om/", "/");
            foreach (var u in urls)
            {
                string us = u.Replace("/om/", "/");
                if (string.Equals(NormalizeForCompare(us), NormalizeForCompare(stripped), StringComparison.OrdinalIgnoreCase))
                    return u;
                if (us.IndexOf(stripped, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    stripped.IndexOf(us, StringComparison.OrdinalIgnoreCase) >= 0)
                    return u;
            }

            return null;
        }

        private static string NormalizeForCompare(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            return url.Trim().TrimEnd('/');
        }
    }
}
