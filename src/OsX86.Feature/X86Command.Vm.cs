using System.Globalization;
using Kazo.Os.AppSdk;
using OsX86.Interface;
using OsX86.Profiles;
using OsX86.Vm;

namespace OsX86.Feature;

public sealed partial class X86Command
{
    /// <summary>The profile <c>x86 vm create</c> uses without <c>--profile</c> (work item 6b: the Linux base image; alpine-test stays for the tests).</summary>
    internal const string DefaultProfile = "linux-base";

    private static readonly string[] NoOptions = [];

    private async ValueTask<int> VmAsync(TerminalExecutionContext context, Guid user, List<string> args, CancellationToken cancellationToken)
    {
        string verb = args.Count > 0 ? args[0] : string.Empty;
        IEnumerable<string> rest = args.Skip(1);
        (string[] values, string[] flags, int positional) = verb switch
        {
            "list" => (NoOptions, NoOptions, 0),
            "create" => (["profile"], NoOptions, 1),
            "start" or "info" => (NoOptions, NoOptions, 1),
            "stop" or "remove" => (NoOptions, ["force"], 1),
            "rename" => (NoOptions, NoOptions, 2),
            _ => (NoOptions, NoOptions, -1),
        };
        if (positional < 0)
        {
            return await UsageErrorAsync(context, verb.Length == 0 ? "vm needs a subcommand" : $"unknown command: vm {verb}", cancellationToken).ConfigureAwait(false);
        }

        CommandArguments? parsed = CommandArguments.Parse(rest, values, flags, out string? error);
        if (parsed is null || parsed.Positional.Count != positional)
        {
            return await UsageErrorAsync(context, error ?? $"vm {verb} takes {positional} argument(s)", cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<string> words = parsed.Positional;
        TextWriter output = context.StandardOutput;
        switch (verb)
        {
            case "list":
                await WriteLinesAsync(output, FormatList(await Manager.ListAsync(user, cancellationToken).ConfigureAwait(false)), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            case "create":
            {
                VmProfile profile = await FindProfileAsync(user, parsed.Value("profile") ?? DefaultProfile, cancellationToken).ConfigureAwait(false);
                VmSummary created = await Manager.CreateAsync(user, words[0], profile, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Created {created.Name} ({created.Id}) from the profile {profile.Name}.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            }

            case "start":
            {
                VmRecord vm = await Manager.ResolveAsync(user, words[0], cancellationToken).ConfigureAwait(false);
                await WithImageProgressAsync(context, user, vm.Id, () => Manager.HoldAsync(user, vm.Id, cancellationToken).AsTask()).ConfigureAwait(false);
                await output.WriteLineAsync($"Started {vm.Name}; it keeps running until `x86 vm stop {vm.Name}`.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            }

            case "stop":
            {
                VmRecord vm = await Manager.ResolveAsync(user, words[0], cancellationToken).ConfigureAwait(false);
                await Manager.StopAsync(user, vm.Id, parsed.Has("force"), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Stopped {vm.Name}.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            }

            case "remove":
            {
                VmRecord vm = await Manager.ResolveAsync(user, words[0], cancellationToken).ConfigureAwait(false);
                await Manager.RemoveAsync(user, vm.Id, parsed.Has("force"), cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Removed {vm.Name} ({vm.Id}).".AsMemory(), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            }

            case "rename":
            {
                VmRecord vm = await Manager.ResolveAsync(user, words[0], cancellationToken).ConfigureAwait(false);
                VmSummary renamed = await Manager.RenameAsync(user, vm.Id, words[1], cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Renamed {vm.Name} to {renamed.Name}.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            }

            default:
            {
                VmRecord vm = await Manager.ResolveAsync(user, words[0], cancellationToken).ConfigureAwait(false);
                VmInstance instance = await Manager.GetInstanceAsync(user, vm.Id, cancellationToken).ConfigureAwait(false);
                await WriteLinesAsync(output, FormatInfo(instance, Manager), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            }
        }
    }

    /// <summary>A profile by name (built-in or imported), or read from a <c>.x86profile</c> file when the name ends so.</summary>
    private async ValueTask<VmProfile> FindProfileAsync(Guid user, string nameOrFile, CancellationToken cancellationToken)
    {
        if (nameOrFile.EndsWith(VmProfile.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            string json = await _services.Files.ReadTextAsync(nameOrFile, cancellationToken).ConfigureAwait(false)
                ?? throw new X86Exception(X86ErrorCodes.VmNotFound, $"No profile file {nameOrFile}.");
            return VmProfile.Parse(json);
        }

        return await Manager.Registry.FindProfileAsync(user, nameOrFile, cancellationToken).ConfigureAwait(false)
            ?? throw new X86Exception(X86ErrorCodes.VmNotFound, $"No profile named '{nameOrFile}': see `x86 profile list`.");
    }

    /// <summary>The lines of <c>x86 vm list</c>: a header, then one VM per line, columns aligned.</summary>
    internal static IReadOnlyList<string> FormatList(IReadOnlyList<VmSummary> vms)
    {
        if (vms.Count == 0)
        {
            return ["No virtual machine."];
        }

        List<string> lines = [Row("ID", "KIND", "STATE", "MEMORY", "IMAGE", "NAME")];
        lines.AddRange(vms.Select(vm => Row(vm.Id.ToString(), VmKindJsonConverter.ToName(vm.Kind), StateName(vm.State), $"{vm.MemoryMiB}M", $"{vm.Image} {vm.ImageVersion}", vm.Name)));
        return lines;

        static string Row(string id, string kind, string state, string memory, string image, string name) =>
            $"{id,-36}  {kind,-16}  {state,-8}  {memory,6}  {image,-20}  {name}".TrimEnd();
    }

    /// <summary>The lines of <c>x86 vm info</c>.</summary>
    internal static IReadOnlyList<string> FormatInfo(VmInstance instance, VmManager manager)
    {
        VmRecord vm = instance.Record;
        VmProfile profile = vm.Profile;
        int used = manager.Running().Sum(running => running.Record.Profile.MemoryMiB);
        string agent = instance.HasAgent switch
        {
            true when instance.IsAgentLost => "lost (no answer to the watchdog)",
            true => instance.LastPingRoundTrip is { } ping ? $"yes (last ping {ping.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture)} ms)" : "yes",
            false => profile.SerialConsole ? "no (serial console)" : "no",
            null => "unknown until it runs",
        };
        List<string> lines =
        [
            $"Name:          {vm.Name}",
            $"ID:            {vm.Id}",
            $"Kind:          {VmKindJsonConverter.ToName(profile.Kind)}",
            $"State:         {StateName(instance.State)}{(instance.IsHeld ? " (started by hand)" : string.Empty)}",
            $"Image:         {profile.Image} {profile.ImageVersion}",
            $"Memory:        {profile.MemoryMiB} MiB",
            $"Serial port:   {(profile.SerialConsole ? "declared" : "none")}",
            $"Prefix:        {profile.Prefix ?? "(none)"}",
            $"Created:       {vm.Created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}",
            $"Agent:         {agent}",
            $"Applications:  {instance.Applications}",
            $"Budget:        {used} of {manager.Budget.LimitMiB} MiB used by running virtual machines",
        ];
        IReadOnlyList<VmChild> children = instance.Children;
        lines.Add($"Children:      {children.Count}");
        lines.AddRange(children.Select(child => $"  {child.Launch}  {(child.Kind == VmChildKind.Console ? "console" : "program")}"));
        return lines;
    }

    private static string StateName(VmState state) => state switch
    {
        VmState.Stopped => "stopped",
        VmState.Starting => "starting",
        VmState.Running => "running",
        VmState.Stopping => "stopping",
        _ => state.ToString().ToLowerInvariant(),
    };
}
