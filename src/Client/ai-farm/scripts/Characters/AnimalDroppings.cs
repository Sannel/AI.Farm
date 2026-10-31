using System.Collections.Generic;
using Godot;

namespace AIFarm.Characters;

/// <summary>Separate ground props that remain in the scene after the animal moves away.</summary>
public static class AnimalDroppings
{
	private static readonly Dictionary<string, string> Props = new()
	{
		["Cow"] = "Cow_Pat", ["Pig"] = "Pig_Pile", ["Chicken"] = "Chicken_Dropping",
		["Goat"] = "Pellets", ["Sheep"] = "Pellets", ["Horse"] = "Horse_Pile",
		["Donkey"] = "Horse_Pile", ["Dog"] = "Dog_Pile",
		["Monkey"] = "Dog_Pile",
		["Gorilla"] = "Dog_Pile", ["Orangutan"] = "Dog_Pile", ["Poodle"] = "Dog_Pile", ["GuideDog"] = "Dog_Pile", ["ServiceDog"] = "Dog_Pile", ["Wolf"] = "Dog_Pile", ["Fox"] = "Dog_Pile", ["Raccoon"] = "Dog_Pile", ["Cat"] = "Dog_Pile", ["BlackCat"] = "Dog_Pile", ["Lion"] = "Dog_Pile", ["Tiger"] = "Dog_Pile", ["Leopard"] = "Dog_Pile", ["Unicorn"] = "Horse_Pile", ["Zebra"] = "Horse_Pile", ["Deer"] = "Pellets", ["Moose"] = "Pellets", ["Bison"] = "Cow_Pat", ["WaterBuffalo"] = "Cow_Pat", ["Boar"] = "Pig_Pile", ["Camel"] = "Pellets", ["BactrianCamel"] = "Pellets", ["Llama"] = "Pellets", ["Giraffe"] = "Pellets", ["Elephant"] = "Horse_Pile", ["Mammoth"] = "Horse_Pile", ["Rhino"] = "Horse_Pile", ["Hippo"] = "Horse_Pile", ["Mouse"] = "Pellets", ["Rat"] = "Pellets", ["Hamster"] = "Pellets", ["Rabbit"] = "Pellets", ["Chipmunk"] = "Pellets", ["Beaver"] = "Pellets", ["Hedgehog"] = "Pellets", ["Bat"] = "Pellets", ["Bear"] = "Dog_Pile", ["PolarBear"] = "Dog_Pile", ["Koala"] = "Pellets", ["Panda"] = "Dog_Pile", ["Sloth"] = "Pellets", ["Otter"] = "Dog_Pile", ["Skunk"] = "Dog_Pile", ["Kangaroo"] = "Pellets", ["Badger"] = "Dog_Pile", ["Turkey"] = "Chicken_Dropping",
	};

	public static void Deposit(Node3D model, Node3D animal)
	{
		var species = model.SceneFilePath.GetFile().GetBaseName().Split('_')[0];
		if (!Props.TryGetValue(species, out var prop)) return;
		var world = animal.GetTree().CurrentScene;
		if (world is null) return;
		// Bounds are in model space; +Z is forward in Godot.
		var bounds = AnimalPen.ModelBounds(model);
		var point = model.ToGlobal(new Vector3(bounds.GetCenter().X, bounds.Position.Y,
			bounds.Position.Z - 0.08f));
		var query = PhysicsRayQueryParameters3D.Create(point + Vector3.Up * 0.5f, point + Vector3.Down * 4);
		if (animal is CollisionObject3D body) query.Exclude = [body.GetRid()];
		var hit = animal.GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (hit.Count > 0) point = hit["position"].AsVector3();
		var pile = GD.Load<PackedScene>($"res://assets/Droppings/{prop}.glb").Instantiate<Node3D>();
		pile.Name = species + "Droppings";
		world.AddChild(pile);
		pile.GlobalPosition = point + Vector3.Up * 0.002f;
		var scale = model.GlobalBasis.Scale.Abs();
		if (species == "Mouse") scale *= 0.12f;
		if (species == "Rat") scale *= 0.2f;
		if (species == "Hamster") scale *= 0.14f;
		if (species == "Rabbit") scale *= 0.3f;
		if (species == "Chipmunk") scale *= 0.14f;
		if (species == "Beaver") scale *= 0.4f;
		if (species == "Hedgehog") scale *= 0.2f;
		if (species == "Bat") scale *= 0.12f;
		if (species == "Koala") scale *= 0.35f;
		if (species == "Sloth") scale *= 0.4f;
		if (species == "Otter") scale *= 0.5f;
		if (species == "Skunk") scale *= 0.45f;
		if (species == "Badger") scale *= 0.5f;
		pile.GlobalBasis = Basis.Identity.Scaled(scale);
		if (hit.Count > 0)
		{
			var normal = hit["normal"].AsVector3().Normalized();
			var tangent = normal.Cross(Vector3.Right);
			if (tangent.LengthSquared() < 0.001f) tangent = normal.Cross(Vector3.Forward);
			tangent = tangent.Normalized();
			pile.GlobalBasis = new Basis(normal.Cross(tangent), normal, tangent).Scaled(scale);
		}
		pile.AddToGroup("animal_droppings");
	}
}
