using System;
using System.Collections.Generic;

namespace ScopeRecallWin.Infrastructure;

/// <summary>
/// 快速搜索（Ctrl+T / Ctrl+K 呼出）的一条候选。
/// </summary>
/// <param name="Kind">候选类型：页面 / 设置 / 检索（检索是动态生成的「搜记忆」动作）。</param>
/// <param name="Tag">目标视图路由 Tag（设置项跳到所在页；页面项即自身）。</param>
/// <param name="Anchor">设置项在视图内的 x:Name，跳转后聚焦并滚动到位；页面项为 null。</param>
/// <param name="Keywords">附加匹配词（英文/同义词），与 Label/Tip/Tag/Group 一样参与过滤。</param>
internal sealed record QuickSearchItem(
    string Kind, string Label, string Tip, string Tag, string Group, string? Anchor, string Keywords)
{
    /// <summary>查询命中判定：标题/副标题/路由/分组/附加词，任一包含即入候选（忽略大小写）。</summary>
    public bool Matches(string query) =>
        Label.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Tip.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Tag.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        Group.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrEmpty(Keywords) && Keywords.Contains(query, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// 应用内设置项目录 —— 快速搜索的「设置」候选数据源。
/// 覆盖三页的可操作项：设置（外观/更新/自启/数据位置）、AI 设置（服务商/模型/密钥/高级）、
/// 云同步配置（后端/地址/账号/口令/云端目录）。Anchor 对应各视图 XAML 里的 x:Name，
/// 命中后跳转到所在页并聚焦该项（途经的 Expander 会先展开）。
/// 新增设置项时同步补这里，否则快速搜索搜不到它。
/// </summary>
internal static class QuickSearchCatalog
{
    public static IReadOnlyList<QuickSearchItem> Settings { get; } = new List<QuickSearchItem>
    {
        // —— 设置页 ——
        new("设置", "主题：明暗风格", "跟随系统 / 浅色 / 深色，标题栏同步变色",
            "settings", "设置", "RadioThemeSystem",
            "theme,外观,颜色,深色,浅色,暗黑,dark,light"),
        new("设置", "更新模式", "自动静默替换 / 仅通知手动下载",
            "settings", "设置", "RadioAuto",
            "update,升级,版本,自动更新,通知"),
        new("设置", "开机自启", "登录后自动启动（注册表 Run key，免管理员）",
            "settings", "设置", "ToggleStartup",
            "startup,启动,自启,开机,后台"),
        new("设置", "记忆库位置", "查看当前生效的记忆库根目录",
            "settings", "设置", "TxtMemoryRoot",
            "data,数据,目录,路径,位置,memory,root"),
        new("设置", "打开设置目录", "在资源管理器中打开配置目录",
            "settings", "设置", "BtnOpenDataDir",
            "folder,文件夹,配置目录,打开"),

        // —— AI 设置页 ——
        new("设置", "选择服务商", "第 1 步：选服务商，自动填好接口地址",
            "ai-settings", "AI 设置", "PickerProvider",
            "provider,服务商,厂商,接口,供应商"),
        new("设置", "模型名称", "第 2 步：记忆所用的模型（如 embedding-3）",
            "ai-settings", "AI 设置", "TxtSimpleModel",
            "model,模型,embedding,名称"),
        new("设置", "API Key", "第 3 步：只存 Windows 凭据管理器，不写配置文件",
            "ai-settings", "AI 设置", "PwdApiKey",
            "key,token,密钥,口令,api,凭据"),
        new("设置", "保存并测试连接", "写入配置并验证连通性",
            "ai-settings", "AI 设置", "BtnSimpleSave",
            "save,保存,测试,连接,验证"),
        new("设置", "高级设置", "嵌入/整合模型、向量维度、接口类型等技术参数",
            "ai-settings", "AI 设置", "AdvExpander",
            "advanced,高级,嵌入,向量,维度,整合,expand"),
        new("设置", "检索灵敏度", "相似度阈值 0~1：越高只有越贴近的记忆才召回",
            "ai-settings", "AI 设置", "AdvExpander",
            "threshold,阈值,灵敏度,相似度,召回,vector"),
        new("设置", "删除已保存的 Key", "清除凭据管理器里的 API Key",
            "ai-settings", "AI 设置", "BtnDeleteKey",
            "delete,删除,凭据,key,清除"),

        // —— 云同步配置页 ——
        new("设置", "云端存储类型", "local / webdav / s3 后端选择",
            "cloudsync", "云同步", "PickerBackend",
            "backend,后端,存储,webdav,s3,坚果云"),
        new("设置", "云端服务地址", "WebDAV 地址（坚果云 dav.jianguoyun.com）",
            "cloudsync", "云同步", "TxtUrl",
            "url,地址,端点,dav,http"),
        new("设置", "云端账号", "WebDAV 用户名 / 邮箱",
            "cloudsync", "云同步", "TxtUser",
            "user,账号,邮箱,用户名"),
        new("设置", "云端口令", "应用密码（坚果云必须用应用密码，登录密码会 401）",
            "cloudsync", "云同步", "PwdSecret",
            "pwd,password,口令,密码,应用密码"),
        new("设置", "云端目录", "留空表示云端根目录（records/ 平铺在根时留空）",
            "cloudsync", "云同步", "TxtRoot",
            "root,根,目录,prefix,路径"),
    };
}
