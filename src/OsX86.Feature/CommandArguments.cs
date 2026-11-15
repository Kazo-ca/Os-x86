namespace OsX86.Feature;

/// <summary>
/// The arguments of one subcommand: positional words, <c>--name value</c> options and <c>--flag</c>
/// switches, in any order; <c>--</c> ends the options. Options a subcommand does not declare are an
/// error, so a typo never changes what runs.
/// </summary>
internal sealed class CommandArguments
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    private CommandArguments(IReadOnlyList<string> positional) => Positional = positional;

    /// <summary>The positional words.</summary>
    public IReadOnlyList<string> Positional { get; }

    /// <summary>
    /// Parses <paramref name="args"/>. <paramref name="valueOptions"/> take a value (<c>--profile x</c>
    /// or <c>--profile=x</c>); <paramref name="flags"/> do not.
    /// </summary>
    /// <returns>The arguments, or <see langword="null"/> with <paramref name="error"/> set.</returns>
    public static CommandArguments? Parse(IEnumerable<string> args, IReadOnlyCollection<string> valueOptions, IReadOnlyCollection<string> flags, out string? error)
    {
        var positional = new List<string>();
        var parsed = new CommandArguments(positional);
        error = null;
        using IEnumerator<string> enumerator = args.GetEnumerator();
        bool optionsEnded = false;
        while (enumerator.MoveNext())
        {
            string arg = enumerator.Current;
            if (!optionsEnded && arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (optionsEnded || !arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }

            string name = arg[2..];
            string? inline = null;
            int equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals >= 0)
            {
                inline = name[(equals + 1)..];
                name = name[..equals];
            }

            if (flags.Contains(name))
            {
                if (inline is not null)
                {
                    error = $"--{name} takes no value";
                    return null;
                }

                parsed._flags.Add(name);
            }
            else if (valueOptions.Contains(name))
            {
                string? value = inline;
                if (value is null)
                {
                    if (!enumerator.MoveNext())
                    {
                        error = $"--{name} needs a value";
                        return null;
                    }

                    value = enumerator.Current;
                }

                parsed._values[name] = value;
            }
            else
            {
                error = $"unknown option --{name}";
                return null;
            }
        }

        return parsed;
    }

    /// <summary>Whether <c>--name</c> was given.</summary>
    public bool Has(string name) => _flags.Contains(name);

    /// <summary>The value of <c>--name</c>, or <see langword="null"/>.</summary>
    public string? Value(string name) => _values.GetValueOrDefault(name);
}
