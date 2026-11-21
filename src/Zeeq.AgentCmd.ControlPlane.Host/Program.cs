using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using Zeeq.AgentCmd.ControlPlane;
using Zeeq.AgentCmd.ControlPlane.Host;

if (!await SqliteMaintenance.TryRunAsync(args))
{
    await ControlPlaneApplication.Build(args).RunAsync();
}

/// <summary>Creates the management host without starting application commands or provider clients.</summary>
public static class ControlPlaneApplication
{
    /// <summary>Builds an independently configurable host; callers may install their authenticated-principal middleware.</summary>
    /// <param name="args">Host/bootstrap settings. Credentials are referenced server-side.</param>
    /// <param name="configure">Optional trusted host registration, including authentication.</param>
    public static WebApplication Build(
        string[] args,
        Action<WebApplicationBuilder>? configure = null
    )
    {
        var builder = WebApplication.CreateBuilder(args);
        if (string.IsNullOrWhiteSpace(builder.Configuration["urls"]))
        {
            builder.WebHost.UseUrls("http://127.0.0.1:5180");
        }
        var options = builder.Configuration.GetSection("ControlPlane").Get<ControlPlaneOptions>()
            ?? new();
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(services => new SqliteHostLease(
            services.GetRequiredService<ControlPlaneOptions>()
        ));
        Func<DbConnection> factory;
        if (options.Provider == "Sqlite")
        {
            var path = Path.GetFullPath(options.DatabasePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            factory = () =>
                new SqliteConnection(
                    new SqliteConnectionStringBuilder
                    {
                        DataSource = path,
                        DefaultTimeout = 10,
                    }.ToString()
                );
        }
        else if (options.Provider == "Postgres")
        {
            if (string.IsNullOrWhiteSpace(options.ConnectionStringReference))
            {
                throw new ManagementException("connection_reference_required", 503);
            }
            var connectionString = System.Environment.GetEnvironmentVariable(
                options.ConnectionStringReference
            );
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new ManagementException("connection_reference_unavailable", 503);
            }
            factory = () => new NpgsqlConnection(connectionString);
        }
        else
        {
            throw new ManagementException("store_provider_unsupported", 503);
        }
        builder.Services.AddSingleton(new ManagementDatabase(options.Provider, factory));
        builder.Services.AddSingleton<RelationalControlPlaneStore>();
        builder.Services.AddSingleton<IControlPlaneStore>(services =>
            services.GetRequiredService<RelationalControlPlaneStore>()
        );
        builder.Services.AddSingleton<IControlPlaneAssetStore>(
            new FileAssetStore(options.AssetPath)
        );
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();
        builder.Services.Scan(scan =>
            scan.FromAssemblyOf<WorkspaceEndpoint>()
                .AddClasses(classes => classes.AssignableTo<IEndpoint>(), publicOnly: false)
                .AsImplementedInterfaces()
                .WithTransientLifetime()
                .AddClasses(classes => classes.AssignableTo<IEndpointHandler>(), publicOnly: false)
                .AsSelf()
                .WithTransientLifetime()
        );
        builder.Services.AddCors(cors =>
            cors.AddDefaultPolicy(policy =>
                policy
                    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
                    .AllowCredentials()
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                    .WithExposedHeaders("ETag", "X-AgentCmd-List-Limit")
            )
        );
        configure?.Invoke(builder);
        var app = builder.Build();
        _ = app.Services.GetRequiredService<SqliteHostLease>();
        app.UseCors();
        app.Use(
            async (context, next) =>
            {
                try
                {
                    await next(context);
                }
                catch (ManagementException error)
                {
                    context.Response.StatusCode = error.Status;
                    await context.Response.WriteAsJsonAsync(
                        new
                        {
                            type = "about:blank",
                            title = error.Code,
                            status = error.Status,
                            code = error.Code,
                            revision = error.Revision,
                        },
                        context.RequestAborted
                    );
                }
                catch (OperationCanceledException)
                    when (context.RequestAborted.IsCancellationRequested)
                {
                    context.Response.StatusCode = 499;
                }
                catch (Exception error)
                    when (error
                            is DbException
                                or IOException
                                or System.Text.Json.JsonException
                                or BadHttpRequestException
                    )
                {
                    // Database/provider diagnostics can contain credentials or retained content. Return only a bounded category.
                    context.Response.StatusCode = error
                        is System.Text.Json.JsonException
                            or BadHttpRequestException
                        ? 400
                        : 503;
                    await context.Response.WriteAsJsonAsync(
                        new
                        {
                            type = "about:blank",
                            title = "request_unavailable",
                            status = context.Response.StatusCode,
                            code = "request_unavailable",
                        },
                        context.RequestAborted
                    );
                }
            }
        );
        app.MapOpenApi();
        var api = app.MapGroup("/api/v1");
        api.AddEndpointFilter<HoldoutResponseFilter>();
        foreach (var endpoint in app.Services.GetRequiredService<IEnumerable<IEndpoint>>())
        {
            endpoint.MapEndpoints(api);
        }
        app.Services.GetRequiredService<RelationalControlPlaneStore>()
            .InitializeAsync()
            .GetAwaiter()
            .GetResult();
        return app;
    }
}
