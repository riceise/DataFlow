using System.Text.Json;
using DataFlow.Application.Imports;
using DataFlow.Application.Rules;
using DataFlow.Domain.Exceptions;

namespace DataFlow.Application.Tests;

public sealed class RuleDefinitionTests
{
    [Fact]
    public void CanonicalDefinitionIgnoresPropertyAndFieldOrder()
    {
        var a = RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>("""{"fields":[{"name":"id"},{"name":"amount","type":"decimal","minimum":0}]}"""));
        var b = RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>("""{"fields":[{"minimum":0,"type":"decimal","name":"amount"},{"header":"id","name":"id"}],"dataType":"Tabular"}"""));
        Assert.Equal(a.Canonical, b.Canonical); Assert.Equal(a.Hash, b.Hash);
        var c = RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>("""{"fields":[{"name":"id"},{"name":"amount","type":"decimal","minimum":1}]}"""));
        Assert.NotEqual(a.Hash, c.Hash);
    }

    [Theory]
    [InlineData("""{"fields":[{"name":"a","unknown":true}]}""")]
    [InlineData("""{"fields":[{"name":"a","name":"b"}]}""")]
    [InlineData("""{"fields":[{"name":"a","regexPattern":"(a)\\1"}]}""")]
    [InlineData("""{"fields":[{"name":"a","type":"decimal","maximum":0,"minimum":1}]}""")]
    [InlineData("""{"fields":[{"name":"a","type":"integer","allowedValues":["1.5"]}]}""")]
    public void InvalidDefinitionsAreRejected(string json) => Assert.Throws<DomainException>(() => RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>(json)));

    [Fact]
    public void MappingTypesRangesAndDuplicateKeysAreApplied()
    {
        var definition = RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>("""
            {"keyFields":["id"],"fields":[{"name":"id","header":"Код","required":true},
            {"name":"amount","header":"Сумма","type":"decimal","minimum":0,"required":true}]}
            """)).Definition;
        var processor = new RuleRowProcessor(Guid.NewGuid(), definition, []);
        ParsedRow Row(string id, string amount) => new(2, new Dictionary<string, string?> { ["Код"] = id, ["Сумма"] = amount }, "[]");
        Assert.True(processor.Process(Row(" a ", "1.20")).IsValid);
        Assert.Contains(processor.Process(Row("a", "1")).Errors, e => e.Code == "duplicate_key_in_file");
        Assert.Contains(processor.Process(Row("b", "-1")).Errors, e => e.Code == "out_of_range");
        Assert.Contains(processor.Process(Row("c", "1,5")).Errors, e => e.Code == "invalid_type");
    }
}
