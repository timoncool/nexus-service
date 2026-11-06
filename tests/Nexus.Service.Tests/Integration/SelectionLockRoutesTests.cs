using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

public sealed class SelectionLockRoutesTests : IClassFixture<StubDeviceHostFactory>
{
    private readonly StubDeviceHostFactory _factory;
    private readonly HttpClient _client;

    public SelectionLockRoutesTests(StubDeviceHostFactory factory)
    {
        _factory = factory;
        _factory.ResetSettings();
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue(
                "Bearer",
                _factory.Services.GetRequiredService<TokenService>().Token);
    }

    private IConfigStore Store => _factory.Services.GetRequiredService<IConfigStore>();

    private Task<HttpResponseMessage> Lock(string id, bool locked) =>
        _client.PostAsync("/devices/lighting-devices/selection-lock",
            new StringContent($$"""{"id":"{{id}}","locked":{{(locked ? "true" : "false")}}}""", Encoding.UTF8, "application/json"));

    [Fact]
    public async Task A_lock_is_stored_once_listed_with_the_devices_and_cleared_by_an_unlock()
    {
        Assert.Equal(HttpStatusCode.OK, (await Lock("lock-a", true)).StatusCode);
        await Lock("lock-a", true);
        Assert.Equal(new[] { "lock-a" }, Store.Load().Lighting.SelectionLockedDevices);

        using (var doc = JsonDocument.Parse(await (await _client.GetAsync("/devices/lighting-devices/all")).Content.ReadAsStringAsync()))
        {
            var listed = doc.RootElement.GetProperty("selectionLocked").EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(new[] { "lock-a" }, listed);
        }

        await Lock("lock-a", false);
        Assert.Empty(Store.Load().Lighting.SelectionLockedDevices);
    }

    [Fact]
    public async Task A_lock_without_an_id_is_refused()
    {
        var res = await _client.PostAsync("/devices/lighting-devices/selection-lock",
            new StringContent("""{"locked":true}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(Store.Load().Lighting.SelectionLockedDevices);
    }
}
