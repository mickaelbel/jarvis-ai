using Microsoft.Extensions.Logging;
using System.Net.NetworkInformation;
using System.Diagnostics;

namespace JarvisAI.Infrastructure.Hardware;

public interface INetworkMonitor
{
    bool IsConnected { get; }
    double LatencyMs { get; }
    NetworkQuality Quality { get; }
    NetworkSnapshot Snapshot();
}

public sealed class NetworkMonitor : INetworkMonitor, IDisposable
{
    private readonly ILogger<NetworkMonitor> _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _monitorThread;
    private bool _isConnected;
    private double _latencyMs;
    private NetworkQuality _quality = NetworkQuality.Unknown;
    private readonly List<double> _latencyHistory = new();
    private readonly Ping _ping = new();

    public bool IsConnected => _isConnected;
    public double LatencyMs => _latencyMs;
    public NetworkQuality Quality => _quality;

    public NetworkMonitor(ILogger<NetworkMonitor> logger)
    {
        _logger = logger;
        _monitorThread = new Thread(MonitorLoop) { IsBackground = true };
        _monitorThread.Start();
    }

    private void MonitorLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                var reply = _ping.Send("8.8.8.8", 2000);
                sw.Stop();

                _isConnected = reply.Status == IPStatus.Success;
                _latencyMs = _isConnected ? sw.Elapsed.TotalMilliseconds : -1;

                if (_isConnected)
                {
                    lock (_latencyHistory)
                    {
                        _latencyHistory.Add(_latencyMs);
                        if (_latencyHistory.Count > 30) _latencyHistory.RemoveAt(0);
                    }

                    var avg = _latencyHistory.Average();
                    _quality = avg < 30 ? NetworkQuality.Excellent
                        : avg < 80 ? NetworkQuality.Good
                        : avg < 200 ? NetworkQuality.Fair
                        : NetworkQuality.Poor;
                }
                else
                {
                    _quality = NetworkQuality.Disconnected;
                }
            }
            catch
            {
                _isConnected = false;
                _quality = NetworkQuality.Disconnected;
            }

            Thread.Sleep(10000);
        }
    }

    public NetworkSnapshot Snapshot()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var reply = _ping.Send("8.8.8.8", 2000);
            sw.Stop();

            return new NetworkSnapshot
            {
                IsConnected = reply.Status == IPStatus.Success,
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                Timestamp = DateTime.UtcNow
            };
        }
        catch
        {
            return new NetworkSnapshot { IsConnected = false, Timestamp = DateTime.UtcNow };
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _ping.Dispose();
    }
}

public enum NetworkQuality { Unknown, Disconnected, Poor, Fair, Good, Excellent }

public sealed class NetworkSnapshot
{
    public bool IsConnected { get; set; }
    public double LatencyMs { get; set; }
    public DateTime Timestamp { get; set; }
}
