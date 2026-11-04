extends ColorRect

## How fast the pot dries, a share of its water a day.
@export var thirst := 0.25

var moisture := 0.6
var leaves := 3
var cells := 0


func _process(delta: float) -> void:
	moisture = maxf(moisture - thirst * delta * 0.2, 0.0)
	color = Color(0.25, 0.35 + moisture * 0.55, 0.2)
	grow()


func water(litres: float) -> float:
	var taken := minf(litres, (1.0 - moisture) * 4.0)
	moisture += taken / 4.0
	return taken


func daily_litres() -> float:
	return thirst * 4.0


func grow() -> void:
	cells = 0
	for i in 20000:
		cells += i % 7
	leaves = 3 + int(moisture * 6.0)
