using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Liuzhuan.Services.Lan;

/// <summary>Write intent -> persist material -> persist receipt. Pending intents replay with a stable material ID.</summary>
public sealed class DurableInbox
{
    public sealed record Receipt(string Key,string Fingerprint,bool Stored,DateTime CreatedUtc);
    private sealed class KeyGate { public readonly SemaphoreSlim Gate=new(1); public int Users; }
    private sealed class Lease:IDisposable
    {
        public SemaphoreSlim Gate {get;}
        private readonly Action _release;
        public Lease(SemaphoreSlim gate,Action release){Gate=gate;_release=release;}
        public void Dispose()=>_release();
    }
    private sealed class DirectoryState
    {
        public readonly SemaphoreSlim Gate=new(1);
        public readonly Dictionary<string,KeyGate> Keys=new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string,DateTime> Times=new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedSet<(DateTime At,string Path)> Expiry=new(Comparer<(DateTime At,string Path)>.Create((a,b)=>{
            var order=a.At.CompareTo(b.At);return order!=0?order:StringComparer.OrdinalIgnoreCase.Compare(a.Path,b.Path);
        }));
        public readonly Func<DateTime> Now;
        public readonly int Capacity;
        public bool Indexed;
        public DateTime LastScan,LastPressure;
        public long Scans;
        public DirectoryState(Func<DateTime> now,int capacity){Now=now;Capacity=capacity;}
    }
    // Text WS and HTTP files share a directory and budget. Retired library states are weakly retained.
    private static readonly Dictionary<string,WeakReference<DirectoryState>> Directories=new(StringComparer.OrdinalIgnoreCase);
    private readonly DirectoryState _state;
    private readonly string _root;
    public long DirectoryScanCount=>Interlocked.Read(ref _state.Scans);
    public DurableInbox(string root,Func<DateTime>? utcNow=null,int capacity=10000)
    {
        if(capacity is <1 or >10000)throw new ArgumentOutOfRangeException(nameof(capacity));
        _root=Path.GetFullPath(Path.Combine(root,"receipts"));Directory.CreateDirectory(_root);
        lock(Directories) {
            foreach(var dead in Directories.Where(x=>!x.Value.TryGetTarget(out _)).Select(x=>x.Key).ToArray())Directories.Remove(dead);
            if(!Directories.TryGetValue(_root,out var weak)||!weak.TryGetTarget(out var current)) {
                current=new(utcNow??(()=>DateTime.UtcNow),capacity);Directories[_root]=new(current);
            }
            if(current.Capacity!=capacity)throw new ArgumentException("Receipt capacity differs for an active library.",nameof(capacity));
            _state=current;
        }
    }
    private static string Id(string key)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    private Lease Rent(string id)
    {
        lock(_state.Keys) {
            if(!_state.Keys.TryGetValue(id,out var node))_state.Keys[id]=node=new();
            node.Users++;
            return new(node.Gate,()=>{lock(_state.Keys){if(--node.Users==0){_state.Keys.Remove(id);node.Gate.Dispose();}}});
        }
    }
    private void Track(string path,DateTime at)
    {
        if(_state.Times.TryGetValue(path,out var previous))_state.Expiry.Remove((previous,path));
        _state.Times[path]=at;_state.Expiry.Add((at,path));
    }
    private void Forget(string path)
    {
        if(_state.Times.Remove(path,out var previous))_state.Expiry.Remove((previous,path));
    }
    private static Receipt? ReadReceipt(string path)
    {
        var info=new FileInfo(path);if(!info.Exists)return null;
        if(info.Length>65536)throw new InvalidDataException("Receipt record exceeds its read budget; the original has been retained.");
        var receipt=DurableJson.Read<Receipt>(path);
        if(receipt==null||string.IsNullOrEmpty(receipt.Key)||string.IsNullOrEmpty(receipt.Fingerprint)||
            receipt.CreatedUtc==default||!string.Equals(Path.GetFileName(path),Id(receipt.Key)+".json",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Receipt record is unreadable; the original has been retained.");
        return receipt;
    }
    // Called under the directory gate. No periodic task or watcher is needed.
    private void Refresh(DateTime now,bool pressure=false)
    {
        var due=!_state.Indexed||now<_state.LastScan||now-_state.LastScan>=TimeSpan.FromMinutes(1);
        var pressured=pressure&&(now<_state.LastPressure||now-_state.LastPressure>=TimeSpan.FromSeconds(1));
        if(!due&&!pressured)return;
        var files=new DirectoryInfo(_root).GetFiles("*.json");
        _state.Times.Clear();_state.Expiry.Clear();
        foreach(var file in files)if(file.Exists)Track(file.FullName,file.LastWriteTimeUtc);
        _state.Indexed=true;_state.LastScan=now;if(pressure)_state.LastPressure=now;
        Interlocked.Increment(ref _state.Scans);
        var cutoff=now.AddDays(-8);
        foreach(var candidate in _state.Expiry.TakeWhile(x=>x.At<cutoff).ToArray()) {
            var id=Path.GetFileNameWithoutExtension(candidate.Path);
            using var lease=Rent(id);
            if(!lease.Gate.Wait(0))continue; // Never wait for a registration while holding the directory gate.
            try {
                var info=new FileInfo(candidate.Path);
                if(!info.Exists){Forget(candidate.Path);continue;}
                if(info.LastWriteTimeUtc>=cutoff){Track(candidate.Path,info.LastWriteTimeUtc);continue;}
                var length=info.Length;var modified=info.LastWriteTimeUtc;
                var receipt=ReadReceipt(candidate.Path);
                if(receipt==null){Forget(candidate.Path);continue;}
                if(receipt.CreatedUtc>=cutoff){Track(candidate.Path,receipt.CreatedUtc);continue;}
                info.Refresh();
                if(!info.Exists){Forget(candidate.Path);continue;}
                if(info.Length!=length||info.LastWriteTimeUtc!=modified){Track(candidate.Path,info.LastWriteTimeUtc);continue;}
                info.Delete();Forget(candidate.Path);
            }catch { /* Invalid or unreadable records remain capacity-consuming recovery evidence. */ }
            finally {lease.Gate.Release();}
        }
    }
    public async Task RegisterAsync(string key,string fingerprint,Func<string,Task> register)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);ArgumentException.ThrowIfNullOrEmpty(fingerprint);
        if(key.Length>4096||fingerprint.Length>4096)throw new ArgumentException("Receipt identity exceeds its write budget.");
        var id=Id(key);var path=Path.Combine(_root,id+".json");
        using var lease=Rent(id);await lease.Gate.WaitAsync();
        try {
            Receipt receipt;
            await _state.Gate.WaitAsync();
            try {
                var now=_state.Now();Refresh(now);
                var old=ReadReceipt(path);
                if(old!=null&&old.Fingerprint!=fingerprint)throw new InvalidOperationException("request_conflict");
                if(old!=null)Track(path,File.GetLastWriteTimeUtc(path));
                if(old?.Stored==true)return;
                if(old==null) {
                    Forget(path);
                    if(_state.Times.Count>=_state.Capacity)Refresh(now,pressure:true);
                    if(_state.Times.Count>=_state.Capacity)throw new IOException("receipt_capacity");
                    receipt=new(key,fingerprint,false,now);DurableJson.Write(path,receipt);
                    Track(path,File.GetLastWriteTimeUtc(path));
                }else receipt=old;
            }finally{_state.Gate.Release();}
            // Serialize only the same request while UI/registration work is pending.
            await register(id);
            await _state.Gate.WaitAsync();
            try {
                var current=ReadReceipt(path)??throw new IOException("Receipt intent disappeared before confirmation.");
                if(current.Key!=receipt.Key||current.Fingerprint!=receipt.Fingerprint)throw new IOException("Receipt intent changed before confirmation.");
                DurableJson.Write(path,receipt with{Stored=true});Track(path,File.GetLastWriteTimeUtc(path));
            }finally{_state.Gate.Release();}
        }finally{lease.Gate.Release();}
    }
}
