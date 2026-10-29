// Material för hela byggsatsen (väggar, golv, tak, trappor).
// Styrs av OcclusionManager.cs, så att spelaren alltid syns, som i Diablo / Path of Exile:
// - cut_height (per objekt): allt ovanför den höjden klipps bort (tak och våningar ovanför spelaren).
// - ghost (per objekt, 0-1): bitar som skymmer spelaren blir genomskinliga ovanför stub_top,
//   med ett fint rutmönster. Under stub_top är de solida, så rummets form syns.
// I skuggberäkningen görs ingenting av detta, så skuggorna är alltid hela.
shader_type spatial;
render_mode cull_disabled;

uniform sampler2D albedo_tex : source_color, filter_nearest, hint_default_white;
uniform vec4 albedo : source_color = vec4(1.0);
uniform float roughness = 0.7;
// Hur stor del av en skymmande bit som försvinner (0 = inget, 1 = allt).
uniform float ghost_amount = 0.85;

// Kamerans projektion (diagonalen). Skiljer kamerabilden från skuggornas projektion.
global uniform vec2 occlusion_camera_diag;

instance uniform float cut_height = 1000000.0;
instance uniform float ghost = 0.0;
instance uniform float stub_top = -1000000.0;

const float BAYER[16] = float[16](0.0, 8.0, 2.0, 10.0, 12.0, 4.0, 14.0, 6.0, 3.0, 11.0, 1.0, 9.0, 15.0, 7.0, 13.0, 5.0);

varying vec3 world_pos;
varying vec3 local_pos;
varying vec3 local_normal;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	local_pos = VERTEX;
	local_normal = NORMAL;
}

float bayer(vec2 frag) {
	int x = int(mod(frag.x, 4.0));
	int y = int(mod(frag.y, 4.0));
	return (BAYER[x + y * 4] + 0.5) / 16.0;
}

bool is_camera_pass(mat4 projection) {
	vec2 diag = vec2(abs(projection[0][0]), abs(projection[1][1]));
	return all(lessThanEqual(abs(diag - occlusion_camera_diag), occlusion_camera_diag * 0.005 + 1e-6));
}

void fragment() {
	bool camera_pass = is_camera_pass(PROJECTION_MATRIX);
	if (camera_pass) {
		if (world_pos.y > cut_height) {
			discard;
		}
		if (world_pos.y > stub_top && ghost * ghost_amount > bayer(FRAGCOORD.xy)) {
			discard;
		}
	}

	// Där man ser in i en bit genom ett snitt (baksidor, och väggarnas ändytor vid x = 0 och x = 2
	// där två bitar möts) belyses ytan som en vågrät ovansida, så snittet ser massivt ut.
	bool active = cut_height < 100000.0 || ghost > 0.0;
	bool wall_end = abs(local_normal.x) > 0.7 && (abs(local_pos.x) < 0.01 || abs(local_pos.x - 2.0) < 0.01);
	if (active && (!FRONT_FACING || wall_end)) {
		NORMAL = normalize((VIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
	}

	ALBEDO = albedo.rgb * texture(albedo_tex, UV).rgb;
	ROUGHNESS = roughness;
}
