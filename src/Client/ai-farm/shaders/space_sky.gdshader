// Stylised space backdrop: dark gradient, soft nebula clouds and twinkling stars.
shader_type sky;

uniform vec3 space_top : source_color = vec3(0.06, 0.05, 0.16);
uniform vec3 space_bottom : source_color = vec3(0.01, 0.01, 0.05);
uniform vec3 nebula_a : source_color = vec3(0.48, 0.22, 0.7);
uniform vec3 nebula_b : source_color = vec3(0.12, 0.5, 0.62);
uniform float nebula_strength : hint_range(0.0, 1.0) = 0.45;
uniform float star_brightness = 1.6;
uniform float twinkle_speed = 1.5;

float hash13(vec3 p) {
	p = fract(p * 0.1031);
	p += dot(p, p.zyx + 31.32);
	return fract((p.x + p.y) * p.z);
}

vec3 hash33(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yxz + 33.33);
	return fract((p.xxy + p.yxx) * p.zyx);
}

float value_noise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	return mix(
		mix(mix(hash13(i), hash13(i + vec3(1, 0, 0)), f.x),
			mix(hash13(i + vec3(0, 1, 0)), hash13(i + vec3(1, 1, 0)), f.x), f.y),
		mix(mix(hash13(i + vec3(0, 0, 1)), hash13(i + vec3(1, 0, 1)), f.x),
			mix(hash13(i + vec3(0, 1, 1)), hash13(i + vec3(1, 1, 1)), f.x), f.y),
		f.z);
}

float fbm(vec3 p) {
	float total = 0.0;
	float amp = 0.5;
	for (int i = 0; i < 5; i++) {
		total += value_noise(p) * amp;
		p *= 2.03;
		amp *= 0.5;
	}
	return total;
}

// One layer of stars: at most one star per grid cell, most cells empty.
float star_layer(vec3 dir, float scale, float chance, float max_size, float time) {
	vec3 p = dir * scale;
	vec3 cell = floor(p);
	float r = hash13(cell);
	if (r > chance) {
		return 0.0;
	}
	vec3 center = hash33(cell) * 0.6 + 0.2;
	float size = mix(max_size * 0.4, max_size, hash13(cell + 7.13));
	float star = smoothstep(size, 0.0, length(fract(p) - center));
	float twinkle = 0.65 + 0.35 * sin(time * twinkle_speed + r * 400.0);
	return star * twinkle;
}

void sky() {
	vec3 dir = normalize(EYEDIR);
	// Radiance/ambient passes don't need animation; skipping TIME there keeps them cheap.
	float time = AT_CUBEMAP_PASS ? 0.0 : TIME;

	vec3 color = mix(space_bottom, space_top, smoothstep(-0.6, 0.8, dir.y));

	float cloud = fbm(dir * 2.2);
	float cloud_mask = smoothstep(0.35, 0.9, cloud) * nebula_strength;
	vec3 nebula = mix(nebula_a, nebula_b, smoothstep(0.3, 0.7, fbm(dir * 1.3 + 11.0)));
	color += nebula * cloud_mask;

	float stars = star_layer(dir, 90.0, 0.08, 0.22, time)
		+ star_layer(dir, 180.0, 0.06, 0.14, time) * 0.7
		+ star_layer(dir, 40.0, 0.03, 0.16, time) * 1.6;
	// Faintly tint stars with the nebula behind them.
	color += stars * star_brightness * mix(vec3(1.0), nebula + 0.5, 0.25);

	// Dither to hide banding in the dark gradient.
	color += (hash13(dir * 1000.0 + time) - 0.5) / 255.0;

	COLOR = color;
}
