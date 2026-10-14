using System.Globalization;

namespace ScopeRecallWin.Helpers;

/// <summary>
/// 时间戳归一：上游库内存在两种格式——"+00:00 微秒"（如 2026-09-03T12:00:00.000000+00:00）与
/// "Z"（如 2026-09-03T12:00:00Z）。排序/比较前必须归一为同一表示，禁止按文本排序（提示词包约束）。
/// </summary>
public static class TimestampNormalizer
{
    public static DateTimeOffset Parse(string raw)
    {
        // 支持 ISO8601 的两种变体；解析失败返回 Unix 纪元并标注（调用方按"未验证"处理）
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }
        return DateTimeOffset.UnixEpoch;
    }

    /// <summary>归一为统一文本（UTC、ISO8601 秒级），供列表排序与展示。</summary>
    public static string Normalize(string raw)
        => Parse(raw).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
