using FellowshipAnalyzer.Api.Core;
using FellowshipAnalyzer.Api.Core.Generated;
using FellowshipAnalyzer.ServiceDefaults;

using Microsoft.AspNetCore.Diagnostics.HealthChecks;

if (Environment.GetEnvironmentVariable("PORT") is { Length: > 0 } portValue
    && int.TryParse(portValue, out var port))
{
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://+:{port}");
}

var staticRootPath = Environment.GetEnvironmentVariable("StaticRoot");
var serveStaticClient = staticRootPath is { Length: > 0 } && Directory.Exists(staticRootPath);
var builderOptions = new WebApplicationOptions
{
    Args = args,
    WebRootPath = serveStaticClient ? Path.GetFullPath(staticRootPath!) : null,
};

var builder = WebApplication.CreateBuilder(builderOptions);

builder.AddServiceDefaults();

builder.Services.AddFellowshipLogsApi(
    builder.Configuration,
    allowDevelopmentLoopbackOrigins: builder.Environment.IsDevelopment());
builder.Services.AddFilePersistentCache(builder.Configuration);

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = check => check.Tags.Contains("live") });

if (serveStaticClient)
{
    app.UseDefaultFiles();
    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();
}

app.MapFellowshipApiEndpoints();

if (serveStaticClient)
{
    app.MapFallbackToFile("index.html");
}

app.Run();

