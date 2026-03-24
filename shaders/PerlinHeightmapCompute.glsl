#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) restrict buffer ParamsBuffer {
	float resolution;
	float noise_scale;
	float layer_count;
	float _padding;
} params;

layout(set = 0, binding = 1, std430) restrict buffer HeightsBuffer {
	float heights[];
} heights_buffer;

// Hash-based unit gradient (Perlin-style gradient noise on a grid).
vec2 hash_gradient(ivec2 i) {
	float n = float(i.x * 127 + i.y * 311);
	float a = sin(n) * 43758.5453;
	a = fract(a) * 6.28318530718;
	return vec2(cos(a), sin(a));
}

float gradient_noise(vec2 p) {
	ivec2 i = ivec2(floor(p));
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);

	vec2 g00 = hash_gradient(i + ivec2(0, 0));
	vec2 g10 = hash_gradient(i + ivec2(1, 0));
	vec2 g01 = hash_gradient(i + ivec2(0, 1));
	vec2 g11 = hash_gradient(i + ivec2(1, 1));

	float v00 = dot(g00, f - vec2(0.0, 0.0));
	float v10 = dot(g10, f - vec2(1.0, 0.0));
	float v01 = dot(g01, f - vec2(0.0, 1.0));
	float v11 = dot(g11, f - vec2(1.0, 1.0));

	return mix(mix(v00, v10, u.x), mix(v01, v11, u.x), u.y);
}

// Each layer: sample at 2× the previous frequency; strength (amplitude) is ½ the previous layer — summed, not renormalized,
// so layer 1 adds at most half of layer 0’s peak, layer 2 at most half of layer 1’s, etc.
float layered_gradient_noise(vec2 base_p) {
	int layers = clamp(int(params.layer_count + 0.5), 1, 16);
	float amp = 1.0;
	float freq = 1.0;
	float sum = 0.0;
	for (int i = 0; i < layers; i++) {
		sum += amp * gradient_noise(base_p * freq);
		amp *= 0.5;
		freq *= 2.0;
	}
	return sum;
}

void main() {
	uvec2 gid = gl_GlobalInvocationID.xy;
	uint res = uint(params.resolution + 0.5);
	if (gid.x >= res || gid.y >= res) {
		return;
	}

	vec2 p = vec2(float(gid.x), float(gid.y)) * params.noise_scale;
	float h = layered_gradient_noise(p);
	uint idx = gid.x + gid.y * res;
	heights_buffer.heights[idx] = h;
}
