using System;
using ApiTester.Core;
using Xunit;

namespace ApiTester.Tests;

public class AssertionParserTests
{
    [Theory]
    [InlineData("status == 200", AssertTarget.Status, "", AssertOp.Equals, "200")]
    [InlineData("status < 300", AssertTarget.Status, "", AssertOp.LessThan, "300")]
    [InlineData("time < 500", AssertTarget.Time, "", AssertOp.LessThan, "500")]
    [InlineData("header Content-Type contains json", AssertTarget.Header, "Content-Type", AssertOp.Contains, "json")]
    [InlineData("body data.id exists", AssertTarget.Body, "data.id", AssertOp.Exists, "")]
    [InlineData("body data.id !exists", AssertTarget.Body, "data.id", AssertOp.NotExists, "")]
    [InlineData("body-text contains hello world", AssertTarget.BodyText, "", AssertOp.Contains, "hello world")]
    [InlineData("body ok == true", AssertTarget.Body, "ok", AssertOp.Equals, "true")]
    public void Parses_valid_expressions(string expr, AssertTarget target, string path, AssertOp op, string value)
    {
        var rule = AssertionParser.Parse(expr);
        Assert.True(rule.Enabled);
        Assert.Equal(target, rule.Target);
        Assert.Equal(path, rule.Path);
        Assert.Equal(op, rule.Op);
        Assert.Equal(value, rule.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bogus == 1")]          // unknown target
    [InlineData("status ~~ 1")]         // unknown operator
    [InlineData("header")]              // header needs a name + op
    [InlineData("status ==")]           // binary op needs a value
    [InlineData("body data.id")]        // missing operator
    public void Rejects_malformed_expressions(string expr)
    {
        Assert.Throws<FormatException>(() => AssertionParser.Parse(expr));
    }

    [Fact]
    public void Exists_does_not_require_a_value()
    {
        var rule = AssertionParser.Parse("header X-Trace exists");
        Assert.Equal(AssertOp.Exists, rule.Op);
        Assert.Equal("X-Trace", rule.Path);
        Assert.Equal("", rule.Value);
    }
}
