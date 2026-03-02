// Minimal entry point for the CRM microservice.
// This placeholder enables the project to compile as Microsoft.NET.Sdk.Web.
// The full implementation (DI registration, middleware pipeline, gRPC/REST endpoints)
// will be provided by the dedicated Program.cs agent.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "crm" }));
app.Run();
