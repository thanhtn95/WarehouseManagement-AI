using Npgsql;
using Wms.Modules.Inventory.Application;
using Wms.Worker;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

string connectionString = builder.Configuration.GetConnectionString("Wms")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Wms is not configured. The worker must not start without a "
        + "database — a process that starts and silently reconciles nothing is worse "
        + "than one that refuses to start.");

builder.Services.AddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());
builder.Services.AddSingleton<ReconciliationService>();
builder.Services.AddHostedService<ReconciliationJob>();

IHost host = builder.Build();

host.Run();
