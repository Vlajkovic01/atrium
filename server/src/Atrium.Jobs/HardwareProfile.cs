using Atrium.Core.Jobs;

namespace Atrium.Jobs;

/// <summary>How much machine the server runs on, which sets the default job limits (DESIGN.md 5.4).</summary>
public enum HardwareProfile
{
    /// <summary>Under 4 GB of memory: one CPU-heavy job at a time.</summary>
    Small,

    /// <summary>4 to 16 GB, or more memory with fewer than 8 cores.</summary>
    Medium,

    /// <summary>Over 16 GB and at least 8 cores.</summary>
    Large,
}

/// <summary>What the process may use. Both values respect container limits.</summary>
/// <param name="ProcessorCount">Cores available to the process.</param>
/// <param name="MemoryBytes">Memory available to the process.</param>
public sealed record HardwareInfo(int ProcessorCount, long MemoryBytes)
{
    private const long GiB = 1024L * 1024 * 1024;

    /// <summary>
    /// Reads the machine. .NET reports the container's CPU and memory limits
    /// rather than the host's, so a small container on a large host counts
    /// as small.
    /// </summary>
    public static HardwareInfo Detect() =>
        new(Environment.ProcessorCount, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    public HardwareProfile Profile => MemoryBytes switch
    {
        < 4 * GiB => HardwareProfile.Small,
        > 16 * GiB when ProcessorCount >= 8 => HardwareProfile.Large,
        _ => HardwareProfile.Medium,
    };

    /// <summary>Default number of jobs of each class that run at once on this machine.</summary>
    public IReadOnlyDictionary<JobResourceClass, int> DefaultLimits()
    {
        var cores = Math.Max(1, ProcessorCount);
        return Profile switch
        {
            HardwareProfile.Small => new Dictionary<JobResourceClass, int>
            {
                [JobResourceClass.Cpu] = 1,
                [JobResourceClass.Io] = 2,
                [JobResourceClass.Network] = 2,
                [JobResourceClass.Transcode] = 1,
                [JobResourceClass.Gpu] = 1,
            },
            HardwareProfile.Medium => new Dictionary<JobResourceClass, int>
            {
                [JobResourceClass.Cpu] = Math.Max(1, cores / 2),
                [JobResourceClass.Io] = 4,
                [JobResourceClass.Network] = 2,
                [JobResourceClass.Transcode] = 2,
                [JobResourceClass.Gpu] = 1,
            },
            _ => new Dictionary<JobResourceClass, int>
            {
                [JobResourceClass.Cpu] = Math.Max(1, cores - 2),
                [JobResourceClass.Io] = 8,
                [JobResourceClass.Network] = 4,
                [JobResourceClass.Transcode] = 4,
                [JobResourceClass.Gpu] = 2,
            },
        };
    }
}
