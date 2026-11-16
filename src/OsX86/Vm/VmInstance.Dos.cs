using OsX86.Dos;
using OsX86.Engine;
using OsX86.Interface;

namespace OsX86.Vm;

/// <content>
/// Work item 11: a DOS program in a VM of the kind <see cref="VmKind.Dos"/> (decisions 212 A, 214 A,
/// 166 A). The program comes on its own disk (<see cref="DosProgramDisk"/>, <c>hdb</c>), which only a
/// start can attach: a DOS VM runs one program per start, and stops as soon as it ends.
/// </content>
public sealed partial class VmInstance
{
    /// <summary>
    /// Starts the VM with <paramref name="programDisk"/> as <c>hdb</c> and the keyboard on, for one DOS
    /// program; the session is the application that holds the VM (166 A).
    /// </summary>
    /// <exception cref="X86Exception"><see cref="X86ErrorCodes.VmBusy"/> when the VM runs, <see cref="X86ErrorCodes.ProgramNotSupported"/> when it is not a DOS VM, or a start failure.</exception>
    internal async ValueTask<DosProgramSession> RunDosProgramAsync(byte[] programDisk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(programDisk);
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        DosProgramSession? session = null;
        try
        {
            ThrowIfRetired();
            VmRecord record = Record;
            if (record.Profile.Kind != VmKind.Dos)
            {
                throw new X86Exception(X86ErrorCodes.ProgramNotSupported, $"The virtual machine {record.Name} is not a DOS one: DOS programs run in a virtual machine of the kind dos.");
            }

            if (State != VmState.Stopped)
            {
                throw new X86Exception(X86ErrorCodes.VmBusy, $"The virtual machine {record.Name} is running: a DOS virtual machine runs one program per start. Wait for it to stop, or stop it with `x86 vm stop {record.Name}`.");
            }

            await StartUnderLockAsync(
                cancellationToken,
                options => WithProgramDisk(options, programDisk, record),
                engine => session = new DosProgramSession(this, engine, _time)).ConfigureAwait(false);
            lock (_lock)
            {
                _applications++;
                CancelGraceUnsafe();
                session!.Attach(new VmApplication(this, "dos program", _generation));
            }

            return session!;
        }
        catch
        {
            if (session is not null)
            {
                await session.AbandonAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>
    /// Stops the run <paramref name="generation"/> at once when nothing holds it any more: what a DOS
    /// VM does when its program ends (its program disk cannot change while it runs, so the grace of
    /// 166 A would only keep a VM nobody can use).
    /// </summary>
    internal async Task StopIdleRunAsync(long generation, string reason)
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_generation != generation || _applications > 0 || _held || _state != VmState.Running)
                {
                    return;
                }
            }

            await StopUnderLockAsync(reason).ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private static EngineStartOptions WithProgramDisk(EngineStartOptions options, byte[] programDisk, VmRecord record)
    {
        if (options.Kind != EngineBootKind.Dos || options.Disks.ContainsKey("hdb"))
        {
            throw new X86Exception(X86ErrorCodes.ImageUnavailable, $"The image {record.Profile.Image} {record.Profile.ImageVersion} does not boot DOS from hda alone: it cannot take a program disk.");
        }

        var disks = new Dictionary<string, EngineDiskOptions>(options.Disks, StringComparer.Ordinal)
        {
            ["hdb"] = new EngineDiskOptions(EngineFileSource.InMemory(programDisk), EngineWriteLayer.Discard),
        };
        return options with { Disks = disks, Keyboard = true };
    }
}
