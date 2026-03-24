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

void fragment() {
	vec3 n = normalize(v_world_normal);
	float h = v_world_pos.y;

	// 0 = flat facing up, 1 = cliff
	float slope = 1.0 - clamp(dot(n, vec3(0.0, 1.0, 0.0)), 0.0, 1.0);

	vec2 uv = world_xz_uv(v_world_pos);
	vec4 sand_c = texture(sand_albedo, uv);
	vec4 grass_c = texture(grass_albedo, uv);
	// Rock uses triplanar to reduce streaking on steep faces
	vec4 rock_c = sample_tri(rock_albedo, v_world_pos, n);
	vec4 snow_c = texture(snow_albedo, uv);

	// Height: sand -> grass -> snow
	float sand_w = 1.0 - smoothstep(sand_max_height, grass_begins_height, h);
	float snow_w = smoothstep(snow_begins_height, snow_full_height, h);
	float grass_w = (1.0 - sand_w) * (1.0 - snow_w);

	vec3 base = sand_c.rgb * sand_w + grass_c.rgb * grass_w + snow_c.rgb * snow_w;

	float rock_w = smoothstep(rock_slope_start, rock_slope_full, slope);
	rock_w *= mix(1.0, rock_sand_suppress, sand_w);
	vec3 with_rock = mix(base, rock_c.rgb, rock_w);

	// Underwater / wet sand tint
	float below = 1.0 - smoothstep(water_level - 0.15, water_level + 0.05, h);
	vec3 final_albedo = mix(with_rock, with_rock * underwater_tint.rgb, below * underwater_blend);

	ALBEDO = final_albedo;
	// Simple material response: snow slightly glossier, rock mid, sand/grass rougher
	float rough = mix(0.9, 0.4, snow_w);
	rough = mix(rough, 0.55, rock_w * (1.0 - snow_w * 0.5));
	ROUGHNESS = rough;
}
