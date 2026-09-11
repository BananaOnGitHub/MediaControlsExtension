// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Diagnostics;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure.ITunes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure.Composite;

internal sealed class CompositeMediaBackend : IMediaBackend
{
    internal const long ITunesSessionIdFlag = 1L << 60;

    private readonly IMediaBackend _gsmtcBackend;
    private readonly IMediaBackend _itunesBackend;
    private readonly ILogger _logger;
    private readonly Channel<MediaBackendSignal> _signals;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly Lock _stateLock = new();

    private Task? _gsmtcPumpTask;
    private Task? _itunesPumpTask;
    private long _revision = 1;
    private int _disposeState;
    private int _startState;

    public CompositeMediaBackend(
        IMediaBackend gsmtcBackend,
        IMediaBackend itunesBackend,
        ILogger<CompositeMediaBackend>? logger = null)
    {
        this._gsmtcBackend = gsmtcBackend ?? throw new ArgumentNullException(nameof(gsmtcBackend));
        this._itunesBackend = itunesBackend ?? throw new ArgumentNullException(nameof(itunesBackend));
        this._logger = logger ?? NullLogger<CompositeMediaBackend>.Instance;
        this._signals = Channel.CreateBounded<MediaBackendSignal>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
    }

    public void SetITunesEnabled(bool enabled)
    {
        if (this._itunesBackend is ITunesBackend itunes)
        {
            itunes.IsEnabled = enabled;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);
        if (Interlocked.CompareExchange(ref this._startState, 1, 0) != 0)
        {
            throw new InvalidOperationException("The composite backend has already been started.");
        }

        await this._gsmtcBackend.StartAsync(cancellationToken).ConfigureAwait(false);
        await this._itunesBackend.StartAsync(cancellationToken).ConfigureAwait(false);

        this._gsmtcPumpTask = Task.Run(() => this.PumpBackendSignalsAsync(this._gsmtcBackend, this._disposeCts.Token), cancellationToken);
        this._itunesPumpTask = Task.Run(() => this.PumpBackendSignalsAsync(this._itunesBackend, this._disposeCts.Token), cancellationToken);
    }

    private async Task PumpBackendSignalsAsync(IMediaBackend backend, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var signal in backend.WatchAsync(cancellationToken).ConfigureAwait(false))
            {
                this._signals.Writer.TryWrite(signal);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            var backendName = this._logger.IsEnabled(LogLevel.Warning) ? backend.GetType().Name : string.Empty;
            MediaLog.ChildBackendSignalPumpFailed(this._logger, backendName, ex);
        }
    }

    public async IAsyncEnumerable<MediaBackendSignal> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var signal in this._signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return signal;
        }
    }

    public async Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);

        var gsmtcSnapshotTask = this._gsmtcBackend.ReadSnapshotAsync(cancellationToken);
        var itunesSnapshotTask = this._itunesBackend.ReadSnapshotAsync(cancellationToken);

        await Task.WhenAll(gsmtcSnapshotTask, itunesSnapshotTask).ConfigureAwait(false);

        var gsmtcSnapshot = await gsmtcSnapshotTask.ConfigureAwait(false);
        var itunesSnapshot = await itunesSnapshotTask.ConfigureAwait(false);

        var hasGsmtcITunesSession = gsmtcSnapshot.Sessions.Any(static s =>
            s.MediaProperties.Application.ApplicationId.Contains("iTunes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.MediaProperties.Application.DisplayName, "iTunes", StringComparison.OrdinalIgnoreCase));

        var sessionsBuilder = ImmutableArray.CreateBuilder<MediaBackendSessionSnapshot>(
            gsmtcSnapshot.Sessions.Length + (hasGsmtcITunesSession ? 0 : itunesSnapshot.Sessions.Length));

        sessionsBuilder.AddRange(gsmtcSnapshot.Sessions);

        MediaBackendSessionId? itunesMappedCurrentId = null;

        if (!hasGsmtcITunesSession)
        {
            foreach (var session in itunesSnapshot.Sessions)
            {
                var mappedId = new MediaBackendSessionId(session.Id.Value | ITunesSessionIdFlag);
                var mappedArtworkKey = session.MediaProperties.Artwork is { } artKey
                    ? (MediaArtworkKey?)new MediaArtworkKey(new MediaSessionId(mappedId.Value), artKey.Version)
                    : null;

                var mappedMediaProperties = session.MediaProperties with
                {
                    Artwork = mappedArtworkKey
                };

                sessionsBuilder.Add(session with
                {
                    Id = mappedId,
                    MediaProperties = mappedMediaProperties,
                });

                if (itunesSnapshot.CurrentSessionId == session.Id)
                {
                    itunesMappedCurrentId = mappedId;
                }
            }
        }

        MediaBackendSessionId? currentSessionId = gsmtcSnapshot.CurrentSessionId;

        // If GSMTC has no current session or GSMTC is stopped, but iTunes is actively playing, prefer iTunes
        if (currentSessionId == null)
        {
            currentSessionId = itunesMappedCurrentId;
        }
        else if (itunesMappedCurrentId is { } itunesId)
        {
            var gsmtcCurrent = gsmtcSnapshot.Sessions.FirstOrDefault(s => s.Id == currentSessionId.Value);
            if (gsmtcCurrent != null && gsmtcCurrent.PlaybackState != MediaPlaybackState.Playing)
            {
                var itunesCurrent = itunesSnapshot.Sessions.FirstOrDefault(s => s.Id == itunesSnapshot.CurrentSessionId);
                if (itunesCurrent is { PlaybackState: MediaPlaybackState.Playing })
                {
                    currentSessionId = itunesId;
                }
            }
        }

        var availability = gsmtcSnapshot.Availability != MediaControlAvailability.Unavailable
            ? gsmtcSnapshot.Availability
            : itunesSnapshot.Availability;

        long revision;
        lock (this._stateLock)
        {
            revision = ++this._revision;
        }

        return new MediaBackendSnapshot(
            revision,
            sessionsBuilder.ToImmutable(),
            currentSessionId,
            availability);
    }

    public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests)
    {
        var gsmtcRequests = ImmutableArray.CreateBuilder<MediaBackendObservationRequest>();
        var itunesRequests = ImmutableArray.CreateBuilder<MediaBackendObservationRequest>();

        foreach (var req in requests)
        {
            if ((req.SessionId.Value & ITunesSessionIdFlag) != 0)
            {
                itunesRequests.Add(new MediaBackendObservationRequest(
                    new MediaBackendSessionId(req.SessionId.Value & ~ITunesSessionIdFlag),
                    req.Changes));
            }
            else
            {
                gsmtcRequests.Add(req);
            }
        }

        if (gsmtcRequests.Count > 0)
        {
            this._gsmtcBackend.InvalidateObservations(gsmtcRequests.ToImmutable());
        }

        if (itunesRequests.Count > 0)
        {
            this._itunesBackend.InvalidateObservations(itunesRequests.ToImmutable());
        }
    }

    public async Task<MediaBackendCommandResult> ExecuteAsync(
        MediaBackendCommand command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);

        var isITunes = (command.SessionId.Value & ITunesSessionIdFlag) != 0;

        // Pause other sessions across backends if requested
        if (command.SessionsToPause.Length > 0)
        {
            var gsmtcPauseIds = ImmutableArray.CreateBuilder<MediaBackendSessionId>();
            var itunesPauseIds = ImmutableArray.CreateBuilder<MediaBackendSessionId>();

            foreach (var pauseId in command.SessionsToPause)
            {
                if ((pauseId.Value & ITunesSessionIdFlag) != 0)
                {
                    itunesPauseIds.Add(new MediaBackendSessionId(pauseId.Value & ~ITunesSessionIdFlag));
                }
                else
                {
                    gsmtcPauseIds.Add(pauseId);
                }
            }

            if (isITunes && gsmtcPauseIds.Count > 0)
            {
                foreach (var gsmtcId in gsmtcPauseIds)
                {
                    _ = this._gsmtcBackend.ExecuteAsync(
                        new MediaBackendCommand(gsmtcId, 0, MediaOperation.Pause, []),
                        cancellationToken);
                }
            }
            else if (!isITunes && itunesPauseIds.Count > 0)
            {
                foreach (var itunesId in itunesPauseIds)
                {
                    _ = this._itunesBackend.ExecuteAsync(
                        new MediaBackendCommand(itunesId, 0, MediaOperation.Pause, []),
                        cancellationToken);
                }
            }
        }

        if (isITunes)
        {
            var unmappedCommand = new MediaBackendCommand(
                new MediaBackendSessionId(command.SessionId.Value & ~ITunesSessionIdFlag),
                command.BindingGeneration,
                command.Operation,
                []);

            return await this._itunesBackend.ExecuteAsync(unmappedCommand, cancellationToken).ConfigureAwait(false);
        }

        return await this._gsmtcBackend.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken)
    {
        if ((key.SessionId.Value & ITunesSessionIdFlag) != 0)
        {
            var unmappedSessionId = new MediaSessionId(key.SessionId.Value & ~ITunesSessionIdFlag);
            return this._itunesBackend.GetArtworkAsync(
                new MediaArtworkKey(unmappedSessionId, key.Version),
                cancellationToken);
        }

        return this._gsmtcBackend.GetArtworkAsync(key, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this._disposeState, 1) != 0)
        {
            return;
        }

        this._disposeCts.Cancel();
        this._signals.Writer.TryComplete();

        var gsmtcTask = this._gsmtcPumpTask;
        if (gsmtcTask != null)
        {
            try
            {
                await gsmtcTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        var itunesTask = this._itunesPumpTask;
        if (itunesTask != null)
        {
            try
            {
                await itunesTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        await this._gsmtcBackend.DisposeAsync().ConfigureAwait(false);
        await this._itunesBackend.DisposeAsync().ConfigureAwait(false);

        this._disposeCts.Dispose();
    }
}
