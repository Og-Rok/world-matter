#[compute]
#version 450

layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;

// std430: 6× vec4 = 96 bytes — shared by plane (ProceduralPlaneTest) and sphere patches (WorldBuilderPlane).
layout(set = 0, binding = 0, std430) restrict buffer ParamsBuffer {
	vec4 header; // x=resolution, y=noise_scale, z=layer_count, w=mode (0=XZ plane, 1=sphere in WorldBuilder space)
	vec4 plane; // x=physical_size (plane only)
	vec4 corner_00;
	vec4 corner_10;
	vec4 corner_11;
	vec4 corner_01;
} params;

layout(set = 0, binding = 1, std430) restrict buffer HeightsBuffer {
	float heights[];
} heights_buffer;

// --- 2D noise (plane mode) ---

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

float layered_gradient_noise_2d(vec2 base_p) {
	int layers = clamp(int(params.header.z + 0.5), 1, 16);
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

// --- 3D noise (sphere: sample at each vertex WorldBuilder-space position) ---

vec3 hash_gradient_3(ivec3 c) {
	float n = float(c.x * 127 + c.y * 311 + c.z * 74);
	float a = sin(n) * 43758.5453;
	a = fract(a) * 6.28318530718;
	float b = sin(n * 1.234 + 19.1) * 12345.678;
	b = fract(b) * 6.28318530718;
	float ca = cos(a);
	float sa = sin(a);
	float cb = cos(b);
	float sb = sin(b);
	return normalize(vec3(sa * cb, sa * sb, ca));
}

float gradient_noise_3d(vec3 p) {
	ivec3 i = ivec3(floor(p));
	vec3 f = fract(p);
	vec3 u = f * f * (3.0 - 2.0 * f);

	float n000 = dot(hash_gradient_3(i + ivec3(0, 0, 0)), f - vec3(0.0, 0.0, 0.0));
	float n100 = dot(hash_gradient_3(i + ivec3(1, 0, 0)), f - vec3(1.0, 0.0, 0.0));
	float n010 = dot(hash_gradient_3(i + ivec3(0, 1, 0)), f - vec3(0.0, 1.0, 0.0));
	float n110 = dot(hash_gradient_3(i + ivec3(1, 1, 0)), f - vec3(1.0, 1.0, 0.0));
	float n001 = dot(hash_gradient_3(i + ivec3(0, 0, 1)), f - vec3(0.0, 0.0, 1.0));
	float n101 = dot(hash_gradient_3(i + ivec3(1, 0, 1)), f - vec3(1.0, 0.0, 1.0));
	float n011 = dot(hash_gradient_3(i + ivec3(0, 1, 1)), f - vec3(0.0, 1.0, 1.0));
	float n111 = dot(hash_gradient_3(i + ivec3(1, 1, 1)), f - vec3(1.0, 1.0, 1.0));

	float nx00 = mix(n000, n100, u.x);
	float nx10 = mix(n010, n110, u.x);
	float nx01 = mix(n001, n101, u.x);
	float nx11 = mix(n011, n111, u.x);

	float nxy0 = mix(nx00, nx10, u.y);
	float nxy1 = mix(nx01, nx11, u.y);

	return mix(nxy0, nxy1, u.z);
}

float layered_gradient_noise_3d(vec3 base_p) {
	int layers = clamp(int(params.header.z + 0.5), 1, 16);
	float amp = 1.0;
	float freq = 1.0;
	float sum = 0.0;
	for (int i = 0; i < layers; i++) {
		sum += amp * gradient_noise_3d(base_p * freq);
		amp *= 0.5;
		freq *= 2.0;
	}
	return sum;
}

/// Undisplaced patch vertex in WorldBuilder (planet) space — same as WorldBuilderPlane.sampleSphericalQuadWorld.
vec3 world_builder_vertex_on_sphere(vec2 uv) {
	vec3 p00 = params.corner_00.xyz;
	vec3 p10 = params.corner_10.xyz;
	vec3 p11 = params.corner_11.xyz;
	vec3 p01 = params.corner_01.xyz;
	vec3 blended = (1.0 - uv.x) * (1.0 - uv.y) * p00
		+ uv.x * (1.0 - uv.y) * p10
		+ uv.x * uv.y * p11
		+ (1.0 - uv.x) * uv.y * p01;
	float r = (length(p00) + length(p10) + length(p11) + length(p01)) * 0.25;
	if (dot(blended, blended) < 1e-20) {
		return p00;
	}
	return normalize(blended) * r;
}

void main() {
	uvec2 gid = gl_GlobalInvocationID.xy;
	uint res = uint(params.header.x + 0.5);
	if (gid.x >= res || gid.y >= res) {
		return;
	}

	float rf = max(float(res) - 1.0, 1.0);
	vec2 uv = vec2(float(gid.x), float(gid.y)) / rf;
	float h;

	if (params.header.w < 0.5) {
		// XZ plane: same extent at any vertex count (unchanged for ProceduralPlaneTest).
		float extent = max(params.plane.x, 0.0001);
		vec2 world_xz = (uv - vec2(0.5)) * extent;
		vec2 p = world_xz * params.header.y;
		h = layered_gradient_noise_2d(p);
	} else {
		vec3 world_pos = world_builder_vertex_on_sphere(uv);
		vec3 sample_p = world_pos * params.header.y;
		h = layered_gradient_noise_3d(sample_p);
	}

	uint idx = gid.x + gid.y * res;
	heights_buffer.heights[idx] = h;
}
