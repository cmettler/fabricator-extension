using System.Diagnostics;

namespace Fabricator.Installer;

/// <summary>
/// A whole-file exclusive lock used to serialize extraction between concurrent DuckDB processes
/// (a CI job starting eight workers at once is the normal case, not an edge case).
/// </summary>
internal sealed class CrossProcessLock : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly FileStream _stream;

    private CrossProcessLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Blocks until the lock is held or <paramref name="timeout"/> elapses.
    /// </summary>
    /// <remarks>
    /// The lock file is deliberately never deleted. Unlinking a file while holding a lock on it lets
    /// a second process create a fresh file and lock that instead — two winners — on POSIX, where
    /// .NET implements <see cref="FileShare"/> with advisory <c>flock</c> on the inode. A stray
    /// zero-byte lock file in the extension directory is the cheaper trade.
    /// </remarks>
    internal static CrossProcessLock Acquire(string path, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        IOException? contention = null;

        while (true)
        {
            try
            {
                return new CrossProcessLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or PathTooLongException)
            {
                // Structural, not contention: retrying for the whole timeout would only delay the error.
                throw new InstallerException($"Cannot create the fabricator install lock '{path}': {ex.Message}", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InstallerException(
                    $"No permission to create the fabricator install lock '{path}'. Point DuckDB at a writable " +
                    "extension directory with SET extension_directory='/path/to/dir'.",
                    ex);
            }
            catch (IOException ex)
            {
                contention = ex;
            }

            if (elapsed.Elapsed >= timeout)
            {
                throw new InstallerException(
                    $"Timed out after {timeout.TotalSeconds:0.#}s waiting for the fabricator install lock '{path}'. " +
                    "Another DuckDB process is extracting the payload — retry, or delete that file if no other " +
                    $"process is running. ({contention?.Message})",
                    contention);
            }

            Thread.Sleep(PollInterval);
        }
    }

    public void Dispose() => _stream.Dispose();
}
