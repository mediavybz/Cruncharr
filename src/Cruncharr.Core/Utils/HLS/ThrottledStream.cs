using System;
using System.IO;
using System.Threading;

namespace Cruncharr.Core.Utils.HLS;

public class GlobalThrottler
{
    private static GlobalThrottler? _instance;
    private static readonly object _lock = new object();
    private long _totalBytesRead;
    private DateTime _lastReadTime;
    private int _downloadSpeedLimit;

    private GlobalThrottler()
    {
        _totalBytesRead = 0;
        _lastReadTime = DateTime.Now;
        _downloadSpeedLimit = 0;
    }

    public static GlobalThrottler Instance()
    {
        if (_instance == null)
        {
            lock (_lock)
            {
                if (_instance == null)
                {
                    _instance = new GlobalThrottler();
                }
            }
        }

        return _instance;
    }

    public void SetSpeedLimit(int limitKbPerSecond)
    {
        lock (_lock)
        {
            _downloadSpeedLimit = limitKbPerSecond;
        }
    }

    public void Throttle(int bytesRead)
    {
        int limit;
        lock (_lock)
        {
            limit = _downloadSpeedLimit;
        }

        if (limit == 0) return;

        lock (_lock)
        {
            _totalBytesRead += bytesRead;
            if (_totalBytesRead >= ((limit * 1024) / 10))
            {
                var timeElapsed = DateTime.Now - _lastReadTime;
                if (timeElapsed.TotalMilliseconds < 100)
                {
                    // Use SpinWait for short delays to avoid thread pool starvation in async paths
                    var delayMs = 100 - (int)timeElapsed.TotalMilliseconds;
                    if (delayMs > 30)
                    {
                        // For longer delays, use Thread.Sleep but release the lock first
                        Monitor.Exit(_lock);
                        try
                        {
                            Thread.Sleep(delayMs);
                        }
                        finally
                        {
                            Monitor.Enter(_lock);
                        }
                    }
                    else
                    {
                        Thread.SpinWait(100 * delayMs);
                    }
                }

                _totalBytesRead = 0;
                _lastReadTime = DateTime.Now;
            }
        }
    }

    public async Task ThrottleAsync(int bytesRead, CancellationToken cancellationToken = default)
    {
        int limit;
        lock (_lock)
        {
            limit = _downloadSpeedLimit;
        }

        if (limit == 0) return;

        long totalBytes;
        DateTime lastRead;
        lock (_lock)
        {
            _totalBytesRead += bytesRead;
            totalBytes = _totalBytesRead;
            lastRead = _lastReadTime;
        }

        if (totalBytes >= ((limit * 1024) / 10))
        {
            var timeElapsed = DateTime.Now - lastRead;
            if (timeElapsed.TotalMilliseconds < 100)
            {
                var delayMs = 100 - (int)timeElapsed.TotalMilliseconds;
                await Task.Delay(delayMs, cancellationToken);
            }

            lock (_lock)
            {
                _totalBytesRead = 0;
                _lastReadTime = DateTime.Now;
            }
        }
    }
}

public class ThrottledStream : Stream
{
    private readonly Stream _baseStream;
    private readonly GlobalThrottler _throttler;
    private int _downloadSpeedLimit;

    public ThrottledStream(Stream baseStream, int downloadSpeedLimit = 0)
    {
        _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
        _throttler = GlobalThrottler.Instance();
        _downloadSpeedLimit = downloadSpeedLimit;
    }

    public override bool CanRead => _baseStream.CanRead;
    public override bool CanSeek => _baseStream.CanSeek;
    public override bool CanWrite => _baseStream.CanWrite;
    public override long Length => _baseStream.Length;

    public override long Position
    {
        get => _baseStream.Position;
        set => _baseStream.Position = value;
    }

    public override void Flush() => _baseStream.Flush();

    public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);

    public override void SetLength(long value) => _baseStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => _baseStream.Write(buffer, offset, count);

    public override int Read(byte[] buffer, int offset, int count)
    {
        int bytesRead = 0;
        if (_downloadSpeedLimit != 0)
        {
            int bytesToRead = Math.Min(count, (_downloadSpeedLimit * 1024) / 10);
            bytesRead = _baseStream.Read(buffer, offset, bytesToRead);
            _throttler.Throttle(bytesRead);
        }
        else
        {
            bytesRead = _baseStream.Read(buffer, offset, count);
        }
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        int bytesRead = 0;
        if (_downloadSpeedLimit != 0)
        {
            int bytesToRead = Math.Min(buffer.Length, (_downloadSpeedLimit * 1024) / 10);
            bytesRead = await _baseStream.ReadAsync(buffer.Slice(0, bytesToRead), cancellationToken);
            await _throttler.ThrottleAsync(bytesRead, cancellationToken);
        }
        else
        {
            bytesRead = await _baseStream.ReadAsync(buffer, cancellationToken);
        }
        return bytesRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _baseStream.Dispose();
        }

        base.Dispose(disposing);
    }
}
