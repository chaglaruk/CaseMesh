using System.Threading.Channels;
using HRCompanion.Core.Abstractions;

namespace HRCompanion.Infrastructure.OpenAI;

internal sealed class AudioFrameSendPump : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly int _capacity;
    private readonly Channel<AudioFrame> _channel;
    private readonly Func<AudioFrame, CancellationToken, Task<bool>> _send;
    private readonly CancellationTokenSource _stopCts = new();
    private Task? _worker;
    private long _accepted;
    private long _sent;
    private long _dropped;
    private int _depth;
    private int _highWaterMark;
    private bool _hasGap;
    private bool _completed;

    public AudioFrameSendPump(int capacity, Func<AudioFrame, CancellationToken, Task<bool>> send)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        _send = send;
        _channel = Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public event EventHandler<TranscriberDiagnostics>? DiagnosticsChanged;

    public TranscriberDiagnostics Diagnostics
    {
        get
        {
            lock (_sync) return SnapshotLocked();
        }
    }

    public void Start()
    {
        lock (_sync) _worker ??= RunAsync(_stopCts.Token);
    }

    public bool TryEnqueue(AudioFrame frame)
    {
        TranscriberDiagnostics diagnostics;
        bool accepted;
        lock (_sync)
        {
            if (_completed) return false;

            if (_depth >= _capacity && _channel.Reader.TryRead(out _))
            {
                _depth--;
                _dropped++;
                _hasGap = true;
            }

            accepted = _channel.Writer.TryWrite(frame);
            if (accepted)
            {
                _accepted++;
                _depth++;
                _highWaterMark = Math.Max(_highWaterMark, _depth);
            }
            else
            {
                _dropped++;
                _hasGap = true;
            }
            diagnostics = SnapshotLocked();
        }
        DiagnosticsChanged?.Invoke(this, diagnostics);
        return accepted;
    }

    public async Task StopAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task? worker;
        lock (_sync)
        {
            if (!_completed)
            {
                _completed = true;
                _channel.Writer.TryComplete();
            }
            worker = _worker;
        }
        if (worker is null) return;

        try
        {
            await worker.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _stopCts.Cancel();
            try { await worker.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                AudioFrame frame;
                lock (_sync)
                {
                    if (!_channel.Reader.TryRead(out var queued)) continue;
                    frame = queued!;
                    _depth--;
                }

                var sent = false;
                try { sent = await _send(frame, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }

                TranscriberDiagnostics diagnostics;
                lock (_sync)
                {
                    if (sent) _sent++;
                    else
                    {
                        _dropped++;
                        _hasGap = true;
                    }
                    diagnostics = SnapshotLocked();
                }
                DiagnosticsChanged?.Invoke(this, diagnostics);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private TranscriberDiagnostics SnapshotLocked() => new(
        _accepted,
        _sent,
        _dropped,
        _depth,
        _highWaterMark,
        _hasGap);

    public async ValueTask DisposeAsync()
    {
        await StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        _stopCts.Cancel();
        _stopCts.Dispose();
    }
}

internal sealed class ReconnectRetryBudget
{
    private readonly int _maximumAttempts;

    public ReconnectRetryBudget(int maximumAttempts)
    {
        if (maximumAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        _maximumAttempts = maximumAttempts;
    }

    public int AttemptsUsed { get; private set; }
    public bool TryUseAttempt()
    {
        if (AttemptsUsed >= _maximumAttempts) return false;
        AttemptsUsed++;
        return true;
    }

    public void Reset() => AttemptsUsed = 0;
}
