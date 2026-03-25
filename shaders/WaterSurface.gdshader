shader_type spatial;
render_mode blend_mix, cull_back, depth_draw_always, diffuse_lambert, specular_schlick_ggx;

uniform sampler2D DEPTH_TEXTURE : hint_depth_texture, filter_linear_mipmap;
uniform sampler2D SCREEN_TEXTURE : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D wave_normal : hint_normal, filter_linear_mipmap, repeat_enable;

/// Tint mixed in over distance (murk / depth fog).
uniform vec4 deep_color : source_color = vec4(0.03, 0.14, 0.22, 1.0);
/// Shallow / fresnel highlight tint.
uniform vec4 shallow_color : source_color = vec4(0.22, 0.62, 0.68, 1.0);
/// How fast view-through water fogs to deep_color (higher = murkier faster).
uniform float murkiness : hint_range(0.0, 6.0) = 1.85;
/// UV distortion for refraction / ripples.
uniform float refraction_strength : hint_range(0.0, 0.08) = 0.018;
/// Fresnel exponent (higher = more opaque at grazing angles).
uniform float fresnel_power : hint_range(0.5, 8.0) = 4.8;
/// Minimum opacity (looking straight down through water).
uniform float alpha_base : hint_range(0.05, 0.95) = 0.38;
/// Maximum opacity at edges / full fresnel.
uniform float alpha_max : hint_range(0.3, 1.0) = 0.88;
uniform float roughness : hint_range(0.0, 0.4) = 0.045;
uniform float specular : hint_range(0.0, 1.0) = 0.92;

uniform float wave_tiling : hint_range(0.5, 64.0) = 8.0;
uniform vec2 wave_scroll_a = vec2(0.04, 0.02);
uniform vec2 wave_scroll_b = vec2(-0.03, 0.045);

varying vec3 v_world_vertex;
// Transparent fragment pass has no PROJECTION_MATRIX; pass it from vertex where it is available.
// INV_PROJECTION_MATRIX is identical for all vertices (camera matrix), so interpolation is harmless.
varying mat4 v_inv_projection;

void vertex() {
	v_world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	v_inv_projection = INV_PROJECTION_MATRIX;
}

void fragment() {
	vec2 wuv_a = UV * wave_tiling + TIME * wave_scroll_a;
	vec2 wuv_b = UV * wave_tiling * 1.37 + TIME * wave_scroll_b;
	vec3 map_a = texture(wave_normal, wuv_a).xyz * 2.0 - 1.0;
	vec3 map_b = texture(wave_normal, wuv_b).xyz * 2.0 - 1.0;
	vec3 map = normalize(vec3(map_a.xy + map_b.xy, mix(map_a.z, map_b.z, 0.5)));

	vec3 Nw = normalize(
			map.x * TANGENT +
			map.y * BINORMAL +
			map.z * NORMAL);
	NORMAL = Nw;

	float fresnel = pow(1.0 - clamp(dot(Nw, normalize(VIEW)), 0.0, 1.0), fresnel_power);

	vec2 refr_uv = SCREEN_UV + Nw.xy * refraction_strength;
	refr_uv = clamp(refr_uv, vec2(0.001), vec2(0.999));
	vec3 behind = texture(SCREEN_TEXTURE, refr_uv).rgb;

	float raw_d = texture(DEPTH_TEXTURE, SCREEN_UV).r;
	vec3 ndc = vec3(SCREEN_UV * 2.0 - 1.0, raw_d * 2.0 - 1.0);
	vec4 scene_view = v_inv_projection * vec4(ndc, 1.0);
	scene_view.xyz /= scene_view.w;
	float scene_z = -scene_view.z;

	vec4 vpos = VIEW_MATRIX * vec4(v_world_vertex, 1.0);
	float water_z = -vpos.z;

	float travel = max(0.0, scene_z - water_z);
	float murk = 1.0 - exp(-murkiness * 0.018 * travel);
	vec3 water_rgb = mix(behind, deep_color.rgb, murk);
	water_rgb = mix(water_rgb, shallow_color.rgb, fresnel * 0.35);

	ALBEDO = water_rgb;
	ALPHA = mix(alpha_base, alpha_max, fresnel);
	ROUGHNESS = roughness;
	SPECULAR = specular;
	METALLIC = 0.02;
}
