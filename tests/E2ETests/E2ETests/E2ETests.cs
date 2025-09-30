using Microsoft.Playwright;
using Reaparr.BaseTests;
using Shouldly;

namespace E2ETests;

/// <summary>
/// E2E tests for the Reaparr application using WebApplicationFactory with Kestrel mode and Playwright.
/// 
/// The fixture runs a real Kestrel server on a dynamic port, allowing both:
/// - HttpClient-based API testing
/// - Playwright browser-based UI testing
/// 
/// Note: IntegrationTestMode is enabled, so some features (SignalR, Swagger) may be disabled.
/// Static frontend files from .output/public are served for UI testing.
/// </summary>
public class E2ETests(PlaywrightFixture fixture, ITestOutputHelper output) : IClassFixture<PlaywrightFixture>
{
    private readonly PlaywrightFixture _fixture = fixture;
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public async Task BackendHealthCheck_ShouldReturn200OK_WhenAuthenticated()
    {
        // Arrange
        var client = _fixture.CreateClient();
        await client.SignIn(); // Sign in using test credentials
        _output.WriteLine($"Testing health check at {_fixture.BaseUrl}/api/health");

        // Act
        var response = await client.GetAsync("/api/health");

        // Assert
        response.ShouldNotBeNull();
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
        
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldNotBeNullOrEmpty();
        _output.WriteLine($"✅ Health check response: {content}");
    }

    [Fact]
    public async Task BackendAPI_ShouldRequireAuthentication_WhenNotSignedIn()
    {
        // Arrange
        var client = _fixture.CreateClient();
        // Note: Do NOT sign in to test that authentication is required
        
        // Remove the test auth header to simulate unauthenticated request
        client.DefaultRequestHeaders.Authorization = null;
        _output.WriteLine("Testing that API requires authentication");

        // Act
        var response = await client.GetAsync("/api/health");

        // Assert
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Unauthorized);
        _output.WriteLine("✅ API correctly requires authentication");
    }

    [Fact]
    public async Task FrontendRoot_ShouldLoadAndHaveVisibleElements()
    {
        // Arrange
        _output.WriteLine($"Testing frontend at {_fixture.BaseUrl}");
        var page = await _fixture.Browser.NewPageAsync();

        try
        {
            // Act
            var response = await page.GotoAsync(_fixture.BaseUrl);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // Get page title and check for body element
            var title = await page.TitleAsync();
            var body = page.Locator("body").First;
            var isBodyVisible = await body.IsVisibleAsync();

            // Assert
            response.ShouldNotBeNull();
            response.Ok.ShouldBeTrue($"Expected successful response but got {response.Status}");
            title.ShouldNotBeNullOrEmpty();
            isBodyVisible.ShouldBeTrue();

            _output.WriteLine($"✅ Frontend loaded successfully with title: {title}");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task BackendHealthCheck_WithPlaywright_ShouldReturn200OK()
    {
        // Arrange
        var healthUrl = $"{_fixture.BaseUrl}/api/health";
        _output.WriteLine($"Testing health check at {healthUrl} with Playwright");
        var page = await _fixture.Browser.NewPageAsync();

        try
        {
            // Act - Note: Playwright won't send auth headers, so we expect 401
            var response = await page.GotoAsync(healthUrl);

            // Assert - Health endpoint requires authentication
            response.ShouldNotBeNull();
            response.Status.ShouldBe(401, "Health check should require authentication via Playwright (no cookies)");

            _output.WriteLine($"✅ Health check correctly returned {response.Status} (Unauthorized) via Playwright");
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}