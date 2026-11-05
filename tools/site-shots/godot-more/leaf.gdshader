shader_type canvas_item;

// The leaves' green, and how far they sway in the draught from the door.
uniform vec4 leaf_colour : source_color = vec4(0.25, 0.6, 0.2, 1.0);
uniform float sway : hint_range(0.0, 8.0) = 2.0;

void vertex() {
	VERTEX.x += sin(TIME * 1.5 + VERTEX.y * 0.05) * sway;
}

void fragment() {
	vec4 leaf = texture(TEXTURE, UV);
	float shade = 0.8 + 0.2 * UV.y;
	vec3 green = leaf_colour * shade;
	COLOR = vec4(green, leaf.a);
}
