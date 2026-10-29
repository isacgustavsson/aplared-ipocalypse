using Godot;

// Klicka-för-att-gå i Diablo-stil.
// Högerklicka (eller håll inne höger musknapp) där figuren ska gå.
// Kräver en NavigationAgent3D som barn till Player, och en bakad NavigationRegion3D i scenen.
// Kameran ska INTE vara barn till Player; den följer efter med fast avstånd.
//
// Hur spelaren tar sig till punkten man pekar på:
// 1. Går det att nå punkten utan stor omväg används navigationen (t.ex. in genom en dörr som
//    ligger ungefär åt rätt håll, eller runt ett litet hinder).
// 2. Annars, om spelaren är inne i ett hus: spring rakt mot punkten och stanna mot väggen.
// 3. Annars, om man pekar på/in i ett hus utifrån: gå till närmaste punkt utanför huset.
// 4. Annars (utomhus till utomhus): navigationen, även om det blir en omväg runt ett hus.
//
// Animationer: en AnimationPlayer på "Knight/AnimationPlayer" spelar IdleAnimation när figuren
// står still och RunAnimation när den faktiskt rör sig (KayKit Rig_Medium-animationerna).
public partial class PlayerClick : CharacterBody3D
{
	private const float Speed = 5.0f;
	private const float CameraDistance = 20.0f;
	private const float RayLength = 200.0f;

	// Fysiklager (namnges i Project Settings > Layer Names > 3D Physics).
	// World:      väggar, golv och mark.
	// ClickBlock: tak och gavlar. Används för att avgöra vad som är "inne".
	public const uint WorldLayer = 1u << 0;
	public const uint ClickBlockLayer = 1u << 1;

	// Hur långt bort (meter) vi letar efter en punkt utanför huset när man pekar in i ett hus.
	private const float OutsideSearchRadius = 12.0f;
	private const float OutsideSearchStep = 0.75f;
	private const int OutsideSearchDirections = 16;

	// Omväg = vägen är längre än DetourFactor * fågelvägen + DetourMargin meter.
	// När spelaren redan valt "omväg" krävs lite mindre för att fortsätta så (DetourKeep...),
	// så att beslutet inte hoppar fram och tillbaka medan man håller inne knappen.
	private const float DetourFactor = 1.4f;
	private const float DetourMargin = 2.0f;
	private const float DetourKeepFactor = 1.25f;
	private const float DetourKeepMargin = 1.0f;

	// Utifrån och in i ett hus är man mycket mer generös: man går in genom dörren även om det
	// blir en rejäl omväg. Bara om man står helt fel (t.ex. på baksidan långt från dörren)
	// går spelaren i stället till närmaste punkt utanför.
	private const float EnterDetourFactor = 2.5f;
	private const float EnterDetourMargin = 8.0f;

	private const float ArriveDistance = 0.3f;
	// Håller man inne knappen och pekar närmare än så här på spelaren händer inget (inget snurrande).
	private const float HoldDeadZone = 0.6f;
	// Pekaren måste flytta sig minst så här mycket innan vi räknar om (när man håller inne).
	private const float HoldUpdateDistance = 0.15f;

	// Rak gång: rör vi oss långsammare än denna andel av farten i StuckTime sekunder står vi mot en vägg.
	private const float StuckSpeedFraction = 0.2f;
	private const float StuckTime = 0.2f;

	// Vägföljning: en punkt i vägen räknas som nådd inom så här många meter (mätt längs marken).
	// Litet värde = spelaren följer vägen tätt och skär inte hörn mot dörrkarmar.
	private const float WaypointReach = 0.1f;
	// Kommer spelaren inte framåt längs vägen räknas vägen om från där spelaren står,
	// högst så här många gånger i rad innan spelaren ger upp.
	private const int MaxRepaths = 3;

	private enum MoveMode { Idle, Path, Direct }
	private MoveMode _mode = MoveMode.Idle;
	private Vector3 _directTarget;
	private float _stuckTimer;
	private bool _blocked;

	private Vector3[] _path = System.Array.Empty<Vector3>();
	private int _pathIndex;
	private Vector3 _pathTarget;
	private int _repaths;

	private bool _wasPressed;
	private bool _lastWasDetour;
	private Vector3 _lastPointer = new(float.NaN, 0, 0);

	[Export]
	public StringName IdleAnimation { get; set; } = "Idle_A";

	[Export]
	public StringName RunAnimation { get; set; } = "Running_A";

	// Hur lång tid (sekunder) det tar att glida över från en animation till nästa.
	[Export]
	public float AnimationBlendTime { get; set; } = 0.15f;

	// Under den här farten (m/s) räknas figuren som stillastående, t.ex. när den trycker mot en vägg.
	private const float RunAnimationMinSpeed = 0.5f;

	private float _gravity = ProjectSettings.GetSetting("physics/3d/default_gravity").AsSingle();
	private Camera3D _camera;
	private Vector3 _cameraOffset = Vector3.Zero;
	private NavigationAgent3D _agent;
	private AnimationPlayer _animation;

	public override void _Ready()
	{
		// Agenten används bara för att hitta navigationskartan; vägen följer vi själva (se FollowPath).
		_agent = GetNode<NavigationAgent3D>("NavigationAgent3D");

		_animation = GetNodeOrNull<AnimationPlayer>("Knight/AnimationPlayer");
		if (_animation != null)
		{
			// Animationerna är gjorda för att loopa, men importen markerar dem inte som loopar.
			foreach (StringName name in new[] { IdleAnimation, RunAnimation })
			{
				if (_animation.HasAnimation(name))
					_animation.GetAnimation(name).LoopMode = Animation.LoopModeEnum.Linear;
			}
			PlayAnimation(IdleAnimation);
		}

		_camera = GetViewport().GetCamera3D();
		if (_camera != null)
		{
			// Kameran tittar längs sin -Z, så vi backar den längs +Z.
			_cameraOffset = _camera.GlobalBasis.Z * CameraDistance;
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		bool pressed = Input.IsMouseButtonPressed(MouseButton.Right);
		if (pressed)
			HandlePointer(justPressed: !_wasPressed);
		_wasPressed = pressed;

		Vector3 velocity = Velocity;

		if (!IsOnFloor())
			velocity.Y -= _gravity * (float)delta;

		Vector3 direction = Vector3.Zero;
		if (_mode == MoveMode.Path)
		{
			direction = FollowPath();
		}
		else if (_mode == MoveMode.Direct && !_blocked)
		{
			Vector3 toTarget = Flat(_directTarget - GlobalPosition);
			if (toTarget.Length() <= ArriveDistance)
				_mode = MoveMode.Idle;
			else
				direction = toTarget.Normalized();
		}

		velocity.X = direction.X * Speed;
		velocity.Z = direction.Z * Speed;
		Velocity = velocity;

		if (direction != Vector3.Zero)
			LookAt(GlobalPosition + direction, Vector3.Up);

		Vector3 before = GlobalPosition;
		MoveAndSlide();

		// Animationen följer hur fort figuren faktiskt rör sig, inte bara om den vill röra sig.
		float flatSpeed = Flat(GlobalPosition - before).Length() / (float)delta;
		bool running = direction != Vector3.Zero && flatSpeed > RunAnimationMinSpeed;
		PlayAnimation(running ? RunAnimation : IdleAnimation);

		if (direction == Vector3.Zero)
		{
			_stuckTimer = 0;
			return;
		}

		float moved = Flat(GlobalPosition - before).Length();
		bool slow = moved < Speed * (float)delta * StuckSpeedFraction;
		_stuckTimer = slow ? _stuckTimer + (float)delta : 0;
		if (_stuckTimer < StuckTime)
			return;
		_stuckTimer = 0;

		if (_mode == MoveMode.Direct)
		{
			// Rak gång mot en vägg: vi har glidit så långt det går. Stanna.
			_blocked = true;
		}
		else if (_mode == MoveMode.Path)
		{
			// Fastnat på vägen (t.ex. i en dörrkarm): räkna om vägen från där vi står.
			if (_repaths >= MaxRepaths || !ComputePath(_pathTarget))
				_mode = MoveMode.Idle;
			else
				_repaths++;
		}
	}

	private void PlayAnimation(StringName name)
	{
		if (_animation == null || (string)_animation.CurrentAnimation == (string)name || !_animation.HasAnimation(name))
			return;
		_animation.Play(name, AnimationBlendTime);
	}

	// Riktningen mot nästa punkt i vägen. Avstånden mäts längs marken, så att höjdskillnaden
	// mellan spelarens fötter och navigationsytan inte spelar någon roll.
	private Vector3 FollowPath()
	{
		while (_pathIndex < _path.Length)
		{
			bool last = _pathIndex == _path.Length - 1;
			float reach = last ? ArriveDistance : WaypointReach;
			if (Flat(_path[_pathIndex] - GlobalPosition).Length() > reach)
				break;
			_pathIndex++;
		}

		if (_pathIndex >= _path.Length)
		{
			_mode = MoveMode.Idle;
			return Vector3.Zero;
		}

		return Flat(_path[_pathIndex] - GlobalPosition).Normalized();
	}

	// Räknar ut en ny väg från spelaren till målet. Falskt om det inte finns någon väg.
	private bool ComputePath(Vector3 target)
	{
		_pathTarget = target;
		_path = NavigationServer3D.MapGetPath(_agent.GetNavigationMap(), GlobalPosition, target, true);
		// Punkt 0 är närmaste punkt på navigationsytan. Står spelaren en bit utanför den
		// (t.ex. intryckt mot en karm) går vi först dit, annars hoppas den över direkt.
		_pathIndex = 0;
		return _path.Length >= 2;
	}

	public override void _Process(double delta)
	{
		if (_camera != null)
			_camera.GlobalPosition = GlobalPosition + _cameraOffset;
	}

	// ---------- Vart ska vi? ----------

	private void HandlePointer(bool justPressed)
	{
		if (!TryGetPointer(out Vector3 point, out bool onRoof))
			return;

		if (!justPressed)
		{
			// Håller inne: räkna bara om när pekaren flyttat sig, och strunta i pekare rakt på spelaren.
			if (Flat(point - _lastPointer).Length() < HoldUpdateDistance)
				return;
			if (Flat(point - GlobalPosition).Length() < HoldDeadZone)
				return;
		}
		else
		{
			_lastWasDetour = false;
		}
		_lastPointer = point;

		bool playerInside = IsUnderRoof(GlobalPosition);
		bool targetInHouse = onRoof || IsUnderRoof(point);

		if (playerInside)
		{
			// Inne: stor omväg (t.ex. ut genom dörren och runt huset) => spring rakt och stanna mot väggen.
			bool detour = IsDetour(point, _lastWasDetour, DetourFactor, DetourMargin);
			_lastWasDetour = detour;
			if (detour)
				GoDirect(point);
			else
				GoPath(point);
		}
		else if (targetInHouse)
		{
			// Ute och pekar in i ett hus: gå in genom dörren, om det inte är en orimlig omväg.
			bool detour = IsDetour(point, _lastWasDetour, EnterDetourFactor, EnterDetourMargin);
			_lastWasDetour = detour;
			if (!detour)
			{
				GoPath(point);
			}
			else
			{
				Vector3? outside = FindNearestOutsidePoint(point);
				if (outside.HasValue)
					GoPath(outside.Value);
			}
		}
		else
		{
			// Ute till ute: navigationen, även runt hus.
			_lastWasDetour = false;
			GoPath(point);
		}
	}

	private void GoPath(Vector3 target)
	{
		if (_mode == MoveMode.Path && target.DistanceTo(_pathTarget) <= 0.05f)
			return;

		_repaths = 0;
		_stuckTimer = 0;
		_mode = ComputePath(target) ? MoveMode.Path : MoveMode.Idle;
	}

	private void GoDirect(Vector3 target)
	{
		// Ny punkt en bit bort: försök igen även om vi stod mot väggen.
		if (_mode != MoveMode.Direct || Flat(target - _directTarget).Length() > 0.5f)
		{
			_blocked = false;
			_stuckTimer = 0;
		}
		_directTarget = target;
		_mode = MoveMode.Direct;
	}

	// Sant om navigationsvägen till punkten är en stor omväg jämfört med fågelvägen.
	private bool IsDetour(Vector3 target, bool alreadyDetour, float baseFactor, float baseMargin)
	{
		Rid map = _agent.GetNavigationMap();
		Vector3[] path = NavigationServer3D.MapGetPath(map, GlobalPosition, target, true);
		if (path.Length < 2)
			return true; // ingen väg alls

		float pathLength = 0;
		for (int i = 1; i < path.Length; i++)
			pathLength += Flat(path[i] - path[i - 1]).Length();

		float straight = Flat(target - GlobalPosition).Length();
		// Hysteres: har vi redan valt "omväg" krävs lite mindre för att fortsätta så.
		float factor = alreadyDetour ? baseFactor * (DetourKeepFactor / DetourFactor) : baseFactor;
		float margin = alreadyDetour ? baseMargin * (DetourKeepMargin / DetourMargin) : baseMargin;
		return pathLength > straight * factor + margin;
	}

	// Sant om det finns ett tak (ClickBlock) rakt ovanför, även om taket just nu är gömt.
	private bool IsUnderRoof(Vector3 point)
	{
		Vector3 from = point + Vector3.Up * 0.5f;
		var query = PhysicsRayQueryParameters3D.Create(from, from + Vector3.Up * 30.0f, ClickBlockLayer);
		query.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
		return GetWorld3D().DirectSpaceState.IntersectRay(query).Count > 0;
	}

	private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

	// Skjuter en stråle från kameran genom muspekaren och tar fram punkten på marken man pekar på.
	// Gömda och utonade objekt (t.ex. taket och väggarna när man är inne) hoppas över.
	// Träffar strålen ett tak tar vi i stället punkten på marken "under" pekaren (i spelarens höjd),
	// så att man kan peka in i ett hus genom taket, t.ex. precis innanför dörren.
	private bool TryGetPointer(out Vector3 point, out bool onRoof)
	{
		point = Vector3.Zero;
		onRoof = false;
		if (_camera == null)
			return false;

		Vector2 mouse = GetViewport().GetMousePosition();
		Vector3 from = _camera.ProjectRayOrigin(mouse);
		Vector3 direction = _camera.ProjectRayNormal(mouse);

		Godot.Collections.Dictionary hit = RaycastVisible(from, from + direction * RayLength, WorldLayer | ClickBlockLayer);
		if (hit == null)
			return false;

		if (hit["collider"].AsGodotObject() is CollisionObject3D body
			&& (body.CollisionLayer & ClickBlockLayer) != 0)
		{
			if (Mathf.Abs(direction.Y) < 0.001f)
				return false;
			float distance = (GlobalPosition.Y - from.Y) / direction.Y;
			if (distance < 0)
				return false;
			point = from + direction * distance;
			onRoof = true;
			return true;
		}

		point = hit["position"].AsVector3();
		return true;
	}

	private Vector3? FindNearestOutsidePoint(Vector3 ground)
	{
		Rid map = _agent.GetNavigationMap();

		for (float radius = 0; radius <= OutsideSearchRadius; radius += OutsideSearchStep)
		{
			int count = radius == 0 ? 1 : OutsideSearchDirections;
			for (int i = 0; i < count; i++)
			{
				float angle = Mathf.Tau * i / count;
				Vector3 candidate = ground + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius;

				Vector3 onMesh = NavigationServer3D.MapGetClosestPoint(map, candidate);
				Vector3 offset = onMesh - candidate;
				offset.Y = 0;
				if (offset.Length() > OutsideSearchStep)
					continue; // ingen gångbar yta här

				if (!IsUnderBlocker(onMesh))
					return onMesh;
			}
		}

		return null;
	}

	// Sant om det finns ett synligt tak (ClickBlock) rakt ovanför punkten.
	private bool IsUnderBlocker(Vector3 point)
	{
		Vector3 from = point + Vector3.Up * 0.5f;
		return RaycastVisible(from, from + Vector3.Up * 30.0f, ClickBlockLayer) != null;
	}

	// Första träffen som syns, eller null. Gömda och utonade objekt hoppas över.
	private Godot.Collections.Dictionary RaycastVisible(Vector3 from, Vector3 to, uint mask)
	{
		PhysicsDirectSpaceState3D space = GetWorld3D().DirectSpaceState;
		var exclude = new Godot.Collections.Array<Rid> { GetRid() };

		for (int i = 0; i < 16; i++)
		{
			var query = PhysicsRayQueryParameters3D.Create(from, to, mask);
			query.Exclude = exclude;

			Godot.Collections.Dictionary result = space.IntersectRay(query);
			if (result.Count == 0)
				return null;

			if (result["collider"].AsGodotObject() is Node3D node
				&& (!node.IsVisibleInTree() || IsSeeThrough(node)
					|| OcclusionManager.IsHidden(node, result["position"].AsVector3())))
			{
				exclude.Add(result["rid"].AsRid());
				continue;
			}

			return result;
		}

		return null;
	}

	// Sant om noden, eller något ovanför den, just nu är genomskinlig (se OcclusionManager.cs).
	private static bool IsSeeThrough(Node node)
	{
		for (Node current = node; current != null; current = current.GetParent())
		{
			if (current.IsInGroup(OcclusionManager.SeeThroughGroup))
				return true;
		}
		return false;
	}
}
