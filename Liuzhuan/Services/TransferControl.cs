namespace Liuzhuan.Services;

/// <summary>当前 HTTP 连接的读写门闩；暂停不承诺跨断线或进程恢复。</summary>
public sealed class TransferControl : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cts;
    private TaskCompletionSource<bool>? _resume;
    private readonly Action _close;
    private readonly Action _changed;
    private readonly CancellationToken _token;
    private bool _disposed;
    private readonly TimeSpan _pauseTimeout;
    private Timer? _pauseTimer;
    private bool _pauseExpired;
    public bool IsPauseExpired { get { lock(_gate) return _pauseExpired; } }
    public bool IsPaused { get { lock(_gate) return _resume != null; } }
    public bool IsCancelled { get; private set; }
    public CancellationToken Token => _token;
    public TransferControl(Action close, Action changed, CancellationToken lifetime=default, TimeSpan? pauseTimeout=null) {
        _close=close;_changed=changed;_cts=CancellationTokenSource.CreateLinkedTokenSource(lifetime);_token=_cts.Token;
        _pauseTimeout=pauseTimeout ?? TimeSpan.FromSeconds(90);
    }
    public void TogglePause()
    {
        lock(_gate) {
            if(_disposed || IsCancelled || Token.IsCancellationRequested) return;
            if(_resume == null) {
                _resume=new(TaskCreationOptions.RunContinuationsAsynchronously);
                var lease=_resume;
                // 从点击暂停计时，也能结束已经阻塞在网络 read/write 的当前块。
                _pauseTimer=new Timer(_=>Expire(lease),null,_pauseTimeout,Timeout.InfiniteTimeSpan);
            }
            else { _pauseTimer?.Dispose();_pauseTimer=null;_resume.TrySetResult(true);_resume=null; }
        }
        _changed();
    }
    public async Task WaitAsync()
    {
        Task? pending;
        lock(_gate) { Token.ThrowIfCancellationRequested();pending=_resume?.Task; }
        try {
            if(pending != null) await pending.WaitAsync(Token);
            Token.ThrowIfCancellationRequested();
        } catch(OperationCanceledException) when(IsPauseExpired) {throw new TimeoutException("暂停时间已到");}
    }
    private void Expire(TaskCompletionSource<bool> lease) {
        lock(_gate) {
            if(_disposed || IsCancelled || !ReferenceEquals(_resume,lease)) return;
            _pauseExpired=true;_cts.Cancel();
        }
        try {_close();} catch { }
    }
    public void Cancel()
    {
        lock(_gate) { if(_disposed || IsCancelled) return;IsCancelled=true;_cts.Cancel(); }
        try { _close(); } catch { }
        _changed();
    }
    public void Dispose() { lock(_gate) { if(_disposed) return;_disposed=true;_pauseTimer?.Dispose();_pauseTimer=null;_resume?.TrySetResult(true);_resume=null;_cts.Dispose(); } }
}
