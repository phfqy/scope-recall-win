using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ScopeRecallWin.Helpers;

/// <summary>导出助手：CSV（公式注入防护）/ Markdown / JSON。</summary>
public static class Exporter
{
    /// <summary>生成 CSV（复用 CsvExporter 的公式注入防护）。</summary>
    public static string ToCsv(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        var headerCells = new List<string>(headers.Count);
        foreach (var h in headers)
        {
            headerCells.Add(CsvExporter.EscapeCell(h));
        }
        sb.AppendLine(string.Join(",", headerCells));
        foreach (var row in rows)
        {
            var cells = new List<string>(row.Count);
            foreach (var cell in row)
            {
                cells.Add(CsvExporter.EscapeCell(cell));
            }
            sb.AppendLine(string.Join(",", cells));
        }
        return sb.ToString();
    }

    /// <summary>生成 Markdown 表格。</summary>
    public static string ToMarkdown(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| " + string.Join(" | ", headers) + " |");
        var separators = new List<string>(headers.Count);
        for (int i = 0; i < headers.Count; i++)
        {
            separators.Add("---");
        }
        sb.AppendLine("| " + string.Join(" | ", separators) + " |");
        foreach (var row in rows)
        {
            var cells = new List<string>(row.Count);
            foreach (var cell in row)
            {
                cells.Add((cell ?? "").Replace("|", "\\|").Replace("\n", " "));
            }
            sb.AppendLine("| " + string.Join(" | ", cells) + " |");
        }
        return sb.ToString();
    }

    /// <summary>生成 JSON 数组（对象数组）。</summary>
    public static string ToJson(IReadOnlyList<string> keys, IReadOnlyList<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder("[\n");
        for (int r = 0; r < rows.Count; r++)
        {
            sb.Append("  {");
            for (int c = 0; c < keys.Count && c < rows[r].Count; c++)
            {
                if (c > 0)
                {
                    sb.Append(", ");
                }
                sb.Append('"').Append(keys[c]).Append("\": \"")
                  .Append((rows[r][c] ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n"))
                  .Append('"');
            }
            sb.Append('}');
            sb.Append(r < rows.Count - 1 ? ",\n" : "\n");
        }
        sb.Append(']');
        return sb.ToString();
    }

    /// <summary>弹出保存对话框并写入文件（WinUI FileSavePicker）。</summary>
    public static async System.Threading.Tasks.Task<string?> SaveWithPickerAsync(
        string suggestedName, string content, string extension, string typeLabel)
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker();
        picker.SuggestedFileName = suggestedName;
        picker.FileTypeChoices.Add(typeLabel, new List<string> { extension });
        if (App.MainWindow is not null)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        }
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return null;
        }
        await Windows.Storage.FileIO.WriteTextAsync(file, content);
        return file.Path;
    }

    /// <summary>文件大小人读（KB/MB/GB）。</summary>
    public static string HumanSize(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int u = 0;
        while (bytes >= 1024 && u < units.Length - 1)
        {
            bytes /= 1024;
            u++;
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", bytes, units[u]);
    }
}
