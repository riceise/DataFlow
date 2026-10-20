using System.Diagnostics;
using Serilog.Context;

namespace DataFlow.Api.Middleware;

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-ID";
    private const int MaxHeaderLength = 128;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = ResolveCorrelationId(context);
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;

        var traceId = Activity.Current?.TraceId.ToString() ?? correlationId;

        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("TraceId", traceId))
        {
            await next(context);
        }
    }

    private static string ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            var candidate = values.ToString().Trim();
            if (IsSafeCorrelationId(candidate))
            {
                return candidate;
            }
        }

        return Activity.Current?.TraceId.ToString() ?? Guid.CreateVersion7().ToString("N");
    }

    private static bool IsSafeCorrelationId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxHeaderLength)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (!char.IsLetterOrDigit(ch) && ch is not ('-' or '_' or '.' or ':'))
            {
                return false;
            }
        }

        return true;
    }
}
