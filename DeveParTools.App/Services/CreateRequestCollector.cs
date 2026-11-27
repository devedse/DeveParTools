using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace DeveParTools.App.Services;

/// <summary>
/// Gathers one multi-selection "Create recovery set" from Explorer into a single window.
/// </summary>
/// <remarks>
/// <para>Explorer runs the context menu command once per selected item, so selecting a.jpg, b.jpg and c.jpg starts
/// three processes at almost the same time:</para>
/// <code>
/// deveparui --create a.jpg          → gets the mutex: shows the window and collects    (collector)
/// deveparui --create b.jpg          → mutex taken: sends "b.jpg" over the pipe, exits
/// deveparui --create c.jpg          → mutex taken: sends "c.jpg" over the pipe, exits
///                                      … no new path for IdleTimeout: the collector stops listening
/// </code>
/// <para>The collector is whichever process first owns a named mutex. It listens on a named pipe until no request
/// arrived for <c>idleTimeout</c>, then closes the pipe and releases the mutex, so a later, separate right-click
/// opens a new window. A sender that finds the mutex taken but cannot reach the pipe (the collector was just stopping)
/// tries again to become the collector. The mutex and pipe names include a hash of the user name, and the pipe only
/// accepts connections from the same user (<see cref="PipeOptions.CurrentUserOnly"/>).</para>
/// <para>Wire format, per connection: a 32-bit count, then that many length-prefixed UTF-8 paths
/// (<see cref="BinaryWriter.Write(string)"/>); the collector answers with one byte (1) once it has the paths.</para>
/// </remarks>
public sealed class CreateRequestCollector : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _pending = [];
    private readonly object _lock = new();
    private Action<IReadOnlyList<string>>? _handler;
    private volatile bool _collecting;

    private CreateRequestCollector()
    {
    }

    /// <summary>The per-user name for the app's create requests.</summary>
    public static string DefaultName => "DeveParTools.App.Create." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16];

    /// <summary>True while this process still accepts paths from other processes.</summary>
    public bool IsCollecting => _collecting;

    /// <summary>
    /// Becomes the collector for <paramref name="name"/>, or hands <paramref name="paths"/> to the running collector.
    /// </summary>
    /// <returns>A collector when this process should show the window (when collecting failed it simply does not
    /// collect), or null when the paths were handed over and this process should exit.</returns>
    public static CreateRequestCollector? StartOrForward(string name, IReadOnlyList<string> paths, TimeSpan idleTimeout)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            var collector = new CreateRequestCollector();
            if (collector.TryStart(name, idleTimeout))
            {
                return collector;
            }

            if (TrySend(name, paths, TimeSpan.FromSeconds(2)))
            {
                return null;
            }
        }

        return new CreateRequestCollector();
    }

    /// <summary>
    /// Sets the code that receives the other processes' paths (on the collector's thread). Paths that arrived before
    /// are passed to it right away.
    /// </summary>
    public void Attach(Action<IReadOnlyList<string>> handler)
    {
        List<string> early;
        lock (_lock)
        {
            _handler = handler;
            early = [.. _pending];
            _pending.Clear();
        }

        if (early.Count > 0)
        {
            handler(early);
        }
    }

    /// <summary>Stops collecting (the collector thread still uses the token, so it is cancelled, not disposed).</summary>
    public void Dispose() => _stop.Cancel();

    private bool TryStart(string name, TimeSpan idleTimeout)
    {
        using var decided = new ManualResetEventSlim();
        bool owner = false;
        var thread = new Thread(() =>
        {
            // The mutex must be released by the thread that owns it, so this thread owns, serves and releases it.
            using var mutex = new Mutex(false, name);
            try
            {
                owner = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                owner = true; // the previous collector crashed; the mutex is ours now
            }

            _collecting = owner;
            decided.Set();
            if (!owner)
            {
                return;
            }

            try
            {
                Serve(name, idleTimeout);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The pipe could not be created: this window just does not collect.
            }
            finally
            {
                _collecting = false;
                mutex.ReleaseMutex();
            }
        })
        {
            IsBackground = true,
            Name = "Create request collector",
        };
        thread.Start();
        decided.Wait();
        return owner;
    }

    private void Serve(string name, TimeSpan idleTimeout)
    {
        while (!_stop.IsCancellationRequested)
        {
            using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            idle.CancelAfter(idleTimeout);
            try
            {
                server.WaitForConnectionAsync(idle.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                using var reader = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
                int count = reader.ReadInt32();
                var paths = new List<string>(Math.Clamp(count, 0, 4096));
                for (int i = 0; i < count; i++)
                {
                    paths.Add(reader.ReadString());
                }

                Deliver(paths);
                server.WriteByte(1);
                server.Flush();
            }
            catch (IOException)
            {
                // The sender went away mid-message (EndOfStreamException is an IOException); it retries or opens
                // its own window.
            }
        }
    }

    private void Deliver(List<string> paths)
    {
        Action<IReadOnlyList<string>>? handler;
        lock (_lock)
        {
            handler = _handler;
            if (handler == null)
            {
                _pending.AddRange(paths);
                return;
            }
        }

        handler(paths);
    }

    private static bool TrySend(string name, IReadOnlyList<string> paths, TimeSpan timeout)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            using (var writer = new BinaryWriter(client, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(paths.Count);
                foreach (var p in paths)
                {
                    writer.Write(p);
                }
            }

            client.Flush();
            var ack = new byte[1];
            using var wait = new CancellationTokenSource(timeout);
            return client.ReadAsync(ack, wait.Token).AsTask().GetAwaiter().GetResult() == 1 && ack[0] == 1;
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
