using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.Tests;

public sealed class DocumentStoreResultTests
{
    [Fact]
    public void Identity_conflict_exposes_the_authoritative_document_id()
    {
        var result = DocumentStoreWriteResult.IdentityConflict("Authoritative-Id");

        Assert.Equal(DocumentStoreWriteStatus.IdentityConflict, result.Status);
        Assert.Equal("Authoritative-Id", result.AuthoritativeId);
        Assert.Null(result.Document);
    }

    [Fact]
    public void Lookup_collision_exposes_provider_neutral_diagnostic_identity()
    {
        var exception = new DocumentIdentityLookupCollisionException(
            "configurationDocument",
            "Requested-Id",
            "Retained-Id",
            "5a8f9c");

        Assert.Equal("configurationDocument", exception.DocumentKind);
        Assert.Equal("Requested-Id", exception.RequestedId);
        Assert.Equal("Retained-Id", exception.RetainedId);
        Assert.Equal("5a8f9c", exception.LookupKey);
    }
}
