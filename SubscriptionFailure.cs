using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace subs_check.win.gui
{
    /// <summary>内核报告的一种订阅失败。</summary>
    public class SubscriptionFailure
    {
        /// <summary>内核日志中的 url= 字段。</summary>
        public string Url { get; set; }
        /// <summary>原始日志行。</summary>
        public string RawLine { get; set; }
        /// <summary>错误正文（err= 字段）。</summary>
        public string Error { get; set; }
        /// <summary>是否为确定性失效（内容型错误，重试无意义）。</summary>
        public bool IsDefinitive { get; set; }
    }

    /// <summary>
    /// 解析 subs-check 内核日志中的订阅失败信息，并决定是否剔除某个订阅源。
    ///
    /// 内核（beck-8/subs-check v1.6.x, proxy/get.go）会输出：
    ///   ERR 获取订阅链接错误跳过 source=本地配置 url=&lt;地址&gt; err="&lt;原因&gt;"
    ///   ERR 订阅链接没有proxies source=本地配置 url=&lt;地址&gt;
    ///   ERR 解析proxy错误 source=本地配置 url=&lt;地址&gt; err="&lt;原因&gt;"
    /// </summary>
    public static class SubscriptionFailureParser
    {
        /// <summary>需要匹配的内核日志标记。</summary>
        private static readonly string[] FailureMarkers =
        {
            "获取订阅链接错误跳过",
            "订阅链接没有proxies",
            "解析proxy错误",
            "获取远程订阅清单失败"
        };

        // slog / tint 的 url 字段：url=https://... 一直到下一个 " key=" 之前
        private static readonly Regex UrlFieldRegex = new Regex(
            @"\burl=(?<v>"".*?""|\S+)", RegexOptions.Compiled);

        // err 字段：err="..." （内含空格与转义引号）或 err=token
        private static readonly Regex ErrFieldRegex = new Regex(
            @"\berr=(?<v>"".*?(?<!\\)""|\S+)", RegexOptions.Compiled);

        /// <summary>
        /// 判断一行日志是否是订阅失败行。
        /// </summary>
        public static bool IsFailureLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            foreach (var marker in FailureMarkers)
                if (line.IndexOf(marker, StringComparison.Ordinal) >= 0)
                    return true;
            return false;
        }

        /// <summary>
        /// 尝试把一行日志解析为订阅失败；解析不出 url 时返回 null。
        /// </summary>
        public static SubscriptionFailure TryParseLine(string line)
        {
            if (!IsFailureLine(line)) return null;

            string url = ExtractField(line, UrlFieldRegex);
            if (string.IsNullOrWhiteSpace(url)) return null;

            string err = ExtractField(line, ErrFieldRegex);
            bool definitive = IsDefinitiveFailure(line, err);

            return new SubscriptionFailure
            {
                Url = url.Trim(),
                RawLine = line,
                Error = string.IsNullOrWhiteSpace(err) ? StripMarkers(line) : err,
                IsDefinitive = definitive
            };
        }

        /// <summary>
        /// 从整段日志里解析出所有订阅失败（按 url 去重，保留最后一条原因）。
        /// </summary>
        public static List<SubscriptionFailure> ParseAll(IEnumerable<string> lines)
        {
            var byUrl = new Dictionary<string, SubscriptionFailure>(StringComparer.OrdinalIgnoreCase);
            if (lines == null) return new List<SubscriptionFailure>();

            foreach (var line in lines)
            {
                var f = TryParseLine(line);
                if (f == null) continue;
                // 同一个地址多轮出现时，以最后一条为准；只要出现过确定性失效就保持确定性命中
                SubscriptionFailure old;
                if (byUrl.TryGetValue(f.Url, out old) && old.IsDefinitive && !f.IsDefinitive)
                    continue;
                byUrl[f.Url] = f;
            }

            return byUrl.Values.ToList();
        }

        /// <summary>
        /// 判定是否为「确定性失效」：网站真的没了/内容不可用，重试无意义。
        ///
        /// 确定性（立即剔除）：
        ///   返回状态码: 404 / 410 / 401 / 403 / 400   —— 服务端明确答复
        ///   订阅链接没有proxies / 解析proxy错误      —— 拉到了但内容不可解析
        ///   format invalid / 无法解析                 —— 内容格式错误，重试不会变好
        ///
        /// 网络类（需连续 2 轮才剔除，避免误杀）：
        ///   context deadline exceeded / timeout
        ///   connection refused / reset / EOF
        ///   no such host / DNS 解析失败
        ///   TLS 握手失败 等
        /// </summary>
        public static bool IsDefinitiveFailure(string line, string err)
        {
            // 内容型失效：不依赖 err 字段
            if (!string.IsNullOrEmpty(line))
            {
                if (line.IndexOf("没有proxies", StringComparison.OrdinalIgnoreCase) >= 0) return true;
                if (line.IndexOf("解析proxy错误", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }

            if (string.IsNullOrWhiteSpace(err)) return false;

            string e = err.ToLowerInvariant();

            // 内容格式错误：拉取成功但解析不出节点，重试多少次都一样
            // 实测最常见（代理被套错时会命中全部订阅）：convert v2ray subscribe error: format invalid
            if (e.IndexOf("format invalid", StringComparison.Ordinal) >= 0) return true;
            if (e.IndexOf("无法解析", StringComparison.Ordinal) >= 0) return true;
            if (e.IndexOf("invalid format", StringComparison.Ordinal) >= 0) return true;

            // 明确的 HTTP 状态码
            // 404/410 是「没了」；401/403/400 说明服务端拒绝该请求，同样重试无意义
            if (Regex.IsMatch(e, @"返回状态码:\s*(400|401|403|404|410)\b")) return true;

            // 网络类关键词：只要命中，就视为非确定性
            string[] transientMarkers =
            {
                "deadline exceeded", "timeout", "timed out",
                "connection refused", "connection reset", "connection closed",
                "eof", "broken pipe", "no route to host",
                "no such host", "dns", "lookup ",
                "tls", "handshake", "certificate",
                "temporary failure", "network is unreachable",
                "context canceled", "context cancellation"
            };

            foreach (var marker in transientMarkers)
                if (e.IndexOf(marker, StringComparison.Ordinal) >= 0)
                    return false;

            // 其余带状态码的情况（如 5xx）视为服务端暂时故障，允许重试
            if (Regex.IsMatch(e, @"返回状态码:\s*(\d{3})")) return false;

            // 无法归类时不自动剔除，保守处理
            return false;
        }

        /// <summary>连续失败达到该轮数即剔除（确定性失效只需 1 轮）。</summary>
        public const int RequiredConsecutiveFailures = 2;

        /// <summary>
        /// 根据失败信息与历史记录判断是否应当剔除。
        /// </summary>
        public static bool ShouldRemove(SubscriptionFailure failure, SubscriptionRecord record)
        {
            if (failure == null) return false;

            // 确定性失效：立即剔除
            if (failure.IsDefinitive) return true;

            // 网络类：连续 2 轮才剔除
            int consecutive = record == null ? 0 : record.ConsecutiveFailures;
            return consecutive >= RequiredConsecutiveFailures;
        }

        /// <summary>剔除原因的可读描述。</summary>
        public static string DescribeReason(SubscriptionFailure failure, SubscriptionRecord record)
        {
            if (failure == null) return "未知";

            if (failure.IsDefinitive)
                return string.IsNullOrWhiteSpace(failure.Error) ? "确定性失效" : failure.Error;

            int consecutive = record == null ? 0 : record.ConsecutiveFailures;
            string detail = string.IsNullOrWhiteSpace(failure.Error) ? "网络错误" : failure.Error;
            return $"连续 {consecutive} 轮网络失败: {detail}";
        }

        /// <summary>把日志行压成简短原因，便于写进失效列表。</summary>
        private static string StripMarkers(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return "未知";

            string s = line.Trim();

            // 去掉时间戳与前缀级别（tint/slog 形如：2026-01-01 00:00:00 ERR ...）
            var m = Regex.Match(s, @"^(?:\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?\s+)?(?:DBG|INF|WRN|ERR|DEB|INFO|WARN|ERROR)?\s*");
            if (m.Success && m.Length > 0) s = s.Substring(m.Length);

            // 截掉字段部分，只保留人类可读的说明
            int idx = s.IndexOf(" url=", StringComparison.Ordinal);
            if (idx > 0) s = s.Substring(0, idx);

            s = s.Replace("source=本地配置", "").Replace("source=", "").Trim();

            return s.Length > 120 ? s.Substring(0, 120) + "..." : s;
        }

        /// <summary>取出 key=value，自动去掉包裹引号与转义。</summary>
        private static string ExtractField(string line, Regex regex)
        {
            if (string.IsNullOrEmpty(line)) return null;

            var m = regex.Match(line);
            if (!m.Success) return null;

            string v = m.Groups["v"].Value;
            if (string.IsNullOrEmpty(v)) return null;

            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                v = v.Substring(1, v.Length - 2);

            v = v.Replace("\\\"", "\"").Replace("\\\\", "\\");
            return v.Trim();
        }
    }
}
