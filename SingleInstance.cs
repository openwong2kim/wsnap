using System;
using System.Threading;

namespace Wsnap;

/// <summary>
/// Guarantees one running wsnap. A second launch signals the first to start a
/// capture (so re-running the exe = "take a shot"), then exits immediately.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = "wsnap.singleton.v1";
    private const string SignalName = "wsnap.signal.capture.v1";

    private static Mutex? _mutex;
    private static EventWaitHandle? _signal;
    private static Thread? _listener;

    /// <summary>
    /// Returns true if this is the primary instance. If false, the running instance
    /// has been told to capture and the caller should exit.
    /// </summary>
    public static bool TryAcquire(Action onSecondLaunch)
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool isNew);

        if (!isNew)
        {
            // Already running: poke it, then bail.
            try
            {
                if (EventWaitHandle.TryOpenExisting(SignalName, out var existing))
                    existing!.Set();
            }
            catch { }
            return false;
        }

        // Primary instance: listen for future launches.
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
        _listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _signal.WaitOne();
                    onSecondLaunch();
                }
                catch { break; }
            }
        })
        { IsBackground = true, Name = "wsnap-singleinstance" };
        _listener.Start();
        return true;
    }

    public static void Release()
    {
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        _signal?.Dispose();
    }
}
