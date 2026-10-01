using System.ComponentModel;
using System.Diagnostics;
using GameLibrary.Contracts;
using GameLibrary.Domain.Detection;

namespace GameLibrary.Host.Launching;

public sealed partial class LaunchRegistry
{
    // ponytail: one profile lock per Host; split by game only if measured contention warrants it.
    private readonly object _profileLock = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource> _observations = new();
    public Func<string, bool>? IsActiveGame { get; set; }
    public Func<LaunchProfile, int, bool>? TryCommitValidation { get; set; }
    internal TimeSpan VerificationDuration { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan ObservationTimeout { get; set; } = TimeSpan.FromSeconds(120);

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    public LaunchProfile AddProfile(string gameId, string executablePath, IReadOnlyList<string> arguments,
        string workingDirectory, string? toolId = null, bool isDefault = false)
    {
        lock (_profileLock) return AddProfileCore(gameId, executablePath, arguments, workingDirectory, toolId, isDefault);
    }

    private void CheckProfileRevision(string profileId, int? expectedRevision)
    {
        if (expectedRevision is not null && GetProfile(profileId)?.Revision != expectedRevision)
            throw new LaunchException(ErrorCodes.RevisionConflict, "启动方式已更新，请刷新后重试");
    }

    public LaunchProfile UpdateProfile(string profileId, string executablePath, IReadOnlyList<string> arguments, string workingDirectory, int? expectedRevision = null)
    {
        lock (_profileLock)
        {
            CheckProfileRevision(profileId, expectedRevision);
            return UpdateProfileCore(profileId, executablePath, arguments, workingDirectory);
        }
    }

    public LaunchProfile SetDefault(string gameId, string profileId, int? expectedRevision = null)
    {
        lock (_profileLock)
        {
            CheckProfileRevision(profileId, expectedRevision);
            return SetDefaultCore(gameId, profileId);
        }
    }

    public LaunchProfile RemoveProfile(string profileId, int? expectedRevision = null)
    {
        lock (_profileLock)
        {
            CheckProfileRevision(profileId, expectedRevision);
            return RemoveProfileCore(profileId);
        }
    }

    public void AddSuggestions(string gameId, string directory, IReadOnlyList<EntryCandidate> entries)
    {
        lock (_profileLock)
        {
            foreach (var entry in entries)
            {
                var path = Path.GetFullPath(Path.Combine(directory, entry.RelativePath));
                var existing = _profiles.Values.FirstOrDefault(p => p.GameId == gameId && SamePath(p.ExecutablePath, path));
                if (existing is not null)
                {
                    // discarded/deleted entries are durable suppression records. Manual/verified profiles retain their evidence.
                    if (existing.Source == "automatic" && existing.ValidationStatus is "suggested" or "inconclusive"
                        && (existing.SuggestionScore != entry.Score || !existing.SuggestionReasons.SequenceEqual(entry.Reasons)))
                    {
                        var refreshed = existing with { SuggestionScore = entry.Score, SuggestionReasons = entry.Reasons, Revision = existing.Revision + 1 };
                        _profiles[existing.ProfileId] = refreshed;
                        OnProfileChanged?.Invoke(refreshed);
                    }
                    continue;
                }
                var profile = new LaunchProfile
                {
                    ProfileId = $"profile-{Guid.NewGuid():N}",
                    GameId = gameId,
                    ExecutablePath = path,
                    WorkingDirectory = directory,
                    Arguments = [],
                    Source = "automatic",
                    ValidationStatus = "suggested",
                    SuggestionScore = entry.Score,
                    SuggestionReasons = entry.Reasons,
                };
                _profiles[profile.ProfileId] = profile;
                OnProfileChanged?.Invoke(profile);
            }
        }
    }

    public string? RecommendedProfileId(string gameId)
    {
        lock (_profileLock)
        {
            if (GetDefaultProfile(gameId) is { } chosen) return chosen.ProfileId;
            // Keep excluded entries in ranking: discarding one ambiguous entry never silently selects another.
            var ranked = _profiles.Values.Where(p => p.GameId == gameId && p.Source == "automatic")
                .OrderByDescending(p => p.SuggestionScore).ThenBy(p => p.ProfileId, StringComparer.Ordinal).ToArray();
            if (ranked.Length == 0 || ranked[0].ValidationStatus is "discarded" or "deleted" or "verifying") return null;
            return EntryScoring.MeetsPrescore(new EntryCandidate("", ranked[0].SuggestionScore, []),
                ranked.Length > 1 ? new EntryCandidate("", ranked[1].SuggestionScore, []) : null) ? ranked[0].ProfileId : null;
        }
    }

    public LaunchProfile RestoreSuggestion(string profileId, int expectedRevision)
    {
        lock (_profileLock)
        {
            var profile = _profiles.GetValueOrDefault(profileId) ?? throw new LaunchException(ErrorCodes.NotFound, "启动方式不存在");
            if (profile.Revision != expectedRevision) throw new LaunchException(ErrorCodes.RevisionConflict, "启动方式已更新，请刷新");
            var updated = profile with { ValidationStatus = profile.Source == "automatic" ? "suggested" : "manual", Revision = profile.Revision + 1 };
            _profiles[profileId] = updated;
            OnProfileChanged?.Invoke(updated);
            return updated;
        }
    }

    internal static bool IsBadExecutable(Exception error) => error is Win32Exception { NativeErrorCode: 193 or 216 };
    internal static bool IsCrash(int? exitCode) => exitCode is unchecked((int)0xC0000005) // access violation
        or unchecked((int)0xC000001D) or unchecked((int)0xC0000094) // illegal instruction / divide by zero
        or unchecked((int)0xC00000FD) or unchecked((int)0xC0000409); // stack overflow / fail-fast

    private void CompleteSuggestion(string profileId, int revision, string status)
    {
        lock (_profileLock)
        {
            if (!_profiles.TryGetValue(profileId, out var profile) || profile.Source != "automatic"
                || profile.Revision != revision || profile.ValidationStatus is not ("suggested" or "verifying" or "inconclusive") || IsActiveGame?.Invoke(profile.GameId) == false) return;
            var updated = profile with
            {
                ValidationStatus = status,
                IsDefault = profile.IsDefault || (status == "verified" && GetDefaultProfile(profile.GameId) is null),
                Revision = profile.Revision + 1,
            };
            if (TryCommitValidation?.Invoke(updated, revision) == false) return;
            _profiles[profileId] = updated;
            OnProfileChanged?.Invoke(updated);
        }
    }

    public void CancelObservations()
    {
        foreach (var observation in _observations.Values)
        {
            try { observation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        _observations.Clear();
    }

    private void ObserveSuggestion(LaunchAttempt attempt, Process process, int revision)
    {
        var cancellation = new CancellationTokenSource();
        _observations[attempt.AttemptId] = cancellation;
        var directory = Path.GetDirectoryName(attempt.ExecutablePath)!;
        _ = Task.Run(async () =>
        {
            var tracked = new Dictionary<int, (Process Process, DateTime Started, Stopwatch Alive)>();
            tracked[process.Id] = (process, process.StartTime.ToUniversalTime(), Stopwatch.StartNew());
            var elapsed = Stopwatch.StartNew();
            var needsValidation = GetProfile(attempt.ProfileId)?.ValidationStatus is "suggested" or "inconclusive";
            var resolved = !needsValidation;
            var uncertain = false;
            int? exitCode = null;
            try
            {
                var rootIsGame = false;
                try
                {
                    rootIsGame = process.MainModule?.FileName is { } path
                        && GameLibrary.Infrastructure.Persistence.RuntimeStateStore.ContainsPath(directory, path);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { uncertain = true; }
                using (var lease = AcquireLibraryLease?.Invoke())
                {
                    if (AcquireLibraryLease is not null && lease is null) return;
                    lock (_profileLock)
                    {
                        if (cancellation.IsCancellationRequested) return;
                        if (needsValidation && _profiles.TryGetValue(attempt.ProfileId, out var profile) && profile.Revision == revision)
                        {
                            var verifying = profile with { ValidationStatus = "verifying" };
                            _profiles[profile.ProfileId] = verifying;
                            OnProfileChanged?.Invoke(verifying);
                        }
                    }
                }
                while (!cancellation.IsCancellationRequested)
                {
                    using (var lease = AcquireLibraryLease?.Invoke())
                    {
                        if (AcquireLibraryLease is not null && lease is null) return;
                        if (!_observations.TryGetValue(attempt.AttemptId, out var currentToken) || currentToken != cancellation) return;
                        try { GameProcessTree.Discover(tracked, directory); }
                        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { uncertain = true; }
                        var alive = 0;
                        var longLived = false;
                        foreach (var entry in tracked.Values)
                        {
                            try
                            {
                                entry.Process.Refresh();
                                if (entry.Process.HasExited)
                                {
                                    exitCode = IsCrash(entry.Process.ExitCode) ? entry.Process.ExitCode : exitCode ?? entry.Process.ExitCode;
                                    continue;
                                }
                                if (entry.Process.StartTime.ToUniversalTime() != entry.Started) { uncertain = true; continue; }
                                alive++;
                                longLived |= (entry.Process != process || rootIsGame) && entry.Alive.Elapsed >= VerificationDuration;
                            }
                            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { uncertain = true; alive++; }
                        }
                        if (!resolved && (longLived || alive == 0 || elapsed.Elapsed >= ObservationTimeout))
                        {
                            var status = !uncertain && longLived ? "verified"
                                : !uncertain && alive == 0 && IsCrash(exitCode) ? "discarded" : "inconclusive";
                            CompleteSuggestion(attempt.ProfileId, revision, status);
                            resolved = true;
                        }
                        if (alive == 0)
                        {
                            lock (_refreshLock)
                            {
                                if (_attempts.TryGetValue(attempt.AttemptId, out var current) && !current.IsTerminal)
                                {
                                    var exited = current with { State = "exited", ExitCode = exitCode, FinishedUtc = DateTime.UtcNow };
                                    _attempts[attempt.AttemptId] = exited;
                                    _processes.TryRemove(attempt.AttemptId, out _);
                                    _activeByGame.TryRemove(attempt.GameId, out _);
                                    NotifyAttempt(exited);
                                }
                            }
                            return;
                        }
                    }
                    await Task.Delay(500, cancellation.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception) // Observation failures are uncertain, never grounds for discarding an entry.
            {
                using var lease = AcquireLibraryLease?.Invoke();
                if ((AcquireLibraryLease is null || lease is not null) && !cancellation.IsCancellationRequested)
                    CompleteSuggestion(attempt.ProfileId, revision, "inconclusive");
            }
            finally
            {
                _observations.TryRemove(attempt.AttemptId, out _);
                var stillActive = !cancellation.IsCancellationRequested && _attempts.TryGetValue(attempt.AttemptId, out var current) && !current.IsTerminal;
                if (stillActive) WatchProcessExit(attempt.AttemptId, process);
                foreach (var entry in tracked.Values)
                    if (!stillActive || entry.Process != process) entry.Process.Dispose();
                cancellation.Dispose();
            }
        });
    }
}
