using System.Collections.ObjectModel;
using OfficeAssistant.App.Infrastructure;
using OfficeAssistant.Core;
using OfficeAssistant.Core.Network;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.App.ViewModels;

/// <summary>测速结果行。</summary>
public sealed class ProbeRowViewModel : ObservableObject
{
    private ProbeResult _result;

    public ProbeRowViewModel(ProbeResult result) => _result = result;

    public ProbeResult Result
    {
        get => _result;
        set
        {
            if (!SetProperty(ref _result, value)) return;
            OnPropertyChanged(nameof(Host));
            OnPropertyChanged(nameof(Group));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(LatencyText));
            OnPropertyChanged(nameof(DnsText));
            OnPropertyChanged(nameof(TcpText));
            OnPropertyChanged(nameof(HttpText));
            OnPropertyChanged(nameof(Message));
            OnPropertyChanged(nameof(IsReachable));
        }
    }

    public string Host => $"{Result.Host}:{Result.Port}";
    public string Group => Result.Group;
    public string StatusText => Result.Status switch
    {
        ProbeStatus.Reachable => "可达",
        ProbeStatus.Slow => "偏慢",
        ProbeStatus.Unreachable => "不可达",
        ProbeStatus.DnsFailed => "DNS 失败",
        _ => "未测",
    };
    public string LatencyText => Result.Status == ProbeStatus.Unknown ? "—" : Result.LatencyText;
    public string DnsText => Result.DnsMs > 0 ? $"{Result.DnsMs} ms" : "—";
    public string TcpText => Result.TcpMs > 0 ? $"{Result.TcpMs} ms" : "—";
    public string HttpText => Result.HttpMs > 0 ? $"{Result.HttpMs} ms（HTTP {Result.HttpStatus}）" : "—";
    public string Message => Result.Message;
    public bool IsReachable => Result.Status is ProbeStatus.Reachable or ProbeStatus.Slow;

    /// <summary>
    /// 失败卡在哪一段 + 该怎么办；能直连时为空。
    /// 只说"不可连"没法行动，因此这里把三段计时翻译成具体结论与建议。
    /// </summary>
    public string FailureText => Result.FailureStage.Length == 0
        ? string.Empty
        : $"{Result.FailureStage}｜建议：{Result.Advice}";

    public bool HasFailure => Result.FailureStage.Length > 0;
}

/// <summary>镜像源行。</summary>
public sealed class MirrorRowViewModel : ObservableObject
{
    private bool _isCurrent;

    public MirrorRowViewModel(MirrorEntry entry) => Entry = entry;

    public MirrorEntry Entry { get; }

    public string Name => Entry.Name;
    public string UrlPrefix => string.IsNullOrWhiteSpace(Entry.UrlPrefix) ? "（未填写）" : Entry.UrlPrefix;
    public string Note => Entry.Note;
    public string Region => Entry.Region;

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }
}

/// <summary>Git 配置建议行。</summary>
public sealed class GitOptionViewModel : ObservableObject
{
    private bool _selected = true;

    public GitOptionViewModel(string key, string value, string reason)
    {
        Key = key;
        Value = value;
        Reason = reason;
    }

    public string Key { get; }
    public string Value { get; }
    public string Reason { get; }

    public bool Selected
    {
        get => _selected;
        set => SetProperty(ref _selected, value);
    }
}

/// <summary>
/// 网络访问优化页视图模型（功能一）。
///
/// 合规边界（页面顶部常驻提示 + 代码层面无实现）：
/// 只做镜像切换、配置优化、连通性测速与代理配置引导；
/// 不修改 hosts、不内置代理/VPN、不提供任何绕过网络管理措施的能力。
/// </summary>
public sealed class NetworkPageViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly ProbeService _probe = new();

    private string _statusText = "点击「开始测速」对 GitHub 与 Steam 常用域名做连通性探测（≤15 秒）。";
    private bool _isProbing;
    private bool _isBusy;
    private bool _featureEnabled;
    private string _proxyText = string.Empty;
    private string _proxyInput = string.Empty;
    private string _snapshotText = "尚无配置快照";
    private string _cloneWizardText = string.Empty;
    private string _repositoryUrl = "https://github.com/torvalds/linux";
    private string _sparsePath = string.Empty;
    private bool _shallowClone = true;
    private int _cloneDepth = 1;
    private bool _sparseCheckout;
    private string _customMirrorPrefix = string.Empty;
    private MirrorRowViewModel? _selectedMirror;

    public NetworkPageViewModel(AppHost host)
    {
        _host = host;
        _featureEnabled = host.Settings.NetworkFeatureEnabled;

        ProbeCommand = new AsyncRelayCommand(ProbeAllAsync, () => !IsProbing && FeatureEnabled);
        ApplyMirrorCommand = new AsyncRelayCommand(ApplyMirrorAsync, () => FeatureEnabled && SelectedMirror is not null);
        ApplyGitOptionsCommand = new AsyncRelayCommand(ApplyGitOptionsAsync, () => FeatureEnabled && GitAvailable);
        ApplySshCommand = new AsyncRelayCommand(ApplySshOver443Async, () => FeatureEnabled);
        ApplyProxyCommand = new AsyncRelayCommand(ApplyProxyAsync, () => FeatureEnabled && !string.IsNullOrWhiteSpace(ProxyInput));
        RestoreCommand = new AsyncRelayCommand(RestoreSnapshotAsync, () => FeatureEnabled);
        RefreshProxyCommand = new AsyncRelayCommand(RefreshProxyAsync, () => FeatureEnabled);
        BuildCloneCommand = new RelayCommand(BuildCloneText);
        MeasureCommand = new AsyncRelayCommand(MeasureAsync, () => FeatureEnabled && GitAvailable && !IsBusy);

        foreach (var mirror in MirrorCatalog.BuiltIn)
            Mirrors.Add(new MirrorRowViewModel(mirror));

        foreach (var option in MirrorCatalog.RecommendedGitConfig)
            GitOptions.Add(new GitOptionViewModel(option.Key, option.Value, option.Reason));
    }

    public AsyncRelayCommand ProbeCommand { get; }
    public AsyncRelayCommand ApplyMirrorCommand { get; }
    public AsyncRelayCommand ApplyGitOptionsCommand { get; }
    public AsyncRelayCommand ApplySshCommand { get; }
    public AsyncRelayCommand ApplyProxyCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand RefreshProxyCommand { get; }
    public RelayCommand BuildCloneCommand { get; }
    public AsyncRelayCommand MeasureCommand { get; }

    /// <summary>由视图注入：展示变更明细（含一键还原提示）。</summary>
    public Action<string, string>? ShowResultDialog { get; set; }

    public Func<string, string, bool>? RequestConfirm { get; set; }

    public ObservableCollection<ProbeRowViewModel> Probes { get; } = [];

    /// <summary>
    /// 测速失败的目标（用于界面上的诊断区）。
    /// 单独暴露一份是因为界面只该展示"需要处理的行"，
    /// 而可达的行已经在状态条目里看得到了。
    /// </summary>
    public ObservableCollection<ProbeRowViewModel> FailedProbes { get; } = [];

    public ObservableCollection<MirrorRowViewModel> Mirrors { get; } = [];

    public ObservableCollection<GitOptionViewModel> GitOptions { get; } = [];

    /// <summary>合规说明文本（首屏常驻展示）。</summary>
    public string ComplianceNotice =>
        "合规红线（本工具不实现、也不会实现以下任何一项）：不修改 hosts 文件；不内置或自动启用代理、VPN；" +
        "不安装网络驱动或过滤规则；不改写系统代理设置；不针对受限资源提供自动访问。\n" +
        "本页只做四件合法的事：① 常用域名连通性测速；② 公开镜像源前缀一键切换；③ Git 配置优化（写入前快照、可一键还原）；" +
        "④ 你自己提供代理时的配置引导（可一键还原）。测不通的目标请通过官方渠道处理。\n" +
        "依据：《中华人民共和国网络安全法》《计算机信息网络国际联网管理暂行规定》。";

    public bool FeatureEnabled
    {
        get => _featureEnabled;
        set
        {
            if (!SetProperty(ref _featureEnabled, value)) return;
            _host.Settings.NetworkFeatureEnabled = value;
            ProbeCommand.RaiseCanExecuteChanged();
            ApplyMirrorCommand.RaiseCanExecuteChanged();
            ApplyGitOptionsCommand.RaiseCanExecuteChanged();
            ApplySshCommand.RaiseCanExecuteChanged();
            ApplyProxyCommand.RaiseCanExecuteChanged();
            RestoreCommand.RaiseCanExecuteChanged();
            RefreshProxyCommand.RaiseCanExecuteChanged();
            MeasureCommand.RaiseCanExecuteChanged();
            StatusText = value ? "网络优化功能已启用。" : "网络优化功能已整体停用（不进行任何测速或配置写入）。";
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsProbing
    {
        get => _isProbing;
        private set
        {
            if (!SetProperty(ref _isProbing, value)) return;
            ProbeCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            MeasureCommand.RaiseCanExecuteChanged();
        }
    }

    public bool GitAvailable => _host.GitConfig.GitAvailable;

    public string GitStatusText => _host.GitConfig.GitAvailable
        ? $"已检测到 Git：{_host.GitConfig.GitVersion}（{_host.GitConfig.GitPath}）"
        : "未检测到 Git。测速与镜像管理仍可用；配置优化需要先安装 Git for Windows。";

    public MirrorRowViewModel? SelectedMirror
    {
        get => _selectedMirror;
        set
        {
            if (!SetProperty(ref _selectedMirror, value)) return;
            ApplyMirrorCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(SelectedMirrorHint));
        }
    }

    public string SelectedMirrorHint => SelectedMirror is null
        ? "请选择镜像源"
        : string.IsNullOrWhiteSpace(SelectedMirror.Entry.UrlPrefix)
            ? "自定义镜像：请在下方填写完整前缀，例如 https://your-mirror.example.com/https://github.com"
            : $"克隆前缀：{SelectedMirror.Entry.UrlPrefix}";

    public string CustomMirrorPrefix
    {
        get => _customMirrorPrefix;
        set => SetProperty(ref _customMirrorPrefix, value);
    }

    public string ProxyText
    {
        get => _proxyText;
        private set => SetProperty(ref _proxyText, value);
    }

    public string ProxyInput
    {
        get => _proxyInput;
        set
        {
            if (!SetProperty(ref _proxyInput, value)) return;
            ApplyProxyCommand.RaiseCanExecuteChanged();
        }
    }

    public string SnapshotText
    {
        get => _snapshotText;
        private set => SetProperty(ref _snapshotText, value);
    }

    public string RepositoryUrl
    {
        get => _repositoryUrl;
        set => SetProperty(ref _repositoryUrl, value);
    }

    public string SparsePath
    {
        get => _sparsePath;
        set => SetProperty(ref _sparsePath, value);
    }

    public bool ShallowClone
    {
        get => _shallowClone;
        set => SetProperty(ref _shallowClone, value);
    }

    public int CloneDepth
    {
        get => _cloneDepth;
        set => SetProperty(ref _cloneDepth, Math.Clamp(value, 1, 1000));
    }

    public bool SparseCheckout
    {
        get => _sparseCheckout;
        set => SetProperty(ref _sparseCheckout, value);
    }

    public string CloneWizardText
    {
        get => _cloneWizardText;
        private set => SetProperty(ref _cloneWizardText, value);
    }

    public async Task InitializeAsync()
    {
        _host.GitConfig.DetectGit();
        OnPropertyChanged(nameof(GitAvailable));
        OnPropertyChanged(nameof(GitStatusText));
        ApplyGitOptionsCommand.RaiseCanExecuteChanged();
        await RefreshProxyAsync().ConfigureAwait(true);
        await RefreshSnapshotsAsync().ConfigureAwait(true);
    }

    // ------------------------------------------------------------------ 测速

    private async Task ProbeAllAsync()
    {
        if (!FeatureEnabled) return;
        IsProbing = true;
        StatusText = "正在并发探测 GitHub / Steam 常用域名（DNS → TCP → HTTPS），随后自动应用可用的加速方案…";
        try
        {
            Probes.Clear();
            FailedProbes.Clear();
            foreach (var (host, port, group, _) in ProbeService.DefaultTargets)
                Probes.Add(new ProbeRowViewModel(new ProbeResult(host, port, ProbeStatus.Unknown, 0, 0, 0, 0, "等待探测") { Group = group }));

            var progress = new Progress<ProbeResult>(result =>
            {
                var row = Probes.FirstOrDefault(p => p.Result.Host == result.Host && p.Result.Port == result.Port);
                if (row is not null) row.Result = result;
            });

            var results = await _probe.ProbeAllAsync(ProbeService.DefaultTargets, progress).ConfigureAwait(true);

            var reachable = results.Count(r => r.Status is ProbeStatus.Reachable or ProbeStatus.Slow);
            var slow = results.Count(r => r.Status == ProbeStatus.Slow);
            var failed = results.Count - reachable;
            var fastest = results.Where(r => r.Status is ProbeStatus.Reachable or ProbeStatus.Slow)
                .OrderBy(r => r.LatencyMs).FirstOrDefault();

            // 把"需要处理的目标 + 为什么 + 怎么办"单列出来，而不是只报一个数字
            foreach (var row in Probes.Where(p => p.HasFailure).OrderBy(p => p.Group)) FailedProbes.Add(row);

            var githubFailed = Probes.Any(p => p.Group == "GitHub" && p.HasFailure);

            // 探测只是第一步：GitHub 有目标不可达时，立刻把可用的加速方案真正应用上去。
            // 早先这里只报一个数字就结束了，用户点完看不到任何变化，观感就是"加速不成功"。
            var applied = githubFailed ? await ApplyBestEffortAsync().ConfigureAwait(true) : string.Empty;

            StatusText = $"测速完成：{reachable}/{results.Count} 个目标可达（其中 {slow} 个偏慢，{failed} 个不可达）。" +
                         (fastest is not null ? $" 最快：{fastest.Host} {fastest.LatencyMs} ms。" : string.Empty) +
                         (failed > 0 ? " 下方列出了每个不可达目标卡在哪一段以及建议处理方式。" : string.Empty) +
                         applied;
        }
        catch (Exception ex)
        {
            StatusText = $"测速失败：{ex.Message}";
        }
        finally
        {
            IsProbing = false;
        }
    }

    /// <summary>
    /// 探测后的一键加速：在**合规手段**范围内把能做的都做掉。
    ///
    /// 明确边界：只做镜像前缀改写与 git 传输参数优化，
    /// 不修改 hosts、不写入系统代理、不建立任何隧道——无法合法直连的目标不会"变通"，
    /// 只会给用户可执行的替代方案。因此这里返回的是"做了什么"的说明，而不是承诺提速倍数。
    /// </summary>
    private async Task<string> ApplyBestEffortAsync()
    {
        if (!GitAvailable)
            return " GitHub 有目标不可达；本机未检测到 git，已生成可复制的镜像克隆命令供手动使用。";

        try
        {
            // 选一个非官方、且带有效前缀的镜像作为克隆前缀
            var mirror = Mirrors.FirstOrDefault(m => !m.Entry.IsOfficial && !string.IsNullOrWhiteSpace(m.Entry.UrlPrefix));
            if (mirror is null) return " GitHub 有目标不可达，但没有可用的内置镜像源，请自行填写镜像前缀。";

            SelectedMirror = mirror;
            var applied = await ApplyMirrorCoreAsync(mirror).ConfigureAwait(true);
            return applied
                ? $" 已自动应用镜像源「{mirror.Name}」到 git 配置（可一键还原为官方源）。"
                : " 镜像源应用未完成，请查看下方结果。";
        }
        catch (Exception ex)
        {
            return $" 自动应用镜像失败：{ex.Message}";
        }
    }

    // ------------------------------------------------------------------ 镜像

    private async Task ApplyMirrorAsync()
    {
        if (SelectedMirror is null) return;
        var entry = SelectedMirror.Entry;
        var prefix = string.IsNullOrWhiteSpace(entry.UrlPrefix) ? CustomMirrorPrefix.Trim() : entry.UrlPrefix;

        if (string.IsNullOrWhiteSpace(prefix))
        {
            StatusText = "请先填写自定义镜像前缀。";
            return;
        }

        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            StatusText = "镜像前缀必须是合法的 https 地址。";
            return;
        }

        if (!_host.GitConfig.GitAvailable)
        {
            StatusText = "未检测到 Git，无法写入镜像配置。你仍可复制下方命令手动使用。";
            return;
        }

        var confirmed = RequestConfirm?.Invoke("切换 GitHub 镜像源",
            $"将把 git 全局配置 url.\"{prefix}/\".insteadOf 指向 https://github.com/，\n" +
            $"用于把克隆前缀改写为：\n{prefix}\n\n" +
            "写入前会自动创建配置快照，之后可一键还原。是否继续？") ?? false;
        if (!confirmed) return;

        await ApplyMirrorCoreAsync(SelectedMirror).ConfigureAwait(true);
    }

    /// <summary>
    /// 写入镜像前缀（不带确认，供"一键加速"在探测后自动调用）。
    /// 写入仍然是合法的 git 配置改写，并且照旧创建快照、可一键还原。
    /// </summary>
    private async Task<bool> ApplyMirrorCoreAsync(MirrorRowViewModel mirror)
    {
        var entry = mirror.Entry;
        var prefix = string.IsNullOrWhiteSpace(entry.UrlPrefix) ? CustomMirrorPrefix.Trim() : entry.UrlPrefix;
        if (string.IsNullOrWhiteSpace(prefix)) return false;
        if (!Uri.TryCreate(prefix, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return false;
        if (!_host.GitConfig.GitAvailable) return false;

        IsBusy = true;
        try
        {
            var result = _host.GitConfig.ApplyGitConfig(
            [
                ($"url.{prefix}/.insteadOf", "https://github.com/"),
            ], $"一键加速：切换镜像源 {entry.Name}");

            foreach (var m in Mirrors) m.IsCurrent = ReferenceEquals(m, mirror);
            await RefreshSnapshotsAsync().ConfigureAwait(true);

            StatusText = result.Message;
            Log.Info($"一键加速已应用镜像源：{entry.Name} -> {prefix}");
            return result.Success;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ Git 配置

    private async Task ApplyGitOptionsAsync()
    {
        var selected = GitOptions.Where(o => o.Selected).Select(o => (o.Key, o.Value)).ToList();
        if (selected.Count == 0)
        {
            StatusText = "没有勾选任何配置项。";
            return;
        }

        var confirmed = RequestConfirm?.Invoke("应用 Git 配置优化",
            $"将写入以下 {selected.Count} 项 git 全局配置：\n" +
            string.Join("\n", selected.Select(s => $"  {s.Key} = {s.Value}")) +
            "\n\n写入前会自动创建快照，之后可一键还原。是否继续？") ?? false;
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = _host.GitConfig.ApplyGitConfig(selected, "应用 Git 配置优化建议");
            await RefreshSnapshotsAsync().ConfigureAwait(true);
            StatusText = result.Message;
            ShowResultDialog?.Invoke("Git 配置应用结果", result.Message + "\n\n" + string.Join("\n", result.Details));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplySshOver443Async()
    {
        var confirmed = RequestConfirm?.Invoke("写入 SSH over 443 配置",
            $"将在 {GitConfigEngine.SshConfigPath} 追加以下内容：\n\n" +
            MirrorCatalog.BuildSshOver443Config() +
            "\n\n写入前会自动创建快照，之后可一键还原。是否继续？") ?? false;
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = _host.GitConfig.WriteSshOver443();
            await RefreshSnapshotsAsync().ConfigureAwait(true);
            StatusText = result.Message;
            ShowResultDialog?.Invoke("SSH 配置写入结果", result.Message + "\n\n" + string.Join("\n", result.Details));
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ 代理引导

    private async Task RefreshProxyAsync()
    {
        var info = await Task.Run(ProbeService.ReadSystemProxy).ConfigureAwait(true);
        ProxyText = info.Describe();
    }

    private async Task ApplyProxyAsync()
    {
        var url = ProxyInput.Trim();
        var confirmed = RequestConfirm?.Invoke("写入代理配置",
            $"将写入：\n  · git 全局配置 http.proxy / https.proxy = {url}\n" +
            "  · 用户级环境变量 HTTP_PROXY / HTTPS_PROXY = " + url + "\n\n" +
            "说明：代理由你自行提供与维护，本工具不内置、不启动任何代理或隧道进程；" +
            "也不会改动 Windows「Internet 选项」里的系统代理。写入前会自动创建快照，可一键还原。是否继续？") ?? false;
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = _host.GitConfig.ApplyProxy(url, true, true);
            await RefreshSnapshotsAsync().ConfigureAwait(true);
            await RefreshProxyAsync().ConfigureAwait(true);
            StatusText = result.Message;
            ShowResultDialog?.Invoke("代理配置写入结果", result.Message + "\n\n" + string.Join("\n", result.Details));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreSnapshotAsync()
    {
        var snapshots = _host.GitConfig.ListSnapshots();
        if (snapshots.Count == 0)
        {
            StatusText = "没有可用的配置快照（尚未执行过任何写入）。";
            return;
        }

        var latest = snapshots[0];
        var confirmed = RequestConfirm?.Invoke("一键还原配置",
            $"将按最近一次快照还原所有配置：\n  快照：{latest.Id}（{latest.CreatedAt:yyyy-MM-dd HH:mm}）\n" +
            $"  说明：{latest.Description}\n  含 {latest.GitConfig.Count} 项 git 配置、~/.ssh/config 与代理环境变量。\n\n是否继续？") ?? false;
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = _host.GitConfig.RestoreSnapshot(latest);
            await RefreshSnapshotsAsync().ConfigureAwait(true);
            await RefreshProxyAsync().ConfigureAwait(true);
            StatusText = result.Message;
            ShowResultDialog?.Invoke("一键还原结果", result.Message + "\n\n" + string.Join("\n", result.Details));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshSnapshotsAsync()
    {
        var snapshots = await Task.Run(_host.GitConfig.ListSnapshots).ConfigureAwait(true);
        SnapshotText = snapshots.Count == 0
            ? "尚无配置快照（任何写入之前都会自动创建）"
            : $"共 {snapshots.Count} 个快照，最近一次：{snapshots[0].CreatedAt:yyyy-MM-dd HH:mm}（{snapshots[0].Description}）";
        RestoreCommand.RaiseCanExecuteChanged();
    }

    // ------------------------------------------------------------------ 克隆向导

    private void BuildCloneText()
    {
        var prefix = SelectedMirror is null
            ? null
            : string.IsNullOrWhiteSpace(SelectedMirror.Entry.UrlPrefix) ? CustomMirrorPrefix : SelectedMirror.Entry.UrlPrefix;

        CloneWizardText = MirrorCatalog.BuildCloneWizardText(RepositoryUrl, prefix, ShallowClone, CloneDepth, SparseCheckout, SparsePath);
        StatusText = "已生成克隆命令（可直接复制到终端执行）。";
    }

    private async Task MeasureAsync()
    {
        IsBusy = true;
        StatusText = "正在用 git ls-remote 分别测量官方源与镜像源的握手耗时…";
        try
        {
            var prefix = SelectedMirror is null
                ? null
                : string.IsNullOrWhiteSpace(SelectedMirror.Entry.UrlPrefix) ? CustomMirrorPrefix : SelectedMirror.Entry.UrlPrefix;

            var report = await _host.GitConfig.MeasureCloneAsync(RepositoryUrl, prefix, CancellationToken.None)
                .ConfigureAwait(true);
            CloneWizardText = report;
            StatusText = "测量完成（ls-remote 只读取引用列表，不下载仓库对象）。";
        }
        catch (Exception ex)
        {
            StatusText = $"测量失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ 包管理器镜像（只展示命令，不自动执行）

    public IReadOnlyList<PackageMirrorEntry> PackageMirrors => MirrorCatalog.PackageMirrors;
}
