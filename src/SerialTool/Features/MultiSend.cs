using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SerialTool.Features;

/// <summary>多项发送列表中的一条。</summary>
public sealed class MultiSendItem
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public string Content { get; set; } = "";
    public bool Hex { get; set; }
    /// <summary>ChecksumKind 名称；None = 不加校验。</summary>
    public string Checksum { get; set; } = "None";
    /// <summary>本条发送后、下一条之前等待的毫秒数。</summary>
    public int DelayMs { get; set; } = 100;

    public MultiSendItem Clone() => (MultiSendItem)MemberwiseClone();

    public void Normalize()
    {
        Name ??= "";
        Content ??= "";
        Checksum ??= "None";
        if (DelayMs < 0) DelayMs = 0;
        if (DelayMs > 3_600_000) DelayMs = 3_600_000;
    }
}

public enum MultiSendState { Idle, Running, Paused }

/// <summary>
/// 多项发送引擎（WPF 无关）：按勾选顺序发送，条目间延时，可循环 / 暂停 / 继续 / 停止。
/// 暂停会卡住当前延时或下一次发送前；继续从卡住的位置往下走；停止重置到空闲。
/// </summary>
public sealed class MultiSendEngine
{
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private ManualResetEventSlim _pauseGate = new(true);
    private Task? _task;
    private volatile bool _pauseRequested;
    private int _currentIndex = -1;
    private MultiSendState _state = MultiSendState.Idle;

    /// <summary>可替换的延时（测试可立即返回）。默认 Thread.Sleep 风格 WaitHandle。</summary>
    public Func<int, CancellationToken, bool>? DelayFn { get; set; }

    public MultiSendState State
    {
        get { lock (_lock) return _state; }
        private set
        {
            lock (_lock) _state = value;
            StateChanged?.Invoke(value);
        }
    }

    /// <summary>当前正在发送或刚发送完、正在延时的条目在列表中的下标；空闲为 -1。</summary>
    public int CurrentIndex
    {
        get { lock (_lock) return _currentIndex; }
        private set
        {
            lock (_lock) _currentIndex = value;
            CurrentChanged?.Invoke(value);
        }
    }

    public int LoopsCompleted { get; private set; }
    public int SendsCompleted { get; private set; }
    public bool IsBusy => State is MultiSendState.Running or MultiSendState.Paused;

    public event Action<MultiSendState>? StateChanged;
    public event Action<int>? CurrentChanged;
    /// <summary>即将发送某条目（列表下标）。</summary>
    public event Action<int, MultiSendItem>? ItemSending;
    /// <summary>一条发送完成（成功）。</summary>
    public event Action<int, MultiSendItem>? ItemSent;
    /// <summary>整次运行结束（自然结束 / 停止 / 发送失败）。error 非空表示失败。</summary>
    public event Action<string?>? Finished;

    /// <summary>
    /// 启动。send 返回 null 表示成功，非空错误信息则停止。
    /// items 快照在调用时固定；运行中改列表不影响本次序列（勾选状态以启动时为准）。
    /// </summary>
    public Task StartAsync(IReadOnlyList<MultiSendItem> items, bool loop, Func<MultiSendItem, string?> send, CancellationToken external = default)
    {
        if (items == null) throw new ArgumentNullException(nameof(items));
        if (send == null) throw new ArgumentNullException(nameof(send));
        lock (_lock)
        {
            if (_task != null && !_task.IsCompleted) throw new InvalidOperationException("多项发送已在运行");
            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(external);
            _pauseGate.Set();
            _pauseRequested = false;
            LoopsCompleted = 0;
            SendsCompleted = 0;
            CurrentIndex = -1;
            State = MultiSendState.Running;
            var ct = _cts.Token;
            var snap = items.Select(x => x.Clone()).ToList();
            _task = Task.Run(() => Run(snap, loop, send, ct), CancellationToken.None);
            return _task;
        }
    }

    public void Pause()
    {
        if (State != MultiSendState.Running) return;
        _pauseRequested = true;
        _pauseGate.Reset();
        State = MultiSendState.Paused;
    }

    public void Resume()
    {
        if (State != MultiSendState.Paused) return;
        _pauseRequested = false;
        State = MultiSendState.Running;
        _pauseGate.Set();
    }

    public void Stop()
    {
        var cts = _cts;
        if (cts == null) return;
        try { cts.Cancel(); } catch { /* ignore */ }
        _pauseGate.Set();
    }

    /// <summary>等待当前任务结束（测试用）。</summary>
    public Task WaitAsync() => _task ?? Task.CompletedTask;

    private void Run(List<MultiSendItem> items, bool loop, Func<MultiSendItem, string?> send, CancellationToken ct)
    {
        string? err = null;
        try
        {
            var enabled = new List<int>();
            for (int i = 0; i < items.Count; i++)
                if (items[i].Enabled && !string.IsNullOrEmpty(items[i].Content))
                    enabled.Add(i);
            if (enabled.Count == 0)
            {
                err = "没有勾选且内容非空的条目";
                return;
            }

            int pos = 0;
            while (!ct.IsCancellationRequested)
            {
                WaitIfPaused(ct);
                if (ct.IsCancellationRequested) break;

                int idx = enabled[pos];
                var item = items[idx];
                CurrentIndex = idx;
                ItemSending?.Invoke(idx, item);
                err = send(item);
                if (err != null) return;
                SendsCompleted++;
                ItemSent?.Invoke(idx, item);

                // 延时：最后一条且不循环时也延时（用户可把 Delay 当“发送后等待”）
                int delay = Math.Max(0, item.DelayMs);
                // 下一条
                int nextPos = pos + 1;
                bool wrap = nextPos >= enabled.Count;
                if (wrap)
                {
                    LoopsCompleted++;
                    if (!loop) break;
                    nextPos = 0;
                }
                pos = nextPos;

                if (delay > 0)
                {
                    if (!DoDelay(delay, ct)) break; // cancelled
                }
                else
                {
                    WaitIfPaused(ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stop
        }
        catch (Exception ex)
        {
            err = ex.Message;
        }
        finally
        {
            CurrentIndex = -1;
            State = MultiSendState.Idle;
            Finished?.Invoke(ct.IsCancellationRequested && err == null ? null : err);
            // 若是用户停止，Finished 传 null（正常停止）；发送失败传错误
            // 上面逻辑：取消且无 err → null；有 err → err；自然结束 → null
        }
    }

    private void WaitIfPaused(CancellationToken ct)
    {
        while (_pauseRequested && !ct.IsCancellationRequested)
        {
            State = MultiSendState.Paused;
            _pauseGate.Wait(50);
            ct.ThrowIfCancellationRequested();
        }
        if (!ct.IsCancellationRequested && State == MultiSendState.Paused)
            State = MultiSendState.Running;
    }

    /// <summary>返回 false 表示被取消。</summary>
    private bool DoDelay(int ms, CancellationToken ct)
    {
        var fn = DelayFn;
        if (fn != null) return fn(ms, ct);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (ct.IsCancellationRequested) return false;
            WaitIfPaused(ct);
            if (ct.IsCancellationRequested) return false;
            int left = ms - (int)sw.ElapsedMilliseconds;
            if (left <= 0) break;
            if (ct.WaitHandle.WaitOne(Math.Min(left, 50))) return false;
        }
        return !ct.IsCancellationRequested;
    }
}
