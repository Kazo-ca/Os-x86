using System.Formats.Tar;
using System.Text;
using System.Text.Json;

namespace OsX86.Tests.Images;

/// <summary>
/// <c>images/tools/image-check.py</c> (decision 210 A): the image test on an exported tree. A
/// synthetic <c>docker export</c> style tar is built with the real <c>init.sh</c> of linux-base,
/// then broken in the ways the 22 R134.5 checks of trial E7 and the 210 A inventory must catch.
/// </summary>
[Trait("Category", "unit")]
public sealed class ImageCheckTests : IDisposable
{
    private const int Dir755 = 0b111_101_101;
    private const int File644 = 0b110_100_100;
    private const int Sticky1777 = 0b001_111_111_111;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "osx86-check-" + Guid.NewGuid().ToString("N"));

    public ImageCheckTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [SkippableFact]
    public void A_hardened_tree_holds_the_22_checks_and_the_inventory()
    {
        string tar = WriteTar("good.tar", _ => { });
        (int exitCode, string output) = Run(tar, out JsonElement report);
        Assert.True(exitCode == 0, output);
        Assert.True(report.GetProperty("ok").GetBoolean());
        Assert.Equal(22, report.GetProperty("r134_5").GetProperty("total").GetInt32());
        Assert.Equal(22, report.GetProperty("r134_5").GetProperty("held").GetInt32());
        Assert.Equal("0755", report.GetProperty("inventory").GetProperty("rootMode").GetString());
        Assert.Equal(new[] { "tmp", "var/tmp" }, report.GetProperty("inventory").GetProperty("worldWritable").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [SkippableFact]
    public void The_init_is_found_through_the_merged_usr_symlinks_of_debian_and_comments_are_not_evidence()
    {
        // Debian trixie: /sbin is a symlink to usr/sbin, so the init of the export is usr/sbin/init.
        string tar = WriteTar("symlinks.tar", entries =>
        {
            entries.RemoveAll(e => e.Path is "sbin" or "sbin/init");
            entries.Add(("usr/sbin", TarEntryType.Directory, Dir755, null));
            entries.Add(("usr/sbin/init", TarEntryType.RegularFile, Dir755, File.ReadAllText(Path.Combine(PythonTools.RepositoryRoot, "images", "linux-base", "init.sh"))));
            entries.Add(("sbin", TarEntryType.SymbolicLink, 0b111_111_111, "usr/sbin"));
        });
        (int exitCode, string output) = Run(tar, out JsonElement report);
        Assert.True(exitCode == 0, output);
        Assert.Equal(22, report.GetProperty("r134_5").GetProperty("held").GetInt32());

        // The same init with its sysctl line commented out: the namespace checks must break.
        string commented = WriteTar("commented.tar", entries =>
        {
            int index = entries.FindIndex(e => e.Path == "sbin/init");
            string init = entries[index].Content!.Replace("sysctl -q -w", "# sysctl -q -w", StringComparison.Ordinal);
            entries[index] = ("sbin/init", TarEntryType.RegularFile, Dir755, init);
        });
        (exitCode, _) = Run(commented, out report);
        Assert.NotEqual(0, exitCode);
        Assert.Equal("LEAK", Status(report, "unshare_userns"));
    }

    [SkippableFact]
    public void The_agent_is_inventoried_and_must_not_be_replaceable()
    {
        // No agent in the tree: nothing to break, an empty inventory.
        (int exitCode, _) = Run(WriteTar("no-agent.tar", _ => { }), out JsonElement report);
        Assert.Equal(0, exitCode);
        Assert.Equal("HELD", Status(report, "agent_files_root_only"));
        Assert.Empty(report.GetProperty("inventory").GetProperty("agent").EnumerateObject());

        // The agent as the recipe installs it: 0755, root.
        string installed = WriteTar("agent.tar", entries =>
        {
            entries.Add(("usr/lib/osx86/agent", TarEntryType.RegularFile, Dir755, "binary"));
            entries.Add(("usr/bin/osx86-open-url", TarEntryType.RegularFile, Dir755, "binary"));
        });
        (exitCode, string output) = Run(installed, out report);
        Assert.True(exitCode == 0, output);
        Assert.Equal(22, report.GetProperty("r134_5").GetProperty("held").GetInt32());
        Assert.Equal("0755", report.GetProperty("inventory").GetProperty("agent").GetProperty("usr/lib/osx86/agent").GetProperty("mode").GetString());

        // Writable by others: any guest user could put its own program in root's place.
        string writable = WriteTar("agent-writable.tar", entries => entries.Add(("usr/lib/osx86/agent", TarEntryType.RegularFile, 0b111_101_111, "binary")));
        (exitCode, output) = Run(writable, out report);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("agent_files_root_only", output, StringComparison.Ordinal);
        Assert.Equal("LEAK", Status(report, "agent_files_root_only"));

        // Not executable: the init would silently start nothing.
        string inert = WriteTar("agent-inert.tar", entries => entries.Add(("usr/lib/osx86/agent", TarEntryType.RegularFile, File644, "binary")));
        (exitCode, _) = Run(inert, out report);
        Assert.NotEqual(0, exitCode);
        Assert.Equal("LEAK", Status(report, "agent_files_root_only"));
    }

    [SkippableFact]
    public void A_setuid_program_breaks_no_setuid_and_the_checks_that_rest_on_it()
    {
        string tar = WriteTar("setuid.tar", entries => entries.Add(("usr/bin/su", TarEntryType.RegularFile, 0b100_111_101_101, "binary")));
        (int exitCode, string output) = Run(tar, out JsonElement report);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("no_setuid", output, StringComparison.Ordinal);
        Assert.Equal("LEAK", Status(report, "no_setuid"));
        Assert.Equal("LEAK", Status(report, "raw_socket"));
        Assert.Equal("HELD", Status(report, "unshare_userns"));
    }

    [SkippableFact]
    public void A_world_writable_directory_outside_tmp_breaks_the_inventory()
    {
        string tar = WriteTar("runlock.tar", entries => entries.Add(("run/lock", TarEntryType.Directory, Sticky1777, null)));
        (int exitCode, string output) = Run(tar, out JsonElement report);
        Assert.NotEqual(0, exitCode);
        Assert.Contains("run/lock", output, StringComparison.Ordinal);
        Assert.Equal("LEAK", Status(report, "world_writable_inventory"));
    }

    [SkippableFact]
    public void An_init_without_the_sysctls_and_mount_options_breaks_the_namespace_and_nosuid_checks()
    {
        string tar = WriteTar("weak-init.tar", entries =>
        {
            entries.RemoveAll(e => e.Path == "sbin/init");
            entries.Add(("sbin/init", TarEntryType.RegularFile, Dir755,
                "#!/bin/sh\nmount -t proc proc /proc\nmount -t tmpfs tmpfs /tmp\nmount -t tmpfs tmpfs /home\nmount -t tmpfs tmpfs /run\nexec sh\n"));
        });
        (int exitCode, _) = Run(tar, out JsonElement report);
        Assert.NotEqual(0, exitCode);
        Assert.Equal("LEAK", Status(report, "unshare_userns"));
        Assert.Equal("LEAK", Status(report, "home_nosuid"));
        Assert.Equal("LEAK", Status(report, "tmp_nosuid"));
        Assert.Equal("LEAK", Status(report, "run_mode_0755"));
        Assert.Equal("LEAK", Status(report, "ptrace_other"));
        Assert.Equal("LEAK", Status(report, "display_input_devices_0600"));
    }

    [SkippableFact]
    public void An_open_root_home_breaks_the_other_prefix_checks()
    {
        string tar = WriteTar("open-root.tar", entries =>
        {
            entries.RemoveAll(e => e.Path == "root");
            entries.Add(("root", TarEntryType.Directory, Dir755, null));
        });
        (int exitCode, _) = Run(tar, out JsonElement report);
        Assert.NotEqual(0, exitCode);
        Assert.Equal("LEAK", Status(report, "other_bottle_home"));
        Assert.Equal("LEAK", Status(report, "other_bottle_prefix"));
    }

    private (int ExitCode, string Output) Run(string tar, out JsonElement report)
    {
        string reportPath = tar + ".json";
        (int exitCode, string output) = PythonTools.Run(PythonTools.ImageCheck, "--tar", tar, "--report", reportPath);
        report = JsonDocument.Parse(File.ReadAllText(reportPath)).RootElement.Clone();
        return (exitCode, output);
    }

    private static string Status(JsonElement report, string id) =>
        report.GetProperty("controls").EnumerateArray().Single(c => c.GetProperty("id").GetString() == id).GetProperty("status").GetString()!;

    /// <summary>A minimal hardened tree: the real init of linux-base, 0700 root home, sticky tmp dirs, login.defs, adduser.conf, the probes.</summary>
    private string WriteTar(string name, Action<List<(string Path, TarEntryType Type, int Mode, string? Content)>> mutate)
    {
        string init = File.ReadAllText(Path.Combine(PythonTools.RepositoryRoot, "images", "linux-base", "init.sh"));
        var entries = new List<(string Path, TarEntryType Type, int Mode, string? Content)>
        {
            ("", TarEntryType.Directory, Dir755, null),
            ("sbin", TarEntryType.Directory, Dir755, null),
            ("sbin/init", TarEntryType.RegularFile, Dir755, init),
            ("etc", TarEntryType.Directory, Dir755, null),
            ("etc/login.defs", TarEntryType.RegularFile, File644, "UMASK 022\nHOME_MODE\t0700\n"),
            ("etc/adduser.conf", TarEntryType.RegularFile, File644, "DIR_MODE=0700\n"),
            ("root", TarEntryType.Directory, 0b111_000_000, null),
            ("home", TarEntryType.Directory, Dir755, null),
            ("tmp", TarEntryType.Directory, Sticky1777, null),
            ("var", TarEntryType.Directory, Dir755, null),
            ("var/tmp", TarEntryType.Directory, Sticky1777, null),
            ("run", TarEntryType.Directory, Dir755, null),
            ("usr", TarEntryType.Directory, Dir755, null),
            ("usr/bin", TarEntryType.Directory, Dir755, null),
            ("usr/bin/sh", TarEntryType.RegularFile, Dir755, "binary"),
            ("usr/lib", TarEntryType.Directory, Dir755, null),
            ("usr/lib/osx86", TarEntryType.Directory, Dir755, null),
            ("usr/lib/osx86/verify-console.sh", TarEntryType.RegularFile, Dir755, "#!/bin/sh\n"),
            ("usr/lib/osx86/osx86-rawsock", TarEntryType.RegularFile, Dir755, "binary"),
            ("usr/lib/osx86/osx86-ptrace", TarEntryType.RegularFile, Dir755, "binary"),
        };
        mutate(entries);

        string path = Path.Combine(_directory, name);
        using FileStream stream = File.Create(path);
        using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: false);
        foreach ((string entryPath, TarEntryType type, int mode, string? content) in entries)
        {
            var entry = new PaxTarEntry(type, entryPath == "" ? "./" : "./" + entryPath)
            {
                Mode = (UnixFileMode)mode,
            };
            if (type == TarEntryType.SymbolicLink)
            {
                entry.LinkName = content!;
            }
            else if (content is not null)
            {
                entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content));
            }

            writer.WriteEntry(entry);
        }

        return path;
    }
}
