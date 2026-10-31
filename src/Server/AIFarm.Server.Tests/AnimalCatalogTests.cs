using AIFarm.Server.Game;

namespace AIFarm.Server.Tests;

public class AnimalCatalogTests
{
	[Fact]
	public void SheepHaveBothSexesAndKeepTheLegacyModelAlias()
	{
		var ewe = AnimalCatalog.Find("Sheep_Female");
		var ram = AnimalCatalog.Find("Sheep_Male");
		Assert.NotNull(ewe);
		Assert.NotNull(ram);
		Assert.Equal("Female", ewe.Sex);
		Assert.Equal("Male", ram.Sex);
		Assert.Equal("Ram", ram.Name);
		Assert.Same(ewe, AnimalCatalog.Find("Sheep"));
		Assert.Contains(GameLoop.StarterAnimals, a => a.Model == ewe.Model);
		Assert.Contains(GameLoop.StarterAnimals, a => a.Model == ram.Model);
	}
}
