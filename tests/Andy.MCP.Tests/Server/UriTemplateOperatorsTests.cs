using Andy.MCP.Server;
namespace Andy.MCP.Tests.Server;

public class UriTemplateOperatorsTests
{
    [Theory]
    [InlineData("https://example.com{/path*}", "https://example.com/a/b/c", "path", "a/b/c")]
    [InlineData("https://example.com{?q,lang}", "https://example.com?q=a%20b&lang=en", "q", "a b")]
    [InlineData("https://example.com{?q,lang}", "https://example.com?lang=en", "lang", "en")]
    [InlineData("x://a?p=1{&q}", "x://a?p=1&q=a%2Bb", "q", "a+b")]
    [InlineData("x://a{;x,y}", "x://a;x=1;y", "y", "")]
    [InlineData("x://a{.labels*}", "x://a.red.green", "labels", "red.green")]
    [InlineData("x://a{#fragment}", "x://a#a/b?c", "fragment", "a/b?c")]
    [InlineData("x://a{?list*}", "x://a?list=red&list=green", "list", "red&green")]
    [InlineData("x://a{?map*}", "x://a?first=red&second=green", "map", "first=red&second=green")]
    [InlineData("x://a/{term:1}/{term}", "x://a/c/cat", "term", "cat")]
    [InlineData("x://a/{term}/{term:1}", "x://a/cat/c", "term", "cat")]
    [InlineData("x://a/{name:1}", "x://a/%F0%9F%98%80", "name", "😀")]
    [InlineData("x://a/{some.name}", "x://a/value", "some.name", "value")]
    public void Operators_ExtractDecodedVariables(string template, string uri, string key, string expected)
    {
        Assert.True(new UriTemplate(template).TryMatch(uri, out var values));
        Assert.Equal(expected, values[key]);
    }
    [Theory]
    [InlineData("x://a/{x}", "x://a/%zz")]
    [InlineData("x://a/{x}", "x://a/value?unexpected")]
    [InlineData("x://a/{x:1}", "x://a/ab")]
    [InlineData("x://a/{x}/{x}", "x://a/one/two")]
    [InlineData("x://a/{x:1}/{x}", "x://a/d/cat")]
    [InlineData("x://a{?x}", "x://a?y=2")]
    [InlineData("x://a{?x}", "x://a?x=1&x=2")]
    public void InvalidValues_DoNotMatch(string template, string uri) => Assert.False(new UriTemplate(template).TryMatch(uri, out _));
    [Theory]
    [InlineData("x://a/{x")]
    [InlineData("x://a/{}")]
    [InlineData("x://a/{x:0}")]
    [InlineData("x://a/{x:10000}")]
    [InlineData("x://a/{x**}")]
    [InlineData("x://a/{x:1*}")]
    [InlineData("x://a/{!x}")]
    [InlineData("x://a/}")]
    public void InvalidTemplates_FailAtRegistration(string template) => Assert.Throws<ArgumentException>(() => new UriTemplate(template));
}
