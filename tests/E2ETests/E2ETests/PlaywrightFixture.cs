using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Serilog;
using Reaparr.BaseTests;
using Reaparr.Environment;
using AppHostProgram = Reaparr.AppHost.Program;

namespace E2ETests;

/// <summary>
/// E2E test fixture that manages the WebApplicationFactory for the backend with static frontend files,
/// and Playwright browser instances for E2E testing.
/// </summary>
public class PlaywrightFixture : IAsyncLifetime
{
    private static readonly object _lock = new();
    private static bool _isInitialized = false;
    private static WebApplicationFactory<AppHostProgram>? _factory;
    private static IPlaywright? _playwright;
    private static IBrowser? _browser;
    private static string? _baseUrl;
    
    private ILogger? _logger;

    public WebApplicationFactory<AppHostProgram> Factory => _factory ?? throw new InvalidOperationException("Fixture not initialized");
    public string BaseUrl => _baseUrl ?? throw new InvalidOperationException("Fixture not initialized");
    
    // Playwright is now supported with Kestrel mode
    public IPlaywright Playwright => _playwright ?? throw new InvalidOperationException("Fixture not initialized");
    public IBrowser Browser => _browser ?? throw new InvalidOperationException("Fixture not initialized");
    
    /// <summary>
    /// Creates an HttpClient for API testing with authentication headers.
    /// </summary>
    public HttpClient CreateClient()
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        
        // Add test authentication header
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("TestScheme");
        
        return client;
    }

    public async ValueTask InitializeAsync()
    {
        lock (_lock)
        {
            if (_isInitialized)
                return;
        }

        try
        {
            // Set integration test mode to disable SignalR, Swagger, and SPA middleware
            EnvironmentExtensions.SetIntegrationTestMode(true);

            // 1. Build the Nuxt frontend once
            await BuildFrontendAsync();

            // 2. Setup database and create WebApplicationFactory with Kestrel mode
            var seed = new Seed(Random.Shared.Next(1, int.MaxValue));
            var dbName = MockDatabase.GetMemoryDatabaseName();
            
            LogMessage($"Initialized E2E test with database name: {dbName}");
            
            // Initialize the database before creating the factory
            await MockDatabase.GetMemoryDbContext(dbName).Setup(seed, options: null);
            
            // Use Kestrel mode (useKestrel: true) for E2E tests to enable Playwright
            _factory = new ReaparrWebApplicationFactory(seed, dbName, options: null, useKestrel: true);
            
            // Ensure the server is started (WebApplicationFactory starts it automatically when Server property is accessed)
            _ = _factory.Server;
            
            // Get the server address from the factory
            var reaparrFactory = (ReaparrWebApplicationFactory)_factory;
            LogMessage($"Server address from factory: {reaparrFactory.ServerAddress ?? "null"}");
            
            _baseUrl = reaparrFactory.ServerAddress ?? throw new InvalidOperationException("Failed to get server address from Kestrel - server may not have started properly or address feature is not available");
            
            LogMessage($"Kestrel server started at: {_baseUrl}");

            // 3. Setup Playwright
            LogMessage("Initializing Playwright...");
            _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = ["--no-sandbox", "--disable-dev-shm-usage"] // Required for Linux containers
            });

            lock (_lock)
            {
                _isInitialized = true;
            }

            LogMessage("PlaywrightFixture initialization completed successfully");
        }
        catch (Exception ex)
        {
            LogMessage($"Failed to initialize PlaywrightFixture: {ex.Message}");
            await CleanupAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
    }

    private static async Task CleanupAsync()
    {
        LogMessage("Cleaning up PlaywrightFixture...");

        // Dispose browser and playwright
        if (_browser != null)
        {
            await _browser.DisposeAsync();
            _browser = null;
        }

        _playwright?.Dispose();
        _playwright = null;

        // Dispose factory
        _factory?.Dispose();
        _factory = null;

        lock (_lock)
        {
            _isInitialized = false;
        }

        LogMessage("PlaywrightFixture cleanup completed");
    }

    private static async Task BuildFrontendAsync()
    {
        LogMessage("Building frontend with 'bun run generate'...");

        var projectRoot = FindProjectRoot();
        var frontendPath = Path.Combine(projectRoot, "src", "AppHost", "ClientApp");

        if (!Directory.Exists(frontendPath))
            throw new DirectoryNotFoundException($"Frontend directory not found at {frontendPath}");

        var processStartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "bun",
            Arguments = "run generate",
            WorkingDirectory = frontendPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(processStartInfo);
        if (process == null)
            throw new InvalidOperationException("Failed to start frontend build process");

        // Read output
        var output = await process.StandardOutput.ReadToEndAsync();
        var errors = await process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            LogMessage($"Frontend build failed with exit code {process.ExitCode}");
            LogMessage($"Output: {output}");
            LogMessage($"Errors: {errors}");
            throw new InvalidOperationException($"Frontend build failed with exit code {process.ExitCode}");
        }

        LogMessage("Frontend build completed successfully");

        // Verify output directory exists
        var outputPath = Path.Combine(frontendPath, ".output", "public");
        if (!Directory.Exists(outputPath))
            throw new DirectoryNotFoundException($"Frontend output directory not found at {outputPath}");
    }

    private static string FindProjectRoot()
    {
        var currentDir = Directory.GetCurrentDirectory();
        var projectRoot = currentDir;

        // Walk up the directory tree to find the solution file
        while (!string.IsNullOrEmpty(projectRoot) && !File.Exists(Path.Combine(projectRoot, "Reaparr.sln")))
        {
            var parent = Directory.GetParent(projectRoot);
            projectRoot = parent?.FullName;
        }

        if (string.IsNullOrEmpty(projectRoot))
            throw new InvalidOperationException("Could not find Reaparr.sln file to determine project root");

        return projectRoot;
    }

    private static void LogMessage(string message)
    {
        // Use Serilog for logging
        Log.Information("[E2E] {Message}", message);
    }
    
    /// <summary>
    /// Sets the logger for this fixture instance
    /// </summary>
    public void SetLogger(ILogger logger)
    {
        _logger = logger;
    }
}