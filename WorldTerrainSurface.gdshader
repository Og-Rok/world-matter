shader_type spatial;
render_mode cull_back, depth_draw_opaque;

// --- Visual (matches DebugSquares style) ---
uniform vec4 dark_pink : source_color = vec4(0.55, 0.10, 0.35, 1.0);
uniform vec4 grey : source_color = vec4(0.55, 0.55, 0.55, 1.0);
uniform float square_scale = 12.0;

// --- Extra height from GPU noise (radial bump along mesh normal) ---
uniform float shader_noise_height : hint_range(0.0, 30.0) = 1.5;
uniform float shader_noise_frequency = 0.015;

float hash3(vec3 p) {
	return fract(sin(dot(p, vec3(127.1, 311.7, 74.7))) * 43758.5453123);
}

float noise3d(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash3(i + vec3(0.0, 0.0, 0.0));
	float n100 = hash3(i + vec3(1.0, 0.0, 0.0));
	float n010 = hash3(i + vec3(0.0, 1.0, 0.0));
	float n110 = hash3(i + vec3(1.0, 1.0, 0.0));
	float n001 = hash3(i + vec3(0.0, 0.0, 1.0));
	float n101 = hash3(i + vec3(1.0, 0.0, 1.0));
	float n011 = hash3(i + vec3(0.0, 1.0, 1.0));
	float n111 = hash3(i + vec3(1.0, 1.0, 1.0));
	return mix(
			mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
			mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y),
			f.z);
}

float fbm3(vec3 p) {
	float amp = 0.5;
	float sum = 0.0;
	vec3 x = p;
	sum += amp * noise3d(x);
	x *= 2.02;
	amp *= 0.5;
	sum += amp * noise3d(x);
	x *= 2.02;
	amp *= 0.5;
	sum += amp * noise3d(x);
	x *= 2.02;
	amp *= 0.5;
	sum += amp * noise3d(x);
	return sum;
}

void vertex() {
	vec3 world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	float n = fbm3(world_pos * shader_noise_frequency);
	n = n * 2.0 - 1.0;
	VERTEX += NORMAL * n * shader_noise_height;
}

void fragment() {
	vec2 grid_uv = UV * square_scale;
	vec2 cell = floor(grid_uv);
	float parity = mod(cell.x + cell.y, 2.0);
	vec3 albedo = mix(dark_pink.rgb, grey.rgb, parity);
	ALBEDO = albedo;
	ROUGHNESS = 1.0;
	METALLIC = 0.0;
}
