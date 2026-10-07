using OfficeAssistant.App.Infrastructure;
using OfficeAssistant.Core;
using OfficeAssistant.Core.Utils;

namespace OfficeAssistant.App.ViewModels;

/// <summary>
/// 主界面（文档 3.4 界面 1）的视图模型。
///
/// 主界面按规格是"极简入口"：只有标题、齿轮、三张功能卡片、底部版本号。
/// 因此这里只提供每张卡片需要的一个主读数与一行辅助文字，不放列表、不放说明文案。
/// </summary>
public sealed class HomeViewModel : ObservableObject
{
    private readonly AppHost _host;

    private string _networkValue = "待测速";
    private string _networkNote = "GitHub / Steam";
    private string _diskValue = "未扫描";
    private string _diskNote = "点击进入磁盘管理";
    private string _memoryValue = "0";
    private string _memoryNote = "MB 已释放";

    public HomeViewModel(AppHost host)
    {
        _host = host;
        Refresh();
    }

    public string ProductName => AppPaths.ProductName;

    /// <summary>底部居中文案：文档规定为"版本 1.0"。</summary>
    public string VersionText => "版本 1.0";

    // ---- 卡片 1：网络加速
    public string NetworkValue
    {
        get => _networkValue;
        private set => SetProperty(ref _networkValue, value);
    }

    public string NetworkNote
    {
        get => _networkNote;
        private set => SetProperty(ref _networkNote, value);
    }

    // ---- 卡片 2：磁盘管理
    public string DiskValue
    {
        get => _diskValue;
        private set => SetProperty(ref _diskValue, value);
    }

    public string DiskNote
    {
        get => _diskNote;
        private set => SetProperty(ref _diskNote, value);
    }

    // ---- 卡片 3：内存清理
    public string MemoryValue
    {
        get => _memoryValue;
        private set => SetProperty(ref _memoryValue, value);
    }

    public string MemoryNote
    {
        get => _memoryNote;
        private set => SetProperty(ref _memoryNote, value);
    }

    /// <summary>
    /// 刷新卡片读数。每次返回主界面时调用，保证卡片反映最新状态。
    /// </summary>
    /// <param name="network">网络页视图模型（提供最近一次测速结果）；未初始化时传 null。</param>
    public void Refresh(NetworkPageViewModel? network = null)
    {
        if (network is not null) RefreshNetwork(network);
        RefreshDisk();
        RefreshMemory();
    }

    private void RefreshNetwork(NetworkPageViewModel network)
    {
        try
        {
            var rows = network.Probes;
            if (rows.Count == 0)
            {
                NetworkValue = "待测速";
                NetworkNote = "GitHub / Steam";
                return;
            }

            var reachable = rows.Count(r => r.IsReachable);
            NetworkValue = $"{reachable}/{rows.Count}";
            NetworkNote = reachable == rows.Count ? "全部可直连" : "有目标不可直连";
        }
        catch (Exception ex)
        {
            Log.Debug($"主界面网络读数失败：{ex.Message}");
        }
    }

    private void RefreshDisk()
    {
        try
        {
            var files = _host.Database.CountFiles();
            if (files == 0)
            {
                DiskValue = "未扫描";
                DiskNote = "点击进入磁盘管理";
                return;
            }

            DiskValue = Format.Bytes(_host.Database.TotalBytes());
            DiskNote = $"{files:N0} 个文件";
        }
        catch (Exception ex)
        {
            Log.Debug($"主界面磁盘读数失败：{ex.Message}");
        }
    }

    private void RefreshMemory()
    {
        try
        {
            // "累计已释放"取调度器保留的清理报告之和，与内存清理页圆环同一口径。
            // 清理报告只保留在内存中（不落库），所以这里读运行期列表而不是数据库。
            long total = 0;
            lock (_host.ResourceScheduler.RecentReports)
            {
                foreach (var report in _host.ResourceScheduler.RecentReports)
                    total += report.WorkingSetReleasedBytes;
            }

            MemoryValue = (total / (1024d * 1024d)).ToString("0");
            MemoryNote = "MB 已释放";
        }
        catch (Exception ex)
        {
            Log.Debug($"主界面内存读数失败：{ex.Message}");
        }
    }
}
