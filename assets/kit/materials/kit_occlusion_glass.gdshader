// Fönsterglas i byggsatsen. Samma styrning som kit_occlusion.gdshader,
// men tonas mjukt i stället för rutmönster.
shader_type spatial;
render_mode cull_back, blend_mix, depth_draw_opaque;

uniform vec4 albedo : source_color = vec4(0.66, 0.85, 0.92, 0.3);
uniform float roughness = 0.1;
uniform float ghost_amount = 0.85;

global uniform vec2 occlusion_camera_diag;

instance uniform float cut_height = 1000000.0;
instance uniform float ghost = 0.0;
instance uniform float stub_top = -1000000.0;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	vec2 diag = vec2(abs(PROJECTION_MATRIX[0][0]), abs(PROJECTION_MATRIX[1][1]));
	bool camera_pass = all(lessThanEqual(abs(diag - occlusion_camera_diag), occlusion_camera_diag * 0.005 + 1e-6));
	float alpha = albedo.a;
	if (camera_pass) {
		if (world_pos.y > cut_height) {
			discard;
		}
		if (world_pos.y > stub_top) {
			alpha *= 1.0 - ghost * ghost_amount;
		}
	}
	ALBEDO = albedo.rgb;
	ALPHA = alpha;
	ROUGHNESS = roughness;
}
