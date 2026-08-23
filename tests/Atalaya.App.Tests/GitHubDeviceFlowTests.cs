using Atalaya.App.Services;
using FluentAssertions;
using Xunit;

namespace Atalaya.App.Tests;

/// <summary>
/// The OAuth device flow state machine (F2.2), driven entirely offline: the HTTP conversation is
/// scripted and both the clock and the delay are injected, so <c>slow_down</c> and expiry are
/// exercised without waiting a single real second.
/// </summary>
public sealed class GitHubDeviceFlowTests
{
    private const string ClientId = "Iv1.testclientid";

    /// <summary>Records what the flow waited for, and advances the fake clock by that much.</summary>
    private sealed class FakeClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

        public List<TimeSpan> Waits { get; } = new();

        public Task Delay(TimeSpan d, CancellationToken ct)
        {
            Waits.Add(d);
            Now += d;
            return Task.CompletedTask;
        }
    }

    private static GitHubDeviceFlow Flow(HttpStub stub, FakeClock clock) =>
        new(stub.Client(), clock.Delay, () => clock.Now);

    [Fact]
    public async Task Request_code_returns_the_grant_to_display()
    {
        var stub = new HttpStub().Json("""
            {"device_code":"dc","user_code":"WDJB-MJHT",
             "verification_uri":"https://github.com/login/device","expires_in":900,"interval":5}
            """);
        var clock = new FakeClock();

        DeviceCodeGrant grant = await Flow(stub, clock).RequestCodeAsync(ClientId, CancellationToken.None);

        grant.UserCode.Should().Be("WDJB-MJHT");
        grant.DeviceCode.Should().Be("dc");
        grant.Interval.Should().Be(TimeSpan.FromSeconds(5));
        grant.ExpiresIn.Should().Be(TimeSpan.FromMinutes(15));

        // The three scopes of D1 must be requested up front.
        stub.Bodies[0].Should().Contain("client_id=" + ClientId);
        Uri.UnescapeDataString(stub.Bodies[0]).Should().Contain("repo+read:org+read:user");
    }

    [Fact]
    public async Task Polling_survives_authorization_pending_and_slow_down()
    {
        var stub = new HttpStub()
            .Json("""{"error":"authorization_pending"}""")
            .Json("""{"error":"slow_down","interval":10}""")
            .Json("""{"access_token":"gho_abc123","token_type":"bearer","scope":"repo,read:org,read:user"}""");
        var clock = new FakeClock();
        var grant = new DeviceCodeGrant("dc", "WDJB-MJHT", "https://github.com/login/device",
            TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));

        string token = await Flow(stub, clock).WaitForTokenAsync(ClientId, grant, null, CancellationToken.None);

        token.Should().Be("gho_abc123");
        // 5 s, 5 s, then +5 s after slow_down: the client must back off, never keep hammering.
        clock.Waits.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Poll_sends_the_device_code_grant_type()
    {
        var stub = new HttpStub().Json("""{"access_token":"gho_x"}""");
        var clock = new FakeClock();

        await Flow(stub, clock).PollOnceAsync(ClientId, "dc", CancellationToken.None);

        Uri.UnescapeDataString(stub.Bodies[0])
            .Should().Contain("grant_type=urn:ietf:params:oauth:grant-type:device_code")
            .And.Contain("device_code=dc");
    }

    [Fact]
    public async Task Expired_code_stops_the_wait_with_a_retry_instruction()
    {
        // Never authorized: every poll is pending until the 15-minute window closes.
        var stub = new HttpStub();
        for (int i = 0; i < 200; i++)
        {
            stub.Json("""{"error":"authorization_pending"}""");
        }

        var clock = new FakeClock();
        var grant = new DeviceCodeGrant("dc", "CODE", "https://github.com/login/device",
            TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));

        Func<Task> act = () => Flow(stub, clock).WaitForTokenAsync(ClientId, grant, null, CancellationToken.None);

        (await act.Should().ThrowAsync<DeviceFlowException>())
            .Which.Error.Should().Be(DeviceFlowError.ExpiredToken);
    }

    [Fact]
    public async Task Explicit_expired_token_is_reported_as_expiry()
    {
        var stub = new HttpStub().Json("""{"error":"expired_token"}""");
        var clock = new FakeClock();
        var grant = new DeviceCodeGrant("dc", "CODE", "u", TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));

        Func<Task> act = () => Flow(stub, clock).WaitForTokenAsync(ClientId, grant, null, CancellationToken.None);

        DeviceFlowException ex = (await act.Should().ThrowAsync<DeviceFlowException>()).Which;
        ex.Error.Should().Be(DeviceFlowError.ExpiredToken);
        ex.Message.Should().Contain("Conectar con GitHub");
    }

    [Fact]
    public async Task Access_denied_explains_the_org_oauth_policy()
    {
        var stub = new HttpStub().Json("""{"error":"access_denied"}""");
        var clock = new FakeClock();
        var grant = new DeviceCodeGrant("dc", "CODE", "u", TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));

        Func<Task> act = () => Flow(stub, clock).WaitForTokenAsync(ClientId, grant, null, CancellationToken.None);

        DeviceFlowException ex = (await act.Should().ThrowAsync<DeviceFlowException>()).Which;
        ex.Error.Should().Be(DeviceFlowError.AccessDenied);
        ex.Message.Should().Contain("Request").And.Contain("owner");
    }

    [Fact]
    public async Task Device_flow_not_enabled_points_at_the_app_registration()
    {
        var stub = new HttpStub().Json("""{"error":"device_flow_disabled"}""");
        var clock = new FakeClock();

        Func<Task> act = () => Flow(stub, clock).RequestCodeAsync(ClientId, CancellationToken.None);

        DeviceFlowException ex = (await act.Should().ThrowAsync<DeviceFlowException>()).Which;
        ex.Error.Should().Be(DeviceFlowError.DeviceFlowDisabled);
        ex.Message.Should().Contain("device flow");
    }

    [Fact]
    public async Task Waiting_reports_the_expiry_countdown()
    {
        var stub = new HttpStub()
            .Json("""{"error":"authorization_pending"}""")
            .Json("""{"access_token":"gho_x"}""");
        var clock = new FakeClock();
        var grant = new DeviceCodeGrant("dc", "CODE", "u", TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));

        var seen = new List<TimeSpan>();
        var progress = new Progress<DeviceFlowProgress>(p => seen.Add(p.Remaining));

        await Flow(stub, clock).WaitForTokenAsync(ClientId, grant, progress, CancellationToken.None);

        // Progress is delivered on the captured context; drain it.
        await Task.Delay(30);
        seen.Should().NotBeEmpty();
        seen[0].Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Cancelling_stops_the_wait()
    {
        var stub = new HttpStub().Json("""{"error":"authorization_pending"}""");
        var clock = new FakeClock();
        var grant = new DeviceCodeGrant("dc", "CODE", "u", TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => Flow(stub, clock).WaitForTokenAsync(ClientId, grant, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
