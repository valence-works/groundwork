using Groundwork.Core.Manifests;
using Groundwork.Core.SchemaEvolution;
using Xunit;

namespace Groundwork.Tests;

public sealed class DocumentStoreIdentitySchemaStateTests
{
    [Fact]
    public void Captured_state_has_a_canonical_restart_safe_representation()
    {
        var state = DocumentStoreIdentitySchemaState.Capture(
            IdentityPolicy.StringId(stringCasePolicy: StringIdentityCasePolicy.Ordinal));

        var json = state.ToCanonicalJson();

        Assert.Equal(
            "{\"FormatVersion\":1,\"StringCasePolicy\":0,\"ComparisonAlgorithmId\":\"groundwork-utf16-hex-v1\",\"LookupAlgorithmId\":\"groundwork-sha256-utf8-lowerhex-v1\"}",
            json);
        Assert.Equal(state, DocumentStoreIdentitySchemaState.FromCanonicalJson(json));
    }

    [Fact]
    public void Unsupported_persisted_state_fails_closed()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            DocumentStoreIdentitySchemaState.FromCanonicalJson(
                "{\"FormatVersion\":2,\"StringCasePolicy\":0,\"ComparisonAlgorithmId\":\"other\",\"LookupAlgorithmId\":\"other\"}"));

        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Different_identity_semantics_produce_distinct_typed_states()
    {
        var ordinal = DocumentStoreIdentitySchemaState.Capture(IdentityPolicy.StringId());
        var ignoreCase = DocumentStoreIdentitySchemaState.Capture(
            IdentityPolicy.StringId(stringCasePolicy: StringIdentityCasePolicy.UnicodeOrdinalIgnoreCase));

        Assert.NotEqual(ordinal, ignoreCase);
    }
}
