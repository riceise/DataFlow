using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DataFlow.Application.DataSources;
using DataFlow.Application.Dispatching;
using DataFlow.Domain.Entities;
using DataFlow.Domain.Exceptions;
using DataFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Testcontainers.PostgreSql;

namespace DataFlow.IntegrationTests;

public sealed class DataSourcesApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:DataFlow"] = _postgres.GetConnectionString() }));
    }

    public DataFlowDbContext CreateContext() => new(new DbContextOptionsBuilder<DataFlowDbContext>()
        .UseNpgsql(_postgres.GetConnectionString()).Options);

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var database = CreateContext();
        await database.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public sealed class DataSourcesStage4Tests(DataSourcesApiFixture fixture) : IClassFixture<DataSourcesApiFixture>, IAsyncLifetime
{
    private const string Sources = "/api/v1/data-sources";
    private readonly HttpClient _client = fixture.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });

    public async Task InitializeAsync()
    {
        await using var database = fixture.CreateContext();
        await database.ImportJobs.ExecuteDeleteAsync();
        await database.DataSources.ExecuteDeleteAsync();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CreateGetUpdate_ReturnContractAndPersistChanges()
    {
        var request = ValidRequest();
        using var response = await _client.PostAsJsonAsync(Sources, request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var source = await ReadSource(response);
        Assert.Equal($"{Sources}/{source.Id}", response.Headers.Location!.OriginalString);
        Assert.Equal("Клиенты", source.Name);
        Assert.Equal("Customer", source.DataType);
        Assert.Equal(new[] { "Csv", "Xlsx" }, source.AcceptedFormats);
        Assert.Equal(1, source.SchemaVersion);
        Assert.Equal(source.SchemaVersion, source.Schema.Version);
        Assert.Equal(new[] { "externalId" }, source.Schema.KeyFields);
        Assert.Equal(",", source.Schema.CsvDelimiter);
        Assert.Equal(5, source.Schema.Fields.Count);
        Assert.True(source.IsActive);
        Assert.Equal(TimeSpan.Zero, source.CreatedAt.Offset);
        Assert.Equal(source.CreatedAt, source.UpdatedAt);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "acceptedFormats", "createdAt", "dataType", "id", "isActive", "name", "schema", "schemaVersion", "updatedAt" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("acceptedFormats").ValueKind);
        var booleanField = json.RootElement.GetProperty("schema").GetProperty("fields")[4];
        Assert.False(booleanField.TryGetProperty("maxLength", out _));
        Assert.False(booleanField.TryGetProperty("format", out _));
        Assert.False(booleanField.TryGetProperty("regexPattern", out _));

        var fetched = await _client.GetFromJsonAsync<DataSourceDto>(response.Headers.Location);
        Assert.Equal(source.Id, fetched!.Id);
        Assert.Equal(source.Name, fetched.Name);

        using var updatedResponse = await _client.PutAsJsonAsync($"{Sources}/{source.Id}", request with
        {
            Name = " Новое имя ",
            AcceptedFormats = ["Csv"]
        });
        Assert.Equal(HttpStatusCode.OK, updatedResponse.StatusCode);
        var updated = await ReadSource(updatedResponse);
        Assert.Equal("Новое имя", updated.Name);
        Assert.Equal(new[] { "Csv" }, updated.AcceptedFormats);
        Assert.Equal(1, updated.SchemaVersion);
        Assert.Equal(source.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= source.UpdatedAt);

        await using var database = fixture.CreateContext();
        var saved = await database.DataSources.AsNoTracking().SingleAsync();
        Assert.Equal(updated.Name, saved.Name);
        Assert.Equal(updated.SchemaVersion, saved.SchemaVersion);
    }

    [Fact]
    public async Task List_UsesDefaultsPaginationAndDescendingCreationOrder()
    {
        var ids = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            ids.Add((await CreateSource(ValidRequest() with { Name = $"Source {index}" })).Id);
        }

        var all = await _client.GetFromJsonAsync<PagedResult<DataSourceDto>>(Sources);
        Assert.NotNull(all);
        Assert.Equal(1, all.Page);
        Assert.Equal(50, all.PageSize);
        Assert.Equal(3, all.Total);
        Assert.Equal(ids.AsEnumerable().Reverse(), all.Items.Select(source => source.Id));
        var page = await _client.GetFromJsonAsync<PagedResult<DataSourceDto>>($"{Sources}?page=2&pageSize=1");
        Assert.NotNull(page);
        Assert.Equal(2, page.Page);
        Assert.Equal(1, page.PageSize);
        Assert.Equal(3, page.Total);
        Assert.Equal(ids[1], Assert.Single(page.Items).Id);
        var maximum = await _client.GetFromJsonAsync<PagedResult<DataSourceDto>>($"{Sources}?pageSize=200");
        Assert.Equal(3, maximum!.Items.Count);
        var empty = await _client.GetFromJsonAsync<PagedResult<DataSourceDto>>($"{Sources}?page={int.MaxValue}&pageSize=200");
        Assert.Empty(empty!.Items);
        Assert.Equal(3, empty.Total);
    }

    [Fact]
    public async Task ActivateDeactivate_AreIdempotentAndPreserveSchemaVersion()
    {
        var source = await CreateSource();
        foreach (var action in new[] { "deactivate", "deactivate", "activate", "activate" })
        {
            using var response = await _client.PostAsync($"{Sources}/{source.Id}/{action}", null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var updated = await ReadSource(response);
            Assert.Equal(action == "activate", updated.IsActive);
            Assert.Equal(1, updated.SchemaVersion);
            Assert.Equal(source.CreatedAt, updated.CreatedAt);
            var fetched = await _client.GetFromJsonAsync<DataSourceDto>($"{Sources}/{source.Id}");
            Assert.Equal(updated.IsActive, fetched!.IsActive);
        }
    }

    [Theory]
    [InlineData("get")]
    [InlineData("put")]
    [InlineData("activate")]
    [InlineData("deactivate")]
    public async Task MissingSource_ReturnsNotFoundProblem(string action)
    {
        var url = $"{Sources}/{Guid.NewGuid()}";
        using var response = action switch
        {
            "get" => await _client.GetAsync(url),
            "put" => await _client.PutAsJsonAsync(url, ValidRequest()),
            _ => await _client.PostAsync($"{url}/{action}", null)
        };
        await AssertProblem(response, HttpStatusCode.NotFound, "source_not_found");
    }

    [Theory]
    [InlineData("unknown", "schema.fields[0].name")]
    [InlineData("duplicate", "schema.fields[5].name")]
    [InlineData("caseDuplicate", "schema.fields[5].name")]
    [InlineData("key", "schema.keyFields")]
    [InlineData("keys", "schema.keyFields")]
    [InlineData("missing", "schema.fields")]
    [InlineData("type", "schema.fields[0].type")]
    [InlineData("required", "schema.fields[0].required")]
    [InlineData("optionalRequired", "schema.fields[3].required")]
    [InlineData("length", "schema.fields[0].maxLength")]
    [InlineData("zeroLength", "schema.fields[0].maxLength")]
    [InlineData("missingLength", "schema.fields[0].maxLength")]
    [InlineData("nameLength", "schema.fields[1].maxLength")]
    [InlineData("emailLength", "schema.fields[2].maxLength")]
    [InlineData("dateLength", "schema.fields[3].maxLength")]
    [InlineData("booleanLength", "schema.fields[4].maxLength")]
    [InlineData("dateFormat", "schema.fields[3].format")]
    [InlineData("extraFormat", "schema.fields[0].format")]
    [InlineData("regex", "schema.fields[0].regexPattern")]
    [InlineData("delimiter", "schema.csvDelimiter")]
    [InlineData("nullDelimiter", "schema.csvDelimiter")]
    [InlineData("nullField", "schema.fields[0]")]
    [InlineData("nullSchema", "schema")]
    [InlineData("emptyFields", "schema.fields")]
    public async Task InvalidSchemas_ReturnFieldErrorsAndDoNotWrite(string variant, string errorPath)
    {
        var request = InvalidSchema(variant);
        using var response = await _client.PostAsJsonAsync(Sources, request);
        var problem = await AssertProblem(response, HttpStatusCode.BadRequest, "invalid_schema");
        Assert.True(problem.GetProperty("errors").TryGetProperty(errorPath, out var messages));
        Assert.NotEmpty(messages.EnumerateArray());
        await using var database = fixture.CreateContext();
        Assert.Equal(0, await database.DataSources.CountAsync());
    }

    [Fact]
    public async Task InvalidUpdate_DoesNotChangeStoredSource()
    {
        var source = await CreateSource();
        using var response = await _client.PutAsJsonAsync($"{Sources}/{source.Id}", InvalidSchema("unknown") with { Name = "Changed" });
        await AssertProblem(response, HttpStatusCode.BadRequest, "invalid_schema");
        var saved = await _client.GetFromJsonAsync<DataSourceDto>($"{Sources}/{source.Id}");
        Assert.Equal(source.Name, saved!.Name);
        Assert.Equal(1, saved.SchemaVersion);
    }

    [Theory]
    [InlineData("emptyName", "name")]
    [InlineData("longName", "name")]
    [InlineData("emptyFormats", "acceptedFormats")]
    [InlineData("invalidFormat", "acceptedFormats[0]")]
    [InlineData("nullFormat", "acceptedFormats[0]")]
    public async Task InvalidRequests_ReturnValidationFailed(string variant, string path)
    {
        var valid = ValidRequest();
        var request = variant switch
        {
            "emptyName" => valid with { Name = " " },
            "longName" => valid with { Name = new string('a', 201) },
            "emptyFormats" => valid with { AcceptedFormats = [] },
            "invalidFormat" => valid with { AcceptedFormats = ["csv"] },
            _ => valid with { AcceptedFormats = [null] }
        };
        using var response = await _client.PostAsJsonAsync(Sources, request);
        var problem = await AssertProblem(response, HttpStatusCode.BadRequest, "validation_failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty(path, out _));
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=201")]
    [InlineData("page=abc")]
    public async Task InvalidPagination_ReturnsValidationProblem(string query)
    {
        using var response = await _client.GetAsync($"{Sources}?{query}");
        await AssertProblem(response, HttpStatusCode.BadRequest, "validation_failed");
    }

    [Theory]
    [InlineData("{", "validation_failed")]
    [InlineData("null", "validation_failed")]
    [InlineData("{\"dataType\":\"Customer\"}", "validation_failed")]
    [InlineData("{\"acceptedFormats\":1}", "validation_failed")]
    [InlineData("{\"schema\":{\"fields\":\"invalid\"}}", "invalid_schema")]
    [InlineData("{\"schema\":{\"extra\":true}}", "invalid_schema")]
    public async Task MalformedJson_ReturnsSanitizedProblem(string body, string code)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(Sources, content);
        await AssertProblem(response, HttpStatusCode.BadRequest, code);
    }

    [Fact]
    public async Task SchemaVersion_ChangesOnlyWithSchemaAndPreservesJobSnapshot()
    {
        var request = ValidRequest();
        var source = await CreateSource(request);
        Guid jobId;
        string snapshot;
        await using (var database = fixture.CreateContext())
        {
            var saved = await database.DataSources.SingleAsync();
            snapshot = saved.SchemaJson;
            var job = ImportJob.Create(saved.Id, "customers.csv", "test/customers", new string('a', 64),
                snapshot, saved.SchemaVersion, DateTimeOffset.UtcNow);
            jobId = job.Id;
            database.ImportJobs.Add(job);
            await database.SaveChangesAsync();
        }

        var reordered = request with { Schema = request.Schema! with { Fields = request.Schema.Fields!.Reverse().ToArray() } };
        using var sameResponse = await _client.PutAsJsonAsync($"{Sources}/{source.Id}", reordered);
        Assert.Equal(HttpStatusCode.OK, sameResponse.StatusCode);
        Assert.Equal(1, (await ReadSource(sameResponse)).SchemaVersion);

        var fields = request.Schema!.Fields!.ToArray();
        fields[0] = fields[0]! with { MaxLength = 79 };
        var changed = request with { Schema = request.Schema with { Fields = fields } };
        using var changedResponse = await _client.PutAsJsonAsync($"{Sources}/{source.Id}", changed);
        Assert.Equal(HttpStatusCode.OK, changedResponse.StatusCode);
        var updated = await ReadSource(changedResponse);
        Assert.Equal(2, updated.SchemaVersion);
        Assert.Equal(2, updated.Schema.Version);
        using var repeatResponse = await _client.PutAsJsonAsync($"{Sources}/{source.Id}", changed);
        Assert.Equal(2, (await ReadSource(repeatResponse)).SchemaVersion);

        using var delimiterResponse = await _client.PutAsJsonAsync($"{Sources}/{source.Id}", changed with
        {
            Schema = changed.Schema! with { CsvDelimiter = ";" }
        });
        Assert.Equal(3, (await ReadSource(delimiterResponse)).SchemaVersion);

        await using var read = fixture.CreateContext();
        var savedJob = await read.ImportJobs.SingleAsync(job => job.Id == jobId);
        Assert.Equal(1, savedJob.SchemaVersion);
        Assert.Equal(snapshot, savedJob.SchemaJson);
    }

    [Fact]
    public async Task OptionalRegisteredAtAndDefaultDelimiter_AreSupported()
    {
        var request = ValidRequest();
        var source = await CreateSource(request with
        {
            AcceptedFormats = ["Xlsx"],
            Schema = new DataSourceSchemaRequest
            {
                KeyFields = ["externalId"],
                Fields = request.Schema!.Fields!.Where(field => field!.Name != "registeredAt").ToArray()
            }
        });
        Assert.Equal(4, source.Schema.Fields.Count);
        Assert.Equal(",", source.Schema.CsvDelimiter);
        Assert.Equal(new[] { "Xlsx" }, source.AcceptedFormats);
    }

    [Fact]
    public async Task OpenApiAndScalar_AreAvailableInDevelopment()
    {
        using var response = await _client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.True(paths.GetProperty(Sources).TryGetProperty("post", out _));
        Assert.True(paths.GetProperty($"{Sources}/{{id}}").TryGetProperty("put", out _));
        using var ui = await _client.GetAsync("/scalar/v1");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("api-reference", await ui.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Exceptions_AreMappedCentrallyWithoutExposingDetails(bool domain)
    {
        using var factory = fixture.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IRequestHandler<GetDataSourceQuery, DataSourceDto>>(_ =>
                new ThrowingHandler(domain)))));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var response = await client.GetAsync($"{Sources}/{Guid.NewGuid()}");
        await AssertProblem(response, domain ? HttpStatusCode.NotFound : HttpStatusCode.InternalServerError,
            domain ? "source_not_found" : "internal_error");
        Assert.DoesNotContain("private-exception-data", await response.Content.ReadAsStringAsync());
    }

    private sealed class ThrowingHandler(bool domain) : IQueryHandler<GetDataSourceQuery, DataSourceDto>
    {
        public Task<DataSourceDto> HandleAsync(GetDataSourceQuery request, CancellationToken cancellationToken) =>
            Task.FromException<DataSourceDto>(domain
                ? new DomainException("source_not_found", "private-exception-data")
                : new InvalidOperationException("private-exception-data"));
    }

    private async Task<DataSourceDto> CreateSource(SaveDataSourceRequest? request = null)
    {
        using var response = await _client.PostAsJsonAsync(Sources, request ?? ValidRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadSource(response);
    }

    private static async Task<DataSourceDto> ReadSource(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<DataSourceDto>())!;

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var problem = document.RootElement;
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("type").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.False(problem.TryGetProperty("exception", out _));
        Assert.False(problem.TryGetProperty("stackTrace", out _));
        return problem.Clone();
    }

    private static SaveDataSourceRequest ValidRequest() => new()
    {
        Name = "Клиенты",
        AcceptedFormats = ["Csv", "Xlsx"],
        Schema = new DataSourceSchemaRequest
        {
            KeyFields = ["externalId"],
            Fields =
            [
                new() { Name = "externalId", Type = "string", Required = true, MaxLength = 80 },
                new() { Name = "name", Type = "string", Required = true, MaxLength = 200 },
                new() { Name = "email", Type = "email", Required = true, MaxLength = 254 },
                new() { Name = "registeredAt", Type = "date", Required = false, Format = "yyyy-MM-dd" },
                new() { Name = "isActive", Type = "boolean", Required = true }
            ]
        }
    };

    private static SaveDataSourceRequest InvalidSchema(string variant)
    {
        var request = ValidRequest();
        var schema = request.Schema!;
        var fields = schema.Fields!.ToList();
        switch (variant)
        {
            case "unknown": fields[0] = fields[0]! with { Name = "unknown" }; break;
            case "duplicate": fields.Add(fields[0]); break;
            case "caseDuplicate": fields.Add(fields[0]! with { Name = "EXTERNALID" }); break;
            case "key": schema = schema with { KeyFields = ["email"] }; break;
            case "keys": schema = schema with { KeyFields = ["externalId", "email"] }; break;
            case "missing": fields.RemoveAt(0); break;
            case "type": fields[0] = fields[0]! with { Type = "integer" }; break;
            case "required": fields[0] = fields[0]! with { Required = false }; break;
            case "optionalRequired": fields[3] = fields[3]! with { Required = true }; break;
            case "length": fields[0] = fields[0]! with { MaxLength = 81 }; break;
            case "zeroLength": fields[0] = fields[0]! with { MaxLength = 0 }; break;
            case "missingLength": fields[0] = fields[0]! with { MaxLength = null }; break;
            case "nameLength": fields[1] = fields[1]! with { MaxLength = 201 }; break;
            case "emailLength": fields[2] = fields[2]! with { MaxLength = 255 }; break;
            case "dateLength": fields[3] = fields[3]! with { MaxLength = 10 }; break;
            case "booleanLength": fields[4] = fields[4]! with { MaxLength = 1 }; break;
            case "dateFormat": fields[3] = fields[3]! with { Format = "dd.MM.yyyy" }; break;
            case "extraFormat": fields[0] = fields[0]! with { Format = "custom" }; break;
            case "regex": fields[0] = fields[0]! with { RegexPattern = ".*" }; break;
            case "delimiter": schema = schema with { CsvDelimiter = "::" }; break;
            case "nullDelimiter": schema = schema with { CsvDelimiter = null }; break;
            case "nullField": fields[0] = null; break;
            case "nullSchema": return request with { Schema = null };
            case "emptyFields": fields.Clear(); break;
            default: throw new ArgumentOutOfRangeException(nameof(variant));
        }

        return request with { Schema = schema with { Fields = fields } };
    }
}
