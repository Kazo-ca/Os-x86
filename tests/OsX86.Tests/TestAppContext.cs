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
internal sealed class TestAppContext(string userId) : IAppExecutionContext
{
    /// <summary>The stable identifier Kazo gives a user (decision 190 A): 32 hexadecimal digits.</summary>
    public const string DefaultUserId = "0000000000000000000000000000a001";

    public TestAppContext()
        : this(DefaultUserId)
    {
    }

    public AppManifest Manifest { get; } = AppDefinitionMapper.ToManifest(new X86FeatureApp());
    public Guid InstanceId { get; } = Guid.NewGuid();
    public string UserId { get; } = userId;
    public AppAuthority UserAuthority => AppAuthority.User;
    public IReadOnlySet<string> GrantedCapabilities { get; } = new HashSet<string>(StringComparer.Ordinal);
    public SessionId SessionId { get; } = SessionId.FromGuid(Guid.NewGuid());
    public ProcessId ProcessId { get; } = ProcessId.FromInt64(100);
    public CancellationToken CancellationToken { get; init; } = CancellationToken.None;
    public ICapabilityChecker Capabilities => throw new NotSupportedException();
    public IAppFileSystemGateway FileSystem => throw new NotSupportedException();
    public IAppEventGateway Events => throw new NotSupportedException();
    public IAppNotificationGateway Notifications => throw new NotSupportedException();
    public IAppLoggingGateway Logging => throw new NotSupportedException();
    public IAppClockGateway Clock => throw new NotSupportedException();
    public IAppProcessGateway Processes => throw new NotSupportedException();

    /// <summary>Builds a terminal context on this app context, with captured output.</summary>
    public static TerminalExecutionContext Terminal(IReadOnlyList<string> arguments, TextWriter stdout, TextWriter stderr, TextReader? stdin = null, string userId = DefaultUserId, CancellationToken process = default, IFullScreenTerminalSession? fullScreen = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["USER"] = "user-1", ["HOME"] = "/home/user-1" };
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            variables[name] = value;
        }

        return new(new TestAppContext(userId) { CancellationToken = process }, arguments, stdin ?? new StringReader(string.Empty), stdout, stderr, "/home/user-1", variables, fullScreen);
    }
}
