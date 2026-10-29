using Godot;
using System.Collections.Generic;

// Ser till att spelaren alltid syns, som i Diablo / Path of Exile. Inga triggers behövs:
//
// 1. Byggnader: noder i gruppen "building" (t.ex. roten i house_14x14.tscn).
//    Står spelaren på ett golv eller en trappa som hör till en byggnad är spelaren "inne".
//    Då klipps allt i byggnaden bort ovanför spelarens våning (tak, gavlar, våningar ovanför).
//    Våningen räknas fram från höjden: byggnadens golv + LevelHeight * n.
//
// 2. Skymmande bitar: noder i gruppen "occluder" (sätts i bitarnas egna scener, t.ex.
//    assets/kit/pieces/wall.tscn). En bit blir genomskinlig när den står mellan kameran och spelaren:
//    - inne i en byggnad: alla bitar på spelarens våning som står närmare kameran än spelaren.
//      Byggnadens mappar (t.ex. Level1, Level2, Extension) avgör våningen: en mapps golvhöjd är
//      dess lägsta bit, och mappar vars golvhöjd är spelarens våning räknas,
//    - annars: bitar närmare kameran än spelaren som på skärmen överlappar ett område runt spelaren.
//    Under spelarens golv + StubHeight förblir bitarna solida, så rummets form syns.
//
// Allt ritas av byggsatsens material (assets/kit/materials/kit_occlusion.gdshader) och styrs med
// "instance uniforms", så inga material byts under spelets gång.
// Genomskinliga bitar läggs i gruppen "see_through" så att klick går igenom dem (se PlayerClick.cs),
// och IsHidden() talar om ifall en punkt är bortklippt.
public partial class OcclusionManager : Node
{
	public const string BuildingGroup = "building";
	public const string OccluderGroup = "occluder";
	public const string SeeThroughGroup = "see_through";
	// Bitar som aldrig ska klippas, t.ex. trappor (de går upp till nästa våning).
	public const string NeverCutGroup = "never_cut";

	[Export]
	public Node3D Player { get; set; }

	// Våningshöjd i byggsatsen (meter).
	[Export]
	public float LevelHeight { get; set; } = 3.0f;

	// Hur hög den solida nederkanten på genomskinliga bitar är (meter över spelarens golv).
	[Export]
	public float StubHeight { get; set; } = 0.4f;

	// Utomhus: hur stort område runt spelaren på skärmen som räknas (andel av skärmhöjden).
	[Export(PropertyHint.Range, "0,0.5,0.01")]
	public float ScreenRadius { get; set; } = 0.12f;

	// Hur lång tid (sekunder) det tar att tona in/ut och klippa bort taket.
	[Export]
	public float TransitionTime { get; set; } = 0.25f;

	// Inne i en byggnad klipps allt bort ovanför våningens topp minus så här mycket (meter).
	// Måste vara större än takfotens överhäng, som sluttar ner under väggarnas överkant.
	[Export]
	public float CutBelowLevelTop { get; set; } = 0.3f;

	// Spelarens höjd (meter), för området på skärmen.
	[Export]
	public float PlayerHeight { get; set; } = 1.8f;

	private const float NoCut = 1000000.0f;
	private const uint WorldLayer = 1u << 0;
	// Bitar längre bort än så här från spelaren (meter, längs marken) behöver aldrig bli genomskinliga.
	private const float MaxOccluderDistance = 40.0f;

	private sealed class Building
	{
		public Node3D Root;
		public readonly List<GeometryInstance3D> Geometry = new();
		public float Top;
		public float Cut = NoCut;
	}

	private sealed class Piece
	{
		public Node3D Root;
		public Building Building;
		public readonly List<GeometryInstance3D> Geometry = new();
		public Aabb Bounds;
		// Golvhöjden för mappen (direkt under byggnaden) som biten ligger i.
		public float SectionFloor;
		public float Ghost;
		public bool Target;
		public float StubTop = float.NaN;
	}

	private static OcclusionManager _instance;

	private readonly List<Building> _buildings = new();
	private readonly Dictionary<Node, Building> _buildingByRoot = new();
	private readonly List<Piece> _pieces = new();
	private Vector2 _lastDiagonal;

	public override void _EnterTree()
	{
		_instance = this;
	}

	public override void _ExitTree()
	{
		if (_instance == this)
			_instance = null;
	}

	public override void _Ready()
	{
		// Vänta tills hela scenen (alla hus) är klar innan vi letar.
		CallDeferred(MethodName.Collect);
	}

	// Sant om punkten är bortklippt (t.ex. ett tak ovanför spelaren). Används av PlayerClick.
	public static bool IsHidden(Node node, Vector3 point)
	{
		if (_instance == null)
			return false;

		for (Node current = node; current != null; current = current.GetParent())
		{
			if (current.IsInGroup(NeverCutGroup))
				return false;
		}

		Building building = _instance.FindBuilding(node);
		return building != null && point.Y > building.Cut;
	}

	private void Collect()
	{
		_buildings.Clear();
		_buildingByRoot.Clear();
		_pieces.Clear();

		foreach (Node node in GetTree().GetNodesInGroup(BuildingGroup))
		{
			if (node is not Node3D root)
				continue;

			var building = new Building { Root = root, Top = float.MinValue };
			CollectGeometry(root, building.Geometry, NeverCutGroup);
			foreach (GeometryInstance3D geometry in building.Geometry)
			{
				Aabb box = geometry.GlobalTransform * geometry.GetAabb();
				building.Top = Mathf.Max(building.Top, box.End.Y);
			}
			_buildings.Add(building);
			_buildingByRoot[root] = building;
		}

		foreach (Node node in GetTree().GetNodesInGroup(OccluderGroup))
		{
			if (node is not Node3D root)
				continue;

			var piece = new Piece { Root = root, Building = FindBuilding(root) };
			CollectGeometry(root, piece.Geometry);
			if (piece.Geometry.Count == 0)
				continue;

			piece.Bounds = piece.Geometry[0].GlobalTransform * piece.Geometry[0].GetAabb();
			foreach (GeometryInstance3D geometry in piece.Geometry)
				piece.Bounds = piece.Bounds.Merge(geometry.GlobalTransform * geometry.GetAabb());
			_pieces.Add(piece);
		}

		// Varje mapp direkt under en byggnad (t.ex. Level1, Level2, Extension) får en golvhöjd:
		// den lägsta biten i mappen.
		var sectionFloor = new Dictionary<Node, float>();
		var sectionOf = new Dictionary<Piece, Node>();
		foreach (Piece piece in _pieces)
		{
			Node section = FindSection(piece.Root, piece.Building);
			sectionOf[piece] = section;
			float y = piece.Bounds.Position.Y;
			sectionFloor[section] = sectionFloor.TryGetValue(section, out float lowest) ? Mathf.Min(lowest, y) : y;
		}
		foreach (Piece piece in _pieces)
			piece.SectionFloor = sectionFloor[sectionOf[piece]];
	}

	// Mappen direkt under byggnadens rot som noden ligger i (eller noden själv om den saknar byggnad).
	private static Node FindSection(Node node, Building building)
	{
		if (building == null)
			return node;

		Node current = node;
		while (current.GetParent() != null && current.GetParent() != building.Root)
			current = current.GetParent();
		return current;
	}

	// Körs i fysiksteget eftersom vi frågar fysikvärlden (vad spelaren står på).
	public override void _PhysicsProcess(double delta)
	{
		Camera3D camera = GetViewport().GetCamera3D();
		if (camera == null || Player == null || !IsInstanceValid(Player))
			return;

		UpdateCameraDiagonal(camera);

		Building inside = FindBuildingUnderPlayer(out float floorY);
		float step = (float)delta / Mathf.Max(TransitionTime, 0.001f);

		UpdateCuts(inside, floorY, step);
		UpdateGhosts(camera, inside, floorY, step);
	}

	// Kamerans projektion skickas till shadern, som använder den för att inte röra skuggorna.
	private void UpdateCameraDiagonal(Camera3D camera)
	{
		Projection projection = camera.GetCameraProjection();
		var diagonal = new Vector2(Mathf.Abs(projection.X.X), Mathf.Abs(projection.Y.Y));
		if (diagonal == _lastDiagonal)
			return;
		_lastDiagonal = diagonal;
		RenderingServer.GlobalShaderParameterSet("occlusion_camera_diag", diagonal);
	}

	// Den byggnad vars golv (eller trappa) spelaren står på, och golvhöjden för spelarens våning.
	private Building FindBuildingUnderPlayer(out float floorY)
	{
		Vector3 feet = Player.GlobalPosition;
		floorY = feet.Y;

		var query = PhysicsRayQueryParameters3D.Create(feet + Vector3.Up * 0.5f, feet + Vector3.Down * 1.0f, WorldLayer);
		if (Player is CollisionObject3D body)
			query.Exclude = new Godot.Collections.Array<Rid> { body.GetRid() };

		Godot.Collections.Dictionary hit = Player.GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (hit.Count == 0 || hit["collider"].AsGodotObject() is not Node collider)
			return null;

		Building building = FindBuilding(collider);
		if (building == null)
			return null;

		// Byt våning en halv meter innan nästa golv, så att övervåningen syns när man är nästan uppe i trappan.
		float baseY = building.Root.GlobalPosition.Y;
		float level = Mathf.Floor((feet.Y - baseY + 0.5f) / LevelHeight);
		floorY = baseY + Mathf.Max(level, 0) * LevelHeight;
		return building;
	}

	private void UpdateCuts(Building inside, float floorY, float step)
	{
		foreach (Building building in _buildings)
		{
			float target = building == inside ? floorY + LevelHeight - CutBelowLevelTop : NoCut;
			float cut = building.Cut;
			// Taket "torkas bort" uppifrån: klippet glider från byggnadens topp ner till målet, och tillbaka.
			float travel = Mathf.Max(building.Top - (floorY + LevelHeight), 1.0f);

			if (target < NoCut)
			{
				if (cut >= NoCut)
					cut = building.Top;
				cut = Mathf.MoveToward(cut, target, travel * step);
			}
			else if (cut < NoCut)
			{
				cut += travel * step;
				if (cut >= building.Top)
					cut = NoCut;
			}

			if (cut == building.Cut)
				continue;

			building.Cut = cut;
			foreach (GeometryInstance3D geometry in building.Geometry)
			{
				if (IsInstanceValid(geometry))
					geometry.SetInstanceShaderParameter("cut_height", cut);
			}
		}
	}

	private void UpdateGhosts(Camera3D camera, Building inside, float floorY, float step)
	{
		Transform3D toView = camera.GlobalTransform.AffineInverse();
		Vector3 playerPosition = Player.GlobalPosition;
		Vector3 playerCenter = playerPosition + Vector3.Up * (PlayerHeight * 0.5f);
		float playerDepth = -(toView * playerCenter).Z;

		// Spelaren på skärmen, med marginal runt omkring.
		Vector2 feetOnScreen = camera.UnprojectPosition(playerPosition);
		Vector2 headOnScreen = camera.UnprojectPosition(playerPosition + Vector3.Up * PlayerHeight);
		float margin = ScreenRadius * GetViewport().GetVisibleRect().Size.Y;
		Rect2 playerRect = new Rect2(feetOnScreen, Vector2.Zero).Expand(headOnScreen).Grow(margin);

		float stubTop = floorY + StubHeight;

		foreach (Piece piece in _pieces)
		{
			if (!IsInstanceValid(piece.Root))
				continue;

			bool target = false;
			Vector3 center = piece.Bounds.GetCenter();
			bool cutAway = piece.Building != null && piece.Bounds.Position.Y >= piece.Building.Cut;
			Vector2 flat = new Vector2(center.X - playerPosition.X, center.Z - playerPosition.Z);

			if (!cutAway && flat.Length() < MaxOccluderDistance)
			{
				float depth = -(toView * center).Z;
				if (depth < playerDepth - 0.3f)
				{
					// Inne: hela väggar på spelarens våning (rummets framsida), som i Diablo.
					bool onPlayerLevel = Mathf.Abs(piece.SectionFloor - floorY) < 0.5f
						&& Mathf.Abs(piece.Bounds.Position.Y - floorY) < 0.5f;
					if (inside != null && piece.Building == inside && onPlayerLevel)
						target = true;
					else
						target = ScreenRect(camera, piece.Bounds).Intersects(playerRect);
				}
			}

			float ghost = Mathf.MoveToward(piece.Ghost, target ? 1.0f : 0.0f, step);
			bool changed = ghost != piece.Ghost;
			piece.Ghost = ghost;

			if (target != piece.Target)
			{
				piece.Target = target;
				if (target)
					piece.Root.AddToGroup(SeeThroughGroup);
				else
					piece.Root.RemoveFromGroup(SeeThroughGroup);
			}

			bool stubChanged = ghost > 0 && piece.StubTop != stubTop;
			if (!changed && !stubChanged)
				continue;

			if (stubChanged)
				piece.StubTop = stubTop;

			foreach (GeometryInstance3D geometry in piece.Geometry)
			{
				if (!IsInstanceValid(geometry))
					continue;
				geometry.SetInstanceShaderParameter("ghost", ghost);
				if (stubChanged)
					geometry.SetInstanceShaderParameter("stub_top", stubTop);
			}
		}
	}

	private static Rect2 ScreenRect(Camera3D camera, Aabb box)
	{
		Rect2 rect = new Rect2(camera.UnprojectPosition(box.GetEndpoint(0)), Vector2.Zero);
		for (int i = 1; i < 8; i++)
			rect = rect.Expand(camera.UnprojectPosition(box.GetEndpoint(i)));
		return rect;
	}

	private Building FindBuilding(Node node)
	{
		for (Node current = node; current != null; current = current.GetParent())
		{
			if (_buildingByRoot.TryGetValue(current, out Building building))
				return building;
		}
		return null;
	}

	private static void CollectGeometry(Node node, List<GeometryInstance3D> result, string skipGroup = null)
	{
		if (skipGroup != null && node.IsInGroup(skipGroup))
			return;

		if (node is GeometryInstance3D geometry)
			result.Add(geometry);

		foreach (Node child in node.GetChildren())
			CollectGeometry(child, result, skipGroup);
	}
}
