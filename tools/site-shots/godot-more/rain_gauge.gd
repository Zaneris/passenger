class_name RainGauge
extends Node2D

## Rain the roof caught, in millimetres, each day it was read.
@export var readings: Array[float] = []

## Litres a millimetre of rain on the roof puts in the tank.
@export var litres_per_millimetre := 4.5


func wet_days_a_week() -> int:
	var wet := 0
	for millimetres in readings:
		if millimetres > 1.0:
			wet += 1
	var days := readings.size()
	return wet * 7 / days


func add_from(other: RainGauge) -> void:
	for millimetres in other.readings:
		record(millimetres)


func record(millimetres: float) -> void:
	readings.append(millimetres)


func litres_caught() -> float:
	var total := 0.0
	for millimetres in readings:
		total += millimetres * litres_per_millimetre
	return total
