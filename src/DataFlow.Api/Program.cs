using DataFlow.Api.Authentication;
using DataFlow.Api.Endpoints;
using DataFlow.Api.Middleware;
using DataFlow.Application;
using DataFlow.Application.Abstractions;
using DataFlow.Application.Dispatching;
using DataFlow.Application.Jobs;
using DataFlow.Infrastructure.Configuration;
using DataFlow.Infrastructure.Imports;
using DataFlow.Infrastructure.Observability;
using DataFlow.Infrastructure.Persistence;
using DataFlow.Infrastructure.Storage;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Scalar.AspNetCore;
using Serilog;
using Serilog.Formatting.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, loggerConfiguration) =>
{
    loggerConfiguration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", "DataFlow.Api")
        .WriteTo.Console(new JsonFormatter(renderMessage: true));
});

builder.Services.AddDataFlowConfiguration(builder.Configuration);
builder.Services.AddDataFlowPersistence();
builder.Services.AddDataFlowStorage();
builder.Services.AddDataFlowObservability(builder.Configuration, "DataFlow.Api");
var protection = builder.Services.AddDataProtection().SetApplicationName("DataFlow");
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    protection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}
builder.Services.AddDataFlowApplication();
builder.Services.AddScoped<DataFlow.Infrastructure.Rules.RuleLibraryService>();
builder.Services.AddScoped<JobControlHandler>();
builder.Services.AddScoped<IRequestHandler<RetryJobCommand, JobAcceptedDto>>(p => p.GetRequiredService<JobControlHandler>());
builder.Services.AddScoped<IRequestHandler<CancelJobCommand, JobAcceptedDto>>(p => p.GetRequiredService<JobControlHandler>());
builder.Services.AddScoped<IRequestHandler<UploadFileCommand, JobAcceptedDto>, UploadFileHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddAuthorization();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options => options.User.RequireUniqueEmail = true)
    .AddEntityFrameworkStores<DataFlowDbContext>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "DataFlow.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.Configure<Microsoft.AspNetCore.Routing.RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseSerilogRequestLogging(options =>
{
    options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
    {
        diagnosticContext.Set("CorrelationId", httpContext.TraceIdentifier);
    };
});
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    var unsafeMethod = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
        && !HttpMethods.IsOptions(context.Request.Method);
    // Identity cookies are sent automatically by browsers, including before login.
    var cookieRequest = context.User.Identity?.AuthenticationType == IdentityConstants.ApplicationScheme
        || context.Request.Path.StartsWithSegments("/api/v1/auth");
    if (unsafeMethod && cookieRequest)
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException)
        {
            await Results.Problem(statusCode: 400, title: "Invalid antiforgery token.",
                extensions: new Dictionary<string, object?> { ["code"] = "invalid_antiforgery_token" }).ExecuteAsync(context);
            return;
        }
    }
    await next(context);
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();
app.MapDataFlowHealth();
app.MapAuthEndpoints();
app.MapRuleEndpoints();
app.MapRuleSourceEndpoints();
app.MapDataSourceEndpoints();
app.MapFileEndpoints();

await app.RunAsync();

public partial class Program;
