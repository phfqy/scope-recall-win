using System.Text;

namespace ScopeRecallWin.Helpers;

/// <summary>
/// CSV 导出：统一转义 + 防公式注入（约束 8）。
/// Excel 会把以 = + - @ 开头、或以制表符/回车开头的单元格当公式执行——导出前必须转义。
/// </summary>
public static class CsvExporter
{
    public static string EscapeCell(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = value;
        // 公式注入防护：前置单引号（Excel 视为文本）
        if (text.Length > 0 && (text[0] is '=' or '+' or '-' or '@'
                                || text[0] == '\t' || text[0] == '\r' || text[0] == '\n'))
        {
            text = "'" + text;
        }

        // 含逗号/引号/换行 → 用引号包裹并转义内部引号
        if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
        {
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return text;
    }

    public static string BuildCsv(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(EscapeCell)));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", row.Select(EscapeCell)));
        }
        return sb.ToString();
    }
}
