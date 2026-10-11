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
        ///
        /// 关键点：内核 utils.WarpUrl 会做 `githubProxy + url`，而日志里的 URL 也不一定是
        /// 订阅框里的原文。实测（真实日志 + 真实 config）存在这些形态：
        ///   订阅框原始   : https://raw.githubusercontent.com/O/R/main/x.txt
        ///   内核日志(包装): https://gh.tryxd.cn/raw.githubusercontent.com/O/R/main/x.txt
        ///   镜像变体     : https://cdn.jsdelivr.net/gh/O/R@main/x.txt
        /// 注意包装后**没有第二个 https://**，所以不能只靠简单子串包含来匹配。
        /// 这里改用「归一化主体路径」比较：剥掉协议、代理前缀、镜像域名差异，只比 O/R/path。
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

            // 2) 归一化后相等
            string keyLogged = NormalizeToRepoPath(loggedUrl);
            if (keyLogged != null)
            {
                foreach (var u in urls)
                {
                    if (string.Equals(NormalizeToRepoPath(u), keyLogged, StringComparison.OrdinalIgnoreCase))
                        return u;
                }
            }

            // 3) 一方包含另一方（忽略大小写与尾部斜杠）
            string normLogged = NormalizeForCompare(loggedUrl);
            foreach (var u in urls)
            {
                string nu = NormalizeForCompare(u);
                if (nu.Length == 0 || normLogged.Length == 0) continue;
                if (nu.IndexOf(normLogged, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    normLogged.IndexOf(nu, StringComparison.OrdinalIgnoreCase) >= 0)
                    return u;
            }

            // 4) 去掉 /om/ 之类修复痕迹后再比一次
            string stripped = loggedUrl.Replace("/om/", "/");
            foreach (var u in urls)
            {
                string us = u.Replace("/om/", "/");
                if (string.Equals(NormalizeForCompare(us), NormalizeForCompare(stripped), StringComparison.OrdinalIgnoreCase))
                    return u;
                string kUs = NormalizeToRepoPath(us);
                if (kUs != null && string.Equals(kUs, NormalizeToRepoPath(stripped), StringComparison.OrdinalIgnoreCase))
                    return u;
            }

            return null;
        }

        /// <summary>
        /// 抽取「仓库路径」用于跨代理/镜像比对：把各种写法统一成 "owner/repo/路径"。
        /// 处理 raw.githubusercontent.com、cdn.jsdelivr.net/gh、以及任意 GitHub 代理前缀。
        /// 无法识别时返回 null。
        /// </summary>
        private static string NormalizeToRepoPath(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            string s = url.Trim();

            // 去掉协议
            int scheme = s.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) s = s.Substring(scheme + 3);

            // 去掉查询与锚点
            int cut = s.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) s = s.Substring(0, cut);

            // raw.githubusercontent.com/<owner>/<repo>/<ref>/<path>  -> owner/repo/path
            const string rawHost = "raw.githubusercontent.com/";
            int rawIdx = s.IndexOf(rawHost, StringComparison.OrdinalIgnoreCase);
            if (rawIdx >= 0)
            {
                string rest = s.Substring(rawIdx + rawHost.Length);
                var parts = rest.Split('/');
                if (parts.Length >= 4)
                {
                    // 剔除 ref 段，使 raw 写法与 jsDelivr(@ref) 写法归一化到同一形态：
                    //   owner/repo/refs/heads/main/path   -> owner/repo/path
                    //   owner/repo/refs/tags/v1/path      -> owner/repo/path
                    //   owner/repo/main/path              -> owner/repo/path
                    int pathStart;
                    if (parts.Length >= 6 && parts[2] == "refs" &&
                        (parts[3] == "heads" || parts[3] == "tags"))
                        pathStart = 5;          // refs/heads/<branch>/path
                    else if (parts.Length >= 5 && parts[2] == "refs")
                        pathStart = 4;          // refs/<ref>/path
                    else
                        pathStart = 3;          // <branch>/path

                    if (pathStart < parts.Length)
                        return parts[0] + "/" + parts[1] + "/" + string.Join("/", parts, pathStart, parts.Length - pathStart);
                }
                return rest;
            }

            // cdn.jsdelivr.net/gh/<owner>/<repo>@<ref>/<path>  -> owner/repo/path
            const string jsdHost = "cdn.jsdelivr.net/gh/";
            int jsdIdx = s.IndexOf(jsdHost, StringComparison.OrdinalIgnoreCase);
            if (jsdIdx >= 0)
            {
                string rest = s.Substring(jsdIdx + jsdHost.Length);
                int at = rest.IndexOf('@');
                if (at >= 0)
                {
                    string repoPart = rest.Substring(0, at);          // owner/repo
                    int slash = rest.IndexOf('/', at);
                    string pathPart = slash >= 0 ? rest.Substring(slash + 1) : "";
                    return repoPart + "/" + pathPart;
                }
                return rest;
            }

            // github.com/<owner>/<repo>/blob|raw/<ref>/<path> -> owner/repo/path
            const string ghHost = "github.com/";
            int ghIdx = s.IndexOf(ghHost, StringComparison.OrdinalIgnoreCase);
            if (ghIdx >= 0)
            {
                string rest = s.Substring(ghIdx + ghHost.Length);
                var parts = rest.Split('/');
                if (parts.Length >= 5 && (parts[2] == "blob" || parts[2] == "raw" || parts[2] == "tree"))
                    return parts[0] + "/" + parts[1] + "/" + string.Join("/", parts, 4, parts.Length - 4);
            }

            // 其它（含代理前缀但路径里带 raw.githubusercontent.com 的情形）
            int anyRaw = s.IndexOf(rawHost, StringComparison.OrdinalIgnoreCase);
            if (anyRaw >= 0) return NormalizeToRepoPath("https://" + s.Substring(anyRaw));

            return null;
        }

        private static string NormalizeForCompare(string url)
        {
            if (string.IsNullOrEmpty(url)) return string.Empty;
            return url.Trim().TrimEnd('/');
        }
    }
}
