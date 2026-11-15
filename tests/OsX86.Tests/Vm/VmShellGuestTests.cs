using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using OsX86.Feature;
using OsX86.Interface;
using OsX86.Tests.Guest;
using OsX86.Vm;
using Xunit.Abstractions;

namespace OsX86.Tests.Vm;

/// <summary>
/// The <c>guest</c> test of work item 6: in a real browser, the Alpine test image with the guest
/// agent is booted by the production <see cref="V86Engine"/> (only the JS interop is replaced by
/// Playwright) under the VM manager, and the <c>x86</c> command drives it: <c>x86 vm create</c>,
/// then <c>x86 shell</c> starts the VM on demand and opens a shell in the guest through the agent;
/// a command answers, Ctrl+C interrupts a program, <c>exit</c> gives its code back, and the VM
/// stops after the grace (166 A). Skipped with a message when the image is not built with the
/// agent or no browser can be launched.
/// </summary>
[Trait("Category", "guest")]
[Collection(X86CommandCollection.Name)]
public sealed class VmShellGuestTests(ITestOutputHelper output)
{
    private static readonly TimeSpan Boot = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);

    [SkippableFact]
    public async Task X86_shell_opens_a_shell_in_the_guest_and_the_vm_stops_after_the_grace()
    {
        string root = Images.PythonTools.RepositoryRoot;
        string imageDirectory = Path.Combine(root, "images", "build", "alpine-test");
        string manifestPath = Path.Combine(imageDirectory, "manifest.json");
        Skip.IfNot(File.Exists(manifestPath), $"Alpine test image not built ({manifestPath} missing): run `python -I images/agent/build-agent.py` then `python -I images/alpine-test/build-alpine-test.py`.");
        using (JsonDocument manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath)))
        {
            Skip.IfNot(manifest.RootElement.TryGetProperty("agent", out JsonElement agent) && agent.ValueKind == JsonValueKind.Object, "The Alpine test image was built without the agent.");
        }

        await using var server = new StaticFileServer(
        [
            ("/engine/", Path.Combine(root, "src", "OsX86", "wwwroot", "engine")),
            ("/v86/", Path.Combine(root, "src", "OsX86", "wwwroot", "v86")),
            ("/image/", imageDirectory),
            ("/page/", Path.Combine(root, "tests", "OsX86.Tests", "Vm")),
        ]);
        server.Start();
        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await GuestBrowser.LaunchAsync(playwright);
        await using IBrowserContext context = await browser.NewContextAsync();
        IPage page = await context.NewPageAsync();
        page.Console += (_, message) => output.WriteLine($"[browser {message.Type}] {message.Text}");
        page.PageError += (_, error) => output.WriteLine($"[browser error] {error}");
        PlaywrightWorkerHub hub = await PlaywrightWorkerHub.AttachAsync(page);
        await page.GotoAsync($"{server.BaseUrl}page/vm.html?host=/engine/v86-host.js&image=/image/");
        await page.WaitForFunctionAsync("() => typeof window.vm === 'object'");
        JsonElement boot = await page.EvaluateAsync<JsonElement>("() => window.vm.prepare()");

        var images = new StaticBootImageSource().Add("alpine-test", "0.1.0", new BootImage(
            boot.GetProperty("kernelPath").GetString()!, boot.GetProperty("initrdPath").GetString()!, boot.GetProperty("cmdline").GetString()!));
        var options = new VmManagerOptions { StopGrace = Grace, AgentWatchdogInterval = TimeSpan.FromSeconds(5) };
        await using var manager = new VmManager(new InMemoryVmRegistryStore(), new PlaywrightEngineFactory(page, hub), images, new FixedHostMemory(8L * 1024 * 1024 * 1024), options);
        var states = new List<(VmState State, TimeSpan At)>();
        long start = Stopwatch.GetTimestamp();
        manager.InstanceStateChanged += (_, e) =>
        {
            lock (states)
            {
                states.Add((e.Data.Current, Stopwatch.GetElapsedTime(start)));
            }
        };
        var services = new X86CommandServices(manager, new X86CommandTests.FakeFiles());
        X86Command.ServicesFactory = () => services;
        try
        {
            var created = new StringWriter();
            Assert.Equal(0, await new X86Command().ExecuteAsync(TestAppContext.Terminal(["vm", "create", "Alpine"], created, new StringWriter()), CancellationToken.None));
            output.WriteLine(created.ToString().TrimEnd());

            var input = new QueueTextReader();
            var terminal = new CapturingWriter();
            var errors = new CapturingWriter();
            start = Stopwatch.GetTimestamp();
            Task<int> shell = new X86Command().ExecuteAsync(TestAppContext.Terminal(["shell"], terminal, errors, input), CancellationToken.None).AsTask();

            try
            {
                await terminal.WaitForAsync("$ ", Boot, shell);
            }
            catch
            {
                output.WriteLine($"x86 shell standard error: {errors.Text}");
                throw;
            }

            output.WriteLine($"x86 shell to the first prompt (boot included): {Stopwatch.GetElapsedTime(start).TotalSeconds:0.0} s");
            VmInstance vm = manager.Running().Single();
            Assert.True(vm.HasAgent);
            Assert.Equal(VmChildKind.Console, Assert.Single(vm.Children).Kind);

            // A command answers, as the prefix user, and its echo is not doubled.
            input.Add("echo vm-$((6*7))-$(id -u)");
            await terminal.WaitForAsync("vm-42-1001\n", Soon, shell);
            Assert.DoesNotContain("echo vm-", terminal.Text, StringComparison.Ordinal);

            // Ctrl+C interrupts the program in the foreground (216 A), the shell stays.
            input.Add("sleep 600");
            await Task.Delay(1000);
            int mark = terminal.Text.Length;
            long interrupt = Stopwatch.GetTimestamp();
            input.Add(X86Command.InterruptLine);
            await terminal.WaitForAsync("$ ", Soon, shell, mark);
            output.WriteLine($"Ctrl+C to the next prompt: {Stopwatch.GetElapsedTime(interrupt).TotalMilliseconds:0} ms");
            input.Add("echo status-$?");
            await terminal.WaitForAsync("status-130\n", Soon, shell);

            output.WriteLine($"agent ping by the watchdog or on demand: {(await vm.PingAgentAsync(CancellationToken.None)).TotalMilliseconds:0.0} ms");

            // exit gives its code back; the shell was the last application: the VM stops after the grace.
            input.Add("exit 5");
            Assert.Equal(5, await shell.WaitAsync(Soon));
            Assert.Equal(string.Empty, errors.Text);
            TimeSpan exited = Stopwatch.GetElapsedTime(start);
            Assert.Equal(0, vm.Applications);
            Assert.Equal(VmState.Running, vm.State);
            long deadline = Stopwatch.GetTimestamp();
            while (vm.State != VmState.Stopped)
            {
                Assert.True(Stopwatch.GetElapsedTime(deadline) < Grace + Soon, "the VM did not stop after the grace");
                await Task.Delay(100);
            }

            TimeSpan stopping;
            lock (states)
            {
                stopping = states.Last(s => s.State == VmState.Stopping).At;
            }

            output.WriteLine($"exit to the start of the stop: {(stopping - exited).TotalSeconds:0.0} s (grace {Grace.TotalSeconds:0} s)");
            Assert.True(stopping - exited >= Grace - TimeSpan.FromMilliseconds(200), "the VM stopped before the grace");
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.vm.hostCount"));
            output.WriteLine(terminal.Text);
        }
        finally
        {
            X86Command.ServicesFactory = static () => X86Runtime.Services;
        }
    }

    /// <summary>A standard output the test reads while the command writes it.</summary>
    private sealed class CapturingWriter : TextWriter
    {
        private readonly StringBuilder _text = new();
        private readonly Lock _lock = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get
            {
                lock (_lock)
                {
                    return _text.ToString();
                }
            }
        }

        public override void Write(char value)
        {
            lock (_lock)
            {
                _text.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_lock)
            {
                _text.Append(value);
            }
        }

        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _text.Append(buffer.Span);
            }

            return Task.CompletedTask;
        }

        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _text.Append(buffer.Span).Append('\n');
            }

            return Task.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task WaitForAsync(string needle, TimeSpan timeout, Task<int> command, int from = 0)
        {
            long start = Stopwatch.GetTimestamp();
            while (Text.IndexOf(needle, Math.Min(from, Text.Length), StringComparison.Ordinal) < 0)
            {
                Assert.False(command.IsCompleted, $"the command ended ({(command.IsCompletedSuccessfully ? command.Result.ToString() : command.Exception?.Message)}) before {needle.Trim()}; output: {Text}");
                Assert.True(Stopwatch.GetElapsedTime(start) < timeout, $"timed out waiting for {needle.Trim()}; output: {Text}");
                await Task.Delay(50);
            }
        }
    }
}
