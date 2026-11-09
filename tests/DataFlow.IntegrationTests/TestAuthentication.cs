using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DataFlow.IntegrationTests;

public sealed class TestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public static readonly Guid UserId = Guid.Parse("480765d3-10d4-4f15-b922-c60ca761eec9");
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey("X-Test-Anonymous"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var id = Request.Headers["X-Test-User"].FirstOrDefault() ?? UserId.ToString();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "Test"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, "Test")));
    }
}

public sealed class TestCurrentUser : DataFlow.Application.Abstractions.ICurrentUser
{
    public Guid UserId => TestAuthentication.UserId;
}
