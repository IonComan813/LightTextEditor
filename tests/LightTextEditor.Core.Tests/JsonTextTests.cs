using System.Text.Json;
using System.Text.Json.Nodes;
using LightTextEditor.Core.Text;

namespace LightTextEditor.Core.Tests;

public class JsonTextTests
{
    [Fact]
    public void Format_ProducesTheUsualIndentedLayout()
    {
        const string compact = """{"a":1,"b":[true,false,null],"c":{},"d":[],"e":{"f":"g"}}""";
        const string expected = """
            {
              "a": 1,
              "b": [
                true,
                false,
                null
              ],
              "c": {},
              "d": [],
              "e": {
                "f": "g"
              }
            }
            """;
        Assert.Equal(expected.Replace("\r\n", "\n"), JsonText.Format(compact, "  ", "\n"));
    }

    [Fact]
    public void Format_UsesTheGivenIndentAndNewLine() =>
        Assert.Equal("[\r\n\t1,\r\n\t2\r\n]", JsonText.Format("[1,2]", "\t", "\r\n"));

    [Theory]
    [InlineData("""{"s":"a, b: {c} [d] \"e\" \\ \/ \u4e2d 中文"}""")] // 字符串里的标点、转义原样保留
    [InlineData("""[1.50,-0,1E+3,12345678901234567890]""")]          // 数字的写法原样保留
    [InlineData("""{ "a" : [ 1 , 2 ] ,	"b":{ } }""")]                 // 原来就有空白
    public void FormatThenMinify_KeepsTheContent(string json)
    {
        Assert.True(JsonText.IsJson(json));
        var formatted = JsonText.Format(json, "    ", "\n");
        Assert.True(JsonText.IsJson(formatted));
        var minified = JsonText.Minify(formatted);
        Assert.Equal(JsonText.Minify(json), minified);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(formatted)));
        Assert.DoesNotContain("\n", minified);
    }

    [Fact]
    public void Format_KeepsStringsExactlyAsWritten()
    {
        const string json = """{"k":"\u4e2d\/x"}""";
        Assert.Contains("  \"k\": \"\\u4e2d\\/x\"\n", JsonText.Format(json, "  ", "\n"));
    }

    [Fact]
    public void Minify_RemovesWhitespaceOutsideStringsOnly() =>
        Assert.Equal("""{"a b":[1,"c d"]}""", JsonText.Minify("{\n  \"a b\" : [ 1 ,\r\n \"c d\" ]\n}\n"));

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("123")]                // 不是对象或数组
    [InlineData("{\"a\":1,}")]         // 末尾多余的逗号
    [InlineData("{\"a\":1} {\"b\":2}")] // 多个值（如 NDJSON）
    [InlineData("{\"a\":1 // 注释\n}")]
    [InlineData("[1,2")]
    public void Validate_RejectsNonJson(string text)
    {
        Assert.False(JsonText.Validate(text, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Validate_ReportsTheLine()
    {
        Assert.False(JsonText.Validate("{\n\"a\": 1,\n\"b\": ]\n}", out var error));
        Assert.Contains("第 3 行", error);
    }

    [Fact]
    public void LargeMinifiedJson_RoundTrips()
    {
        var items = Enumerable.Range(0, 20000).Select(i => new { id = i, name = $"用户 {i}", tags = new[] { "a", "b" } });
        var json = JsonSerializer.Serialize(items);
        var formatted = JsonText.Format(json, "  ", "\n");
        Assert.Equal(20000 * 8 + 2, formatted.Split('\n').Length);
        Assert.Equal(json, JsonText.Minify(formatted));
    }
}
