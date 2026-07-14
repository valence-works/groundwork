using Groundwork.Core.Manifests;
using Groundwork.Core.SchemaEvolution;

namespace Groundwork.Materialization;

public sealed record DocumentStoreIdentitySchemaAdmission(
    StorageUnitIdentity StorageUnit,
    DocumentStoreIdentitySchemaState RequiredState);

public interface IDocumentStoreIdentitySchemaAdmission
{
    Task AdmitAsync(
        IReadOnlyList<DocumentStoreIdentitySchemaAdmission> admissions,
        CancellationToken cancellationToken = default);
}
