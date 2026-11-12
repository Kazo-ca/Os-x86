using System.Diagnostics;

namespace OsX86.Tests.Images;

/// <summary>
/// Runs the Python tools of <c>images/tools/</c> (work item 4) from the tests, with an isolated
/// interpreter (<c>-I</c>). A Python with zstd (3.14 and its <c>compression.zstd</c>, or the
/// <c>zstandard</c> package) is looked for once; without one the tests are skipped locally and
/// fail in the CI (<c>CI</c> environment variable), where the workflow installs Python 3.14.
/// </summary>
internal static class PythonTools
{
    private static readonly Lazy<string?> Interpreter = new(FindInterpreter);

    public static string RepositoryRoot { get; } = FindRepositoryRoot();

    public static string PackImage => Path.Combine(RepositoryRoot, "images", "tools", "pack-image.py");

    public static string ImageCheck => Path.Combine(RepositoryRoot, "images", "tools", "image-check.py");

    /// <summary>The interpreter, or skips (locally) or fails (CI) the calling test.</summary>
    public static string Require()
    {
        string? python = Interpreter.Value;
        if (python is null)
        {
            const string message = "No Python with zstd found (Python 3.14, or the zstandard package): the image tools cannot run.";
            Skip.If(string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")), message);
            throw new InvalidOperationException(message);
        }

        return python;
    }

    /// <summary>Runs a tool and returns (exit code, stdout + stderr).</summary>
    public static (int ExitCode, string Output) Run(string script, params string[] arguments)
    {
        var start = new ProcessStartInfo(Require())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepositoryRoot,
        };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(script);
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("python did not start");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    public static string RunOrThrow(string script, params string[] arguments)
    {
        (int exitCode, string output) = Run(script, arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(script)} {string.Join(' ', arguments)} exited with {exitCode}:\n{output}");
        }

        return output;
    }

    private static string? FindInterpreter()
    {
        string? configured = Environment.GetEnvironmentVariable("OSX86_PYTHON");
        foreach (string candidate in new[] { configured, "python3", "python", "py" }.OfType<string>().Where(c => c.Length > 0))
        {
            try
            {
                var start = new ProcessStartInfo(candidate)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                start.ArgumentList.Add("-I");
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add("try:\n import compression.zstd\nexcept ImportError:\n import zstandard\nprint('ok')");
                using Process? process = Process.Start(start);
                if (process is null)
                {
                    continue;
                }

                string output = process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
                process.WaitForExit(30_000);
                if (process.ExitCode == 0 && output.Contains("ok", StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // not installed under that name
            }
        }

        return null;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "OsX86.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("OsX86.sln not found above the test directory");
    }
}
