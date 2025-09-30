using Autofac;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Reaparr.AppHost;

namespace Reaparr.BaseTests;

public class ReaparrWebApplicationFactory : WebApplicationFactory<Program>
{
    public Seed Seed { get; }

    public readonly string MemoryDbName;

    private static readonly ILogger _log = new LogConfig().CreateLogInstance<ReaparrWebApplicationFactory>();

    private readonly UnitTestDataConfig _config;
    private readonly bool _useKestrel;

    /// <summary>
    /// Gets the server address when running in Kestrel mode. Returns null for TestServer mode.
    /// </summary>
    public string? ServerAddress { get; private set; }

    /// <summary>
    /// Creates a new ReaparrWebApplicationFactory instance.
    /// </summary>
    /// <param name="seed">Random seed for test data generation</param>
    /// <param name="memoryDbName">In-memory database name</param>
    /// <param name="options">Optional configuration for test data</param>
    /// <param name="useKestrel">If true, uses real Kestrel server on a dynamic port. If false (default), uses in-memory TestServer.</param>
    public ReaparrWebApplicationFactory(
        Seed seed,
        string memoryDbName,
        Action<UnitTestDataConfig>? options = null,
        bool useKestrel = false
    )
    {
        _useKestrel = useKestrel;

        this.WithWebHostBuilder(builder =>
        {
            // Disable caching by using custom configurations
            builder.UseSetting("cacheEnabled", "false");

            if (_useKestrel)
            {
                // Use Kestrel with a dynamic port for E2E tests
                builder.UseKestrel();
                builder.UseUrls("http://127.0.0.1:0"); // Dynamic port assignment
            }

            builder.ConfigureAppConfiguration(
                (ctx, config) =>
                {
                    // tell ASP.NET to serve the prebuilt Nuxt files
                    ctx.Configuration["SpaStaticFiles:RootPath"] = Path.Combine(
                        Directory.GetCurrentDirectory(),
                        "src/AppHost/ClientApp/.output/public"
                    );
                }
            );

            builder.ConfigureTestServices(services =>
            {
                // https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-9.0#mock-authentication
                services
                    .AddAuthentication(defaultScheme: "TestScheme")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("TestScheme", _ => { });
            });
        });

        Seed = seed;

        MemoryDbName = memoryDbName;
        _config = UnitTestDataConfig.FromOptions(options);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureContainer<ContainerBuilder>(autoFacBuilder =>
            autoFacBuilder.RegisterModule(new TestModule { MemoryDbName = MemoryDbName, Config = _config })
        );

        try
        {
            // For Kestrel mode, we need to create the host without automatically starting it
            // to capture the address after it starts
            if (_useKestrel)
            {
                _log.Here().Information("Creating host in Kestrel mode...");
                var testHost = builder.Build();

                if (testHost == null)
                {
                    throw new InvalidOperationException("Failed to create host");
                }

                _log.Here().Information("Starting Kestrel host...");
                // Start the host
                testHost.StartAsync().GetAwaiter().GetResult();

                // Give the server a moment to bind to the port and populate the address feature
                System.Threading.Thread.Sleep(500);

                // Now capture the server address
                var server = testHost.Services.GetService<IServer>();
                _log.Here().Information("Server retrieved: {ServerType}", server?.GetType().Name ?? "null");

                var addressFeature = server?.Features.Get<IServerAddressesFeature>();
                _log.Here().Information("Address feature retrieved: {HasFeature}", addressFeature != null);

                if (addressFeature != null)
                {
                    _log.Here().Information("Addresses in feature: {Count}", addressFeature.Addresses.Count);
                    foreach (var addr in addressFeature.Addresses)
                    {
                        _log.Here().Information("Found address: {Address}", addr);
                    }
                }

                ServerAddress = addressFeature?.Addresses.FirstOrDefault();

                if (ServerAddress != null)
                {
                    _log.Here().Information("Kestrel server started at: {ServerAddress}", ServerAddress);
                }
                else
                {
                    _log.Here()
                        .Warning(
                            "Kestrel server started but address could not be determined. AddressFeature might not be available yet."
                        );
                }

                return testHost;
            }
            else
            {
                // Default TestServer mode
                return base.CreateHost(builder);
            }
        }
        catch (Exception e)
        {
            _log.Here().Fatal(e.Message);
            throw;
        }
    }
}
