using Groundwork.Documents.UnitOfWork;
using Xunit;

namespace Groundwork.Tests;

public sealed class DocumentCommitContractTests
{
    [Fact]
    public void Commit_scope_snapshots_and_normalizes_caller_owned_kinds()
    {
        var callerKinds = new List<string> { "workItem", "auditItem" };

        var scope = new DocumentCommitScope(callerKinds);
        callerKinds.Clear();
        callerKinds.Add("replacement");

        Assert.Equal(["auditItem", "workItem"], scope.Kinds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Commit_scope_rejects_null_or_blank_kinds(string? kind)
    {
        Assert.Throws<ArgumentException>(() => new DocumentCommitScope([kind!]));
    }

    [Fact]
    public void Acknowledgement_uncertainty_uses_one_normalized_kind_set_for_property_and_message()
    {
        var exception = new DocumentCommitAcknowledgementUncertainException(
            ["workItem", "auditItem", "workItem"]);

        Assert.Equal(["auditItem", "workItem"], exception.DocumentKinds);
        Assert.Equal(
            "The document transaction for [auditItem, workItem] may have committed before acknowledgement was lost.",
            exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Acknowledgement_uncertainty_rejects_null_or_blank_kinds(string? kind)
    {
        Assert.Throws<ArgumentException>(() =>
            new DocumentCommitAcknowledgementUncertainException([kind!]));
    }
}
