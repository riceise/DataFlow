using DataFlow.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;

namespace DataFlow.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record Credentials(string Email, string Password);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth").WithTags("Authentication");
        group.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
            Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken }));
        group.MapPost("/register", async (Credentials request, UserManager<ApplicationUser> users) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || request.Email.Length > 254 ||
                !System.Net.Mail.MailAddress.TryCreate(request.Email, out var address) || address.Address != request.Email)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["email"] = ["A valid email is required."] });
            }

            var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = request.Email, Email = request.Email };
            var result = await users.CreateAsync(user, request.Password);
            return result.Succeeded ? Results.Created("/api/v1/auth/me", new { user.Id, user.Email })
                : Results.ValidationProblem(result.Errors.GroupBy(x => x.Code).ToDictionary(x => x.Key, x => x.Select(e => e.Description).ToArray()));
        });
        group.MapPost("/login", async (Credentials request, SignInManager<ApplicationUser> signIn, UserManager<ApplicationUser> users) =>
        {
            var user = string.IsNullOrWhiteSpace(request.Email) ? null : await users.FindByEmailAsync(request.Email);
            if (user is null)
            {
                return Results.Unauthorized();
            }

            var result = await signIn.PasswordSignInAsync(user, request.Password, isPersistent: false, lockoutOnFailure: true);
            return result.Succeeded ? Results.Ok(new { user.Id, user.Email }) : Results.Unauthorized();
        });
        group.MapPost("/logout", async (SignInManager<ApplicationUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.NoContent();
        }).RequireAuthorization();
        group.MapGet("/me", async (HttpContext context, UserManager<ApplicationUser> users) =>
        {
            var user = await users.GetUserAsync(context.User);
            return user is null ? Results.Unauthorized() : Results.Ok(new { user.Id, user.Email });
        }).RequireAuthorization();
    }
}
