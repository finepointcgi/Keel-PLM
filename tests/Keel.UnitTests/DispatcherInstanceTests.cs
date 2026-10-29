using Keel.Integration;

namespace Keel.UnitTests;

public sealed class DispatcherInstanceTests
{
    [Fact]
    public void An_instance_id_fits_the_lease_column_whatever_the_machine_is_called_and_stays_unique()
    {
        var ids = Enumerable.Range(0, 200).Select(_ => DeliveryDispatcher.InstanceId()).ToList();
        Assert.All(ids, id => Assert.InRange(id.Length, 1, 48));
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        // The random part is never cut off, so two processes on one machine cannot share an id (a short machine name once made the old slice throw at startup on Cloud Run).
        Assert.All(ids, id => Assert.Equal(24, id[(id.LastIndexOf(':') + 1)..].Length));
    }
}
