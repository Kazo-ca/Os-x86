namespace OsX86.Agent;

/// <summary>
/// The error codes of the agent protocol. The first group is what the guest agent answers
/// (<c>images/agent/src</c> uses the same strings); the second is what <see cref="AgentClient"/>
/// reports on its own.
/// </summary>
public static class AgentErrorCodes
{
    /// <summary>The agent received a line that is not a JSON object.</summary>
    public const string InvalidJson = "invalid-json";

    /// <summary>The agent received a request longer than it accepts (64 KiB).</summary>
    public const string LineTooLong = "line-too-long";

    /// <summary>The agent does not know the operation.</summary>
    public const string UnknownOperation = "unknown-op";

    /// <summary>A field of the request is missing or out of range.</summary>
    public const string InvalidRequest = "invalid-request";

    /// <summary>The prefix name is missing or malformed.</summary>
    public const string InvalidPrefix = "invalid-prefix";

    /// <summary>The image has no guest user for the prefix.</summary>
    public const string UnknownPrefix = "unknown-prefix";

    /// <summary>The program of a launch does not exist in the guest.</summary>
    public const string ProgramNotFound = "program-not-found";

    /// <summary>The program exists but could not be run.</summary>
    public const string ExecFailed = "exec-failed";

    /// <summary>The cgroup of a launch could not be created.</summary>
    public const string CgroupFailed = "cgroup-failed";

    /// <summary>The guest has no cgroup v2 (the operation needs one).</summary>
    public const string NoCgroup = "no-cgroup";

    /// <summary>The launch does not exist, or is already over.</summary>
    public const string UnknownLaunch = "unknown-launch";

    /// <summary>A kill of the same launch is already under way.</summary>
    public const string KillPending = "kill-pending";

    /// <summary>The process does not exist.</summary>
    public const string UnknownProcess = "unknown-pid";

    /// <summary>The process belongs to root and the request did not allow it.</summary>
    public const string RootProcess = "root-process";

    /// <summary>The signal could not be sent.</summary>
    public const string KillFailed = "kill-failed";

    /// <summary>The image has no X server: windows cannot be listed.</summary>
    public const string NoXServer = "no-x-server";

    /// <summary>The guest has no console port (the second virtio-console port).</summary>
    public const string NoConsolePort = "no-console-port";

    /// <summary>The console does not exist, or is already closed.</summary>
    public const string UnknownConsole = "unknown-console";

    /// <summary>The pseudo-terminal of a console could not be set up.</summary>
    public const string PtyFailed = "pty-failed";

    /// <summary>The agent tracks as many launches as it can.</summary>
    public const string TooManyLaunches = "too-many-launches";

    /// <summary>The agent keeps as many consoles open as it can.</summary>
    public const string TooManyConsoles = "too-many-consoles";

    /// <summary>The agent runs as many commands at a time as it can.</summary>
    public const string TooManyJobs = "too-many-jobs";

    /// <summary>Client: no reply arrived in time.</summary>
    public const string Timeout = "timeout";

    /// <summary>Client: the agent did not answer during the connection (no agent in the image, or the guest is not up).</summary>
    public const string NotReady = "not-ready";

    /// <summary>Client: the channel ended or the client was disposed before the reply.</summary>
    public const string Closed = "closed";

    /// <summary>Client: the agent sent something that does not follow the protocol.</summary>
    public const string Protocol = "protocol";

    /// <summary>Client: the agent speaks another version of the protocol.</summary>
    public const string UnsupportedVersion = "unsupported-version";
}

/// <summary>An error of the agent protocol, with the code of <see cref="AgentErrorCodes"/> that names it.</summary>
public class AgentException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">One of <see cref="AgentErrorCodes"/>, or a code a newer agent invented.</param>
    /// <param name="message">What happened, for logs and the user.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public AgentException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);
        Code = code;
    }

    /// <summary>The error code.</summary>
    public string Code { get; }
}

/// <summary>No reply arrived in time (<see cref="AgentErrorCodes.Timeout"/> or <see cref="AgentErrorCodes.NotReady"/>).</summary>
public sealed class AgentTimeoutException : AgentException
{
    /// <summary>Creates the exception for <paramref name="operation"/> left unanswered for <paramref name="timeout"/>.</summary>
    public AgentTimeoutException(string code, string operation, TimeSpan timeout)
        : base(code, FormattableString.Invariant($"The agent did not answer '{operation}' within {timeout.TotalSeconds:0.###} s."))
    {
        Operation = operation;
        Timeout = timeout;
    }

    /// <summary>The operation that was waiting.</summary>
    public string Operation { get; }

    /// <summary>How long the client waited.</summary>
    public TimeSpan Timeout { get; }
}

/// <summary>The channel ended or the client was disposed while an operation was pending (<see cref="AgentErrorCodes.Closed"/>).</summary>
public sealed class AgentClosedException : AgentException
{
    /// <summary>Creates the exception.</summary>
    public AgentClosedException(string message)
        : base(AgentErrorCodes.Closed, message)
    {
    }
}

/// <summary>The agent sent something that does not follow the protocol (<see cref="AgentErrorCodes.Protocol"/> or <see cref="AgentErrorCodes.UnsupportedVersion"/>).</summary>
public sealed class AgentProtocolException : AgentException
{
    /// <summary>Creates the exception.</summary>
    public AgentProtocolException(string code, string message, Exception? innerException = null)
        : base(code, message, innerException)
    {
    }
}
