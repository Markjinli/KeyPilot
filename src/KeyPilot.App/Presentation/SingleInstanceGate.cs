using System.Security.Principal;

namespace KeyPilot.App.Presentation;

internal static class SingleInstanceGate
{
    public static bool TryAcquireForCurrentUser(out SingleInstanceLease? lease)
    {
        string identity;
        try
        {
            identity = WindowsIdentity.GetCurrent().User?.Value
                ?? $"{Environment.UserDomainName}-{Environment.UserName}";
        }
        catch
        {
            identity = $"{Environment.UserDomainName}-{Environment.UserName}";
        }

        return TryAcquire($@"Local\KeyPilot.App.SingleInstance.{identity}", out lease);
    }

    internal static bool TryAcquire(string mutexName, out SingleInstanceLease? lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        var mutex = new Mutex(initiallyOwned: false, mutexName);
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The previous process died while owning it; this process now owns the mutex.
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                lease = null;
                return false;
            }

            lease = new SingleInstanceLease(mutex);
            return true;
        }
        catch
        {
            if (!acquired)
            {
                mutex.Dispose();
            }

            throw;
        }
    }
}

internal sealed class SingleInstanceLease(Mutex mutex) : IDisposable
{
    private Mutex? _mutex = mutex;

    public void Dispose()
    {
        var retained = Interlocked.Exchange(ref _mutex, null);
        if (retained is null)
        {
            return;
        }

        try
        {
            retained.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Ownership can only be lost through external process teardown; disposal stays safe.
        }
        finally
        {
            retained.Dispose();
        }
    }
}
