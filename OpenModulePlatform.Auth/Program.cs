// File: OpenModulePlatform.Auth/Program.cs
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using OpenModulePlatform.Auth.Services;
using OpenModulePlatform.Web.Shared.Extensions;
using OpenModulePlatform.Web.Shared.Security;
using OpenModulePlatform.Web.Shared.Services;

var builder = WebApplication.CreateBuilder(args);

builder.AddOmpWebLogging();
builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<SqlConnectionFactory>();
builder.Services.AddSingleton<LocalPasswordHasher>();
builder.Services.AddSingleton<WindowsPrincipalReader>();
builder.Services.AddScoped<OmpAuthRepository>();
builder.Services.AddOmpCookieAuthentication(builder.Configuration);
var runningUnderIis = !string.IsNullOrWhiteSpace(
    Environment.GetEnvironmentVariable("ASPNETCORE_IIS_PHYSICAL_PATH"));

if (!runningUnderIis)
{
    builder.Services.AddAuthentication()
        .AddNegotiate();
}

builder.Services.AddAuthorization();

var app = builder.Build();

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();

app.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(OmpAuthDefaults.AuthenticationScheme);
    return Results.LocalRedirect("/auth/login");
}).RequireAuthorization();

app.Run();
