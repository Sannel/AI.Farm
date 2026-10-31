extends SceneTree
## Headless integration check: every animal deposits once and leaves an independent ground prop.
const MODELS := ["Cow_Female", "Cow_Male", "Pig_Female", "Pig_Male", "Chicken_Female", "Chicken_Male", "Goat_Female", "Goat_Male", "Horse_Female", "Horse_Male", "Donkey_Female", "Donkey_Male", "Sheep", "Dog", "Sheep_Female", "Sheep_Male", "Dog_Female", "Dog_Male", "Monkey_Female", "Monkey_Male", "Gorilla_Female", "Gorilla_Male", "Orangutan_Female", "Orangutan_Male", "Poodle_Female", "Poodle_Male", "GuideDog_Female", "GuideDog_Male", "ServiceDog_Female", "ServiceDog_Male", "Wolf_Female", "Wolf_Male", "Fox_Female", "Fox_Male", "Raccoon_Female", "Raccoon_Male", "Cat_Female", "Cat_Male", "BlackCat_Female", "BlackCat_Male", "Lion_Female", "Lion_Male", "Tiger_Female", "Tiger_Male", "Leopard_Female", "Leopard_Male", "Unicorn_Female", "Unicorn_Male", "Zebra_Female", "Zebra_Male", "Deer_Female", "Deer_Male", "Moose_Female", "Moose_Male", "Bison_Female", "Bison_Male", "WaterBuffalo_Female", "WaterBuffalo_Male", "Boar_Female", "Boar_Male", "Camel_Female", "Camel_Male", "BactrianCamel_Female", "BactrianCamel_Male", "Llama_Female", "Llama_Male", "Giraffe_Female", "Giraffe_Male", "Elephant_Female", "Elephant_Male", "Mammoth_Female", "Mammoth_Male", "Rhino_Female", "Rhino_Male", "Hippo_Female", "Hippo_Male", "Mouse_Female", "Mouse_Male", "Rat_Female", "Rat_Male", "Hamster_Female", "Hamster_Male", "Rabbit_Female", "Rabbit_Male", "Chipmunk_Female", "Chipmunk_Male", "Beaver_Female", "Beaver_Male", "Hedgehog_Female", "Hedgehog_Male", "Bat_Female", "Bat_Male", "Bear_Female", "Bear_Male", "PolarBear_Female", "PolarBear_Male", "Koala_Female", "Koala_Male", "Panda_Female", "Panda_Male", "Sloth_Female", "Sloth_Male", "Otter_Female", "Otter_Male", "Skunk_Female", "Skunk_Male", "Kangaroo_Female", "Kangaroo_Male", "Badger_Female", "Badger_Male", "Turkey_Female", "Turkey_Male"]

func _initialize() -> void:
	_run.call_deferred()

func _run() -> void:
	var world := Node3D.new()
	root.add_child(world)
	current_scene = world
	var ground := StaticBody3D.new()
	var collision := CollisionShape3D.new()
	var box := BoxShape3D.new()
	box.size = Vector3(200, 0.2, 200)
	collision.shape = box
	collision.position.y = -0.1
	ground.add_child(collision)
	world.add_child(ground)
	var animator_script = load("res://scripts/Characters/CharacterAnimator.cs")
	for animal_name in MODELS:
		var animal := StaticBody3D.new()
		world.add_child(animal)
		var model = load("res://assets/%s/%s.glb" % [animal_name.split("_")[0], animal_name]).instantiate()
		model.name = "Model"
		animal.add_child(model)
		var animator = animator_script.new()
		animator.set("AnimationFolder", "")
		animator.set("DefaultAnimation", "Idle")
		animal.add_child(animator)
		var player := model.get_node("AnimationPlayer") as AnimationPlayer
		if not player.has_animation("Pooping"):
			_fail(animal_name + ": missing Pooping")
			return
		await physics_frame
		var before := get_nodes_in_group("animal_droppings").size()
		animator.call("Play", "Pooping")
		player.advance(player.current_animation_length * 0.6)
		await physics_frame
		await physics_frame
		var piles := get_nodes_in_group("animal_droppings")
		if piles.size() != before + 1:
			_fail(animal_name + ": expected exactly one deposit")
			return
		var pile := piles.back() as Node3D
		var position := pile.global_position
		if absf(position.y - 0.002) > 0.005:
			_fail(animal_name + ": deposit not on ground")
			return
		animal.position.x += 5.0
		await physics_frame
		if not pile.global_position.is_equal_approx(position) or get_nodes_in_group("animal_droppings").size() != before + 1:
			_fail(animal_name + ": deposit moved or duplicated")
			return
		animator.call("Play", "Pooping")
		player.advance(player.current_animation_length * 0.6)
		await physics_frame
		await physics_frame
		if get_nodes_in_group("animal_droppings").size() != before + 2:
			_fail(animal_name + ": repeat did not deposit")
			return
		animator.call("Play", "Pooping")
		animator.call("Play", "Idle")
		await physics_frame
		await physics_frame
		if get_nodes_in_group("animal_droppings").size() != before + 2:
			_fail(animal_name + ": cancelled animation deposited")
			return
		animal.queue_free()
		await process_frame
		print("PASS_DROPPINGS ", animal_name)
	print("PASS: all 112 animals deposit once, repeat, cancel, and leave props on the ground")
	quit(0)

func _fail(message: String) -> void:
	push_error(message)
	quit(1)
