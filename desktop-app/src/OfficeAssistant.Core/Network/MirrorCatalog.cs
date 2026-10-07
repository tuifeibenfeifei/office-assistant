namespace OfficeAssistant.Core.Network;

/// <summary>GitHub 镜像源条目。</summary>
public sealed record MirrorEntry(
    string Name,
    string UrlPrefix,
    string Note,
    string Region,
    bool BuiltIn = true)
{
    /// <summary>把官方仓库地址改写成镜像地址。</summary>
    public string Rewrite(string officialUrl)
    {
        if (string.IsNullOrWhiteSpace(UrlPrefix)) return officialUrl;
        var url = officialUrl.Trim();
        if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
            return UrlPrefix.TrimEnd('/') + "/" + url["https://github.com/".Length..];
        if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
            return UrlPrefix.TrimEnd('/') + "/" + url["git@github.com:".Length..];
        return url;
    }

    public bool IsOfficial => UrlPrefix.Contains("github.com", StringComparison.OrdinalIgnoreCase) &&
                              !UrlPrefix.Contains("mirror", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 包管理器镜像条目。
/// 用记录类型（而非元组）承载：元组的字段是编译器生成的 Item1/Item2…，
/// WPF 数据绑定无法通过 Name/Command/Note 访问，会在运行时产生绑定错误。
/// </summary>
public sealed record PackageMirrorEntry(string Name, string Command, string Note);

/// <summary>
/// GitHub 镜像源清单（功能一）。
///
/// 合规说明：镜像源是第三方同步的公开只读副本，仅用于替换 git 克隆前缀；
/// 不用于绕过任何网络管理措施，可用性与内容完整性由用户自行判断，
/// 任何写入都可一键还原为官方源。
/// </summary>
public static class MirrorCatalog
{
    /// <summary>内置公开镜像清单。列表顺序不代表推荐程度，用户应自行核对可用性。</summary>
    public static IReadOnlyList<MirrorEntry> BuiltIn { get; } =
    [
        new MirrorEntry("官方源（GitHub）", "https://github.com", "GitHub 官方直连，不做任何改写", "官方"),
        new MirrorEntry("ghproxy 前缀代理", "https://ghproxy.net/https://github.com",
            "公开的只读加速前缀，仅加速下载，不改变仓库内容", "第三方"),
        new MirrorEntry("gh-proxy.com 前缀代理", "https://gh-proxy.com/https://github.com",
            "公开的只读加速前缀", "第三方"),
        new MirrorEntry("hub.fastgit 系列", "https://hub.fastgit.xyz",
            "历史公开镜像，可用性波动较大", "第三方"),
        new MirrorEntry("kgithub", "https://kkgithub.com",
            "面向国内的公开只读镜像", "第三方"),
        new MirrorEntry("cnpmjs 镜像（仅 npm 场景）", "https://github.com",
            "npm 请改用 https://registry.npmmirror.com", "第三方"),
        new MirrorEntry("自定义镜像前缀", "",
            "填入你自己的镜像地址，例如 https://your-mirror.example.com/https://github.com", "自定义"),
    ];

    /// <summary>国内常用的包管理器镜像（与 git 前缀互为补充，用于依赖拉取提速）。</summary>
    public static IReadOnlyList<PackageMirrorEntry> PackageMirrors { get; } =
    [
        new PackageMirrorEntry("npm 镜像", "npm config set registry https://registry.npmmirror.com",
            "设置 npm 使用国内镜像；还原：npm config set registry https://registry.npmjs.org"),
        new PackageMirrorEntry("pip 镜像", "pip config set global.index-url https://pypi.tuna.tsinghua.edu.cn/simple",
            "设置 pip 使用清华镜像；还原：pip config unset global.index-url"),
        new PackageMirrorEntry("Maven 镜像", "修改 ~/.m2/settings.xml 的 <mirrors>",
            "建议使用阿里云 maven 镜像，需手工编辑配置文件"),
        new PackageMirrorEntry("Go 模块代理", "go env -w GOPROXY=https://goproxy.cn,direct",
            "设置 Go 模块代理；还原：go env -u GOPROXY"),
    ];

    /// <summary>
    /// 生成"建议配置"清单：只生成命令文本供用户确认，
    /// 由 <see cref="GitConfigEngine"/> 在用户点击应用后执行并保留快照。
    /// </summary>
    public static IReadOnlyList<(string Key, string Value, string Reason)> RecommendedGitConfig { get; } =
    [
        ("http.postBuffer", "524288000", "大仓库推送缓冲区提升到 500MB，减少大对象推送失败"),
        ("http.lowSpeedLimit", "1000", "低于 1000 字节/秒判定为慢速"),
        ("http.lowSpeedTime", "60", "慢速持续 60 秒即中断重试，避免长时间卡死"),
        ("core.compression", "0", "克隆阶段降低压缩率以提升吞吐（可回滚）"),
        ("fetch.parallel", "0", "并行抓取所有远端，减少多子模块场景等待"),
        ("submodule.fetchJobs", "0", "子模块并行拉取"),
        ("core.fscache", "true", "启用文件系统缓存（Windows 专属优化）"),
        ("core.longpaths", "true", "允许超过 260 字符的路径，避免深层目录检出失败"),
        ("gc.auto", "0", "克隆期间关闭自动 gc，避免克隆被后台回收打断"),
    ];

    /// <summary>生成 SSH over 443 的配置建议文本（写入由用户确认后执行）。</summary>
    public static string BuildSshOver443Config() =>
        """
        # ===== 全能办公助手生成的建议配置 =====
        # 作用：GitHub 的 SSH 服务同时监听 443 端口，在公司/校园网络屏蔽 22 端口时可正常使用。
        # 还原方法：删除本段内容即可；应用前助手已保存原文件快照。
        Host github.com
            HostName ssh.github.com
            Port 443
            User git
            # 首次连接会提示确认指纹，可提前信任：
            # ssh-keyscan -p 443 ssh.github.com >> ~/.ssh/known_hosts
        """;

    /// <summary>生成浅克隆 / 稀疏检出的命令模板（不改变仓库内容，只影响本地检出策略）。</summary>
    public static string BuildCloneWizardText(string repositoryUrl, string? mirrorPrefix, bool shallow, int depth, bool sparse, string? sparsePath)
    {
        var effective = repositoryUrl;
        if (!string.IsNullOrWhiteSpace(mirrorPrefix))
        {
            var entry = new MirrorEntry("临时", mirrorPrefix!, "", "");
            effective = entry.Rewrite(repositoryUrl);
        }

        var command = shallow
            ? $"git clone --depth {Math.Max(1, depth)} {effective}"
            : $"git clone {effective}";

        if (sparse)
        {
            var path = string.IsNullOrWhiteSpace(sparsePath) ? "path/to/subdir" : sparsePath!;
            command += $"""

                cd {System.IO.Path.GetFileNameWithoutExtension(repositoryUrl.TrimEnd('/'))}
                git sparse-checkout init --cone
                git sparse-checkout set {path}
                """;
        }

        return command;
    }
}
