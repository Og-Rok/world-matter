shader_type spatial;
render_mode cull_back, depth_draw_opaque;

uniform vec4 dark_pink : source_color = vec4(0.55, 0.10, 0.35, 1.0);
uniform vec4 grey : source_color = vec4(0.55, 0.55, 0.55, 1.0);
uniform float square_scale = 12.0;

void fragment() {
    vec2 grid_uv = UV * square_scale;
    vec2 cell = floor(grid_uv);
    float parity = mod(cell.x + cell.y, 2.0);
    vec3 albedo = mix(dark_pink.rgb, grey.rgb, parity);

    ALBEDO = albedo;
    ROUGHNESS = 1.0;
    METALLIC = 0.0;
}
