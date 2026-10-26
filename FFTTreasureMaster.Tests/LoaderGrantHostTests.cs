using System;
using System.Collections.Generic;
using Xunit;

namespace FFTTreasureMaster.Tests;

/// <summary>
/// LoaderGrantHost arms the loader-backed jobs so they run after the loader's own table merge.
/// No jobs means no subscription and no log. Fire lands after the loader's handler whether the
/// loader was active at Arm or loaded later. Arm, Fire and each job run at most once. No
/// handler the host owns lets an exception escape.
/// </summary>
public class LoaderGrantHostTests
{
    private sealed class Rig
    {
        public readonly List<string> Order = new();
        public readonly List<string> Info = new();
        public readonly List<string> Warn = new();
        public int RunnerCalls;
        public readonly FakeLoaderEvents Events;
        public readonly LoaderGrantHost Host;

        public Rig(int jobCount = 2, Action? firstJob = null, bool throwingRunner = false)
        {
            Events = new FakeLoaderEvents(Order);
            var jobs = new List<(string Name, Action Job)>();
            for (int i = 0; i < jobCount; i++)
            {
                string name = "job" + (i + 1);
                Action body = i == 0 && firstJob != null ? firstJob : () => Order.Add(name);
                jobs.Add((name, body));
            }
            Host = new LoaderGrantHost(jobs, Info.Add, Warn.Add, work =>
            {
                RunnerCalls++;
                if (throwingRunner) throw new InvalidOperationException("no thread");
                work();
            });
        }

        public int Ran(string job) => Order.FindAll(s => s == job).Count;
    }

    [Fact]
    public void NoJobs_ArmSubscribesNothingAndLogsNothing()
    {
        var r = new Rig(jobCount: 0);

        r.Host.Arm(r.Events);

        Assert.Empty(r.Events.InitializedNames);
        Assert.Equal(0, r.Events.ModLoadedCount);
        Assert.Empty(r.Info);
        Assert.Empty(r.Warn);
    }

    [Fact]
    public void SubscribeThrows_WarnsOnce_NoArmLine_JobsNeverRun()
    {
        var r = new Rig();
        r.Events.LoaderActive = true;
        r.Events.ThrowOnSubscribe = true;

        r.Host.Arm(r.Events);

        Assert.Single(r.Warn);
        Assert.Contains("could not be armed", r.Warn[0]);
        Assert.Empty(r.Info);
        Assert.Empty(r.Order);
    }

    [Fact]
    public void Fallback_WhenIsLoaderActiveThrows_WarnsOnce_DoesNotPropagate()
    {
        var r = new Rig();
        r.Events.LoaderActive = false;
        r.Host.Arm(r.Events);
        r.Events.ThrowOnIsLoaderActive = true;

        r.Events.RaiseInitialized();

        Assert.Single(r.Warn);
        Assert.Contains("could not start", r.Warn[0]);
        Assert.Empty(r.Order);
    }

    [Fact]
    public void LoaderLoadsLater_SubscribeThrowsInCallback_WarnsOnce_DoesNotPropagate()
    {
        var r = new Rig();
        r.Events.LoaderActive = false;
        r.Host.Arm(r.Events);
        r.Events.ThrowOnSubscribe = true;

        r.Events.RaiseModLoaded(Tuning.LoaderModId);

        Assert.Single(r.Warn);
        Assert.Contains("could not be armed", r.Warn[0]);

        r.Events.RaiseInitialized();

        Assert.Equal(0, r.Ran("job1"));
        Assert.Equal(0, r.Ran("job2"));
    }

    [Fact]
    public void LoaderActiveAtArm_FireLandsAfterLoaderHandler()
    {
        var r = new Rig();
        r.Events.SeedLoaderHandler();
        r.Events.LoaderActive = true;

        r.Host.Arm(r.Events);

        Assert.Equal(new[] { "LoaderMarker", "Fire" }, r.Events.InitializedNames);
        Assert.Equal(0, r.Events.ModLoadedCount);
        Assert.Single(r.Info);
        Assert.Contains("job1, job2", r.Info[0]);

        r.Events.RaiseInitialized();

        Assert.Equal(new[] { "LoaderMarker", "job1", "job2" }, r.Order);
    }

    [Fact]
    public void LoaderLoadsLater_FireLandsAfterLoaderHandler()
    {
        var r = new Rig();
        r.Events.LoaderActive = false;

        r.Host.Arm(r.Events);

        Assert.Equal(1, r.Events.ModLoadedCount);
        Assert.Equal(new[] { "Fallback" }, r.Events.InitializedNames);

        r.Events.RaiseModLoaded("other");
        Assert.Equal(new[] { "Fallback" }, r.Events.InitializedNames);

        r.Events.RaiseModLoaded(Tuning.LoaderModId);
        Assert.Equal(new[] { "Fallback", "LoaderMarker", "Fire" }, r.Events.InitializedNames);

        r.Events.RaiseInitialized();

        Assert.Equal(new[] { "LoaderMarker", "job1", "job2" }, r.Order);
    }

    [Fact]
    public void LoaderNeverLoads_JobsStillRunAtInitialized()
    {
        var r = new Rig();
        r.Events.LoaderActive = false;
        r.Host.Arm(r.Events);

        r.Events.RaiseInitialized();

        Assert.Equal(new[] { "job1", "job2" }, r.Order);
        Assert.Empty(r.Warn);
    }

    [Fact]
    public void Arm_Twice_SubscribesOnce()
    {
        var r = new Rig();
        r.Events.LoaderActive = true;

        r.Host.Arm(r.Events);
        r.Host.Arm(r.Events);

        Assert.Equal(new[] { "Fire" }, r.Events.InitializedNames);
        Assert.Single(r.Info);
    }

    [Fact]
    public void Fire_Twice_RunsEachJobOnce()
    {
        var r = new Rig();
        r.Events.LoaderActive = false;
        r.Host.Arm(r.Events);

        r.Events.RaiseModLoaded(Tuning.LoaderModId);
        r.Events.RaiseModLoaded(Tuning.LoaderModId);
        r.Events.RaiseInitialized();
        r.Events.RaiseInitialized();

        Assert.Equal(1, r.Ran("job1"));
        Assert.Equal(1, r.Ran("job2"));
        Assert.Equal(1, r.RunnerCalls);
    }

    [Fact]
    public void ThrowingJob_WarnsOnceNamingIt_NextJobStillRuns()
    {
        var r = new Rig(firstJob: () => throw new InvalidOperationException("boom"));
        r.Events.LoaderActive = true;
        r.Host.Arm(r.Events);

        r.Events.RaiseInitialized();

        Assert.Single(r.Warn);
        Assert.Contains("job1", r.Warn[0]);
        Assert.Contains("boom", r.Warn[0]);
        Assert.Equal(new[] { "job2" }, r.Order);
    }

    [Fact]
    public void Fire_WhenRunnerThrows_WarnsOnceAndDoesNotPropagate()
    {
        var r = new Rig(throwingRunner: true);
        r.Events.LoaderActive = true;
        r.Host.Arm(r.Events);

        r.Events.RaiseInitialized();

        Assert.Single(r.Warn);
        Assert.Contains("could not start", r.Warn[0]);
    }
}
