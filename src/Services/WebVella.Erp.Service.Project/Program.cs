// Minimal entry point stub for the Project/Task microservice.
// Full implementation will be provided by the assigned agent for Program.cs.
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.Run();
