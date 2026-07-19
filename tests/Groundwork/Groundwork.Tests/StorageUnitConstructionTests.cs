using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Xunit;

namespace Groundwork.Tests;

public sealed class StorageUnitConstructionTests
{
    [Fact]
    public void Create_uses_current_physical_storage_without_legacy_constructor_inputs()
    {
        var physicalStorage = new StorageUnitPhysicalStorage(
            StorageUnitProvisioningMode.Declared,
            PhysicalStoragePolicy.Default());

        var unit = StorageUnit.Create(
            new StorageUnitIdentity("current-api"),
            "Current API",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            physicalStorage);

        Assert.Same(physicalStorage, unit.PhysicalStorage);
        Assert.Empty(unit.Indexes);
        Assert.Empty(unit.Queries);
    }
}
