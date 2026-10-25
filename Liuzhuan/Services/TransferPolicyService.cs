using System.Diagnostics;
using System.IO;

namespace Liuzhuan.Services;

public sealed record TransferPolicyOptions(string Priority="balanced",int SendMiB=0,int ReceiveMiB=0,bool YieldToAudio=false)
{
    public TransferPolicyOptions Normalized()=>this with {Priority=Priority is "send" or "receive"?Priority:"balanced",SendMiB=Math.Clamp(SendMiB,0,100),ReceiveMiB=Math.Clamp(ReceiveMiB,0,100)};
}

/// <summary>Shared file payload budgets. Protocol controls, durable checkpoints and audio stay outside.</summary>
public static class TransferPolicyService
{
    private static readonly object Gate=new();
    private static readonly SemaphoreSlim SaveGate=new(1);
    private static TransferPolicyOptions _current=new();
    private static string _loadedRoot="";
    public static string Error {get;private set;}="";
    public static Func<bool> AudioActiveProvider {get;set;}=()=>false;
    public static TransferPolicyOptions Current {get {lock(Gate)return _current;}}
    public static void EnsureLoaded()
    {
        var root=ConfigService.GetEffectiveDataDir();
        lock(Gate) {
            if(_loadedRoot==root)return;
            try {_current=(DurableJson.Read<TransferPolicyOptions>(Path.Combine(root,"transfer-policy.json"))??new()).Normalized();Error="";}
            catch {Error="传输偏好无法读取，原文件已保留；当前使用不限速。";_current=new();}
            _loadedRoot=root;
        }
    }
    public static async Task SaveAsync(TransferPolicyOptions options)
    {
        await SaveGate.WaitAsync();
        try {
            var normalized=options.Normalized();var root=ConfigService.GetEffectiveDataDir();
            await Task.Run(()=>DurableJson.Write(Path.Combine(root,"transfer-policy.json"),normalized));
            lock(Gate){_current=normalized;_loadedRoot=root;Error="";}
        }finally{SaveGate.Release();}
    }
    public static int Limit(bool sending)
    {
        var options=Current;var saved=sending?options.SendMiB:options.ReceiveMiB;
        return options.YieldToAudio && AudioActiveProvider()?(saved==0?1:Math.Min(1,saved)):saved;
    }
    public static string Summary {
        get {string Rate(int value)=>value==0?"不限速":$"{value} MiB/s";
            var options=Current;return $"{(options.Priority=="send"?"发送优先":options.Priority=="receive"?"接收优先":"按加入顺序")} · 发送{Rate(Limit(true))} · 接收{Rate(Limit(false))} · 等待数据块 {Queued}"+
                (options.YieldToAudio && AudioActiveProvider()?" · 正为麦克风让路":"");}
    }
    private sealed class Bucket {public double Tokens;public long At=Stopwatch.GetTimestamp();public int Rate;}
    private static readonly Bucket Send=new(),Receive=new();
    public static async Task PayloadAsync(bool sending,int bytes,CancellationToken token)
    {
        var bucket=sending?Send:Receive;var remaining=bytes;
        while(remaining>0) {
            token.ThrowIfCancellationRequested();var cap=Limit(sending);if(cap==0)return;
            int take;
            lock(bucket) {
                var at=Stopwatch.GetTimestamp();if(bucket.Rate!=cap){bucket.Tokens=0;bucket.At=at;bucket.Rate=cap;}
                bucket.Tokens=Math.Min(65536,bucket.Tokens+Stopwatch.GetElapsedTime(bucket.At,at).TotalSeconds*cap*1048576.0);bucket.At=at;
                take=Math.Min(remaining,(int)bucket.Tokens);bucket.Tokens-=take;
            }
            remaining-=take;if(remaining>0)await Task.Delay(10,token);
        }
    }
    private sealed record Waiter(bool Sending,long At,long Sequence,TaskCompletionSource<bool> Completion);
    private static readonly List<Waiter> Waiting=new();
    private static int _active;
    private static long _sequence;
    public static int Queued {get {lock(Waiting)return Waiting.Count;}}
    private static void Drain()
    {
        while(_active<2 && Waiting.Count>0) {
            var priority=Current.Priority;
            var next=Waiting.OrderByDescending(x=>Environment.TickCount64-x.At>=30000?2:(x.Sending && priority=="send" || !x.Sending && priority=="receive")?1:0).ThenBy(x=>x.Sequence).First();
            Waiting.Remove(next);if(next.Completion.TrySetResult(true))_active++;
        }
    }
    public static async Task<IDisposable> EnterPayloadAsync(bool sending,CancellationToken token)
    {
        var waiter=new Waiter(sending,Environment.TickCount64,Interlocked.Increment(ref _sequence),new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock(Waiting){Waiting.Add(waiter);Drain();}
        try {await waiter.Completion.Task.WaitAsync(token);token.ThrowIfCancellationRequested();return new Lease();}
        catch {lock(Waiting){if(waiter.Completion.Task.IsCompletedSuccessfully)_active--;else Waiting.Remove(waiter);Drain();}throw;}
    }
    private sealed class Lease:IDisposable {private int _disposed;public void Dispose(){if(Interlocked.Exchange(ref _disposed,1)!=0)return;lock(Waiting){_active--;Drain();}}}
}
