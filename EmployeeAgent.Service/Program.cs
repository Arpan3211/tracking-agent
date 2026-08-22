using EmployeeAgent.Service;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "EmployeeAgentService");
builder.Services.AddHostedService<SessionSupervisor>();

var host = builder.Build();
host.Run();
