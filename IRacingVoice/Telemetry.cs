// Same SDK reader as IRacingCoach\Telemetry.cs (live memory map only).

using System.IO.MemoryMappedFiles;
using System.Text;

/// <summary>Variable header shared by the live iRacing SDK memory map and .ibt files.</summary>
public record VarHeader(string Name, int Type, int Offset, int Count, string Unit);

public interface ITelemetrySource : IDisposable
{
    /// <summary>Blocks until the next frame is available. Returns false on timeout / end of data.</summary>
    bool WaitForFrame(int timeoutMs);
    bool IsConnected { get; }
    bool IsFinished { get; }
    int SessionInfoVersion { get; }
    string SessionInfo { get; }
    bool Has(string name);
    double Get(string name, int index = 0);
}

static class Irsdk
{
    // irsdk_header layout (both live memory map and .ibt)
    public const int HeaderSize = 112;
    public const int VarHeaderSize = 144;

    public static Dictionary<string, VarHeader> ParseVars(byte[] buf, int varHeaderOffset, int numVars)
    {
        var vars = new Dictionary<string, VarHeader>(StringComparer.Ordinal);
        for (int i = 0; i < numVars; i++)
        {
            int o = varHeaderOffset + i * VarHeaderSize;
            var v = new VarHeader(
                CStr(buf, o + 16, 32),
                BitConverter.ToInt32(buf, o),
                BitConverter.ToInt32(buf, o + 4),
                BitConverter.ToInt32(buf, o + 8),
                CStr(buf, o + 112, 32));
            vars[v.Name] = v;
        }
        return vars;
    }

    public static string CStr(byte[] b, int off, int len)
    {
        int end = off;
        while (end < off + len && end < b.Length && b[end] != 0) end++;
        return Encoding.Latin1.GetString(b, off, end - off);
    }

    public static double Decode(byte[] buf, int baseOffset, VarHeader v, int idx)
    {
        int o = baseOffset + v.Offset;
        return v.Type switch
        {
            0 or 1 => buf[o + idx],
            2 or 3 => BitConverter.ToInt32(buf, o + idx * 4),
            4 => BitConverter.ToSingle(buf, o + idx * 4),
            5 => BitConverter.ToDouble(buf, o + idx * 8),
            _ => double.NaN,
        };
    }
}

/// <summary>Reads live telemetry from the iRacing SDK shared memory (60 Hz).</summary>
public sealed class LiveTelemetry : ITelemetrySource
{
    const string MapName = @"Local\IRSDKMemMapFileName";
    const string EventName = @"Local\IRSDKDataValidEvent";
    const int StatusConnected = 1;

    MemoryMappedFile? _mmf;
    MemoryMappedViewAccessor? _view;
    EventWaitHandle? _event;
    Dictionary<string, VarHeader> _vars = new();
    byte[] _frame = Array.Empty<byte>();
    int _numVars = -1, _lastTick = -1;
    string _sessionInfo = "";

    public bool IsConnected { get; private set; }
    public bool IsFinished => false;
    public int SessionInfoVersion { get; private set; } = -1;
    public string SessionInfo => _sessionInfo;

    bool TryOpen()
    {
        if (_view != null) return true;
        try
        {
            _mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read);
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _event = EventWaitHandle.OpenExisting(EventName);
            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    void Close()
    {
        _view?.Dispose(); _view = null;
        _mmf?.Dispose(); _mmf = null;
        _event?.Dispose(); _event = null;
        IsConnected = false;
        _numVars = -1;
    }

    int ReadInt(long pos) => _view!.ReadInt32(pos);

    public bool WaitForFrame(int timeoutMs)
    {
        if (!TryOpen()) { Thread.Sleep(Math.Min(timeoutMs, 1000)); return false; }
        _event!.WaitOne(timeoutMs);

        int status = ReadInt(4);
        IsConnected = (status & StatusConnected) != 0;
        if (!IsConnected) return false;

        int numVars = ReadInt(24), varHeaderOffset = ReadInt(28), numBuf = ReadInt(32), bufLen = ReadInt(36);
        if (numVars != _numVars)
        {
            var hdr = new byte[varHeaderOffset + numVars * Irsdk.VarHeaderSize];
            _view!.ReadArray(0, hdr, 0, hdr.Length);
            _vars = Irsdk.ParseVars(hdr, varHeaderOffset, numVars);
            _numVars = numVars;
            _frame = new byte[bufLen];
        }

        int sessionInfoUpdate = ReadInt(12);
        if (sessionInfoUpdate != SessionInfoVersion)
        {
            int len = ReadInt(16), off = ReadInt(20);
            var si = new byte[len];
            _view!.ReadArray(off, si, 0, len);
            _sessionInfo = Irsdk.CStr(si, 0, len);
            SessionInfoVersion = sessionInfoUpdate;
        }

        // Pick the newest of the rotating buffers; retry if it was overwritten while copying.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            int best = 0, bestTick = int.MinValue;
            for (int i = 0; i < numBuf; i++)
            {
                int tick = ReadInt(48 + i * 16);
                if (tick > bestTick) { bestTick = tick; best = i; }
            }
            int bufOffset = ReadInt(48 + best * 16 + 4);
            _view!.ReadArray(bufOffset, _frame, 0, bufLen);
            if (ReadInt(48 + best * 16) == bestTick)
            {
                if (bestTick == _lastTick) return false;
                _lastTick = bestTick;
                return true;
            }
        }
        return false;
    }

    public bool Has(string name) => _vars.ContainsKey(name);

    public double Get(string name, int index = 0) =>
        _vars.TryGetValue(name, out var v) && _frame.Length > 0 ? Irsdk.Decode(_frame, 0, v, index) : double.NaN;

    public void Dispose() => Close();
}

/// <summary>Plays back a recorded .ibt file as if it were live, for testing.</summary>
public sealed class IbtReplay : ITelemetrySource
{
    readonly byte[] _data;
    readonly Dictionary<string, VarHeader> _vars;
    readonly int _bufOffset, _bufLen, _records, _tickRate;
    readonly double _speed;
    int _rec = -1;
    DateTime _start;

    public IbtReplay(string path, double speed)
    {
        _data = File.ReadAllBytes(path);
        _tickRate = BitConverter.ToInt32(_data, 8);
        int siLen = BitConverter.ToInt32(_data, 16), siOff = BitConverter.ToInt32(_data, 20);
        int numVars = BitConverter.ToInt32(_data, 24), vhOff = BitConverter.ToInt32(_data, 28);
        _bufLen = BitConverter.ToInt32(_data, 36);
        _bufOffset = BitConverter.ToInt32(_data, 52);
        _records = BitConverter.ToInt32(_data, Irsdk.HeaderSize + 28);
        int maxRecs = (_data.Length - _bufOffset) / _bufLen;
        if (_records <= 0 || _records > maxRecs) _records = maxRecs;
        SessionInfo = Irsdk.CStr(_data, siOff, siLen);
        _vars = Irsdk.ParseVars(_data, vhOff, numVars);
        _speed = speed;
    }

    public bool IsConnected => true;
    public bool IsFinished => _rec >= _records - 1;
    public int SessionInfoVersion => 1;
    public string SessionInfo { get; }

    public bool WaitForFrame(int timeoutMs)
    {
        if (IsFinished) return false;
        if (_rec < 0) _start = DateTime.UtcNow;
        _rec++;
        if (_speed > 0)
        {
            var due = _start + TimeSpan.FromSeconds(_rec / (double)_tickRate / _speed);
            var wait = due - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);
        }
        return true;
    }

    public bool Has(string name) => _vars.ContainsKey(name);

    public double Get(string name, int index = 0) =>
        _vars.TryGetValue(name, out var v) && _rec >= 0 ? Irsdk.Decode(_data, _bufOffset + _rec * _bufLen, v, index) : double.NaN;

    public void Dispose() { }
}
