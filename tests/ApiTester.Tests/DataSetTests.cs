using ApiTester.Core;

namespace ApiTester.Tests;

public class DataSetTests
{
    [Fact]
    public void Csv_parses_headers_and_rows()
    {
        var rows = DataSet.ParseCsv("id,name\n1,Ada\n2,Grace\n");
        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["id"]);
        Assert.Equal("Ada", rows[0]["name"]);
        Assert.Equal("Grace", rows[1]["name"]);
    }

    [Fact]
    public void Csv_handles_quoted_fields_with_commas_and_escaped_quotes()
    {
        var rows = DataSet.ParseCsv("name,note\n\"Smith, Jr\",\"a \"\"quote\"\" here\"\n");
        Assert.Single(rows);
        Assert.Equal("Smith, Jr", rows[0]["name"]);
        Assert.Equal("a \"quote\" here", rows[0]["note"]);
    }

    [Fact]
    public void Csv_with_only_a_header_yields_no_rows()
    {
        Assert.Empty(DataSet.ParseCsv("id,name\n"));
    }

    [Fact]
    public void Json_array_of_objects_stringifies_values()
    {
        var rows = DataSet.ParseJson("[{\"id\":1,\"name\":\"Ada\",\"active\":true},{\"id\":2,\"name\":\"Grace\"}]");
        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["id"]);      // number → text
        Assert.Equal("Ada", rows[0]["name"]);
        Assert.Equal("true", rows[0]["active"]);
        Assert.Equal("Grace", rows[1]["name"]);
    }

    [Fact]
    public void Missing_columns_become_empty()
    {
        var rows = DataSet.ParseCsv("a,b,c\n1,2\n");
        Assert.Equal("1", rows[0]["a"]);
        Assert.Equal("2", rows[0]["b"]);
        Assert.Equal("", rows[0]["c"]);
    }
}
