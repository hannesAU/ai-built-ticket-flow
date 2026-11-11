using System.Text.Json;
using Shouldly;
using TicketFlow.Core;

namespace TicketFlow.Tests;

public class ConfigTests
{
    [Fact]
    public void Example_config_is_embedded_and_matches_code_defaults()
    {
        var example = ConfigStore.Example();
        example.ShouldNotBeNull();
        example.Repos.ShouldNotBeEmpty();

        var seed = ConfigStore.Seed();
        seed.Repos.ShouldBeEmpty();
        seed.JiraBaseUrl.ShouldBeNull();

        // apart from the example-only values, the seed is exactly what the code would default to
        seed.Excluded = [];
        JsonSerializer.Serialize(seed, Json.Options).ShouldBe(JsonSerializer.Serialize(new AppConfig(), Json.Options));
    }
}
