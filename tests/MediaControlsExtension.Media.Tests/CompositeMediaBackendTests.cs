// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure.Composite;
using JPSoftworks.MediaControlsExtension.Media.Tests.Infrastructure;

namespace JPSoftworks.MediaControlsExtension.Media.Tests;

[TestClass]
public sealed class CompositeMediaBackendTests
{
    [TestMethod]
    public async Task ReadSnapshotAsyncCombinesSessionsFromBothBackends()
    {
        var gsmtcBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Spotify Track", sessionId: 10));
        var itunesBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "iTunes Track", sessionId: 20));

        await using var composite = new CompositeMediaBackend(gsmtcBackend, itunesBackend);
        await composite.StartAsync(CancellationToken.None);

        var snapshot = await composite.ReadSnapshotAsync(CancellationToken.None);

        Assert.AreEqual(2, snapshot.Sessions.Length);
        Assert.IsTrue(snapshot.Sessions.Any(s => s.Id.Value == 10 && s.MediaProperties.Title == "Spotify Track"));
        Assert.IsTrue(snapshot.Sessions.Any(s => s.Id.Value == (20 | CompositeMediaBackend.ITunesSessionIdFlag) && s.MediaProperties.Title == "iTunes Track"));
        Assert.AreEqual(MediaControlAvailability.Available, snapshot.Availability);
    }

    [TestMethod]
    public async Task ReadSnapshotAsyncDeduplicatesWhenGsmtcAlreadyHasITunes()
    {
        var gsmtcBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshotWithApplication(
            1,
            sessionId: 10,
            title: "iTunes SMTC Track",
            appId: "iTunes.exe",
            appName: "iTunes"));
        var itunesBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Native iTunes Track", sessionId: 20));

        await using var composite = new CompositeMediaBackend(gsmtcBackend, itunesBackend);
        await composite.StartAsync(CancellationToken.None);

        var snapshot = await composite.ReadSnapshotAsync(CancellationToken.None);

        // Native iTunes session should be deduplicated
        Assert.AreEqual(1, snapshot.Sessions.Length);
        Assert.AreEqual(10, snapshot.Sessions[0].Id.Value);
        Assert.AreEqual("iTunes SMTC Track", snapshot.Sessions[0].MediaProperties.Title);
    }

    [TestMethod]
    public async Task ExecuteAsyncRoutesGsmtcCommandToGsmtcBackend()
    {
        var gsmtcBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Track 1", sessionId: 10));
        var itunesBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Track 2", sessionId: 20));

        await using var composite = new CompositeMediaBackend(gsmtcBackend, itunesBackend);
        await composite.StartAsync(CancellationToken.None);

        var command = new MediaBackendCommand(
            new MediaBackendSessionId(10),
            1,
            MediaOperation.TogglePlayback,
            []);

        var result = await composite.ExecuteAsync(command, CancellationToken.None);

        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        Assert.AreEqual(1, gsmtcBackend.ExecutedCommands.Length);
        Assert.AreEqual(10, gsmtcBackend.ExecutedCommands[0].SessionId.Value);
        Assert.AreEqual(0, itunesBackend.ExecutedCommands.Length);
    }

    [TestMethod]
    public async Task ExecuteAsyncRoutesITunesCommandToITunesBackendWithFlagStripped()
    {
        var gsmtcBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Track 1", sessionId: 10));
        var itunesBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Track 2", sessionId: 20));

        await using var composite = new CompositeMediaBackend(gsmtcBackend, itunesBackend);
        await composite.StartAsync(CancellationToken.None);

        var compositeSessionId = 20 | CompositeMediaBackend.ITunesSessionIdFlag;
        var command = new MediaBackendCommand(
            new MediaBackendSessionId(compositeSessionId),
            1,
            MediaOperation.SkipNext,
            []);

        var result = await composite.ExecuteAsync(command, CancellationToken.None);

        Assert.AreEqual(MediaBackendCommandStatus.Completed, result.Status);
        Assert.AreEqual(0, gsmtcBackend.ExecutedCommands.Length);
        Assert.AreEqual(1, itunesBackend.ExecutedCommands.Length);
        Assert.AreEqual(20, itunesBackend.ExecutedCommands[0].SessionId.Value);
    }

    [TestMethod]
    public async Task WatchAsyncForwardsSignalsFromBothBackends()
    {
        var gsmtcBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Track 1", sessionId: 10));
        var itunesBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Track 2", sessionId: 20));

        await using var composite = new CompositeMediaBackend(gsmtcBackend, itunesBackend);
        await composite.StartAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var signalsReceived = new List<MediaBackendSignal>();

        var watchTask = Task.Run(async () =>
        {
            await foreach (var signal in composite.WatchAsync(cts.Token))
            {
                signalsReceived.Add(signal);
                if (signalsReceived.Count >= 2)
                {
                    break;
                }
            }
        });

        // Emit from GSMTC
        gsmtcBackend.Signal(MediaBackendSignal.SessionsChanged);
        // Emit from iTunes
        itunesBackend.Signal(MediaBackendSignal.ObservationsChanged);

        await watchTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.IsTrue(signalsReceived.Count >= 2);
    }

    [TestMethod]
    public async Task InvalidateObservationsRoutesToRespectiveBackends()
    {
        var gsmtcBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Track 1", sessionId: 10));
        var itunesBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Track 2", sessionId: 20));

        await using var composite = new CompositeMediaBackend(gsmtcBackend, itunesBackend);
        await composite.StartAsync(CancellationToken.None);

        var itunesSessionId = 20 | CompositeMediaBackend.ITunesSessionIdFlag;
        composite.InvalidateObservations([
            new(new MediaBackendSessionId(10), MediaBackendObservationChanges.Playback),
            new(new MediaBackendSessionId(itunesSessionId), MediaBackendObservationChanges.Timeline),
        ]);

        Assert.AreEqual(1, gsmtcBackend.ObservationInvalidations.Length);
        Assert.AreEqual(10, gsmtcBackend.ObservationInvalidations[0][0].SessionId.Value);

        Assert.AreEqual(1, itunesBackend.ObservationInvalidations.Length);
        Assert.AreEqual(20, itunesBackend.ObservationInvalidations[0][0].SessionId.Value);
    }

    [TestMethod]
    public async Task DisposeAsyncDisposesBothBackends()
    {
        var gsmtcBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Track 1", sessionId: 10));
        var itunesBackend = new FakeMediaBackend(FakeMediaBackend.CreateSnapshot(1, "Track 2", sessionId: 20));

        var composite = new CompositeMediaBackend(gsmtcBackend, itunesBackend);
        await composite.StartAsync(CancellationToken.None);
        await composite.DisposeAsync();

        Assert.AreEqual(1, gsmtcBackend.DisposeCount);
        Assert.AreEqual(1, itunesBackend.DisposeCount);
    }
}
