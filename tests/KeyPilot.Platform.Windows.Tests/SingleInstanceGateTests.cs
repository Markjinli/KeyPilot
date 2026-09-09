using KeyPilot.App.Presentation;

namespace KeyPilot.Platform.Windows.Tests;

internal static class SingleInstanceGateTests
{
    public static Task SecondOwnerCannotDisplaceFirstAsync()
    {
        var name = $@"Local\KeyPilot.Tests.{Guid.NewGuid():N}";
        Require(SingleInstanceGate.TryAcquire(name, out var first) && first is not null, "The first instance must acquire the per-user gate.");

        var secondAcquired = true;
        var contender = new Thread(() =>
        {
            secondAcquired = SingleInstanceGate.TryAcquire(name, out var second);
            second?.Dispose();
        });
        contender.Start();
        Require(contender.Join(TimeSpan.FromSeconds(2)), "The second instance probe did not finish.");
        Require(!secondAcquired, "A second instance must not displace or affect the first owner.");

        first!.Dispose();
        Require(SingleInstanceGate.TryAcquire(name, out var replacement) && replacement is not null, "A cleanly closed first instance must release the gate.");
        replacement!.Dispose();
        return Task.CompletedTask;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
