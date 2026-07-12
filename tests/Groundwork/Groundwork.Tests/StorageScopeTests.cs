using Groundwork.Core.Scoping;
using Groundwork.Documents.Scoping;
using Xunit;

namespace Groundwork.Tests;

public sealed class StorageScopeTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("tenant-a ")]
    [InlineData(" tenant-a")]
    [InlineData("tenant\0a")]
    [InlineData("__groundwork_global__")]
    public void ScopeRejectsMissingAndReservedValues(string value) =>
        Assert.ThrowsAny<ArgumentException>(() => new StorageScope(value));

    [Fact]
    public void ScopeRejectsValuesBeyondThePortableProviderLimit() =>
        Assert.Throws<ArgumentException>(() => new StorageScope(new string('a', StorageScope.MaxValueLength + 1)));

    [Fact]
    public void ScopeRejectsMalformedUtf16()
    {
        Assert.Throws<ArgumentException>(() => new StorageScope("tenant\uD800a"));
        Assert.Throws<ArgumentException>(() => new StorageScope("tenant\uDC00a"));
        Assert.Throws<ArgumentException>(() => new StorageScope("tenant\uD800"));
    }

    [Fact]
    public void ScopeAcceptsWellFormedUnicodeAtThePortableProviderLimit()
    {
        var value = string.Concat(new string('a', StorageScope.MaxValueLength - 2), char.ConvertFromUtf32(0x1F680));

        Assert.Equal(StorageScope.MaxValueLength, value.Length);
        Assert.Equal(value, new StorageScope(value).Value);
    }

    [Fact]
    public void OrdinaryAccessIsAlwaysExplicit()
    {
        var scoped = DocumentStoreAccess.Scoped(new StorageScope("tenant-a"));

        Assert.Equal(DocumentStoreAccessKind.Scoped, scoped.Kind);
        Assert.Equal("tenant-a", scoped.Scope!.Value);
        Assert.Equal(DocumentStoreAccessKind.Global, DocumentStoreAccess.Global.Kind);
        Assert.False(scoped.IsPrivileged);
        Assert.False(DocumentStoreAccess.Global.IsPrivileged);
    }

    [Fact]
    public void PrivilegedPathsRequireDistinctCapability()
    {
        var capability = new PrivilegedStorageAccess("operator repair");

        Assert.Equal(
            DocumentStoreAccessKind.PrivilegedScoped,
            DocumentStoreAccess.PrivilegedScoped(capability, new StorageScope("tenant-a")).Kind);
        Assert.Equal(DocumentStoreAccessKind.PrivilegedGlobal, DocumentStoreAccess.PrivilegedGlobal(capability).Kind);
        Assert.Equal(DocumentStoreAccessKind.PrivilegedAcrossScopes, DocumentStoreAccess.PrivilegedAcrossScopes(capability).Kind);
    }
}
