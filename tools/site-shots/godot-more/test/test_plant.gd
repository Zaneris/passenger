extends GutTest

const Plant := preload("res://plant.gd")


func test_a_dry_pot_takes_what_it_holds():
	var plant: Plant = autofree(Plant.new())
	plant.moisture = 0.0
	assert_eq(plant.water(1.5), 1.5, "a dry pot takes all the sprinkler gives")
	assert_almost_eq(plant.moisture, 0.375, 0.001)


func test_a_full_pot_takes_no_water():
	var plant: Plant = autofree(Plant.new())
	plant.moisture = 1.0
	assert_eq(plant.water(2.0), 0.0, "a full pot takes nothing")


func test_a_wet_pot_grows_six_leaves():
	var plant: Plant = autofree(Plant.new())
	plant.moisture = 1.0
	plant.grow()
	assert_eq(plant.leaves, 6, "a wet pot's leaves")


func test_moisture_falls_as_the_day_goes():
	var plant: Plant = autofree(Plant.new())
	plant.moisture = 0.6
	plant._process(1.0)
	assert_lt(plant.moisture, 0.6, "the pot dries")
