using TelltaleKube.Core.Resources;

namespace TelltaleKube.Core.Tests;

public class ColumnQueryTests
{
    [Theory]
    [InlineData("2", "10", -1)]
    [InlineData("10", "2", 1)]
    [InlineData("45s", "5m", -1)]
    [InlineData("3d", "20h", 1)]
    [InlineData("in 30d", "in 2d", 1)]
    [InlineData("1y", "300d", 1)]
    [InlineData("512Mi", "1Gi", -1)]
    [InlineData("1/3", "2/3", -1)]
    [InlineData("3/3", "3/4", -1)]
    [InlineData("node-2", "node-10", -1)]
    [InlineData("Web", "api", 1)]
    [InlineData("9%", "62%", -1)]
    [InlineData("", "a", 1)]
    [InlineData("a", "", -1)]
    public void Compare_is_numeric_age_quantity_and_natural(string a, string b, int expectedSign) =>
        Assert.Equal(expectedSign, Math.Sign(ColumnValue.Compare(a, b)));

    [Theory]
    [InlineData("CrashLoopBackOff", "crash", true)]
    [InlineData("Running", "=running", true)]
    [InlineData("Running", "=run", false)]
    [InlineData("Running", "!Pending", true)]
    [InlineData("Pending", "!Pending", false)]
    [InlineData("web-6f9c7d-abcde", "web-*-abcde", true)]
    [InlineData("web-6f9c7d-abcde", "api-*", false)]
    [InlineData("14", ">3", true)]
    [InlineData("1", ">3", false)]
    [InlineData("3", ">=3", true)]
    [InlineData("2h", "<1d", true)]
    [InlineData("3d", "<1d", false)]
    [InlineData("in 2d", "<7d", true)]
    [InlineData("in 30d", "<7d", false)]
    [InlineData("20Gi", ">10Gi", true)]
    [InlineData("Running", ">3", false)]
    [InlineData("85%", ">80", true)]
    [InlineData("anything", "", true)]
    public void Matches_supports_operators(string value, string expr, bool expected) =>
        Assert.Equal(expected, ColumnValue.Matches(value, expr));

    private static readonly Dictionary<string, string> Pod = new()
    {
        ["name"] = "worker-5d8b9-klmno",
        ["namespace"] = "shop",
        ["ready"] = "0/1",
        ["status"] = "CrashLoopBackOff",
        ["restarts"] = "14",
        ["node"] = "node-a",
        ["age"] = "5h",
    };

    private static readonly Dictionary<string, string> Labels = new() { ["app"] = "worker", ["tier"] = "backend" };

    [Theory]
    [InlineData("", true)]
    [InlineData("worker", true)]
    [InlineData("WORKER shop", true)]
    [InlineData("worker kube-system", false)]
    [InlineData("status:crash", true)]
    [InlineData("Status:Running", false)]
    [InlineData("restarts:>10", true)]
    [InlineData("restarts:>20", false)]
    [InlineData("ns:shop", true)]
    [InlineData("namespace:!shop", false)]
    [InlineData("label:app=worker", true)]
    [InlineData("label:tier=frontend", false)]
    [InlineData("tier=backend", true)]
    [InlineData("!kube-system", true)]
    [InlineData("!shop", false)]
    [InlineData("age:<1d restarts:>=14", true)]
    [InlineData("ghcr.io/app:1.2", false)]
    public void Query_terms_are_anded(string query, bool expected) =>
        Assert.Equal(expected, FilterQuery.Parse(query).Matches(Pod, Labels));

    [Fact]
    public void Quoted_terms_keep_spaces_and_headers_are_normalized()
    {
        var q = FilterQuery.Parse("message:\"back-off restarting\" Cluster-IP:10.0");
        Assert.Equal(2, q.Terms.Count);
        Assert.Equal(new FilterTerm("message", "back-off restarting"), q.Terms[0]);
        Assert.Equal("clusterip", q.Terms[1].Column);
        Assert.Equal("clusterip", ColumnValue.NormalizeKey("Cluster IP"));
        Assert.Equal("uptodate", ColumnValue.NormalizeKey("Up-to-date"));
    }

    [Fact]
    public void Unknown_column_falls_back_to_free_text()
    {
        var cols = new Dictionary<string, string> { ["name"] = "img", ["image"] = "repo:latest" };
        Assert.True(FilterQuery.Parse("repo:latest").Matches(cols, new Dictionary<string, string>()));
    }

    [Fact]
    public void Wildcard_patterns_are_escaped()
    {
        Assert.False(ColumnValue.Matches("abc", "a.c*"));
        Assert.True(ColumnValue.Matches("a.c-1", "a.c*"));
    }
}
