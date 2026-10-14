namespace ScopeRecallWin.Infrastructure;

/// <summary>记忆插件部署路径发现与派生。</summary>
/// <remarks>
/// 目录约定（与本机既有一致）：
///   &lt;root&gt;\shared\            共享记忆库（memory.sqlite3 / installation.json / runtime-config.json / vectors\）
///   &lt;root&gt;\shared-venv\       共享 Python 环境（Scripts\python.exe + site-packages\scope_recall\）
///   &lt;root&gt;\&lt;entry&gt;\            各 Agent 的 entry 家目录（dsh / hermes / workbuddy / workbuddy-ai）
/// 根目录来源优先级：用户设置 &gt; 常见位置探测（E:\ScopeRecall、D:\ScopeRecall、
/// %LOCALAPPDATA%\Programs\ScopeRecallWin\data）。
/// </remarks>
public static class PathDiscovery
{
    public const string SharedDirName = "shared";
    public const string VenvDirName = "shared-venv";
    public const string InstallationFileName = "installation.json";
    public const string MemoryDbFileName = "memory.sqlite3";
    public const string RuntimeConfigFileName = "runtime-config.json";
    public const string EnvFileName = ".env";

    /// <summary>候选根目录（按优先级）。</summary>
    public static IReadOnlyList<string> CandidateRoots(AppSettings settings)
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.MemoryRootPath))
        {
            list.Add(settings.MemoryRootPath);
        }
        list.Add(@"E:\ScopeRecall");
        list.Add(@"D:\ScopeRecall");
        list.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "ScopeRecallWin", "data"));
        list.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScopeRecallWin", "data"));
        return list;
    }

    /// <summary>探测已部署的记忆库根目录；未找到返回 null。</summary>
    public static string? FindMemoryRoot(AppSettings settings)
    {
        foreach (var root in CandidateRoots(settings))
        {
            if (IsMemoryRoot(root))
            {
                return root;
            }
        }
        return null;
    }

    /// <summary>判定是否为有效记忆库根目录：shared\installation.json 存在即认可。</summary>
    public static bool IsMemoryRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return false;
        }
        return File.Exists(Path.Combine(root, SharedDirName, InstallationFileName))
            || File.Exists(Path.Combine(root, SharedDirName, MemoryDbFileName));
    }

    public static string SharedDir(string root) => Path.Combine(root, SharedDirName);

    public static string InstallationJsonPath(string root) =>
        Path.Combine(root, SharedDirName, InstallationFileName);

    public static string MemoryDbPath(string root) =>
        Path.Combine(root, SharedDirName, MemoryDbFileName);

    public static string RuntimeConfigPath(string root) =>
        Path.Combine(root, SharedDirName, RuntimeConfigFileName);

    public static string EnvFilePath(string root) =>
        Path.Combine(root, SharedDirName, EnvFileName);

    /// <summary>定位共享 venv 的 python.exe；未找到返回 null。</summary>
    public static string? FindVenvPython(string root)
    {
        var direct = Path.Combine(root, VenvDirName, "Scripts", "python.exe");
        if (File.Exists(direct))
        {
            return direct;
        }
        // 根目录下其他 *venv* 目录兜底
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "*venv*", SearchOption.TopDirectoryOnly))
            {
                var candidate = Path.Combine(dir, "Scripts", "python.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // 目录不可枚举时忽略
        }
        return null;
    }

    /// <summary>hook-endpoints 注册表目录（resident 端点 port/token/pid 登记处）。</summary>
    public static string HookEndpointsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "scope-recall", "hook-endpoints");

    /// <summary>判断目录对当前用户是否可写（部署权限自检用）。</summary>
    public static bool IsWritableByCurrentUser(string dir)
    {
        try
        {
            if (!Directory.Exists(dir))
            {
                return false;
            }
            var probe = Path.Combine(dir, $".sr-write-probe-{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
