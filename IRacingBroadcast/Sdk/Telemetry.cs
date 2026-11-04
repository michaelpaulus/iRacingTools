// Same SDK reader as IRacingVoice\Telemetry.cs (live memory map only: .ibt files have no CarIdx arrays for the field).

using System.IO.MemoryMappedFiles;
using System.Text;

/// <summary>Variable header of the live iRacing SDK memory map.</summary>
public record VarHeader(string Name, int Type, int Offset, int Count, string Unit);

static class Irsdk
{
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

/// <summary>Reads live telemetry from the iRacing SDK shared memory (60 Hz), which is also what a playing replay publishes.</summary>
public sealed class LiveTelemetry : IDisposable
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

    /// <summary>Blocks until the next frame is available. Returns false on timeout or when iRacing isn't there.</summary>
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

    public int Int(string name, int index = 0, int fallback = 0) => Get(name, index) is var d && !double.IsNaN(d) ? (int)d : fallback;

    public void Dispose() => Close();
}
