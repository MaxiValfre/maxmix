namespace MaxMix.App.Services;

using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Enforces single-instance via a named system mutex AND provides a tiny
/// named-pipe channel so a second-launched instance can ping the first one
/// to surface its window.
///
/// Protocol:
///   - First launch: <see cref="TryAcquire"/> returns true and the instance
///     starts a background pipe listener on <see cref="MutexName"/>.
///   - Second launch: <see cref="TryAcquire"/> returns false; caller invokes
///     <see cref="NotifyExistingInstance"/> which writes one byte to the
///     pipe and exits.
///
/// The named-pipe approach is deliberate: it works without elevation, has no
/// firewall implications, and survives across user sessions on the same
/// machine without needing window-message hacks.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    /// <summary>Mutex + pipe name. Includes a GUID so different builds don't collide.</summary>
    public const string MutexName = "MaxMix.SingleInstance.{B7D0CCB7-7DB8-4C4D-9E8E-9F8C2C0E4CFB}";

    private Mutex? _mutex;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;
    private bool _disposed;

    /// <summary>
    /// Raised when a second instance signals us. UI thread marshalling is the
    /// caller's responsibility.
    /// </summary>
    public event EventHandler? SecondInstanceLaunched;

    /// <summary>
    /// Tries to acquire the single-instance mutex. Returns true if THIS process
    /// is the primary instance; false if another instance already holds it.
    /// </summary>
    public bool TryAcquire(string mutexName)
    {
        if (_mutex is not null) return true;

        // createdNew is true iff WE created the mutex (i.e. we're first).
        _mutex = new Mutex(initiallyOwned: true, name: mutexName, createdNew: out bool createdNew);
        if (!createdNew)
        {
            // Release any handle we held very briefly so the existing owner
            // is unaffected.
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        StartPipeListener(mutexName);
        return true;
    }

    /// <summary>
    /// Send a one-byte ping to the existing primary instance, asking it to
    /// surface its window. Best-effort: failures are silent.
    /// </summary>
    public void NotifyExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", MutexName, PipeDirection.Out);
            client.Connect(timeout: 500);
            client.WriteByte(1);
        }
        catch
        {
            // Existing instance might be torn down between mutex check and
            // pipe connect. Either way, nothing useful to do here.
        }
    }

    private void StartPipeListener(string pipeName)
    {
        _listenerCts = new CancellationTokenSource();
        var ct = _listenerCts.Token;

        _listenerTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                NamedPipeServerStream? server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        pipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    // We don't actually care about the byte — the connection
                    // itself is the signal. Drain anyway to be polite.
                    var buffer = new byte[1];
                    _ = await server.ReadAsync(buffer, ct).ConfigureAwait(false);
                    SecondInstanceLaunched?.Invoke(this, EventArgs.Empty);
                }
                catch (OperationCanceledException) { /* shutting down */ }
                catch (IOException) { /* pipe broken — recreate */ }
                catch (ObjectDisposedException) { /* shutting down */ }
                finally
                {
                    server?.Dispose();
                }
            }
        }, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _listenerCts?.Cancel(); } catch { }
        try { _listenerTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _listenerCts?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { /* not held */ }
        _mutex?.Dispose();
    }
}
