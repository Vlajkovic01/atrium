using Atrium.Core.Jobs;

namespace Atrium.Jobs.Tests;

public sealed class HardwareProfileTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData(4, 2 * GiB, HardwareProfile.Small)]
    [InlineData(16, 3 * GiB, HardwareProfile.Small)]
    [InlineData(4, 4 * GiB, HardwareProfile.Medium)]
    [InlineData(4, 32 * GiB, HardwareProfile.Medium)]
    [InlineData(8, 16 * GiB, HardwareProfile.Medium)]
    [InlineData(8, 17 * GiB, HardwareProfile.Large)]
    public void The_profile_follows_memory_and_cores(int cores, long memory, HardwareProfile expected) =>
        Assert.Equal(expected, new HardwareInfo(cores, memory).Profile);

    [Fact]
    public void The_smallest_device_runs_one_cpu_job_at_a_time()
    {
        var limits = new HardwareInfo(4, 2 * GiB).DefaultLimits();

        Assert.Equal(1, limits[JobResourceClass.Cpu]);
        Assert.Equal(2, limits[JobResourceClass.Io]);
        Assert.Equal(1, limits[JobResourceClass.Transcode]);
    }

    [Fact]
    public void Larger_machines_scale_with_their_cores()
    {
        Assert.Equal(3, new HardwareInfo(6, 8 * GiB).DefaultLimits()[JobResourceClass.Cpu]);
        Assert.Equal(14, new HardwareInfo(16, 64 * GiB).DefaultLimits()[JobResourceClass.Cpu]);
        Assert.Equal(1, new HardwareInfo(1, 8 * GiB).DefaultLimits()[JobResourceClass.Cpu]);
    }

    [Fact]
    public void Every_class_has_a_default_of_at_least_one()
    {
        foreach (var info in new[] { new HardwareInfo(1, GiB), new HardwareInfo(4, 8 * GiB), new HardwareInfo(32, 128 * GiB) })
        {
            var limits = info.DefaultLimits();
            Assert.All(Enum.GetValues<JobResourceClass>(), c => Assert.True(limits[c] >= 1));
        }
    }

    [Fact]
    public void Overrides_replace_defaults_and_can_be_cleared()
    {
        var limits = new JobLimits(new HardwareInfo(4, 2 * GiB), new JobOptions(), TimeProvider.System, new JobSignal());

        limits.SetOverrides(new Dictionary<JobResourceClass, int> { [JobResourceClass.Cpu] = 3 });
        Assert.Equal(3, limits.LimitFor(JobResourceClass.Cpu));
        Assert.Equal(2, limits.LimitFor(JobResourceClass.Io));

        limits.SetOverrides(new Dictionary<JobResourceClass, int>());
        Assert.Equal(1, limits.LimitFor(JobResourceClass.Cpu));
    }

    [Fact]
    public void A_limit_outside_the_allowed_range_is_refused()
    {
        var limits = new JobLimits(new HardwareInfo(4, 2 * GiB), new JobOptions(), TimeProvider.System, new JobSignal());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            limits.SetOverrides(new Dictionary<JobResourceClass, int> { [JobResourceClass.Io] = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            limits.SetOverrides(new Dictionary<JobResourceClass, int> { [JobResourceClass.Io] = JobLimits.MaxLimit + 1 }));
    }

    [Fact]
    public void A_hold_pauses_background_work_until_released()
    {
        var limits = new JobLimits(new HardwareInfo(4, 2 * GiB), new JobOptions(), TimeProvider.System, new JobSignal());
        var before = limits.BackgroundStopToken;

        var hold = limits.HoldBackground();
        Assert.True(limits.IsBackgroundPaused);
        Assert.True(before.IsCancellationRequested);

        hold.Dispose();
        hold.Dispose();
        Assert.False(limits.IsBackgroundPaused);
        Assert.False(limits.BackgroundStopToken.IsCancellationRequested);
    }
}
