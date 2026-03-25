shader_type spatial;
render_mode cull_back, diffuse_burley, specular_schlick_ggx;

// --- Albedo textures (assign in material; tile in world XZ) ---
uniform sampler2D sand_albedo : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D grass_albedo : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D rock_albedo : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D snow_albedo : source_color, filter_linear_mipmap, repeat_enable;

/// World units per texture repeat (larger = smaller tiles).
uniform float texture_scale = 4.0;

// --- Height bands (world Y) ---
/// Below this: treated as underwater (tint + full sand blend toward coast).
uniform float water_level = 0.0;
/// Fully sand up to this height; blends toward grass by grass_begins_height.
uniform float sand_max_height = 0.35;
/// Above this: no sand (grass / snow / rock only).
uniform float grass_begins_height = 0.85;
/// Snow starts blending in.
uniform float snow_begins_height = 1.6;
/// Fully snow above this (before rock mixes back on cliffs).
uniform float snow_full_height = 2.2;

// --- Slope (0 = flat, 1 = vertical): rock ---
uniform float rock_slope_start = 0.35;
uniform float rock_slope_full = 0.65;
/// Reduce rock on flat sand under water / beach.
uniform float rock_sand_suppress = 0.85;

// --- Underwater ---
uniform vec4 underwater_tint : source_color = vec4(0.45, 0.65, 0.85, 1.0);
uniform float underwater_blend : hint_range(0.0, 1.0) = 0.55;

// --- Noisy blend boundaries (breaks up hard snow/grass/rock lines) ---
/// World XZ frequency for blend noise (higher = smaller irregular patches).
uniform float blend_noise_scale = 0.06;
/// How far height thresholds jitter in world Y (meters).
uniform float blend_height_jitter = 0.55;
/// How much slope thresholds jitter (0–1 slope space).
uniform float blend_slope_jitter = 0.14;

varying vec3 v_world_pos;
varying vec3 v_world_normal;

void vertex() {
	v_world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

vec2 world_xz_uv(vec3 world_p) {
	float s = max(texture_scale, 0.001);
	return world_p.xz / s;
}

/// Triplanar weights from world normal (good for steep rock).
vec3 tri_weights(vec3 n) {
	vec3 an = abs(n);
	float sum = an.x + an.y + an.z + 1e-5;
	return an / sum;
}

vec4 sample_tri(sampler2D tex, vec3 world_p, vec3 n) {
	vec2 uvx = world_p.zy / max(texture_scale, 0.001);
	vec2 uvy = world_p.xz / max(texture_scale, 0.001);
	vec2 uvz = world_p.xy / max(texture_scale, 0.001);
	vec3 w = tri_weights(n);
	return texture(tex, uvx) * w.x + texture(tex, uvy) * w.y + texture(tex, uvz) * w.z;
}

float hash21(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}

float value_noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	float a = hash21(i);
	float b = hash21(i + vec2(1.0, 0.0));
	float c = hash21(i + vec2(0.0, 1.0));
	float d = hash21(i + vec2(1.0, 1.0));
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

/// ~0..1, decently smooth; use different seeds via offset p.
float blend_noise_fbm(vec2 p) {
	float s = 0.0;
	float a = 0.5;
	float f = 1.0;
	for (int i = 0; i < 4; i++) {
		s += a * value_noise(p * f);
		f *= 2.02;
		a *= 0.5;
	}
	return s;
}

void fragment() {
	vec3 n = normalize(v_world_normal);
	float h = v_world_pos.y;

	// 0 = flat facing up, 1 = cliff
	float slope = 1.0 - clamp(dot(n, vec3(0.0, 1.0, 0.0)), 0.0, 1.0);

	vec2 bn_uv = v_world_pos.xz * max(blend_noise_scale, 0.0001);
	float n0 = blend_noise_fbm(bn_uv);
	float n1 = blend_noise_fbm(bn_uv * 1.37 + vec2(19.2, 47.5));
	float n3 = blend_noise_fbm(bn_uv * 1.09 + vec2(61.0, 289.0));
	// Center around 0 so thresholds wander both ways
	float j_h0 = (n0 - 0.48) * 2.0 * blend_height_jitter;
	float j_h1 = (n1 - 0.48) * 2.0 * blend_height_jitter;
	float j_slope = (n3 - 0.48) * 2.0 * blend_slope_jitter;

	vec2 uv = world_xz_uv(v_world_pos);
	vec4 sand_c = texture(sand_albedo, uv);
	vec4 grass_c = texture(grass_albedo, uv);
	// Rock uses triplanar to reduce streaking on steep faces
	vec4 rock_c = sample_tri(rock_albedo, v_world_pos, n);
	vec4 snow_c = texture(snow_albedo, uv);

	// Height: sand -> grass -> snow (jittered edges so bands aren’t perfectly straight)
	float sand_w = 1.0 - smoothstep(sand_max_height + j_h0, grass_begins_height + j_h0, h);
	float snow_w = smoothstep(snow_begins_height + j_h1, snow_full_height + j_h1, h);
	float grass_w = (1.0 - sand_w) * (1.0 - snow_w);

	vec3 base = sand_c.rgb * sand_w + grass_c.rgb * grass_w + snow_c.rgb * snow_w;

	float rock_w = smoothstep(rock_slope_start + j_slope, rock_slope_full + j_slope, slope);
	rock_w *= mix(1.0, rock_sand_suppress, sand_w);
	// On snowy slopes, vary how strongly rock replaces snow so the boundary isn’t a smooth ring
	float n_rock = blend_noise_fbm(bn_uv * 2.4 + vec2(400.0, 900.0));
	float rock_soft = mix(0.72, 1.0, n_rock);
	rock_w *= mix(1.0, rock_soft, min(snow_w * 1.8, 1.0));
	vec3 with_rock = mix(base, rock_c.rgb, clamp(rock_w, 0.0, 1.0));

	// Underwater / wet sand tint
	float below = 1.0 - smoothstep(water_level - 0.15, water_level + 0.05, h);
	vec3 final_albedo = mix(with_rock, with_rock * underwater_tint.rgb, below * underwater_blend);

	ALBEDO = final_albedo;
	// Simple material response: snow slightly glossier, rock mid, sand/grass rougher
	float rough = mix(0.9, 0.4, snow_w);
	rough = mix(rough, 0.55, rock_w * (1.0 - snow_w * 0.5));
	ROUGHNESS = rough;
}
