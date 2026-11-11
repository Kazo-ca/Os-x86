using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Processes;
using Kazo.Os.Simulation.Abstractions.Sessions;
using OsX86.Feature;

namespace OsX86.Tests;

/// <summary>
/// A minimal <see cref="IAppExecutionContext"/> for command tests: identity only, every gateway
/// unsupported (the command under test must not touch them). Same shape as the test contexts of Kazo OS.
/// </summary>
internal sealed class TestAppContext : IAppExecutionContext
{
    public AppManifest Manifest { get; } = AppDefinitionMapper.ToManifest(new X86FeatureApp());
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string UserId => "user-1";
    public AppAuthority UserAuthority => AppAuthority.User;
    public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>(StringComparer.Ordinal);
    public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
    public ProcessId ProcessId { get; } = ProcessId.FromInt64(100);
    public CancellationToken CancellationToken => CancellationToken.None;
    public ICapabilityChecker Capabilities => throw new NotSupportedException();
    public IAppFileSystemGateway FileSystem => throw new NotSupportedException();
    public IAppEventGateway Events => throw new NotSupportedException();
    public IAppNotificationGateway Notifications => throw new NotSupportedException();
    public IAppLoggingGateway Logging => throw new NotSupportedException();
    public IAppClockGateway Clock => throw new NotSupportedException();
    public IAppProcessGateway Processes => throw new NotSupportedException();

    /// <summary>Builds a terminal context on this app context, with captured output.</summary>
    public static TerminalExecutionContext Terminal(IReadOnlyList<string> arguments, TextWriter stdout, TextWriter stderr) =>
        new(new TestAppContext(), arguments, new StringReader(string.Empty), stdout, stderr, "/home/user-1",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["USER"] = "user-1", ["HOME"] = "/home/user-1" });
}
