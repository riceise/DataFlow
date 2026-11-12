using System.Text.Json;
using DataFlow.Application.Imports;
using DataFlow.Application.Rules;

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
    [InlineData("""{"fields":[{"name":"a","header":"   "}]}""")]
    [InlineData("""{"fields":[{"name":"a","header":" Col "},{"name":"b","header":"col"}]}""")]
    [InlineData("""{"fields":[{"name":"a","regexPattern":"(a)\\1"}]}""")]
    [InlineData("""{"fields":[{"name":"a","type":"decimal","maximum":0,"minimum":1}]}""")]
    [InlineData("""{"fields":[{"name":"a","type":"integer","allowedValues":["1.5"]}]}""")]
    public void InvalidDefinitionsAreRejected(string json) => Assert.Throws<RuleDefinitionValidationException>(() => RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>(json)));

    [Fact]
    public void TextNumericConstraintReportsOriginalPathAndHowToFixIt()
    {
        const string json = """
            {"fields":[{"name":"code"},{"name":"amount","type":"decimal","minimum":0},
            {"name":"description","header":"описание","type":"string","required":true,"minimum":1}]}
            """;
        var error = Assert.Throws<RuleDefinitionValidationException>(() => RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>(json)));
        var issue = Assert.Single(error.Issues);
        Assert.Equal("constraint_type_mismatch", issue.Code);
        Assert.Equal("fields[2].minimum", issue.Path);
        Assert.Equal("description", issue.FieldName);
        Assert.Contains("minLength", issue.Hint);
        Assert.Contains("integer", issue.Message);
        var corrected = RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>(json.Replace("\"minimum\":1", "\"minLength\":1")));
        Assert.Equal(1, corrected.Definition.Fields.Single(f => f.Name == "description").MinLength);
    }

    [Fact]
    public void IndependentErrorsAreCollectedBeforeCanonicalSorting()
    {
        var error = Assert.Throws<RuleDefinitionValidationException>(() => RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>("""
            {"keyFields":["missing","missing"],"fields":[
            {"name":"z","type":"decimal","minimum":10,"maximum":1,"minLength":2},
            {"name":"a","type":"string","minimum":1,"maximum":2},
            {"name":"a","header":"z","maxLength":0}]}
            """)));
        Assert.Contains(error.Issues, i => i.Path == "fields[0].minimum" && i.Code == "numeric_range");
        Assert.Contains(error.Issues, i => i.Path == "fields[0].minLength" && i.Hint.Contains("minimum"));
        Assert.Contains(error.Issues, i => i.Path == "fields[1].minimum" && i.Hint.Contains("minLength"));
        Assert.Contains(error.Issues, i => i.Path == "fields[1].maximum" && i.Hint.Contains("maxLength"));
        Assert.Contains(error.Issues, i => i.Path == "fields[2].name" && i.Code == "duplicate_field_name");
        Assert.Contains(error.Issues, i => i.Path == "fields[2].header" && i.Code == "duplicate_header");
        Assert.Contains(error.Issues, i => i.Path == "fields[2].maxLength" && i.Code == "invalid_max_length");
        Assert.Contains(error.Issues, i => i.Path == "keyFields[0]" && i.Code == "unknown_key_field");
        Assert.Contains(error.Issues, i => i.Path == "keyFields[1]" && i.Code == "duplicate_key");
    }

    [Theory]
    [InlineData("null", "$", "expected_object")]
    [InlineData("{\"fields\":[null]}", "fields[0]", "expected_object")]
    [InlineData("{\"fields\":[{\"name\":null}]}", "fields[0].name", "invalid_property_type")]
    [InlineData("{\"fields\":[{\"name\":\"a\",\"minLength\":\"один\"}]}", "fields[0].minLength", "invalid_property_type")]
    [InlineData("{\"fields\":[{\"name\":\"a\",\"minLength\":1.5}]}", "fields[0].minLength", "invalid_property_type")]
    [InlineData("{\"fields\":[{\"name\":\"a\",\"minimum\":1e100}]}", "fields[0].minimum", "invalid_property_type")]
    [InlineData("{\"fields\":[{\"name\":\"a\",\"allowedValues\":[null]}]}", "fields[0].allowedValues[0]", "expected_string")]
    [InlineData("{\"csv\":null}", "csv", "expected_object")]
    [InlineData("{\"keyFields\":null}", "keyFields", "expected_array")]
    [InlineData("{\"fields\":[{\"name\":\"a\",\"Name\":\"b\"}]}", "fields[0].Name", "duplicate_property")]
    [InlineData("{\"fields\":[{\"name\":\"a\",\"minLenght\":1}]}", "fields[0].minLenght", "unknown_property")]
    public void InvalidStructureReportsSpecificPath(string json, string path, string code)
    {
        var error = Assert.Throws<RuleDefinitionValidationException>(() => RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.Contains(error.Issues, issue => issue.Path == path && issue.Code == code && issue.Hint.Length > 0);
    }

    [Fact]
    public void MultiplePropertyTypeErrorsAreReportedTogether()
    {
        var error = Assert.Throws<RuleDefinitionValidationException>(() => RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>("""
            {"csv":{"delimiter":3},"fields":[{"name":"a","minLength":"one","required":"true","extra":1}]}
            """)));
        Assert.Equal(new[] { "csv.delimiter", "fields[0].minLength", "fields[0].required", "fields[0].extra" }, error.Issues.Select(i => i.Path));
    }

    [Fact]
    public void MappingTypesRangesAndDuplicateKeysAreApplied()
    {
        var definition = RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>("""
            {"keyFields":["id"],"fields":[{"name":"id","header":" Код ","required":true},
            {"name":"amount","header":" Сумма ","type":"decimal","minimum":0,"required":true}]}
            """)).Definition;
        Assert.Equal("Код", definition.Fields.Single(f => f.Name == "id").Header);
        var processor = new RuleRowProcessor(Guid.NewGuid(), definition, []);
        ParsedRow Row(string id, string amount) => new(2, new Dictionary<string, string?> { ["Код"] = id, ["Сумма"] = amount }, "[]");
        Assert.True(processor.Process(Row(" a ", "1.20")).IsValid);
        Assert.Contains(processor.Process(Row("a", "1")).Errors, e => e.Code == "duplicate_key_in_file");
        Assert.Contains(processor.Process(Row("b", "-1")).Errors, e => e.Code == "out_of_range");
        Assert.Contains(processor.Process(Row("c", "1,5")).Errors, e => e.Code == "invalid_type");
    }

    [Fact]
    public void CustomerRuleSet_DoesNotDuplicateEmailDateOrMaxLengthErrors()
    {
        var definition = RuleDefinitions.Parse(JsonSerializer.Deserialize<JsonElement>("""
            {
              "dataType": "Customer",
              "keyFields": ["externalId"],
              "fields": [
                {"name": "externalId", "type": "string", "required": true, "maxLength": 10},
                {"name": "name", "type": "string", "required": true, "maxLength": 5},
                {"name": "email", "type": "email", "required": true, "maxLength": 254},
                {"name": "registeredAt", "type": "date", "required": false, "format": "yyyy-MM-dd"},
                {"name": "isActive", "type": "boolean", "required": true}
              ]
            }
            """)).Definition;
        var processor = new RuleRowProcessor(Guid.NewGuid(), definition, []);
        var row = new ParsedRow(2, new Dictionary<string, string?>
        {
            ["externalId"] = "ID-1",
            ["name"] = "TooLongName",
            ["email"] = "not-an-email",
            ["registeredAt"] = "2026-99-99",
            ["isActive"] = "yes"
        }, "[]");
        var result = processor.Process(row);
        Assert.False(result.IsValid);
        Assert.Equal(4, result.Errors.Count);
        Assert.Single(result.Errors, e => e.Field == "name" && e.Code == "max_length");
        Assert.Single(result.Errors, e => e.Field == "email" && e.Code == "invalid_email");
        Assert.Single(result.Errors, e => e.Field == "registeredAt" && e.Code == "invalid_date");
        Assert.Single(result.Errors, e => e.Field == "isActive" && e.Code == "invalid_type");
    }
}
