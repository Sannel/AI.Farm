shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_opaque, specular_schlick_ggx;

// A waterfall: streaks of white water racing down a sheet of blue. UV.x runs down the fall in
// metres, UV.y across it; R of the vertex colour is closeness to a side, G is closeness to the
// pool at the bottom (where it turns to foam).
uniform vec4 water_colour : source_color = vec4(0.40, 0.68, 0.84, 1.0);
uniform vec4 foam_colour : source_color = vec4(0.96, 0.99, 1.0, 1.0);
uniform float fall_speed = 5.0;
uniform float streaks = 16.0;

varying float edge;
varying float low;

float hash(float n) {
	return fract(sin(n * 91.7) * 437.58);
}

void vertex() {
	edge = COLOR.r;
	low = COLOR.g;
}

void fragment() {
	float column = floor(UV.y * streaks);
	float h = hash(column);
	float across = abs(fract(UV.y * streaks) - 0.5) * 2.0;
	float run = UV.x * (0.25 + h * 0.3) - TIME * (fall_speed * 0.2) * (0.6 + h);
	float bright = smoothstep(0.35, 0.95, fract(run)) * (1.0 - across * across);
	float shimmer = 0.5 + 0.5 * sin(UV.x * 3.0 - TIME * fall_speed + h * 6.28);

	float foam = clamp(low * 1.2 + edge * 0.5 + bright * 0.55 + shimmer * 0.08, 0.0, 1.0);
	ALBEDO = mix(water_colour.rgb, foam_colour.rgb, foam);
	ALPHA = clamp(0.62 + foam * 0.35 - edge * 0.25, 0.0, 1.0);
	ROUGHNESS = 0.15;
	SPECULAR = 0.5;
	EMISSION = foam_colour.rgb * foam * 0.12;
}
