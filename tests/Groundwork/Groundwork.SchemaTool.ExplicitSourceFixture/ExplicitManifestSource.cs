using Groundwork.Core.Indexing;
using Groundwork.Core.Intents;
using Groundwork.Core.Manifests;
using Groundwork.Core.PhysicalStorage;
using Groundwork.Core.SchemaEvolution;
using Groundwork.Core.Scoping;
using Microsoft.AspNetCore.Identity;

namespace Groundwork.SchemaTool.ExplicitSourceFixture;

public sealed class ExplicitManifestSource : IPhysicalSchemaManifestSource
{
    public StorageManifest CreateManifest()
    {
        var definition = PhysicalTableDefinition.PhysicalEntityTable(
            "explicit_source_documents",
            [
                new ProjectedColumnDefinition(
                    "category",
                    "category",
                    PortablePhysicalType.String,
                    IsNullable: false)
            ]);
        var unit = new StorageUnit(
            new StorageUnitIdentity("documents"),
            "document",
            StorageIntent.PortableDocument(),
            LifecyclePolicy.Mutable,
            IdentityPolicy.StringId(),
            TenancyPolicy.Scoped,
            ConcurrencyPolicy.Optimistic(),
            SerializationPolicy.Json(),
            [],
            [],
            PhysicalizationPolicy.Portable)
        {
            PhysicalStorage = new StorageUnitPhysicalStorage(
                StorageUnitProvisioningMode.Declared,
                PhysicalStoragePolicy.Explicit(definition))
        };

        return new StorageManifest(
            new StorageManifestIdentity("explicit-source-fixture"),
            new StorageManifestOwner("tests"),
            new StorageManifestVersion("1"),
            [unit],
            new HashSet<string>(),
            []);
    }
}

// This type deliberately needs an assembly that the schema-tool host does not carry. Explicit
// source selection must not inspect unrelated types in the supplied assembly.
public sealed class UnrelatedIdentityType : IdentityUser;
