// ------------------------------------------------------------
//
// Copyright (c) Jiří Polášek. All rights reserved.
//
// ------------------------------------------------------------

using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading.Channels;
using JPSoftworks.MediaControlsExtension.Media.Diagnostics;
using JPSoftworks.MediaControlsExtension.Media.Infrastructure.ITunes.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.System;

namespace JPSoftworks.MediaControlsExtension.Media.Infrastructure.ITunes;

internal sealed class ITunesBackend : IMediaBackend
{
    private static readonly MediaBackendSessionId DefaultSessionId = new(1);
    private static readonly TimeSpan ActivePollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly Channel<bool> _signals;
    private readonly Lock _stateLock = new();

    private DispatcherQueueController? _dispatcherController;
    private DispatcherQueue? _dispatcherQueue;
    private DispatcherQueueTimer? _pollTimer;

    private nint _iTunesApp;
    private nint _connectionPoint;
    private uint _adviseCookie;
    private ITunesEventSink? _eventSink;

    private bool _isEnabled = true;
    private bool _isConnected;
    private long _bindingGeneration = 1;
    private long _revision = 1;
    private long _artworkVersion = 1;
    private int _disposeState;
    private int _startState;
    private int _pendingSignals;

    private MediaPropertiesSnapshot? _currentMediaProperties;
    private MediaTimelinePropertiesSnapshot _currentTimeline = MediaTimelinePropertiesSnapshot.Empty;
    private MediaPlaybackState _currentPlaybackState = MediaPlaybackState.Stopped;
    private MediaCapabilities _currentCapabilities = MediaCapabilities.None;
    private MediaArtworkContent? _cachedArtwork;
    private long _cachedArtworkTrackId = -1;

    private string? _executablePath;

    public ITunesBackend(ILogger<ITunesBackend>? logger = null)
    {
        this._logger = logger ?? NullLogger<ITunesBackend>.Instance;
        this._signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
            AllowSynchronousContinuations = false,
        });
    }

    public bool IsEnabled
    {
        get
        {
            lock (this._stateLock)
            {
                return this._isEnabled;
            }
        }
        set
        {
            lock (this._stateLock)
            {
                if (this._isEnabled == value)
                {
                    return;
                }

                this._isEnabled = value;
            }

            if (!value)
            {
                this._dispatcherQueue?.TryEnqueue(this.Disconnect);
            }

            this.SignalChanged(MediaBackendSignal.SessionsChanged | MediaBackendSignal.CurrentSessionChanged);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);
        if (Interlocked.CompareExchange(ref this._startState, 1, 0) != 0)
        {
            throw new InvalidOperationException("The iTunes backend has already been started.");
        }

        this._dispatcherController = DispatcherQueueController.CreateOnDedicatedThread();
        this._dispatcherQueue = this._dispatcherController.DispatcherQueue;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        this._dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                this.ResolveExecutablePath();
                this._pollTimer = this._dispatcherQueue.CreateTimer();
                this._pollTimer.Interval = IdlePollInterval;
                this._pollTimer.IsRepeating = true;
                this._pollTimer.Tick += (_, _) => this.PollState();
                this._pollTimer.Start();

                this.PollState();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        return tcs.Task;
    }

    public async IAsyncEnumerable<MediaBackendSignal> WatchAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var _ in this._signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var signal = (MediaBackendSignal)Interlocked.Exchange(
                ref this._pendingSignals,
                (int)MediaBackendSignal.None);
            if (signal != MediaBackendSignal.None)
            {
                yield return signal;
            }
        }
    }

    public Task<MediaBackendSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);

        lock (this._stateLock)
        {
            if (!this._isEnabled || !this._isConnected || this._currentMediaProperties == null)
            {
                return Task.FromResult(new MediaBackendSnapshot(
                    this._revision,
                    [],
                    null,
                    MediaControlAvailability.Available));
            }

            var session = new MediaBackendSessionSnapshot(
                DefaultSessionId,
                this._bindingGeneration,
                this._currentMediaProperties,
                this._currentTimeline,
                this._currentPlaybackState,
                this._currentCapabilities,
                IsAvailable: true);

            var currentId = this._currentPlaybackState == MediaPlaybackState.Playing
                ? (MediaBackendSessionId?)DefaultSessionId
                : null;

            return Task.FromResult(new MediaBackendSnapshot(
                this._revision,
                [session],
                currentId,
                MediaControlAvailability.Available));
        }
    }

    public void InvalidateObservations(ImmutableArray<MediaBackendObservationRequest> requests)
    {
        this.SignalChanged(MediaBackendSignal.ObservationsChanged);
    }

    public Task<MediaBackendCommandResult> ExecuteAsync(
        MediaBackendCommand command,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref this._disposeState) != 0, this);

        var tcs = new TaskCompletionSource<MediaBackendCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (this._dispatcherQueue == null)
        {
            return Task.FromResult(new MediaBackendCommandResult(
                MediaBackendCommandStatus.Unavailable,
                "iTunes dispatcher is not running."));
        }

        this._dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (this._iTunesApp == 0)
                {
                    tcs.TrySetResult(new(MediaBackendCommandStatus.Unavailable, "iTunes is not connected."));
                    return;
                }

                int hr = 0;
                switch (command.Operation)
                {
                    case MediaOperation.Play:
                        hr = ITunesNative.Play(this._iTunesApp);
                        break;
                    case MediaOperation.Pause:
                        hr = ITunesNative.Pause(this._iTunesApp);
                        break;
                    case MediaOperation.Stop:
                        hr = ITunesNative.Stop(this._iTunesApp);
                        break;
                    case MediaOperation.TogglePlayback:
                        hr = ITunesNative.PlayPause(this._iTunesApp);
                        break;
                    case MediaOperation.SkipNext:
                        hr = ITunesNative.NextTrack(this._iTunesApp);
                        break;
                    case MediaOperation.SkipPrevious:
                        hr = ITunesNative.BackTrack(this._iTunesApp);
                        break;
                    case MediaOperation.ToggleShuffle:
                        hr = this.ExecuteToggleShuffle();
                        break;
                    case MediaOperation.ToggleRepeat:
                        hr = this.ExecuteToggleRepeat();
                        break;
                    default:
                        tcs.TrySetResult(new(MediaBackendCommandStatus.Unsupported, $"Operation {command.Operation} is not supported by iTunes."));
                        return;
                }

                this.PollState();

                if (hr >= 0)
                {
                    tcs.TrySetResult(new(MediaBackendCommandStatus.Completed, null));
                }
                else
                {
                    tcs.TrySetResult(new(MediaBackendCommandStatus.Failed, $"iTunes COM call returned 0x{hr:X8}"));
                }
            }
            catch (Exception ex)
            {
                tcs.TrySetResult(new(MediaBackendCommandStatus.Failed, ex.Message));
            }
        });

        return tcs.Task;
    }

    public ValueTask<MediaArtworkContent?> GetArtworkAsync(
        MediaArtworkKey key,
        CancellationToken cancellationToken)
    {
        lock (this._stateLock)
        {
            return ValueTask.FromResult(this._cachedArtwork);
        }
    }

    private void PollState()
    {
        if (Volatile.Read(ref this._disposeState) != 0)
        {
            return;
        }

        if (!this._isEnabled)
        {
            if (this._isConnected)
            {
                this.Disconnect();
            }

            return;
        }

        var isProcessRunning = IsProcessRunning("iTunes");
        if (!isProcessRunning)
        {
            if (this._isConnected)
            {
                this.Disconnect();
            }

            return;
        }

        if (!this._isConnected)
        {
            this.Connect();
            if (!this._isConnected)
            {
                return;
            }
        }

        this.UpdatePlaybackAndMetadata();
    }

    private static bool IsProcessRunning(string name)
    {
        try
        {
            using var processes = Process.GetProcessesByName(name).FirstOrDefault();
            return processes != null;
        }
        catch
        {
            return false;
        }
    }

    private void Connect()
    {
        try
        {
            var hr = ITunesNative.CoCreateInstance(
                ITunesGuids.ClsidiTunesApp,
                0,
                ITunesNative.ClsCtxLocalServer,
                ITunesGuids.IidIiTunes,
                out this._iTunesApp);

            if (hr < 0 || this._iTunesApp == 0)
            {
                this._isConnected = false;
                return;
            }

            this.HookEvents();
            this._isConnected = true;
            this._bindingGeneration++;
            this._revision++;
            this.SignalChanged(MediaBackendSignal.SessionsChanged | MediaBackendSignal.CurrentSessionChanged);
        }
        catch (Exception ex)
        {
            MediaLog.ITunesConnectFailed(this._logger, ex);
            this.Disconnect();
        }
    }

    private void HookEvents()
    {
        try
        {
            var hr = ITunesNative.QueryInterface(
                this._iTunesApp,
                ITunesGuids.IidIConnectionPointContainer,
                out var pCpc);

            if (hr < 0 || pCpc == 0)
            {
                return;
            }

            try
            {
                hr = ITunesNative.FindConnectionPoint(
                    pCpc,
                    ITunesGuids.DiidIiTunesEvents,
                    out this._connectionPoint);

                if (hr < 0 || this._connectionPoint == 0)
                {
                    return;
                }

                this._eventSink = new ITunesEventSink(this.OnITunesEvent);
                _ = ITunesNative.Advise(
                    this._connectionPoint,
                    this._eventSink.IUnknownPointer,
                    out this._adviseCookie);
            }
            finally
            {
                _ = ITunesNative.Release(pCpc);
            }
        }
        catch (Exception ex)
        {
            MediaLog.ITunesHookEventsFailed(this._logger, ex);
        }
    }

    private void OnITunesEvent(int dispId)
    {
        // Must marshal event back to dispatcher thread
        this._dispatcherQueue?.TryEnqueue(() =>
        {
            switch (dispId)
            {
                case 2: // OnPlayerPlayEvent
                case 3: // OnPlayerStopEvent
                case 4: // OnPlayerPlayingTrackChangedEvent
                    this.UpdatePlaybackAndMetadata();
                    break;
                case 8: // OnQuittingEvent
                    this.Disconnect();
                    break;
            }
        });
    }

    private void Disconnect()
    {
        if (this._connectionPoint != 0 && this._adviseCookie != 0)
        {
            try
            {
                _ = ITunesNative.Unadvise(this._connectionPoint, this._adviseCookie);
            }
            catch
            {
            }

            this._adviseCookie = 0;
        }

        if (this._connectionPoint != 0)
        {
            _ = ITunesNative.Release(this._connectionPoint);
            this._connectionPoint = 0;
        }

        if (this._eventSink != null)
        {
            this._eventSink.Dispose();
            this._eventSink = null;
        }

        if (this._iTunesApp != 0)
        {
            _ = ITunesNative.Release(this._iTunesApp);
            this._iTunesApp = 0;
        }

        lock (this._stateLock)
        {
            this._isConnected = false;
            this._currentMediaProperties = null;
            this._currentTimeline = MediaTimelinePropertiesSnapshot.Empty;
            this._currentPlaybackState = MediaPlaybackState.Stopped;
            this._currentCapabilities = MediaCapabilities.None;
            this._cachedArtwork = null;
            this._cachedArtworkTrackId = -1;
            this._revision++;
        }

        if (this._pollTimer != null)
        {
            this._pollTimer.Interval = IdlePollInterval;
        }

        this.SignalChanged(MediaBackendSignal.SessionsChanged | MediaBackendSignal.CurrentSessionChanged);
    }

    private void UpdatePlaybackAndMetadata()
    {
        if (this._iTunesApp == 0)
        {
            return;
        }

        try
        {
            var hr = ITunesNative.GetPlayerState(this._iTunesApp, out var rawState);
            if (hr < 0)
            {
                // iTunes may have exited or is not responding
                this.Disconnect();
                return;
            }

            var playbackState = rawState switch
            {
                ITPlayerState.Playing => MediaPlaybackState.Playing,
                ITPlayerState.Stopped => MediaPlaybackState.Stopped,
                _ => MediaPlaybackState.Paused,
            };

            _ = ITunesNative.GetPlayerPosition(this._iTunesApp, out var positionSec);

            nint pTrack = 0;
            _ = ITunesNative.GetCurrentTrack(this._iTunesApp, out pTrack);

            string title = string.Empty;
            string artist = string.Empty;
            string album = string.Empty;
            string genre = string.Empty;
            int durationSec = 0;
            int trackNumber = 0;
            int trackCount = 0;
            int trackDatabaseId = 0;
            bool hasArtwork = false;

            if (pTrack != 0)
            {
                try
                {
                    _ = ITunesNative.GetTrackName(pTrack, out title);
                    _ = ITunesNative.GetTrackArtist(pTrack, out artist);
                    _ = ITunesNative.GetTrackAlbum(pTrack, out album);
                    _ = ITunesNative.GetTrackGenre(pTrack, out genre);
                    _ = ITunesNative.GetTrackDuration(pTrack, out durationSec);
                    _ = ITunesNative.GetTrackNumber(pTrack, out trackNumber);
                    _ = ITunesNative.GetTrackCount(pTrack, out trackCount);
                    _ = ITunesNative.GetTrackDatabaseId(pTrack, out trackDatabaseId);

                    nint pArtworks = 0;
                    _ = ITunesNative.GetTrackArtworkCollection(pTrack, out pArtworks);
                    if (pArtworks != 0)
                    {
                        try
                        {
                            _ = ITunesNative.GetArtworkCount(pArtworks, out var artCount);
                            hasArtwork = artCount > 0;
                        }
                        finally
                        {
                            _ = ITunesNative.Release(pArtworks);
                        }
                    }

                    if (hasArtwork && trackDatabaseId != this._cachedArtworkTrackId)
                    {
                        this.UpdateCachedArtwork(pTrack, trackDatabaseId);
                    }
                }
                finally
                {
                    _ = ITunesNative.Release(pTrack);
                }
            }

            var capabilities = MediaCapabilities.Play |
                               MediaCapabilities.Pause |
                               MediaCapabilities.Stop |
                               MediaCapabilities.SkipNext |
                               MediaCapabilities.SkipPrevious;

            nint pPlaylist = 0;
            _ = ITunesNative.GetCurrentPlaylist(this._iTunesApp, out pPlaylist);
            if (pPlaylist != 0)
            {
                try
                {
                    var shuffleHr = ITunesNative.GetPlaylistShuffle(pPlaylist, out _);
                    if (shuffleHr >= 0)
                    {
                        capabilities |= MediaCapabilities.ToggleShuffle;
                    }

                    var repeatHr = ITunesNative.GetPlaylistRepeat(pPlaylist, out _);
                    if (repeatHr >= 0)
                    {
                        capabilities |= MediaCapabilities.ToggleRepeat;
                    }
                }
                finally
                {
                    _ = ITunesNative.Release(pPlaylist);
                }
            }

            var appSnapshot = new MediaApplicationSnapshot(
                "iTunes.exe",
                "iTunes",
                this._executablePath,
                this._executablePath);

            MediaArtworkKey? artworkKey = hasArtwork
                ? new MediaArtworkKey(new MediaSessionId(DefaultSessionId.Value), this._artworkVersion)
                : null;

            var properties = new MediaPropertiesSnapshot(
                appSnapshot,
                title,
                artist,
                album,
                AlbumArtist: string.Empty,
                Subtitle: string.Empty,
                string.IsNullOrEmpty(genre) ? [] : [genre],
                trackNumber,
                trackCount,
                MediaContentType.Music,
                artworkKey);

            var timeline = new MediaTimelinePropertiesSnapshot(
                StartTime: TimeSpan.Zero,
                EndTime: TimeSpan.FromSeconds(durationSec),
                MinSeekTime: TimeSpan.Zero,
                MaxSeekTime: TimeSpan.FromSeconds(durationSec),
                Position: TimeSpan.FromSeconds(positionSec),
                LastUpdatedAt: DateTimeOffset.UtcNow);

            bool stateChanged = false;
            lock (this._stateLock)
            {
                if (this._currentPlaybackState != playbackState ||
                    this._currentCapabilities != capabilities ||
                    this._currentMediaProperties?.Title != title ||
                    this._currentMediaProperties?.Artist != artist ||
                    this._currentMediaProperties?.AlbumTitle != album ||
                    Math.Abs((this._currentTimeline.Position - timeline.Position).TotalSeconds) > 1.5)
                {
                    stateChanged = true;
                    this._revision++;
                }

                this._currentPlaybackState = playbackState;
                this._currentCapabilities = capabilities;
                this._currentMediaProperties = properties;
                this._currentTimeline = timeline;
            }

            if (this._pollTimer != null)
            {
                var targetInterval = playbackState == MediaPlaybackState.Playing
                    ? ActivePollInterval
                    : IdlePollInterval;

                if (this._pollTimer.Interval != targetInterval)
                {
                    this._pollTimer.Interval = targetInterval;
                }
            }

            if (stateChanged)
            {
                this.SignalChanged(MediaBackendSignal.ObservationsChanged);
            }
        }
        catch (Exception ex)
        {
            MediaLog.ITunesUpdatePlaybackStateFailed(this._logger, ex);
        }
    }

    private void UpdateCachedArtwork(nint pTrack, int trackDatabaseId)
    {
        nint pArtworks = 0;
        _ = ITunesNative.GetTrackArtworkCollection(pTrack, out pArtworks);
        if (pArtworks == 0)
        {
            return;
        }

        try
        {
            nint pArtwork = 0;
            _ = ITunesNative.GetArtworkItem(pArtworks, 1, out pArtwork);
            if (pArtwork == 0)
            {
                return;
            }

            try
            {
                _ = ITunesNative.GetArtworkFormat(pArtwork, out var format);
                var tempFile = Path.Combine(Path.GetTempPath(), $"MC_iTunes_Art_{Guid.NewGuid():N}.tmp");

                try
                {
                    var saveHr = ITunesNative.SaveArtworkToFile(pArtwork, tempFile);
                    if (saveHr >= 0 && File.Exists(tempFile))
                    {
                        var bytes = File.ReadAllBytes(tempFile);
                        var contentType = format switch
                        {
                            ITArtworkFormat.PNG => "image/png",
                            ITArtworkFormat.BMP => "image/bmp",
                            _ => "image/jpeg",
                        };

                        var hash = Convert.ToHexString(SHA256.HashData(bytes));
                        lock (this._stateLock)
                        {
                            this._cachedArtwork = new MediaArtworkContent(contentType, bytes, hash);
                            this._cachedArtworkTrackId = trackDatabaseId;
                            this._artworkVersion++;
                        }
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempFile))
                        {
                            File.Delete(tempFile);
                        }
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                _ = ITunesNative.Release(pArtwork);
            }
        }
        finally
        {
            _ = ITunesNative.Release(pArtworks);
        }
    }

    private int ExecuteToggleShuffle()
    {
        nint pPlaylist = 0;
        var hr = ITunesNative.GetCurrentPlaylist(this._iTunesApp, out pPlaylist);
        if (hr < 0 || pPlaylist == 0)
        {
            return hr;
        }

        try
        {
            hr = ITunesNative.GetPlaylistShuffle(pPlaylist, out var currentShuffle);
            if (hr >= 0)
            {
                hr = ITunesNative.SetPlaylistShuffle(pPlaylist, !currentShuffle);
            }

            return hr;
        }
        finally
        {
            _ = ITunesNative.Release(pPlaylist);
        }
    }

    private int ExecuteToggleRepeat()
    {
        nint pPlaylist = 0;
        var hr = ITunesNative.GetCurrentPlaylist(this._iTunesApp, out pPlaylist);
        if (hr < 0 || pPlaylist == 0)
        {
            return hr;
        }

        try
        {
            hr = ITunesNative.GetPlaylistRepeat(pPlaylist, out var currentRepeat);
            if (hr >= 0)
            {
                var nextRepeat = currentRepeat switch
                {
                    ITPlaylistRepeatMode.Off => ITPlaylistRepeatMode.All,
                    ITPlaylistRepeatMode.All => ITPlaylistRepeatMode.One,
                    _ => ITPlaylistRepeatMode.Off,
                };

                hr = ITunesNative.SetPlaylistRepeat(pPlaylist, nextRepeat);
            }

            return hr;
        }
        finally
        {
            _ = ITunesNative.Release(pPlaylist);
        }
    }

    private void ResolveExecutablePath()
    {
        try
        {
            var processes = Process.GetProcessesByName("iTunes");
            if (processes.Length > 0 && processes[0].MainModule?.FileName is { } path && File.Exists(path))
            {
                this._executablePath = path;
                return;
            }
        }
        catch
        {
        }

        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "iTunes",
            "iTunes.exe");

        if (File.Exists(defaultPath))
        {
            this._executablePath = defaultPath;
        }
    }

    private void SignalChanged(MediaBackendSignal signal)
    {
        Interlocked.Or(ref this._pendingSignals, (int)signal);
        this._signals.Writer.TryWrite(true);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this._disposeState, 1) != 0)
        {
            return;
        }

        this._signals.Writer.TryComplete();

        if (this._dispatcherQueue != null)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            this._dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    this._pollTimer?.Stop();
                    this.Disconnect();
                }
                finally
                {
                    tcs.TrySetResult();
                }
            });

            await tcs.Task.ConfigureAwait(false);
        }

        if (this._dispatcherController != null)
        {
            await this._dispatcherController.ShutdownQueueAsync();
        }
    }
}
