using System.Security.Cryptography;
using System.Text;
using OsX86.Agent;
using OsX86.Engine;
using OsX86.Interface;
using OsX86.Prefixes;

namespace OsX86.Vm;

/// <content>
/// Work item 10: the prefixes of a running VM in its guest, the Wine session of each prefix, and
/// the programs run in a console (decisions 110, 144, 152 A, 193 A, 213 A, 216 A).
/// </content>
public sealed partial class VmInstance
{
    /// <summary>The script of the wine image that starts and waits for the Wine session of a prefix.</summary>
    internal const string SessionProgram = "/usr/lib/osx86/wine/prefix-session";

    /// <summary>Where a program is copied in the home of the prefix's user: inside its Wine prefix, drive C:.</summary>
    internal const string ProgramDirectory = ".wine/drive_c/osx86/run";

    private static readonly Dictionary<string, string> WineEnvironment = new() { ["WINEDEBUG"] = "-all" };

    // One gate per prefix: what is slow in the guest (prefix-admin, a session's wineboot) never
    // holds the other prefixes; one more for the deletions left from a stopped VM.
    private readonly Dictionary<PrefixId, SemaphoreSlim> _prefixGates = [];
    private readonly SemaphoreSlim _deletionsGate = new(1, 1);
    private readonly Dictionary<PrefixId, GuestPrefix> _guestPrefixes = [];
    private readonly Dictionary<PrefixId, WineSession> _sessions = [];
    private bool _pendingDeletionsDone;

    /// <summary>
    /// Runs a prepared program in a console of the guest: the prefix made in the guest when needed,
    /// the file copied into it, the prefix's Wine session started when needed, then the program on a
    /// pseudo-terminal. The console is an application of the VM.
    /// </summary>
    internal async ValueTask<IVmConsole> RunProgramAsync(PreparedProgram program, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(program);
        VmApplication application = await AcquireAsync("program", cancellationToken).ConfigureAwait(false);
        WineSession? session = null;
        try
        {
            AgentClient agent = RequireAgent();
            long run = application.Generation;
            GuestPrefix guest = await EnsureGuestPrefixAsync(agent, program.Prefix, run, cancellationToken).ConfigureAwait(false);
            string relative = await CopyProgramAsync(agent, program, cancellationToken).ConfigureAwait(false);
            string path = $"{guest.Home.TrimEnd('/')}/{relative}";
            session = await AcquireSessionAsync(agent, program.Prefix, run, cancellationToken).ConfigureAwait(false);
            var options = new ConsoleOptions
            {
                Prefix = program.Prefix.Value,
                Program = "wine",
                Arguments = program.IsBatch ? ["cmd.exe", "/c", path, .. program.Arguments] : [path, .. program.Arguments],
                Environment = WineEnvironment,
                WorkingDirectory = path[..path.LastIndexOf('/')],
                Columns = program.Columns,
                Rows = program.Rows,
                Raw = true, // work item 10b: what is typed before Wine's console first reads arrives as typed
            };
            ConsoleHandle handle;
            try
            {
                handle = await agent.OpenConsoleAsync(options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw VmConsoleBase.Translate(exception);
            }

            RegisterChild(handle.Launch, VmChildKind.Program, handle.Exited);
            WineSession held = session;
            session = null;
            LaunchId programLaunch = handle.Launch;
            _ = handle.Exited.ContinueWith(_ => ReleaseSession(held, programLaunch), TaskScheduler.Default);
            return new AgentVmConsole(Id, application, handle, _options.ConsoleCloseGrace);
        }
        catch
        {
            if (session is not null)
            {
                ReleaseSession(session, null);
            }

            application.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Makes a prefix in the guest now when the VM runs (its user and files); a stopped VM makes it
    /// at its first use. Returns whether the guest was reached.
    /// </summary>
    internal async ValueTask<bool> MaterializePrefixAsync(PrefixId prefix, CancellationToken cancellationToken)
    {
        if (CurrentAgent() is not { } agent)
        {
            return false;
        }

        await EnsureGuestPrefixAsync(agent, prefix, CurrentRun(), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Deletes a prefix in the guest when the VM runs: its session ends, its launches are killed, its
    /// user and files are removed. Returns whether the guest was reached (a stopped VM deletes it at
    /// its next start).
    /// </summary>
    internal async ValueTask<bool> DeleteGuestPrefixAsync(PrefixId prefix, CancellationToken cancellationToken)
    {
        if (CurrentAgent() is not { } agent || !agent.HasPrefixOperations)
        {
            return false;
        }

        SemaphoreSlim gate = GateOf(prefix);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_sessions.Remove(prefix, out WineSession? session))
                {
                    session.CancelStop();
                }

                _guestPrefixes.Remove(prefix);
            }

            await AgentCallAsync(() => agent.DeletePrefixAsync(prefix.Value, cancellationToken)).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The prefixes the guest has (its <c>osx86-*</c> users), or <see langword="null"/> when the VM is stopped or its agent has no prefix operations.</summary>
    internal async ValueTask<IReadOnlyList<GuestPrefix>?> ListGuestPrefixesAsync(CancellationToken cancellationToken)
    {
        if (CurrentAgent() is not { HasPrefixOperations: true } agent)
        {
            return null;
        }

        return await AgentCallAsync(() => agent.ListPrefixesAsync(cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Whether a Wine session of the prefix runs now.</summary>
    public bool HasSession(PrefixId prefix)
    {
        lock (_lock)
        {
            return _sessions.TryGetValue(prefix, out WineSession? session) && !session.Exited.IsCompleted;
        }
    }

    /// <summary>
    /// The guest user and home of a prefix, made in the guest when missing (once per run): the
    /// deletions left from a stopped VM are done first. An agent of protocol 1 has no prefix
    /// operations: its image ships its users (the Alpine test image).
    /// </summary>
    private async Task<GuestPrefix> EnsureGuestPrefixAsync(AgentClient agent, PrefixId prefix, long run, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = GateOf(prefix);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                ThrowIfOtherRunUnsafe(run);
                if (_guestPrefixes.TryGetValue(prefix, out GuestPrefix? known))
                {
                    return known;
                }
            }

            if (!agent.HasPrefixOperations)
            {
                return Remember(new GuestPrefix(prefix.Value, -1, "/home/" + prefix.GuestUser));
            }

            await DoPendingDeletionsAsync(agent, run, cancellationToken).ConfigureAwait(false);

            // A prefix deleted meanwhile (its record gone) is not made again: the deletion wins.
            if (!Record.KnowsPrefix(prefix))
            {
                throw new X86Exception(X86ErrorCodes.PrefixNotFound, $"The prefix '{prefix}' was deleted.");
            }

            (GuestPrefix guest, _) = await AgentCallAsync(() => agent.EnsurePrefixAsync(prefix.Value, cancellationToken)).ConfigureAwait(false);
            return Remember(guest);
        }
        finally
        {
            gate.Release();
        }

        GuestPrefix Remember(GuestPrefix guest)
        {
            lock (_lock)
            {
                ThrowIfOtherRunUnsafe(run); // a run that ended under us keeps nothing for the next one
                _guestPrefixes[prefix] = guest;
            }

            return guest;
        }
    }

    private async Task DoPendingDeletionsAsync(AgentClient agent, long run, CancellationToken cancellationToken)
    {
        await _deletionsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool done;
            lock (_lock)
            {
                done = _pendingDeletionsDone;
            }

            IReadOnlyList<PrefixId> pending = Record.EffectivePendingPrefixDeletions;
            if (done || pending.Count == 0)
            {
                return;
            }

            foreach (PrefixId prefix in pending)
            {
                await AgentCallAsync(() => agent.DeletePrefixAsync(prefix.Value, cancellationToken)).ConfigureAwait(false);
            }

            Record = await _manager.Registry.CompletePrefixDeletionsAsync(OwnerId, Id, pending, cancellationToken).ConfigureAwait(false);
            lock (_lock)
            {
                ThrowIfOtherRunUnsafe(run);
                _pendingDeletionsDone = true;
            }
        }
        finally
        {
            _deletionsGate.Release();
        }
    }

    /// <summary>The gate of a prefix, made on first use.</summary>
    private SemaphoreSlim GateOf(PrefixId prefix)
    {
        lock (_lock)
        {
            if (!_prefixGates.TryGetValue(prefix, out SemaphoreSlim? gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _prefixGates[prefix] = gate;
            }

            return gate;
        }
    }

    /// <summary>The run (generation) now.</summary>
    private long CurrentRun()
    {
        lock (_lock)
        {
            return _generation;
        }
    }

    /// <summary>Under the lock: refuses work for a run that ended (its state would leak into the next run).</summary>
    private void ThrowIfOtherRunUnsafe(long run)
    {
        if (_generation != run || _state != VmState.Running)
        {
            throw new X86Exception(X86ErrorCodes.ConsoleClosed, "The virtual machine stopped.");
        }
    }

    /// <summary>
    /// Copies the program into the prefix (<c>C:\osx86\run\&lt;hash&gt;\&lt;name&gt;</c>), unless a
    /// copy of the same bytes is already there (named by their SHA-256, kept with its size).
    /// Returns its path relative to the home. Copies of one prefix go one at a time, and a copy is
    /// written aside then renamed: a program never starts on a file another run is still writing,
    /// and a copy cut short never takes the final name.
    /// </summary>
    private async Task<string> CopyProgramAsync(AgentClient agent, PreparedProgram program, CancellationToken cancellationToken)
    {
        string relative = $"{ProgramDirectory}/{program.ContentHash}/{program.FileName}";
        SemaphoreSlim gate = GateOf(program.Prefix);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ExecResult size = await AgentCallAsync(() => agent.ExecInPrefixAsync(new ExecOptions
            {
                Prefix = program.Prefix.Value,
                Program = "stat",
                Arguments = ["-c", "%s", relative],
            }, cancellationToken)).ConfigureAwait(false);
            if (size.Exit.Code == 0 && size.Output.Trim() == program.Content.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                return relative;
            }

            string part = relative + ".part";
            await AgentCallAsync(async () =>
            {
                await agent.PutFileAsync(program.Prefix.Value, part, program.Content, cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            ExecResult moved = await AgentCallAsync(() => agent.ExecInPrefixAsync(new ExecOptions
            {
                Prefix = program.Prefix.Value,
                Program = "mv",
                Arguments = ["-f", "--", part, relative],
            }, cancellationToken)).ConfigureAwait(false);
            if (moved.Exit.Code != 0)
            {
                throw new X86Exception(X86ErrorCodes.AgentError, $"The copy of {program.FileName} could not be put in place: {moved.Output.Trim()}");
            }

            return relative;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The Wine session of the prefix, started when none runs; its count of programs taken.</summary>
    private async Task<WineSession> AcquireSessionAsync(AgentClient agent, PrefixId prefix, long run, CancellationToken cancellationToken)
    {
        SemaphoreSlim gate = GateOf(prefix);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                ThrowIfOtherRunUnsafe(run);
                if (_sessions.TryGetValue(prefix, out WineSession? running) && !running.Exited.IsCompleted)
                {
                    running.Take();
                    return running;
                }
            }

            string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            LaunchResult launch = await AgentCallAsync(() => agent.LaunchAsync(new LaunchOptions
            {
                Prefix = prefix.Value,
                Program = SessionProgram,
                Arguments = ["start", token],
                Environment = WineEnvironment,
            }, cancellationToken)).ConfigureAwait(false);
            RegisterChild(launch.Id, VmChildKind.Session, launch.Exited);
            try
            {
                await WaitForSessionAsync(agent, prefix, launch, token, cancellationToken).ConfigureAwait(false);
                var session = new WineSession(prefix, launch.Id, launch.Exited);
                session.Take();
                lock (_lock)
                {
                    ThrowIfOtherRunUnsafe(run);
                    _sessions[prefix] = session;
                }

                return session;
            }
            catch
            {
                // Whatever went wrong (refused, cancelled, the run ended), the launch dies: no session lives untracked.
                _ = KillQuietlyAsync(agent, launch.Id);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Returns once the session is up; fails at once when its launch ends first (a broken Wine is not waited for).</summary>
    private async Task WaitForSessionAsync(AgentClient agent, PrefixId prefix, LaunchResult launch, string token, CancellationToken cancellationToken)
    {
        int seconds = (int)_options.PrefixSessionStartTimeout.TotalSeconds;
        Task<ExecResult> wait = AgentCallAsync(() => agent.ExecInPrefixAsync(new ExecOptions
        {
            Prefix = prefix.Value,
            Program = SessionProgram,
            Arguments = ["wait", token, seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            Timeout = _options.PrefixSessionStartTimeout + TimeSpan.FromSeconds(10),
        }, cancellationToken));
        Task first = await Task.WhenAny(wait, launch.Exited).ConfigureAwait(false);
        if (ReferenceEquals(first, launch.Exited))
        {
            _ = wait.ContinueWith(task => _ = task.Exception, TaskScheduler.Default); // observed: the wait ends at its own deadline
            string code = launch.Exited.IsCompletedSuccessfully ? launch.Exited.Result.Code.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
            throw new X86Exception(X86ErrorCodes.StartFailed, $"The Wine session of the prefix '{prefix}' did not start: it ended with code {code}.");
        }

        ExecResult ready = await wait.ConfigureAwait(false);
        if (ready.Exit.Code != 0)
        {
            throw new X86Exception(X86ErrorCodes.StartFailed, $"The Wine session of the prefix '{prefix}' did not start: {ready.Output.Trim()}");
        }
    }

    /// <summary>A program of the session ended: when it was the last one, the session ends after <see cref="VmManagerOptions.PrefixSessionGrace"/>.</summary>
    /// <param name="session">The session.</param>
    /// <param name="ended">The program that ended, which may still be listed among the children (their removal runs apart).</param>
    private void ReleaseSession(WineSession session, LaunchId? ended)
    {
        AgentClient? agent;
        lock (_lock)
        {
            if (session.Release() > 0 || !_sessions.TryGetValue(session.Prefix, out WineSession? current) || !ReferenceEquals(current, session))
            {
                return;
            }

            agent = _agent;
        }

        if (agent is null)
        {
            return;
        }

        void Stop()
        {
            lock (_lock)
            {
                if (session.Programs > 0 || !_sessions.TryGetValue(session.Prefix, out WineSession? current) || !ReferenceEquals(current, session))
                {
                    return;
                }

                // A console of the prefix (x86 shell) may run Wine on this session: it keeps it, and
                // the stop is tried again after the grace.
                if (_options.PrefixSessionGrace > TimeSpan.Zero && _children.Any(child => child.Kind != VmChildKind.Session && child.Launch.Prefix == session.Prefix.Value && child.Launch != ended))
                {
                    session.ScheduleStop(_time.CreateTimer(_ => Stop(), null, _options.PrefixSessionGrace, Timeout.InfiniteTimeSpan));
                    return;
                }

                _sessions.Remove(session.Prefix);
            }

            _ = KillQuietlyAsync(agent, session.Launch);
        }

        if (_options.PrefixSessionGrace == TimeSpan.Zero)
        {
            Stop();
            return;
        }

        lock (_lock)
        {
            session.ScheduleStop(_time.CreateTimer(_ => Stop(), null, _options.PrefixSessionGrace, Timeout.InfiniteTimeSpan));
        }
    }

    private static async Task KillQuietlyAsync(AgentClient agent, LaunchId launch)
    {
        try
        {
            await agent.KillAsync(launch).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Already gone, or the VM is stopping: the session dies with it either way.
        }
    }

    /// <summary>Called under the lock when a run ends: what the guest held is gone with it.</summary>
    private void ResetProgramsUnsafe()
    {
        foreach (WineSession session in _sessions.Values)
        {
            session.CancelStop();
        }

        _sessions.Clear();
        _guestPrefixes.Clear();
        _pendingDeletionsDone = false;
    }

    /// <summary>Flushes the guest's file systems before the engine stops; a failure (an old agent, a guest that does not answer) changes nothing to the stop.</summary>
    private static async Task SyncQuietlyAsync(AgentClient agent)
    {
        if (!agent.HasPrefixOperations)
        {
            return;
        }

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await agent.SyncAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the disk has a journal, and dirty pages reach it within a second anyway.
        }
    }

    private AgentClient? CurrentAgent()
    {
        lock (_lock)
        {
            return _hasAgent == true && !_agentLost && _state == VmState.Running ? _agent : null;
        }
    }

    private AgentClient RequireAgent()
    {
        lock (_lock)
        {
            if (_hasAgent != true || _agent is null)
            {
                throw new X86Exception(X86ErrorCodes.NoConsole, "This image has no guest agent: nothing can be run in it (212 A).");
            }

            if (_agentLost)
            {
                throw new X86Exception(X86ErrorCodes.AgentLost, "The guest agent stopped answering; stop the virtual machine and start it again.");
            }

            return _agent;
        }
    }

    private static async Task<T> AgentCallAsync<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not X86Exception)
        {
            throw VmConsoleBase.Translate(exception);
        }
    }

    /// <summary>The Wine session of a prefix in one run: its launch, its programs, its stop timer.</summary>
    private sealed class WineSession(PrefixId prefix, LaunchId launch, Task<ProcessExit> exited)
    {
        private ITimer? _stop;

        public PrefixId Prefix { get; } = prefix;

        public LaunchId Launch { get; } = launch;

        public Task<ProcessExit> Exited { get; } = exited;

        public int Programs { get; private set; }

        public void Take()
        {
            Programs++;
            CancelStop();
        }

        public int Release() => Programs = Math.Max(0, Programs - 1);

        public void ScheduleStop(ITimer timer)
        {
            CancelStop();
            _stop = timer;
        }

        public void CancelStop()
        {
            _stop?.Dispose();
            _stop = null;
        }
    }
}

/// <summary>A program checked and ready to run (work item 10): its bytes, its name in the guest, the prefix.</summary>
internal sealed record PreparedProgram
{
    /// <summary>The prefix it runs in.</summary>
    public required PrefixId Prefix { get; init; }

    /// <summary>The file name it gets in the guest (made safe: no slash, no control character).</summary>
    public required string FileName { get; init; }

    /// <summary>The bytes.</summary>
    public required ReadOnlyMemory<byte> Content { get; init; }

    /// <summary>The first 16 hexadecimal digits of their SHA-256: the directory of the copy.</summary>
    public string ContentHash => Convert.ToHexStringLower(SHA256.HashData(Content.Span))[..16];

    /// <summary>Whether it is a batch file (run by <c>cmd.exe /c</c>).</summary>
    public bool IsBatch { get; init; }

    /// <summary>Its arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>The terminal's width.</summary>
    public int Columns { get; init; } = 80;

    /// <summary>The terminal's height.</summary>
    public int Rows { get; init; } = 25;

    /// <summary>
    /// The name a file gets in the guest: its last path part, characters other than letters,
    /// digits, dot, dash, underscore and space replaced by <c>_</c> (the extension is kept: Wine
    /// needs it), at most 100 characters.
    /// </summary>
    public static string SafeFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        int slash = Math.Max(fileName.LastIndexOf('/'), fileName.LastIndexOf('\\'));
        string name = fileName[(slash + 1)..].Trim();
        var safe = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            safe.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_');
        }

        string result = safe.ToString().TrimStart('.');
        if (result.Length > 100)
        {
            int dot = result.LastIndexOf('.');
            string extension = dot > 0 && result.Length - dot <= 10 ? result[dot..] : string.Empty;
            result = result[..(100 - extension.Length)] + extension;
        }

        return result.Length == 0 ? "program" : result;
    }
}
