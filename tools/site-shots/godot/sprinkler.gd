extends Node2D

## Litres the sprinkler gives each plant a morning.
@export var litres_per_plant := 1.5

var mornings := 0
var last_given := 0.0


func spray() -> float:
	mornings += 1
	last_given = 0.0
	for plant in get_node("../Benches").get_children():
		last_given += plant.water(litres_per_plant)
	return last_given
