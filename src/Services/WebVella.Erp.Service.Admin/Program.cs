// Minimal entry point stub for Admin service compilation.
// This will be replaced by the full implementation agent.
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.Run();
