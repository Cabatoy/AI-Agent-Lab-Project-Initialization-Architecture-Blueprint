using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using ControlPlane.Api.TransactionalMail;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Playwright;
using Xunit.Sdk;

[assembly: HostingStartup(typeof(ControlPlane.Api.Tests.BrowserAcceptanceHostingStartup))]

namespace ControlPlane.Api.Tests;

public sealed class BrowserAcceptanceFactAttribute : FactAttribute
{
    public BrowserAcceptanceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HIC_BROWSER_ACCEPTANCE") != "1")
            Skip = "Requires the isolated browser acceptance fixture.";
    }
}

public sealed class BrowserAcceptanceHostingStartup : IHostingStartup
{
    public void Configure(IWebHostBuilder builder) =>
        builder.ConfigureServices(services => services.AddHostedService<BrowserAcceptanceStartupObserver>());
}

public sealed class BrowserAcceptanceStartupObserver(IServiceProvider provider) : IHostedService
{
    internal static readonly TaskCompletionSource<IServiceProvider> Services =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Services.TrySetResult(provider);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[CollectionDefinition("Browser acceptance", DisableParallelization = true)]
public sealed class BrowserAcceptanceCollection;

[Collection("Browser acceptance")]
public class PasswordRecoveryBrowserTests
{
    [BrowserAcceptanceFact]
    public async Task BrowserResetRevokesOwnerSessionAndPreservesSecondAccount()
    {
        var redis = Environment.GetEnvironmentVariable("HIC_BROWSER_TEST_REDIS");
        if (redis != "127.0.0.1:26379")
            throw new InvalidOperationException("The dedicated browser test Redis endpoint is required.");

        await PasswordResetPostgresTests.WithTables(async (cs, db) =>
        {
            var alice = Assert.IsType<UserIdentity>((await UserIdentityStore.CreateAsync(cs, "alice@example.test", "alice")).Identity);
            var bob = Assert.IsType<UserIdentity>((await UserIdentityStore.CreateAsync(cs, "bob@example.test", "bob")).Identity);
            const string oldPassword = "synthetic-old-password";
            const string newPassword = "synthetic-new-password";
            const string bobPassword = "synthetic-bob-password";
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, alice.Id, oldPassword));
            Assert.True(await LocalCredentialStore.SetInitialPasswordAsync(cs, bob.Id, bobPassword));

            var apiPort = FreePort();
            var webPort = FreePort();
            while (webPort == apiPort) webPort = FreePort();
            var origin = $"https://127.0.0.1:{webPort}";
            var certPath = Path.Combine(Path.GetTempPath(), $"hic215-{Guid.NewGuid():N}.crt");
            var keyPath = Path.Combine(Path.GetTempPath(), $"hic215-{Guid.NewGuid():N}.key");
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            await File.WriteAllTextAsync(certPath, cert.ExportCertificatePem());
            await File.WriteAllTextAsync(keyPath, rsa.ExportPkcs8PrivateKeyPem());
            var settings = new Dictionary<string, string?>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
                ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{apiPort}",
                ["ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"] = typeof(BrowserAcceptanceHostingStartup).Assembly.GetName().Name,
                ["ConnectionStrings__Postgres"] = cs,
                ["ConnectionStrings__Redis"] = redis,
                ["HIC_AUTH_TRUSTED_ORIGIN"] = origin,
                ["HIC_MAIL_TRUSTED_RESET_ORIGIN"] = origin,
                ["HIC_MAIL_CAPTURE_ENABLED"] = "true",
                ["HIC_PASSWORD_RESET_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Logging__LogLevel__Default"] = "Warning",
            };
            var previous = settings.ToDictionary(pair => pair.Key, pair => Environment.GetEnvironmentVariable(pair.Key));
            Task? appTask = null;
            IServiceProvider? services = null;
            Process? vite = null;
            var phase = "starting the fixture";
            try
            {
                foreach (var pair in settings) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                appTask = Task.Run(() =>
                    typeof(UserIdentityStore).Assembly.EntryPoint!.Invoke(null, [Array.Empty<string>()]));
                services = await BrowserAcceptanceStartupObserver.Services.Task.WaitAsync(TimeSpan.FromSeconds(20));
                using var http = new HttpClient();
                await WaitForHttp(http, $"http://127.0.0.1:{apiPort}/health");
                var sink = Assert.IsType<CaptureTransactionalMailSink>(services.GetRequiredService<ITransactionalMailSender>());

                var root = FindRoot();
                var start = new ProcessStartInfo("node")
                {
                    WorkingDirectory = Path.Combine(root, "src", "WebUI"),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.ArgumentList.Add(Path.Combine("node_modules", "vite", "bin", "vite.js"));
                start.ArgumentList.Add("--config");
                start.ArgumentList.Add(Path.Combine("tests", "browser.vite.config.ts"));
                start.Environment["HIC_WEB_PORT"] = webPort.ToString();
                start.Environment["HIC_API_URL"] = $"http://127.0.0.1:{apiPort}";
                start.Environment["HIC_BROWSER_TEST_CERT"] = certPath;
                start.Environment["HIC_BROWSER_TEST_KEY"] = keyPath;
                vite = Process.Start(start) ?? throw new InvalidOperationException("Unable to start test Vite.");
                vite.BeginOutputReadLine();
                vite.BeginErrorReadLine();
                using var tlsHttp = new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
                });
                await WaitForHttp(tlsHttp, origin);
                using var spoofedScheme = new HttpRequestMessage(HttpMethod.Get, $"{origin}/api/auth/csrf");
                spoofedScheme.Headers.Add("X-Forwarded-Proto", "http");
                using var csrfResponse = await tlsHttp.SendAsync(spoofedScheme);
                Assert.Equal(HttpStatusCode.OK, csrfResponse.StatusCode);
                Assert.True(csrfResponse.Headers.TryGetValues("Set-Cookie", out var csrfCookies) &&
                    csrfCookies.Any(cookie => cookie.Contains("secure", StringComparison.OrdinalIgnoreCase)));
                using var wrongHost = new HttpRequestMessage(HttpMethod.Get, $"{origin}/api/auth/session");
                wrongHost.Version = HttpVersion.Version20;
                wrongHost.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
                wrongHost.Headers.Host = "attacker.example";
                using var rejectedHost = await tlsHttp.SendAsync(wrongHost);
                Assert.Equal(HttpStatusCode.Forbidden, rejectedHost.StatusCode);

                phase = "launching the browser";
                using var playwright = await Playwright.CreateAsync();
                var browserPath = Environment.GetEnvironmentVariable("HIC_BROWSER_EXECUTABLE");
                await using var browser = await playwright.Chromium.LaunchAsync(new()
                {
                    Headless = true,
                    ExecutablePath = string.IsNullOrEmpty(browserPath) ? null : browserPath,
                });
                await using var aliceContext = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
                await using var bobContext = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
                await using var recoveryContext = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
                aliceContext.SetDefaultTimeout(45000);
                bobContext.SetDefaultTimeout(45000);
                recoveryContext.SetDefaultTimeout(45000);
                var alicePage = await aliceContext.NewPageAsync();
                var bobPage = await bobContext.NewPageAsync();
                var recoveryPage = await recoveryContext.NewPageAsync();

                phase = "opening the first sign-in page";
                await alicePage.GotoAsync(origin);
                phase = "submitting the first sign-in";
                await SubmitLogin(alicePage, "alice@example.test", oldPassword);
                phase = "verifying the first sign-in";
                await Assertions.Expect(alicePage.GetByText("alice@example.test")).ToBeVisibleAsync();
                phase = "opening the second sign-in page";
                await bobPage.GotoAsync(origin);
                phase = "submitting the second sign-in";
                await SubmitLogin(bobPage, "bob@example.test", bobPassword);
                phase = "verifying the second sign-in";
                await Assertions.Expect(bobPage.GetByText("bob@example.test")).ToBeVisibleAsync();

                phase = "requesting the recovery link";
                await recoveryPage.GotoAsync(origin);
                await recoveryPage.GetByRole(AriaRole.Button, new() { Name = "Forgot password?" }).ClickAsync();
                await recoveryPage.GetByLabel("Account email or username").FillAsync("alice@example.test");
                await recoveryPage.GetByRole(AriaRole.Button, new() { Name = "Request recovery link" }).ClickAsync();
                await Assertions.Expect(recoveryPage.GetByRole(AriaRole.Status))
                    .ToContainTextAsync("If an account exists");
                var captured = Assert.Single(sink.Captures);
                Assert.Equal("alice@example.test", captured.Intent.Recipient);
                var link = captured.Intent.Placeholders["resetUrl"];
                Assert.Equal(origin, new Uri(link).GetLeftPart(UriPartial.Authority));

                phase = "opening the captured link and changing the password";
                await recoveryPage.GotoAsync(link);
                await recoveryPage.GetByLabel("New password", new() { Exact = true }).FillAsync(newPassword);
                await recoveryPage.GetByLabel("Confirm new password").FillAsync(newPassword);
                await recoveryPage.GetByRole(AriaRole.Button, new() { Name = "Change password" }).ClickAsync();
                await Assertions.Expect(recoveryPage.GetByRole(AriaRole.Status)).ToContainTextAsync("Password changed");
                Assert.False(new Uri(recoveryPage.Url).Query.Contains("reset_token", StringComparison.Ordinal));

                phase = "verifying existing sessions";
                await alicePage.GetByRole(AriaRole.Button, new() { Name = "Refresh session" }).ClickAsync();
                await Assertions.Expect(alicePage.GetByRole(AriaRole.Heading, new() { Name = "Sign in to HIC" })).ToBeVisibleAsync();
                await bobPage.GetByRole(AriaRole.Button, new() { Name = "Refresh session" }).ClickAsync();
                await Assertions.Expect(bobPage.GetByText("bob@example.test")).ToBeVisibleAsync();

                phase = "returning to the sign-in form";
                await recoveryPage.GetByRole(AriaRole.Button, new() { Name = "Back to sign in" }).ClickAsync();
                phase = "submitting the old password";
                await SubmitLogin(recoveryPage, "alice@example.test", oldPassword);
                phase = "verifying old password rejection";
                await Assertions.Expect(recoveryPage.GetByRole(AriaRole.Alert)).ToContainTextAsync("incorrect");
                phase = "submitting the new password";
                await SubmitLogin(recoveryPage, "alice@example.test", newPassword);
                phase = "verifying the new password";
                await Assertions.Expect(recoveryPage.GetByText("alice@example.test")).ToBeVisibleAsync();
                phase = "opening an independent second-account sign-in";
                await using var bobNewContext = await browser.NewContextAsync(new() { IgnoreHTTPSErrors = true });
                bobNewContext.SetDefaultTimeout(45000);
                var bobNewPage = await bobNewContext.NewPageAsync();
                await bobNewPage.GotoAsync(origin);
                phase = "submitting the second-account password";
                await SubmitLogin(bobNewPage, "bob@example.test", bobPassword);
                phase = "verifying the second-account password";
                await Assertions.Expect(bobNewPage.GetByText("bob@example.test")).ToBeVisibleAsync();
            }
            catch (Exception exception)
            {
                // Browser exceptions can contain the bearer URL. Never include exception details in test output.
                throw new XunitException($"Browser acceptance failed during {phase} ({exception.GetType().Name}); details withheld.");
            }
            finally
            {
                if (vite is not null && !vite.HasExited)
                {
                    vite.Kill(entireProcessTree: true);
                    await vite.WaitForExitAsync();
                }
                vite?.Dispose();
                if (services is not null)
                    services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
                try
                {
                    if (appTask is not null && services is not null)
                    {
                        try { await appTask.WaitAsync(TimeSpan.FromSeconds(30)); }
                        catch (Exception) { throw new XunitException("Browser fixture host shutdown failed; details withheld."); }
                    }
                }
                finally
                {
                    foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                    File.Delete(certPath);
                    File.Delete(keyPath);
                    await db.ExecuteAsync("drop table if exists execution_plans restrict;");
                }
            }
        });
    }

    private static async Task SubmitLogin(IPage page, string identifier, string password)
    {
        await page.GetByLabel("Email or username").FillAsync(identifier);
        await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForHttp(HttpClient client, string url)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(url);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(250);
        }
        throw new TimeoutException("Test server did not become ready.");
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "HumanInControl.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
