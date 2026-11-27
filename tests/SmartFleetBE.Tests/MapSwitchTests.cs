using Microsoft.AspNetCore.Authorization;
using SmartFleetBE.Constants;
using SmartFleetBE.Controllers;
using SmartFleetBE.Services;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

namespace SmartFleetBE.Tests;

public sealed class MapSwitchTests
{
    [Fact]
    public void SharedSceneBindsToSelectedWarehouseWithoutChangingPhysicalGeometry()
    {
        var path=Path.GetTempFileName();
        try {
            var original=new SimulationScene {WarehouseId=1,SceneId="scene",WorldSha256="fixed",Anchors=[new(2,3,4,90,.04)]};
            File.WriteAllText(path,JsonSerializer.Serialize(original,new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
                ["Simulation:Enabled"]="true",["Simulation:ManifestPath"]=path,["SharedDatabase:Enabled"]="true",["Navigation:WarehouseId"]="2"}).Build();
            var loaded=SimulationScene.Load(config,null!);
            Assert.Equal(2,loaded.WarehouseId);Assert.Equal(original.Anchors,loaded.Anchors);Assert.Equal(original.WorldSha256,loaded.WorldSha256);
        } finally {File.Delete(path);}
    }
    [Theory]
    [InlineData("map3", "map2", true, "test")]
    [InlineData("map1; shutdown", "map2", true, "test")]
    [InlineData("map1", "unknown", true, "test")]
    [InlineData("map1", "map2", false, "test")]
    [InlineData("map1", "map2", true, " ")]
    public void RejectsUnconfirmedOrInvalidSelection(string target, string expected, bool confirmed, string reason)
        => Assert.Throws<ArgumentException>(() => MapSwitchService.Validate(new(Guid.NewGuid(), target, expected, reason, confirmed)));

    [Fact]
    public void SelectionRequiresAdminInAdditionToAuthenticatedReadAccess()
    {
        var method = typeof(MapProfilesController).GetMethod(nameof(MapProfilesController.Select))!;
        Assert.Contains(method.GetCustomAttributes(typeof(AuthorizeAttribute), true).Cast<AuthorizeAttribute>(), a => a.Roles == AppRoles.Admin);
    }
}
