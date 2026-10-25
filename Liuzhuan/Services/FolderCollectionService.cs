using System.IO;
using System.Text.Json;

namespace Liuzhuan.Services;

public sealed record FolderSubscription
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Path { get; init; } = "";
    public bool Enabled { get; set; } = true;
    public Dictionary<string, string> Seen { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Explicit, nonrecursive source subscriptions. Event driven; manual scan recovers missed events.</summary>
public sealed class FolderCollectionService : IDisposable
{
    private readonly string _config;
    private readonly string[] _excluded;
    private readonly Func<string, Task> _collect;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<FolderSubscription> _folders = new();
    private int _scanScheduled;
    private int _rescan;
    private readonly object _watcherGate = new();
    public string Status { get; private set; } = "仅订阅已选择目录的直接子文件；原文件保持原位。";
    public string? LoadError { get; private set; }
    public FolderCollectionService(string directory, Action<string> collect)
        : this(directory, path => { collect(path); return Task.CompletedTask; }) { }
    public FolderCollectionService(string directory, Func<string, Task> collect)
    {
        _config = System.IO.Path.Combine(directory, "collection-folders.json");
        _excluded = new[] { System.IO.Path.GetFullPath(directory), System.IO.Path.GetFullPath(AppContext.BaseDirectory) };
        _collect = collect;
        try
        {
            if (File.Exists(_config))
            {
                if(new FileInfo(_config).Length>32*1024*1024)throw new InvalidDataException();
                var items = JsonSerializer.Deserialize<List<FolderSubscription>>(File.ReadAllText(_config)) ?? throw new InvalidDataException();
                if (items.Count > 10 || items.Any(x => x.Seen == null || x.Seen.Count > 5000)) throw new InvalidDataException();
                foreach (var item in items) { ValidatePath(item.Path); _folders.Add(item with { Seen=new(item.Seen,StringComparer.OrdinalIgnoreCase) }); }
            }
        }
        catch { LoadError = "目录订阅配置无法读取，原文件已保留。"; Status = LoadError; _folders.Clear(); }
    }
    public List<FolderSubscription> Snapshot() { lock (_folders) return _folders.Select(x => x with { Seen = new() }).ToList(); }
    public void Start() { Rewatch(); QueueScan(); }
    private void ValidatePath(string path)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd('\\');
        bool Related(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) || a.StartsWith(b.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase) || b.StartsWith(a+"\\",StringComparison.OrdinalIgnoreCase);
        if (full.Length <= 3 || full.StartsWith("\\\\") || _excluded.Any(x => Related(full, x))) throw new ArgumentException("请选择独立的本地来源文件夹，不能订阅磁盘根目录、流转程序或素材库及其上级目录。");
        for(var ancestor=new DirectoryInfo(path);ancestor!=null;ancestor=ancestor.Parent)
            if(ancestor.Exists && (ancestor.Attributes & FileAttributes.ReparsePoint)!=0)throw new ArgumentException("不支持链接或重解析目录。");
    }
    private static string Stamp(FileInfo file) => $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
    private static List<FileInfo> Files(string path) => new DirectoryInfo(path).EnumerateFiles().Where(x => (x.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden)) == 0 && !new[] { ".part", ".tmp", ".crdownload" }.Contains(x.Extension.ToLowerInvariant())).Take(5001).ToList();
    public async Task AddAsync(string path)
    {
        await _gate.WaitAsync();
        try
        {
            if (LoadError != null) throw new InvalidOperationException(LoadError);
            ValidatePath(path);
            if (!Directory.Exists(path)) throw new ArgumentException("来源文件夹不存在。");
            var full = System.IO.Path.GetFullPath(path);
            if (_folders.Count >= 10 || _folders.Any(x => x.Path.Equals(full, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("已订阅该目录，或已达到 10 个目录上限。");
            var files = Files(full);
            if (files.Count > 5000) throw new ArgumentException("目录直接子文件超过 5000 个，请选择更小的来源文件夹。");
            var item = new FolderSubscription { Path = full, Seen = files.ToDictionary(x => x.Name, Stamp, StringComparer.OrdinalIgnoreCase) };
            lock (_folders) _folders.Add(item);
            try { Save(); } catch { lock (_folders) _folders.Remove(item); throw; }
            Status = "订阅已开启；现有文件已跳过，之后新增或更改的文件会收集。"; Rewatch();
        }
        finally { _gate.Release(); }
    }
    public async Task RemoveAsync(string id)
    {
        await _gate.WaitAsync();
        try { if (LoadError != null) throw new InvalidOperationException(LoadError); var item = _folders.FirstOrDefault(x => x.Id == id); if (item == null) return; lock (_folders) _folders.Remove(item); try { Save(); } catch { lock (_folders) _folders.Add(item); throw; } Rewatch(); }
        finally { _gate.Release(); }
    }
    private void Save()
    {
        var tmp = _config+".tmp";
        var encoded=JsonSerializer.SerializeToUtf8Bytes(_folders);
        if(encoded.Length>32*1024*1024)throw new InvalidOperationException("订阅记录超过容量，请减少来源目录。");
        try { File.WriteAllBytes(tmp, encoded); File.Move(tmp, _config, true); }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
    private void Rewatch()
    {
        lock (_watcherGate) {
        foreach (var w in _watchers) w.Dispose(); _watchers.Clear();
        if (_lifetime.IsCancellationRequested) return;
        foreach (var folder in _folders.Where(x => x.Enabled))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder.Path) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
                watcher.Created += (_,_) => QueueScan(); watcher.Renamed += (_,_) => QueueScan(); watcher.Changed += (_,_) => QueueScan(); watcher.Error += (_,_) => { Status = "目录通知中断，请点检查新文件。"; QueueScan(); };
                watcher.EnableRaisingEvents = true; _watchers.Add(watcher);
            }
            catch { Status = "部分目录不可用，请恢复目录后重新打开软件或检查新文件。"; }
        }
        }
    }
    private void QueueScan()
    {
        Interlocked.Exchange(ref _rescan, 1);
        if (_lifetime.IsCancellationRequested || Interlocked.Exchange(ref _scanScheduled, 1) != 0) return;
        _ = Task.Run(async () => {
            try { do { Interlocked.Exchange(ref _rescan,0); await Task.Delay(1500, _lifetime.Token); await ScanAsync(); } while(Volatile.Read(ref _rescan)!=0 && !_lifetime.IsCancellationRequested); }
            catch (OperationCanceledException) { }
            catch { Status="目录检查失败，请手动检查新文件。"; }
            finally { Interlocked.Exchange(ref _scanScheduled,0);if(Volatile.Read(ref _rescan)!=0 && !_lifetime.IsCancellationRequested)QueueScan(); }
        });
    }
    public Task ScanAsync() => Task.Run(async () =>
    {
        await _gate.WaitAsync(_lifetime.Token);
        try
        {
            var count = 0;
            foreach (var folder in _folders.Where(x => x.Enabled).ToList())
            {
                try
                {
                    ValidatePath(folder.Path);
                    var files = Files(folder.Path);
                    if (files.Count > 5000) throw new IOException("目录超过 5000 个文件，已跳过。");
                    var current = files.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var old in folder.Seen.Keys.Where(x => !current.Contains(x)).ToList()) folder.Seen.Remove(old);
                    var changed = files.Where(file => !folder.Seen.TryGetValue(file.Name,out var old) || old!=Stamp(file)).Select(file=>(File:file,Stamp:Stamp(file))).ToList();
                    if(changed.Count>32)QueueScan();
                    if(changed.Count>0)await Task.Delay(700,_lifetime.Token);
                    foreach (var candidate in changed.Take(32))
                    {
                        _lifetime.Token.ThrowIfCancellationRequested();
                        var file=candidate.File;var stamp=candidate.Stamp; file.Refresh();
                        if((file.Attributes&FileAttributes.ReparsePoint)!=0)continue;
                        if (!file.Exists || file.Length == 0 || Stamp(file) != stamp) { QueueScan(); continue; }
                        using (File.Open(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                        // A successful callback is a durable registration acknowledgement.
                        // Never persist Seen ahead of the material index: a crash must retry safely.
                        await _collect(file.FullName);
                        folder.Seen[file.Name] = stamp; Save(); count++;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { Status = "部分目录无法检查或文件仍被占用，请稍后点检查新文件。"; }
            }
            if (count > 0) Status = $"已收集 {count} 个新增或更改的文件。";
        }
        finally { _gate.Release(); }
    }, _lifetime.Token);
    public void Dispose() { _lifetime.Cancel(); lock(_watcherGate) { foreach (var w in _watchers) w.Dispose(); _watchers.Clear(); } }
}
