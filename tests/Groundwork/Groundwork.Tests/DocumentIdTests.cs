using Groundwork.Documents.Store;
using Xunit;

namespace Groundwork.Tests;

public sealed class DocumentIdTests
{
    [Fact]
    public void ComposeEscapesSeparatorsAndEscapeMarkers()
    {
        var id = DocumentId.Compose("workflow:1", "activity%2", "");

        Assert.Equal("workflow%3A1:activity%252:", id);
    }
    [Fact]
    public void ParseRoundTripsComposedParts()
    {
        var original = new[] { "workflow:1", "activity%2", "", "plain" };
        var parsed = DocumentId.Parse(DocumentId.Compose(original));

        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("%2")]
    [InlineData("%GG")]
    public void TryParseRejectsInvalidEscapeSequences(string id)
    {
        var parsed = DocumentId.TryParse(id, out var parts);

        Assert.False(parsed);
        Assert.Empty(parts);
    }

    [Fact]
    public void ComposeRequiresAtLeastOnePart()
    {
        var exception = Assert.Throws<ArgumentException>(() => DocumentId.Compose());

        Assert.Equal("parts", exception.ParamName);
    }
}
