using Kazo.Os.AppSdk;
using OsX86.Interface;
using OsX86.Profiles;

namespace OsX86.Feature;

public sealed partial class X86Command
{
    private async ValueTask<int> ProfileAsync(TerminalExecutionContext context, Guid user, List<string> args, CancellationToken cancellationToken)
    {
        string verb = args.Count > 0 ? args[0] : string.Empty;
        int expected = verb switch
        {
            "list" => 0,
            "show" or "import" => 1,
            "export" => 2,
            _ => -1,
        };
        if (expected < 0)
        {
            return await UsageErrorAsync(context, verb.Length == 0 ? "profile needs a subcommand" : $"unknown command: profile {verb}", cancellationToken).ConfigureAwait(false);
        }

        CommandArguments? parsed = CommandArguments.Parse(args.Skip(1), NoOptions, NoOptions, out string? error);
        if (parsed is null || parsed.Positional.Count != expected)
        {
            return await UsageErrorAsync(context, error ?? $"profile {verb} takes {expected} argument(s)", cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<string> words = parsed.Positional;
        TextWriter output = context.StandardOutput;
        switch (verb)
        {
            case "list":
            {
                IReadOnlyList<VmProfile> profiles = await Manager.Registry.ListProfilesAsync(user, cancellationToken).ConfigureAwait(false);
                var builtIn = new HashSet<VmProfile>(BuiltInProfiles.All);
                await WriteLinesAsync(output, FormatProfiles(profiles, builtIn), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            }

            case "show":
            {
                VmProfile profile = await FindProfileAsync(user, words[0], cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync(profile.ToJson().AsMemory(), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            }

            case "import":
            {
                string json = await _services.Files.ReadTextAsync(words[0], cancellationToken).ConfigureAwait(false)
                    ?? throw new X86Exception(X86ErrorCodes.VmNotFound, $"No profile file {words[0]}.");
                VmProfile profile = VmProfile.Parse(json);
                await Manager.Registry.ImportProfileAsync(user, profile, cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Imported the profile {profile.Name}.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            }

            default:
            {
                VmProfile profile = await FindProfileAsync(user, words[0], cancellationToken).ConfigureAwait(false);
                string file = words[1].EndsWith(VmProfile.FileExtension, StringComparison.OrdinalIgnoreCase) ? words[1] : words[1] + VmProfile.FileExtension;
                await _services.Files.WriteTextAsync(file, profile.ToJson() + "\n", cancellationToken).ConfigureAwait(false);
                await output.WriteLineAsync($"Wrote the profile {profile.Name} to {file}.".AsMemory(), cancellationToken).ConfigureAwait(false);
                return X86ExitCodes.Ok;
            }
        }
    }

    /// <summary>The lines of <c>x86 profile list</c>.</summary>
    internal static IReadOnlyList<string> FormatProfiles(IReadOnlyList<VmProfile> profiles, IReadOnlySet<VmProfile> builtIn)
    {
        List<string> lines = [Row("NAME", "KIND", "IMAGE", "MEMORY", "ORIGIN")];
        lines.AddRange(profiles.Select(p => Row(p.Name, VmKindJsonConverter.ToName(p.Kind), $"{p.Image} {p.ImageVersion}", $"{p.MemoryMiB}M", builtIn.Contains(p) ? "built-in" : "imported")));
        return lines;

        static string Row(string name, string kind, string image, string memory, string origin) =>
            $"{name,-20}  {kind,-16}  {image,-20}  {memory,6}  {origin}".TrimEnd();
    }
}
