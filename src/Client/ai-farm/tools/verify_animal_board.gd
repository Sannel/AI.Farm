extends SceneTree
## Run with the Godot .NET executable:
## --headless --path <project> --script res://tools/verify_animal_board.gd
## For a rendered overview, omit --headless and add -- --screenshot <png-path>.
## Add --close-up Horse_Female (or another animal node name) for a face review.
## Joint reviews: --view side|rear-quarter|under-jaw --animation Idle_LookAround --time 1.0

const SPECIES := ["Cow", "Pig", "Chicken", "Goat", "Horse", "Donkey", "Sheep", "Dog", "Monkey", "Gorilla", "Orangutan", "Poodle", "GuideDog", "ServiceDog", "Wolf", "Fox", "Raccoon", "Cat", "BlackCat", "Lion", "Tiger", "Leopard", "Unicorn", "Zebra", "Deer", "Moose", "Bison", "WaterBuffalo", "Boar", "Camel", "BactrianCamel", "Llama", "Giraffe", "Elephant", "Mammoth", "Rhino", "Hippo", "Mouse", "Rat", "Hamster", "Rabbit", "Chipmunk", "Beaver", "Hedgehog", "Bat", "Bear", "PolarBear", "Koala", "Panda", "Sloth", "Otter", "Skunk", "Kangaroo", "Badger", "Turkey"]
const SPECIALS := {
	"Cow_Male": ["Paw_Ground"],
	"Chicken_Female": ["Cluck", "Flap_Wings"],
	"Chicken_Male": ["Crow", "Flap_Wings"],
	"Goat_Male": ["Headbutt"],
	"Horse_Male": ["Rear_Up"],
	"Donkey_Female": ["Bray"],
	"Donkey_Male": ["Bray"],
	"Sheep_Female": ["Bleat", "Pooping"],
	"Sheep_Male": ["Bleat", "Pooping", "Headbutt"],
	"Dog_Female": ["Bark", "Pooping"],
	"Dog_Male": ["Bark", "Pooping"],
	"Monkey_Female": ["Chatter", "Pooping"],
	"Monkey_Male": ["Chatter", "Pooping"],
	"Gorilla_Female": ["Chatter", "Pooping"],
	"Gorilla_Male": ["Chatter", "Pooping"],
	"Orangutan_Female": ["Chatter", "Pooping"],
	"Orangutan_Male": ["Chatter", "Pooping"],
	"Poodle_Female": ["Bark", "Pooping"],
	"Poodle_Male": ["Bark", "Pooping"],
	"GuideDog_Female": ["Bark", "Pooping"],
	"GuideDog_Male": ["Bark", "Pooping"],
	"ServiceDog_Female": ["Bark", "Pooping"],
	"ServiceDog_Male": ["Bark", "Pooping"],
	"Wolf_Female": ["Bark", "Pooping"],
	"Wolf_Male": ["Bark", "Pooping"],
	"Fox_Female": ["Bark", "Pooping"],
	"Fox_Male": ["Bark", "Pooping"],
	"Raccoon_Female": ["Bark", "Pooping"],
	"Raccoon_Male": ["Bark", "Pooping"],
	"Cat_Female": ["Meow", "Pooping"],
	"Cat_Male": ["Meow", "Pooping"],
	"BlackCat_Female": ["Meow", "Pooping"],
	"BlackCat_Male": ["Meow", "Pooping"],
	"Lion_Female": ["Roar", "Pooping"],
	"Lion_Male": ["Roar", "Pooping"],
	"Tiger_Female": ["Roar", "Pooping"],
	"Tiger_Male": ["Roar", "Pooping"],
	"Leopard_Female": ["Roar", "Pooping"],
	"Leopard_Male": ["Roar", "Pooping"],
	"Unicorn_Female": ["Neigh", "Trotting", "Pooping"],
	"Unicorn_Male": ["Neigh", "Trotting", "Rear_Up", "Pooping"],
	"Zebra_Female": ["Neigh", "Trotting", "Pooping"],
	"Zebra_Male": ["Neigh", "Trotting", "Rear_Up", "Pooping"],
	"Deer_Female": ["Bleat", "Trotting", "Pooping"],
	"Deer_Male": ["Bleat", "Trotting", "Rear_Up", "Pooping"],
	"Moose_Female": ["Bleat", "Trotting", "Pooping"],
	"Moose_Male": ["Bleat", "Trotting", "Rear_Up", "Pooping"],
	"Bison_Female": ["Moo", "Pooping"],
	"Bison_Male": ["Moo", "Paw_Ground", "Pooping"],
	"WaterBuffalo_Female": ["Moo", "Pooping"],
	"WaterBuffalo_Male": ["Moo", "Paw_Ground", "Pooping"],
	"Boar_Female": ["Oink", "Pooping"],
	"Boar_Male": ["Oink", "Pooping"],
	"Camel_Female": ["Bleat", "Trotting", "Pooping"],
	"Camel_Male": ["Bleat", "Trotting", "Rear_Up", "Pooping"],
	"BactrianCamel_Female": ["Bleat", "Trotting", "Pooping"],
	"BactrianCamel_Male": ["Bleat", "Trotting", "Rear_Up", "Pooping"],
	"Llama_Female": ["Bleat", "Trotting", "Pooping"],
	"Llama_Male": ["Bleat", "Trotting", "Rear_Up", "Pooping"],
	"Giraffe_Female": ["Hum", "Pooping"],
	"Giraffe_Male": ["Hum", "Pooping"],
	"Elephant_Female": ["Trumpet", "Pooping"],
	"Elephant_Male": ["Trumpet", "Pooping"],
	"Mammoth_Female": ["Trumpet", "Pooping"],
	"Mammoth_Male": ["Trumpet", "Pooping"],
	"Rhino_Female": ["Snort", "Pooping"],
	"Rhino_Male": ["Snort", "Pooping"],
	"Hippo_Female": ["Snort", "Pooping"],
	"Hippo_Male": ["Snort", "Pooping"],
	"Mouse_Female": ["Squeak", "Pooping"],
	"Mouse_Male": ["Squeak", "Pooping"],
	"Rat_Female": ["Squeak", "Pooping"],
	"Rat_Male": ["Squeak", "Pooping"],
	"Hamster_Female": ["Squeak", "Pooping"],
	"Hamster_Male": ["Squeak", "Pooping"],
	"Rabbit_Female": ["Sniff", "Hop", "Pooping"],
	"Rabbit_Male": ["Sniff", "Hop", "Pooping"],
	"Chipmunk_Female": ["Squeak", "Pooping"],
	"Chipmunk_Male": ["Squeak", "Pooping"],
	"Beaver_Female": ["Squeak", "Swimming", "Pooping"],
	"Beaver_Male": ["Squeak", "Swimming", "Pooping"],
	"Hedgehog_Female": ["Squeak", "Pooping"],
	"Hedgehog_Male": ["Squeak", "Pooping"],
	"Bat_Female": ["Squeak", "Flying", "Pooping"],
	"Bat_Male": ["Squeak", "Flying", "Pooping"],
	"Bear_Female": ["Growl", "Pooping"],
	"Bear_Male": ["Growl", "Pooping"],
	"PolarBear_Female": ["Growl", "Swimming", "Pooping"],
	"PolarBear_Male": ["Growl", "Swimming", "Pooping"],
	"Koala_Female": ["Growl", "Pooping"],
	"Koala_Male": ["Growl", "Pooping"],
	"Panda_Female": ["Growl", "Pooping"],
	"Panda_Male": ["Growl", "Pooping"],
	"Sloth_Female": ["Chitter", "Hang_Idle", "Pooping"],
	"Sloth_Male": ["Chitter", "Hang_Idle", "Pooping"],
	"Otter_Female": ["Squeak", "Swimming", "Pooping"],
	"Otter_Male": ["Squeak", "Swimming", "Pooping"],
	"Skunk_Female": ["Squeak", "Pooping"],
	"Skunk_Male": ["Squeak", "Pooping"],
	"Kangaroo_Female": ["Chitter", "Pooping"],
	"Kangaroo_Male": ["Chitter", "Pooping"],
	"Badger_Female": ["Squeak", "Pooping"],
	"Badger_Male": ["Squeak", "Pooping"],
	"Turkey_Female": ["Gobble", "Flap_Wings", "Pooping"],
	"Turkey_Male": ["Gobble", "Flap_Wings", "Pooping"],
}


func _initialize() -> void:
	_run.call_deferred()


func _run() -> void:
	var packed := load("res://scenes/game.tscn") as PackedScene
	if packed == null:
		_fail("Could not load the game scene")
		return
	var game := packed.instantiate()
	root.add_child(game)
	current_scene = game
	await process_frame
	await process_frame
	var pen := game.get_node("AnimalPen")
	if pen.get_child_count() != 110:
		_fail("Expected 110 animals, got %d" % pen.get_child_count())
		return
	var bounds := AABB()
	var first := true
	var clips := 0
	for species in SPECIES:
		for sex in ["Female", "Male"]:
			var animal_name: String = species + "_" + sex
			var animal := pen.get_node_or_null(animal_name) as StaticBody3D
			if animal == null:
				_fail("Missing animal: " + animal_name)
				return
			var collision := animal.get_node_or_null("Collision") as CollisionShape3D
			var player := animal.find_child("AnimationPlayer", true, false) as AnimationPlayer
			if collision == null or not collision.shape is BoxShape3D or player == null:
				_fail("Missing collision or animation player: " + animal_name)
				return
			if not player.is_playing() or player.current_animation != "Idle":
				_fail("Idle is not playing: " + animal_name)
				return
			if not player.has_animation("Pooping"):
				_fail("Missing Pooping: " + animal_name)
				return
			for clip in SPECIALS.get(animal_name, []):
				if not player.has_animation(clip):
					_fail("Missing %s on %s" % [clip, animal_name])
					return
			clips += player.get_animation_list().size() - int(player.has_animation("RESET"))
			var shape := collision.shape as BoxShape3D
			var box := AABB(collision.global_position - shape.size / 2.0, shape.size)
			bounds = box if first else bounds.merge(box)
			first = false
			print("BOARD_ANIMAL ", animal_name, " position=", animal.global_position,
				" animations=", player.get_animation_list().size())
	if clips != 1583:
		_fail("Expected 1583 animal clips, got %d" % clips)
		return
	print("PASS: all 110 animals spawned on the game board; 1583 clips; Idle and collisions ready")
	var args := OS.get_cmdline_user_args()
	var index := args.find("--screenshot")
	if index >= 0 and index + 1 < args.size():
		var camera := Camera3D.new()
		game.add_child(camera)
		var center := bounds.get_center()
		camera.global_position = center + Vector3(8.0, 11.0, 15.0)
		camera.look_at(center, Vector3.UP)
		camera.projection = Camera3D.PROJECTION_ORTHOGONAL
		camera.size = maxf(bounds.size.x * 0.65, bounds.size.z * 0.85) + 3.0
		var close_up := args.find("--close-up")
		if close_up >= 0 and close_up + 1 < args.size():
			var subject := pen.get_node_or_null(args[close_up + 1]) as Node3D
			if subject == null:
				_fail("Unknown close-up animal: " + args[close_up + 1])
				return
			camera.projection = Camera3D.PROJECTION_PERSPECTIVE
			camera.fov = 45.0
			var view_index := args.find("--view")
			var view := args[view_index + 1] if view_index >= 0 and view_index + 1 < args.size() else "front-quarter"
			var offset := Vector3(0.7, 1.55, 2.15)
			var focus := Vector3(0.0, 1.35, 0.7)
			if view == "side":
				offset = Vector3(2.0, 1.50, 0.4)
			elif view == "rear-quarter":
				offset = Vector3(1.5, 1.65, -0.6)
			elif view == "under-jaw" or view == "opposite-under-jaw":
				offset = Vector3(1.2 if view == "under-jaw" else -1.2, 1.20, 1.30)
				focus = Vector3(0.0, 1.40, 0.82)
			camera.global_position = subject.global_position + offset
			camera.look_at(subject.global_position + focus, Vector3.UP)
			var animation_index := args.find("--animation")
			if animation_index >= 0 and animation_index + 1 < args.size():
				var player := subject.find_child("AnimationPlayer", true, false) as AnimationPlayer
				var clip := args[animation_index + 1]
				if not player.has_animation(clip):
					_fail("Unknown close-up animation: " + clip)
					return
				var time_index := args.find("--time")
				var time := args[time_index + 1].to_float() if time_index >= 0 and time_index + 1 < args.size() else 0.0
				player.play(clip, 0.0)
				player.seek(time, true)
				player.pause()
		camera.make_current()
		Input.mouse_mode = Input.MOUSE_MODE_VISIBLE
		await process_frame
		await RenderingServer.frame_post_draw
		var error := root.get_texture().get_image().save_png(args[index + 1])
		if error != OK:
			_fail("Could not save board screenshot: %s" % error)
			return
	quit(0)


func _fail(message: String) -> void:
	push_error(message)
	quit(1)
