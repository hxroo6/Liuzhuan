using Liuzhuan.Utils;

namespace Liuzhuan.Services.Lan;

/// <summary>
/// 局域网服务器总入口 — 启动/停止/素材接收
/// M1 范围：文本同步（sync_text / clipboard_push）
/// </summary>
public class LanServer : IDisposable
{
    private readonly WsHub _hub = new();
    private readonly FileHttpServer _fileServer = new();
    private readonly UdpDiscovery _discovery = new();
    private bool _running;

    /// <summary>收到手机文字素材（文本内容, 来源设备）</summary>
    public event Action<string, string>? TextReceived;

    /// <summary>设备连接数变化</summary>
    public event Action<int>? ClientCountChanged;

    /// <summary>手机文件上传完成（文件路径, 显示名）</summary>
    public event Action<string, string>? FileUploaded;

    /// <summary>最近素材摘要提供者（list_sync），由 MainWindow 注入</summary>
    public Func<List<LanItemSummary>>? ListProvider
    {
        get => _hub.ListProvider;
        set => _hub.ListProvider = value;
    }

    /// <summary>素材详情提供者（get_item / HTTP 下载），由 MainWindow 注入</summary>
    public Func<string, Models.MaterialItem?>? ItemLookup
    {
        get => _hub.ItemLookup;
        set
        {
            _hub.ItemLookup = value;
            _fileServer.ItemLookup = value;
        }
    }

    public int ClientCount => _hub.ClientCount;

    /// <summary>获取已连接设备列表（设备名, IP）</summary>
    public List<(string Device, string Ip)> GetDevices() => _hub.GetDevices();

    /// <summary>广播素材摘要给所有已连接设备（接收页实时同步）</summary>
    public void BroadcastItemAdded(LanItemSummary item) => _hub.BroadcastItemAdded(item);

    public void Start()
    {
        if (_running) return;
        if (!LanConfig.Enabled)
        {
            Logger.Run("LanServer: not started (disabled)");
            return;
        }

        _hub.TextReceived += OnTextReceived;
        _hub.ClientCountChanged += OnClientCountChanged;
        _fileServer.FileUploaded += OnFileUploaded;
        _hub.Start(LanConfig.Port, LanConfig.PasswordHash);
        _fileServer.Start(LanConfig.Port + 1);
        _discovery.Start(LanConfig.Port);
        _running = true;
        Logger.Run("LanServer started: port={0} (http data port={1})", LanConfig.Port, LanConfig.Port + 1);
    }

    public void Stop()
    {
        if (!_running) return;
        _hub.TextReceived -= OnTextReceived;
        _hub.ClientCountChanged -= OnClientCountChanged;
        _fileServer.FileUploaded -= OnFileUploaded;
        _hub.Stop();
        _fileServer.Stop();
        _discovery.Stop();
        _running = false;
        Logger.Run("LanServer stopped");
    }

    private void OnTextReceived(string content, string device)
    {
        TextReceived?.Invoke(content, device);
    }

    private void OnFileUploaded(string filePath, string displayName)
    {
        FileUploaded?.Invoke(filePath, displayName);
    }

    private void OnClientCountChanged(int count)
    {
        ClientCountChanged?.Invoke(count);
    }

    public void Dispose()
    {
        Stop();
    }
}
