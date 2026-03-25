shader_type spatial;
render_mode blend_mix, cull_back, depth_draw_always, diffuse_lambert, specular_schlick_ggx;

uniform sampler2D DEPTH_TEXTURE : hint_depth_texture, filter_linear_mipmap;
uniform sampler2D SCREEN_TEXTURE : hint_screen_texture, filter_linear_mipmap;
uniform sampler2D wave_normal : hint_normal, filter_linear_mipmap, repeat_enable;

uniform vec4 deep_color : source_color = vec4(0.03, 0.14, 0.22, 1.0);
uniform vec4 shallow_color : source_color = vec4(0.22, 0.62, 0.68, 1.0);
uniform float murkiness : hint_range(0.0, 6.0) = 1.85;
uniform float refraction_strength : hint_range(0.0, 0.08) = 0.018;
uniform float fresnel_power : hint_range(0.5, 8.0) = 4.8;
uniform float alpha_base : hint_range(0.05, 0.95) = 0.38;
uniform float alpha_max : hint_range(0.3, 1.0) = 0.88;
uniform float roughness : hint_range(0.0, 0.4) = 0.045;
uniform float specular : hint_range(0.0, 1.0) = 0.92;
uniform float wave_tiling : hint_range(0.5, 64.0) = 8.0;
uniform vec2 wave_scroll_a = vec2(0.04, 0.02);
uniform vec2 wave_scroll_b = vec2(-0.03, 0.045);

// Two coefficients extracted from INV_PROJECTION_MATRIX in vertex() let us linearize
// the depth buffer in one multiply+add in fragment instead of a full mat4×vec4 path.
// This replaces the previous 'varying mat4 v_inv_projection' (16 floats → 2 floats).
varying vec2 v_depth_coeffs;  // (INV_PROJ[2].w, INV_PROJ[3].w)
varying float v_view_z;       // view-space depth of this water surface vertex

void vertex() {
	// Linearize depth with: linear_z = 1.0 / (raw_d * coeffs.x + coeffs.y)
	v_depth_coeffs = vec2(INV_PROJECTION_MATRIX[2].w, INV_PROJECTION_MATRIX[3].w);
	// Pre-compute view-space depth so fragment avoids a mat4×vec4 multiply.
	v_view_z = -(MODELVIEW_MATRIX * vec4(VERTEX, 1.0)).z;
}

void fragment() {
	vec2 wuv_a = UV * wave_tiling + TIME * wave_scroll_a;
	vec2 wuv_b = UV * wave_tiling * 1.37 + TIME * wave_scroll_b;
	vec3 map_a = texture(wave_normal, wuv_a).xyz * 2.0 - 1.0;
	vec3 map_b = texture(wave_normal, wuv_b).xyz * 2.0 - 1.0;
	vec3 map = normalize(vec3(map_a.xy + map_b.xy, mix(map_a.z, map_b.z, 0.5)));

	vec3 Nw = normalize(map.x * TANGENT + map.y * BINORMAL + map.z * NORMAL);
	NORMAL = Nw;

	float fresnel = pow(1.0 - clamp(dot(Nw, normalize(VIEW)), 0.0, 1.0), fresnel_power);

	vec2 refr_uv = clamp(SCREEN_UV + Nw.xy * refraction_strength, vec2(0.001), vec2(0.999));
	vec3 behind = texture(SCREEN_TEXTURE, refr_uv).rgb;

	float raw_d = texture(DEPTH_TEXTURE, SCREEN_UV).r;
	float scene_z = 1.0 / (raw_d * v_depth_coeffs.x + v_depth_coeffs.y);

	float travel = max(0.0, scene_z - v_view_z);
	float murk = 1.0 - exp(-murkiness * 0.018 * travel);
	vec3 water_rgb = mix(behind, deep_color.rgb, murk);
	water_rgb = mix(water_rgb, shallow_color.rgb, fresnel * 0.35);

	ALBEDO = water_rgb;
	ALPHA = mix(alpha_base, alpha_max, fresnel);
	ROUGHNESS = roughness;
	SPECULAR = specular;
	METALLIC = 0.02;
}
