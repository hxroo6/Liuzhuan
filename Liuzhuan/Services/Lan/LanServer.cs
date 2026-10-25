using Liuzhuan.Utils;

namespace Liuzhuan.Services.Lan;

/// <summary>
/// 局域网服务器总入口 — 启动/停止/素材接收
/// M1 范围：文本同步（sync_text / clipboard_push）
/// </summary>
public class LanServer : IDisposable
{
    public Func<System.Text.Json.JsonElement,Task<object>>? LibraryProvider {get=>_hub.LibraryProvider;set=>_hub.LibraryProvider=value;}
    public string LibraryId=>_hub.ServerId;
    public void BroadcastItemUpdated(LanItemSummary item)=>_hub.BroadcastItemUpdated(item);
    public Func<System.Text.Json.JsonElement, object>? AutomationProvider { get => _hub.AutomationProvider; set => _hub.AutomationProvider = value; }
    public event Action<string, string, string, string>? FileCollected;
    private readonly WsHub _hub = new();
    private readonly FileHttpServer _fileServer = new();
    private readonly UdpDiscovery _discovery = new();
    private readonly ResumableTransferServer _resume = new();
    public Func<string,string,string,Task>? DurableTextReceiver {get=>_hub.DurableTextReceiver;set=>_hub.DurableTextReceiver=value;}
    public Func<string,string,string,string,string,Task>? DurableFileReceiver {get=>_resume.FileReceiver;set=>_resume.FileReceiver=value;}
    private bool _running;

    /// <summary>收到手机文字素材（文本内容, 来源设备）</summary>
    public event Action<string, string>? TextReceived;

    /// <summary>设备连接数变化</summary>
    public event Action<int>? ClientCountChanged;
    public event Action? ClientAuthenticated;

    /// <summary>手机文件上传完成（文件路径, 显示名）</summary>
    public event Action<string, string>? FileUploaded;

    /// <summary>最近素材摘要提供者（list_sync），由 MainWindow 注入</summary>
    public Func<List<LanItemSummary>>? ListProvider
    {
        get => _hub.ListProvider;
        set => _hub.ListProvider = value;
    }

    public Func<LanListSnapshot>? SnapshotProvider
    {
        get => _hub.SnapshotProvider;
        set => _hub.SnapshotProvider = value;
    }

    public Action<Action>? SnapshotDispatcher
    {
        get => _hub.SnapshotDispatcher;
        set => _hub.SnapshotDispatcher = value;
    }

    /// <summary>素材详情提供者（get_item / HTTP 下载），由 MainWindow 注入</summary>
    public Func<string, Models.MaterialItem?>? ItemLookup
    {
        get => _hub.ItemLookup;
        set
        {
            _hub.ItemLookup = value;
            _fileServer.ItemLookup = value;
            _resume.ItemLookup = value;
        }
    }

    public int ClientCount => _hub.ClientCount;
    public Func<string,Task<Dictionary<string,object?>>>? MicrophoneConfigurationProvider
    {
        get => _hub.MicrophoneConfigurationProvider;
        set => _hub.MicrophoneConfigurationProvider = value;
    }
    public Func<Task<Dictionary<string, object?>>>? MicrophoneInviteProvider
    {
        get => _hub.MicrophoneInviteProvider;
        set => _hub.MicrophoneInviteProvider = value;
    }

    /// <summary>端口自检：TCP 自连 127.0.0.1:Port 确认 WS 真的在监听（Fleck 静默失败检测）</summary>
    public bool IsListening
    {
        get
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                var task = c.ConnectAsync("127.0.0.1", LanConfig.Port);
                task.Wait(1500);
                return c.Connected;
            }
            catch { return false; }
        }
    }

    /// <summary>获取已连接设备列表（设备名, IP）</summary>
    public List<(string Device, string Ip)> GetDevices() => _hub.GetDevices();

    /// <summary>广播素材摘要给所有已连接设备（接收页实时同步）</summary>
    public void BroadcastItemAdded(LanItemSummary item) => _hub.BroadcastItemAdded(item);

    /// <summary>广播素材删除给所有已连接设备</summary>
    public void BroadcastItemDeleted(string id, long sequence) => _hub.BroadcastItemDeleted(id, sequence);

    /// <summary>广播素材清空给所有已连接设备</summary>
    public void BroadcastItemCleared(long sequence) => _hub.BroadcastItemCleared(sequence);

    public void Start()
    {
        if (_running) return;
        if (!LanConfig.Enabled)
        {
            Logger.Run("LanServer: not started (disabled)");
            return;
        }

        var identityRoot=App.DataDir;
        var identity=DeviceIdentity.Load(identityRoot);
        _hub.DeviceId=identity.Id;_hub.DeviceName=()=>DeviceIdentity.Load(identityRoot).Name;
        _discovery.IdentityOffer=(nonce,port)=>{var d=DeviceIdentity.Load(identityRoot);var name=Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(d.Name));return $"LIUZHUAN_OFFER_V2|{d.Id}|{port}|{name}|{nonce}|{DeviceIdentity.Proof(LanConfig.PasswordHash,nonce,d.Id,port,name)}";};
        _resume.ControlChanged=_hub.SendTransferState;
        _hub.TextReceived += OnTextReceived;
        _hub.ClientCountChanged += OnClientCountChanged;
        _hub.ClientAuthenticated += OnClientAuthenticated;
        _fileServer.FileUploaded += OnFileUploaded;
        _fileServer.FileCollected += OnFileCollected;
        // Resume service failure must not take away the existing material/clipboard connection.
        try {_hub.ServerId=ResumableTransferServer.LoadIdentity(ConfigService.GetEffectiveDataDir());}
        catch(Exception ex){_hub.ServerId="";Logger.Error("Library identity unavailable: {0}",ex.GetType().Name);}
        try {_resume.Start(checked(LanConfig.Port+4));_hub.ResumePort=LanConfig.Port+4;_hub.ServerId=_resume.ServerId;}
        catch(Exception ex){_hub.ResumePort=0;Logger.Error("Resume service unavailable: {0}",ex.GetType().Name);}
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
        _hub.ClientAuthenticated -= OnClientAuthenticated;
        _fileServer.FileUploaded -= OnFileUploaded;
        _fileServer.FileCollected -= OnFileCollected;
        _hub.Stop();
        _fileServer.Stop();
        _resume.Dispose();
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
    private void OnFileCollected(string path, string name, string source, string device) => FileCollected?.Invoke(path, name, source, device);

    private void OnClientCountChanged(int count)
    {
        ClientCountChanged?.Invoke(count);
    }
    private void OnClientAuthenticated() => ClientAuthenticated?.Invoke();

    public void Dispose()
    {
        Stop();
    }
}
