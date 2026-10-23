# Terrain route geometry regressions

Run `dotnet run --project tests/terrain_route/TerrainRouteTests.csproj`.
The Windows build runs this project automatically. It compiles the actual
`TerrainRoute`, `MovementHelper`, `FollowNavigation` and `GatherTargets` with a small tile API
fixture. Route execution advances velocity through swept AABB collision;
assertions check arrival, collision safety, bounded motion and unchanged terrain.

Coverage includes supported pond rims requiring jumps, delayed walking off a
wide ledge, landing alignment, off-grid approaches, partially filled shallow
water, both travel directions, submerged starts, follow, dangerous liquid and
unreachable walls. The original 46 navigation checks pass. That navigation suite compiled against the private
0.10.30 source snapshot passed 43 and failed 3 (rim jump, ledge drop, follow).
Use `-p:TerrainRouteSource=<absolute source path>` for a deliberate source
comparison; the default always compiles current production code.

Nine additional selection checks cover sealed veins, preferring exposed ore,
failed-target skipping, shared material protection, named-ore pick power and
actuation. Six failed against 0.10.31 selection; all nine now pass (55 total).
The tool-strength fixture isolates geometry; production ToolSelector thresholds
are exercised by native_replacement.

This fixture is not Terraria's native physics engine. It approximates wet gravity
and drag, and does not cover native slopes, half blocks, other mods or the exact
player world. Those require the player's gameplay acceptance. No game process,
world file, model call, resource consumption or automatic recall is used here.

Ore tunnel coverage additionally compiles the production plan/job. It clears
synthetic terrain, drives the actual movement controller down the staircase,
then drives it back out, both directions at three depths. This uses synthetic
AABB collision, not native Terraria slope physics. 74 total checks currently pass.
