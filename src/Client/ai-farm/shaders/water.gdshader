shader_type spatial;
render_mode blend_mix, cull_disabled, depth_draw_opaque, specular_schlick_ggx;

// Water: a see-through blue-green with drifting ripples and pale foam along the banks.
// UV.x runs along the river in metres (the ripples flow towards +U); R of the vertex colour
// is how close to a bank it is.
uniform vec4 shallow_colour : source_color = vec4(0.30, 0.62, 0.66, 1.0);
uniform vec4 deep_colour : source_color = vec4(0.07, 0.30, 0.45, 1.0);
uniform vec4 foam_colour : source_color = vec4(0.92, 0.97, 1.0, 1.0);
uniform float flow_speed = 0.9;
uniform float ripple_scale = 1.6;
uniform float opacity = 0.78;

varying float edge;

float ripple(vec2 p) {
	return 0.5 + 0.25 * sin(p.x * 6.0 + sin(p.y * 4.0)) + 0.25 * sin(p.y * 7.0 - p.x * 2.5);
}

void vertex() {
	edge = COLOR.r;
}

void fragment() {
	vec2 flow = vec2(TIME * flow_speed, 0.0);
	vec2 p = UV * ripple_scale;
	float a = ripple(p - flow);
	float b = ripple(p * 1.7 + vec2(3.1, 1.3) - flow * 1.4);
	float wave = a * b;
	float shine = smoothstep(0.55, 0.85, wave);

	vec3 colour = mix(deep_colour.rgb, shallow_colour.rgb, clamp(edge * 0.7 + wave * 0.5, 0.0, 1.0));
	float foam = smoothstep(0.55, 1.0, edge) * (0.55 + 0.45 * a);
	colour = mix(colour, foam_colour.rgb, foam * 0.8 + shine * 0.12);

	ALBEDO = colour;
	ALPHA = clamp(opacity + foam * 0.2, 0.0, 1.0);
	ROUGHNESS = 0.06;
	SPECULAR = 0.7;
	METALLIC = 0.0;
	// Gentle ripples in the normal so the sun glints on the surface.
	NORMAL = normalize(NORMAL + vec3((a - 0.5) * 0.12, 0.0, (b - 0.5) * 0.12));
}
