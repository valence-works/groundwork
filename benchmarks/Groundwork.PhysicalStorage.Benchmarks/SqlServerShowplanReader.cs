using System.Data.Common;
using System.Data.SqlTypes;
using System.Xml;
using System.Xml.Linq;

namespace Groundwork.PhysicalStorage.Benchmarks;

internal static class SqlServerShowplanReader
{
    private const string ShowplanColumnMarker = "XML Showplan";
    private const string ShowplanNamespace = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";

    public static async Task<IReadOnlyList<string>> ReadAsync(
        DbDataReader reader,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var plans = new List<string>();
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (reader.FieldCount != 1)
                    continue;

                var value = reader.GetValue(0);
                var isShowplanColumn = reader.GetName(0).Contains(
                    ShowplanColumnMarker,
                    StringComparison.OrdinalIgnoreCase);
                var text = value switch
                {
                    SqlXml xml when isShowplanColumn && !xml.IsNull => xml.Value,
                    SqlString sqlString when isShowplanColumn && !sqlString.IsNull => sqlString.Value,
                    string stringValue when isShowplanColumn => stringValue,
                    _ => null
                };
                if (text is not null && IsShowplanXml(text))
                    plans.Add(text);
            }
        } while (await reader.NextResultAsync(cancellationToken));
        return plans;
    }

    public static void EnsureScaleBearingIndex(string value, string indexName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(indexName);
        var document = Parse(value);
        var operators = document
            .Descendants()
            .Where(element => element.Name.LocalName == "RelOp")
            .ToArray();
        var forbiddenScans = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Table Scan",
            "Index Scan",
            "Clustered Index Scan"
        };
        var hasForbiddenScan = operators.Any(element =>
            forbiddenScans.Contains(element.Attribute("PhysicalOp")?.Value ?? string.Empty));
        var usesDeclaredIndex = operators.Any(element =>
            string.Equals(element.Attribute("PhysicalOp")?.Value, "Index Seek", StringComparison.OrdinalIgnoreCase) &&
            element.Descendants().Any(candidate =>
                candidate.Name.LocalName == "Object" &&
                IsIndex(candidate.Attribute("Index")?.Value, indexName)));
        if (!usesDeclaredIndex || hasForbiddenScan)
        {
            throw new InvalidOperationException(
                $"SQL Server native-plan gate rejected the scale-bearing query. Expected an Index Seek on '{indexName}'. " +
                DescribeAccessPaths(document));
        }
    }

    private static string DescribeAccessPaths(XDocument document)
    {
        var physicalOperators = document
            .Descendants()
            .Attributes("PhysicalOp")
            .Select(attribute => attribute.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        var indexes = document
            .Descendants()
            .Attributes("Index")
            .Select(attribute => attribute.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase);
        return $"Physical operators: [{string.Join(", ", physicalOperators)}]; indexes: [{string.Join(", ", indexes)}].";
    }

    private static bool IsIndex(string? candidate, string expected) =>
        string.Equals(candidate, expected, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(candidate, $"[{expected}]", StringComparison.OrdinalIgnoreCase);

    private static bool IsShowplanXml(string value)
    {
        try
        {
            var document = Parse(value);
            return document.Root?.Name == XName.Get("ShowPlanXML", ShowplanNamespace);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static XDocument Parse(string value)
    {
        using var input = new StringReader(value);
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        return XDocument.Load(reader, LoadOptions.None);
    }
}
