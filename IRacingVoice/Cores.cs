using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// Keeps this process (and the Claude CLI it starts, which inherits it) on the CPU's efficiency cores, so speech
/// recognition and the voice never compete with iRacing for the performance cores.
/// </summary>
public static class Cores
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemCpuSetInformation(IntPtr information, uint bufferLength, out uint returnedLength, IntPtr process, uint flags);

    /// <summary>The efficiency cores' affinity mask; 0 when the CPU doesn't have two kinds of core.</summary>
    public static long EfficiencyMask()
    {
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out uint length, IntPtr.Zero, 0);
        if (length == 0) return 0;
        var buffer = Marshal.AllocHGlobal((int)length);
        try
        {
            if (!GetSystemCpuSetInformation(buffer, length, out length, IntPtr.Zero, 0)) return 0;
            // SYSTEM_CPU_SET_INFORMATION: Size at 0, then the CpuSet fields: Group at 12, LogicalProcessorIndex at 14,
            // EfficiencyClass at 18 (higher = faster cores).
            var sets = new List<(int Group, int Cpu, int Class)>();
            for (int at = 0; at < length;)
            {
                int size = Marshal.ReadInt32(buffer, at);
                if (size <= 0) break;
                sets.Add((Marshal.ReadInt16(buffer, at + 12), Marshal.ReadByte(buffer, at + 14), Marshal.ReadByte(buffer, at + 18)));
                at += size;
            }
            var group0 = sets.Where(s => s.Group == 0 && s.Cpu < 64).ToList();
            if (group0.Select(s => s.Class).Distinct().Count() < 2) return 0;
            int slowest = group0.Min(s => s.Class);
            return group0.Where(s => s.Class == slowest).Aggregate(0L, (mask, s) => mask | 1L << s.Cpu);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Moves this process onto the efficiency cores; returns what it did, for the start-up message.</summary>
    public static string UseEfficiencyCores()
    {
        try
        {
            long mask = EfficiencyMask();
            if (mask == 0) return "all cores (this CPU has only one kind)";
            Process.GetCurrentProcess().ProcessorAffinity = (IntPtr)mask;
            return $"the {long.PopCount(mask)} efficiency cores";
        }
        catch (Exception ex) { return $"all cores (couldn't limit them: {ex.Message})"; }
    }
}
