using System;

namespace SerialTool.Features;

/// <summary>按 1 秒窗口统计 RX/TX 瞬时与峰值吞吐。</summary>
public sealed class ThroughputMeter
{
    private long _rxWindow, _txWindow;
    private long _rxPeak, _txPeak;
    private long _rxTotal, _txTotal;
    private DateTime _windowStart = DateTime.UtcNow;
    private DateTime _sessionStart = DateTime.UtcNow;
    private double _rxRate, _txRate;
    private double _rxAvg, _txAvg;
    private long _frameOk, _frameErr;

    public double RxRateBps => _rxRate;
    public double TxRateBps => _txRate;
    public double RxAvgBps => _rxAvg;
    public double TxAvgBps => _txAvg;
    public long RxPeakBps => _rxPeak;
    public long TxPeakBps => _txPeak;
    public long RxTotal => _rxTotal;
    public long TxTotal => _txTotal;
    public long FrameOk => _frameOk;
    public long FrameErr => _frameErr;
    public TimeSpan SessionElapsed => DateTime.UtcNow - _sessionStart;

    public void AddRx(int bytes) { _rxWindow += bytes; _rxTotal += bytes; }
    public void AddTx(int bytes) { _txWindow += bytes; _txTotal += bytes; }
    public void AddFrame(bool crcOk) { if (crcOk) _frameOk++; else _frameErr++; }

    public void Tick()
    {
        var now = DateTime.UtcNow;
        double sec = (now - _windowStart).TotalSeconds;
        if (sec < 0.2) return;
        _rxRate = _rxWindow / sec;
        _txRate = _txWindow / sec;
        if (_rxWindow > _rxPeak) _rxPeak = _rxWindow;
        if (_txWindow > _txPeak) _txPeak = _txWindow;
        _rxWindow = 0;
        _txWindow = 0;
        _windowStart = now;

        double elapsed = Math.Max(0.001, (now - _sessionStart).TotalSeconds);
        _rxAvg = _rxTotal / elapsed;
        _txAvg = _txTotal / elapsed;
    }

    public void Reset()
    {
        _rxWindow = _txWindow = 0;
        _rxPeak = _txPeak = 0;
        _rxTotal = _txTotal = 0;
        _rxRate = _txRate = _rxAvg = _txAvg = 0;
        _frameOk = _frameErr = 0;
        _windowStart = _sessionStart = DateTime.UtcNow;
    }

    public static string FormatRate(double bps)
    {
        if (bps >= 1024 * 1024) return $"{bps / (1024 * 1024):0.00} MB/s";
        if (bps >= 1024) return $"{bps / 1024:0.00} KB/s";
        return $"{bps:0} B/s";
    }
}
