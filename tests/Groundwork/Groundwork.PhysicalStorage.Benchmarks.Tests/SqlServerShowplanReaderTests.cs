using System.Data;
using System.Data.SqlTypes;
using System.Xml;
using Xunit;

namespace Groundwork.PhysicalStorage.Benchmarks.Tests;

public sealed class SqlServerShowplanReaderTests
{
    private const string NativeShowplan = """
        <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.564">
          <BatchSequence />
        </ShowPlanXML>
        """;

    [Fact]
    public async Task Reads_the_string_shape_returned_by_Microsoft_Data_SqlClient()
    {
        var queryResults = Table("document", typeof(string), "{\"status\":\"open\"}");
        var showplanResults = Table("Microsoft SQL Server 2005 XML Showplan", typeof(string), NativeShowplan);
        await using var reader = new DataTableReader([queryResults, showplanResults]);

        var plans = await SqlServerShowplanReader.ReadAsync(reader, CancellationToken.None);

        Assert.Equal(NativeShowplan, Assert.Single(plans));
    }

    [Fact]
    public async Task Ignores_showplan_xml_in_an_ordinary_string_result()
    {
        var queryResults = Table("document", typeof(string), NativeShowplan);
        await using var reader = new DataTableReader(queryResults);

        var plans = await SqlServerShowplanReader.ReadAsync(reader, CancellationToken.None);

        Assert.Empty(plans);
    }

    [Fact]
    public async Task Rejects_non_showplan_xml_from_the_showplan_column()
    {
        const string applicationXml = """
            <payload xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <ShowPlanXML />
            </payload>
            """;
        var result = Table("Microsoft SQL Server 2005 XML Showplan", typeof(string), applicationXml);
        await using var reader = new DataTableReader(result);

        var plans = await SqlServerShowplanReader.ReadAsync(reader, CancellationToken.None);

        Assert.Empty(plans);
    }

    [Fact]
    public async Task Ignores_showplan_xml_in_an_ordinary_SqlXml_result()
    {
        var queryResults = Table("document", typeof(SqlXml), ToSqlXml(NativeShowplan));
        await using var reader = new DataTableReader(queryResults);

        var plans = await SqlServerShowplanReader.ReadAsync(reader, CancellationToken.None);

        Assert.Empty(plans);
    }

    [Fact]
    public async Task Reads_SqlString_from_the_showplan_column()
    {
        var showplanResults = Table(
            "Microsoft SQL Server 2005 XML Showplan",
            typeof(SqlString),
            new SqlString(NativeShowplan));
        await using var reader = new DataTableReader(showplanResults);

        var plans = await SqlServerShowplanReader.ReadAsync(reader, CancellationToken.None);

        Assert.Equal(NativeShowplan, Assert.Single(plans));
    }

    [Fact]
    public void Rejects_a_declared_index_mentioned_only_in_statistics_metadata()
    {
        const string plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <StatisticsInfo Statistics="[declared_index]" />
              <RelOp PhysicalOp="Index Seek"><Object Index="[other_index]" /></RelOp>
            </ShowPlanXML>
            """;

        var exception = Assert.Throws<InvalidOperationException>(
            () => SqlServerShowplanReader.EnsureScaleBearingIndex(plan, "declared_index"));

        Assert.Contains("declared_index", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_an_Index_Seek_on_the_declared_index()
    {
        const string plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <RelOp PhysicalOp="Index Seek"><Object Index="[declared_index]" /></RelOp>
            </ShowPlanXML>
            """;

        SqlServerShowplanReader.EnsureScaleBearingIndex(plan, "declared_index");
    }

    [Fact]
    public void Rejects_a_scan_even_when_the_plan_also_seeks_the_declared_index()
    {
        const string plan = """
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan">
              <RelOp PhysicalOp="Index Seek"><Object Index="[declared_index]" /></RelOp>
              <RelOp PhysicalOp="Table Scan"><Object /></RelOp>
            </ShowPlanXML>
            """;

        var exception = Assert.Throws<InvalidOperationException>(
            () => SqlServerShowplanReader.EnsureScaleBearingIndex(plan, "declared_index"));

        Assert.Contains("Table Scan", exception.Message, StringComparison.Ordinal);
    }

    private static DataTable Table(string columnName, Type columnType, object value)
    {
        var table = new DataTable();
        table.Columns.Add(columnName, columnType);
        table.Rows.Add(value);
        return table;
    }

    private static SqlXml ToSqlXml(string value)
    {
        using var input = new StringReader(value);
        using var reader = XmlReader.Create(input);
        return new SqlXml(reader);
    }
}
