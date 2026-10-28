# Editor MCP preview

**Experimental test version — PRs and issue reports are welcome.** This Windows
preview has been tested with an aligned Bannerlord game and Modding Kit
**v1.4.8**. It is a bounded scene-authoring bridge, not a complete map editor.

The TypeScript/Bun MCP client sends fixed JSON commands over a token-protected
loopback socket. An authored C# `MBSubModuleBase` module queues engine operations
onto `OnApplicationTick`. It references the installed editor assemblies and
calls native scene APIs; it does not replace/rebuild TaleWorlds DLLs, automate
mouse clicks or depend on the TPAC toolkit. The engine must be initialized and
rendering. This repository ships only source and blueprint asset names, never
game DLLs, meshes, textures or saved game scenes.

## Tools and scope

The normal BannerlordSage server uses the same client as the standalone CLI.
Default mode includes `bannerlord_editor_status`; full mode adds:

- `bannerlord_editor_entities`: managed IDs, prefabs, GUIDs and full transforms.
- `bannerlord_editor_prefab_info`: prefab existence and optional mesh bounds.
- `bannerlord_editor_apply_layout`: create/update up to 64 managed roots.
- `bannerlord_editor_save_scene`: save the active dedicated scene.
- `bannerlord_editor_capture`: return the current native PNG screenshot.
- `bannerlord_editor_module_creation_info`: inspect native map Mod creation support and runtime/game module paths.
- `bannerlord_editor_create_module`: create an asset/map Mod using the engine's `editor.create_new_module` command, which calls the same native creator as the Create New Module menu.
- `bannerlord_editor_save_scene_as`: save the official startup scene or bound scene into a user module through the native Save As function.
- `bannerlord_editor_terrain_info`: inspect terrain dimensions and resolution.
- `bannerlord_editor_terrain_flora_info`: read the running editor's vegetation bindings, density, scale, seeds, color and revision for every season.
- `bannerlord_editor_configure_terrain_flora`: add, remove or replace vegetation bindings and edit their parameters live, without closing the editor or reloading the scene. Native `save_scene` persists the result.
- `bannerlord_editor_generate_terrain`: generate flat terrain; omitted node size/resolution inherit current native values.
- `bannerlord_editor_set_terrain_material`: replace the material of an existing single-layer terrain with an official library template. It copies the complete template and updates plain seasonal references, preserving the rest of the scene XML. Refuses multiple layers or seasonal overrides.

For whole-surface material changes, save the scene and quit normally through MCP,
then call `bannerlord_editor_set_terrain_material`. The tool verifies that no game,
editor or launcher process is running, backs up the XML in the configured output
directory, and changes only the saved material definition. Restart the editor,
open the bound scene through MCP, inspect its native terrain data and viewport,
then save. Geometry and weightmap binaries are not edited. Do not use
`MBTestRun.CloseScene` for this workflow: on the tested v1.4.8 editor it left edit
mode and crashed while returning to the main menu. `MBTestRun.NewScene` has also
crashed; obtain new scenes from the genuine official startup state instead.

For a first map, ask whether the user already has a Mod workspace. If so, obtain
its absolute folder path; otherwise ask for the new Mod folder name. Example
names such as `newmod` are not defaults. Native map Mod creation is separate from
the C# `create_mod_workspace` scaffold. It needs a running editor in edit mode,
but no bound scene. It never opens, saves or replaces a scene. The module name
must be 1–64 ASCII letters, digits or underscores, starting with a letter;
Windows device names and existing target folders are rejected. The native
creator writes the asset folders, `ModuleData/project.mbproj` and `SubModule.xml`.
In an isolated runtime, the client verifies both module roots, moves the new
folder into the real game's Modules directory, and links it back with an NTFS
junction. No write is automatically retried; inspect partial output after a
failure. Creation alone does not activate the Mod or bind the bridge to its scenes.

Deploy the rebuilt bridge DLL before using the new tools; older running bridges
are rejected by a capability check. Status also reports `canEnterEditor` so
internal editor entry can wait for the initialized main menu.

Scene tools require the configured scene/module and do not open, replace,
reload or close scenes. Layout IDs are stable; updates keep GUIDs and reject
prefab conflicts. Positions are finite and bounded, scales positive, and the
whole batch is prevalidated. A runtime failure is not a transaction: inspect
`partial`, `appliedIds` and `executionMayBeRunning` before retrying. No write is
automatically retried. The transport rejects requests larger than 16,384
characters before sending; split large batches manually. Replies/session files
and screenshot reads are bounded, credentials are redacted, and real paths are
checked against the dedicated module/output folder.

## Local setup

Prerequisites: a legitimate local Bannerlord installation plus Modding Kit,
both v1.4.8; Bun; .NET SDK 8+; and the **.NET Framework 4.7.2 Developer Pack or
Targeting Pack**. The project targets x64/net472 and does not download reference
assemblies automatically. Normal BannerlordSage stdio startup also requires its
usual setup/index initialization; the standalone probe can run independently.

From the repository root:

```powershell
bun install --frozen-lockfile
dotnet build tools/editor-probe/EditorProbe.csproj -c Release '-p:GameDir=<REAL_GAME_DIR>'
bun run tools/editor-probe/probe.ts prepare '<REAL_GAME_DIR>'
```

`<REAL_GAME_DIR>` is your actual local installation. `prepare` creates an ignored
`dist/editor-probe/session.json` with a random local token; it does not deploy a
module, copy assets or start the game. Keep this file private.

1. Create `Modules/BannerlordSage.EditorProbe` under that installation. Copy the
   authored `SubModule.xml` there and the built `BannerlordSage.EditorProbe.dll`
   into **both** `bin/Win64_Shipping_Client` and `bin/Win64_Shipping_wEditor`.
   Do not deploy source or redistribute referenced game assemblies. Close your
   own old bridge instance before replacing its loaded DLL.
2. Make a fresh local scene copy from `Native/SceneObj/scn_new`. Copy only
   `scene.xscene`, `atmosphere.xml`, and `references.txt` into the probe module's
   `SceneObj/bannersage_editor_probe`. Never overwrite an existing authored
   scene. Use an XML parser to change the root scene name to
   `bannersage_editor_probe` and assign a fresh `unique_token`; retain
   `<terrain enabled="false"/>`. These copied game files stay local.
3. Read the session locally, set the bridge environment below, then launch the
   editor build with Native and the probe enabled. The ordinary game executable
   does not provide the required editor API. Leave the window visible.

```powershell
$repo = (Resolve-Path .).Path
$config = Get-Content (Join-Path $repo 'dist/editor-probe/session.json') -Raw | ConvertFrom-Json
$env:BANNERSAGE_EDITOR_PROBE_TOKEN = $config.token
$env:BANNERSAGE_EDITOR_PROBE_PORT = [string]$config.port
$env:BANNERSAGE_EDITOR_PROBE_SCENE_DIR = $config.sceneDir
$env:BANNERSAGE_EDITOR_PROBE_SCENE_NAME = 'bannersage_editor_probe'
$env:BANNERSAGE_EDITOR_PROBE_OUTPUT = $config.outputDir
$editorDir = Join-Path $config.gameDir 'bin/Win64_Shipping_wEditor'
Start-Process -FilePath (Join-Path $editorDir 'Bannerlord.exe') -ArgumentList '/singleplayer','_MODULES_*Native*BannerlordSage.EditorProbe*_MODULES_' -WorkingDirectory $editorDir -WindowStyle Normal
```

Do not print the configuration/token or commit it. When starting through an
isolated runtime (see below), change session.sceneDir to the matching module
junction alias before launching; the native engine's scene resolution and the
bridge's scope must agree.

Enter Editor through its UI, then use the internal CLI to open the dedicated
scene. Writes are guarded to that scene, while the startup placeholder is only
allowed for entry/open. Public tools deliberately do not expose lifecycle calls.

```powershell
bun run tools/editor-probe/probe.ts call status
bun run tools/editor-probe/probe.ts call open_scene
bun run tools/editor-probe/probe.ts mcp-call layout_entities
```

The editor scans physical module metadata even when a module is not enabled.
Unrecognized tags in unrelated community modules can cause modal RGL warnings.
For installations affected by this, use an **isolated local working root** under
`dist/editor-probe/runtime`: junction its editor binary directory, Data, GUI,
Shaders, Icons, modding_resources, XmlSchemas and XmlEditor to the real install;
junction only Native and BannerlordSage.EditorProbe under Modules. Launch from
the junction's `bin/Win64_Shipping_wEditor` as the working directory. Verify each
junction target before reuse; do not rewrite other module XML or copy all mods.
No generated runtime folders belong in Git.

## Main MCP configuration

After ordinary BannerlordSage initialization, choose its full entrypoint and
configure the optional local session in your MCP client:

```toml
[mcp_servers.bannerlordsage]
command = "bun"
args = ["run", "src/entrypoints/bannerlord-full-stdio.ts"]
cwd = "<REPO_DIR>"
enabled = true

[mcp_servers.bannerlordsage.env]
BANNERSAGE_EDITOR_PROBE_SESSION = "<ABSOLUTE_LOCAL_SESSION_JSON>"
```

Alternatively pass the optional absolute local `sessionPath` in each editor
tool call. With no configuration, status reports `not_configured`; existing
query tools continue to work. Reconnect the MCP service after changing its
environment/entrypoint. The editor may remain open. The live editor's installed
version is checked independently of the indexed source dataset.

The separate test surface `bun run tools/editor-probe/probe.ts mcp` exposes one
generic `editor_probe` tool, including internal lifecycle actions. Prefer the
eight concrete tools above for normal agent use. Both routes share
`src/utils/bannerlord-editor-client.ts`.

## Blueprint samples

`camp.blueprint.json` defines a 34-root merchant camp; `harbor.blueprint.json`
defines a 55-root dock with Native `ship_a` and `ship_e`. These are layout inputs,
not supplied game assets or playable maps. Prepare independent local scenes
`bannersage_agent_camp` / `bannersage_agent_harbor`, fresh tokens, matching
sceneDir/sceneName process environment and separate ignored session JSON files.
One running bridge is bound to one scene. For the harbor set the copied scene's
water_level=0/water_exists=true, remove the editor ground_plane and use an
outdoor atmosphere; locally copying Native `TOD_12_00_SemiCloudy.xml` and renaming
its atmosphere to `scene_atmosphere` worked in the validation.

```powershell
bun run tools/editor-probe/demo.ts validate tools/editor-probe/camp.blueprint.json
bun run tools/editor-probe/demo.ts validate tools/editor-probe/harbor.blueprint.json
$env:BANNERSAGE_EDITOR_PROBE_SESSION = '<ABSOLUTE_LOCAL_HARBOR_SESSION_JSON>'
bun run tools/editor-probe/demo.ts apply tools/editor-probe/harbor.blueprint.json
```

`demo.ts apply` checks prefabs, applies twice, saves/reloads, verifies GUIDs/full
matrices and takes two previews. **Save manual edits before running it.** Its
separate capture mode edits saved camera metadata and reloads; public
`bannerlord_editor_capture` only captures the current view. Direct SceneView
camera assignment was overwritten by the editor, so preset views use the
XML-aware `set-view.ps1` scoped to the two sample scenes and explicit GameDir.
Routine preview reloads do not need CloseScene or process exit.

`open-harbor.ps1` / `.cmd` are convenience entries for an **already prepared**
harbor session at `dist/editor-probe/harbor-demo/session.json` with the runtime
junctions described above. They are not fresh-clone installers. They preserve
an active harbor's unsaved edits, refuse replacing another existing scene/game,
and leave the editor open. All generated assets, credentials and evidence stay
in the user's local install or ignored dist folder.

## Validation and limitations

### Terrain height range guard

On the verified v1.4.8 native profile, the height texture writer converts
normalized heights to 16-bit integers without clamping. Editable node bounds can
exclude actual height samples after import or sculpting. Saving then wraps low
samples into spikes and high samples into pits, even though the editable float
heightmap remains smooth. A successful save or smooth collision samples alone
cannot detect this failure.

The probe expands editable node bounds to include every finite height sample,
rounding outwards with a small margin for scene XML precision. It refreshes the
native height textures when those bounds change and checks them before Save and
Save As. While the configured scene is open, a periodic guard also covers manual
editor saves. The guard preserves the height samples, validates the native binary
and scene owner, and disables itself on an ABI or scene validation error.
The private `terrain_repair_bounds` action reports the resulting encoding range.

Regression validation must compare the decoded saved `terrain.bin` HGHT images
against the editable float samples after repeated save/reopen cycles, as well as
inspect native screenshots. Do not treat local smoothing as a persistent fix for
this save-time overflow.

### Private landscape authoring actions

`terrain_add_layer` uses the native Add Layer backend with an official template
and an English label. `configure_terrain_layers` routes vegetation-only changes
to the live interface whenever the configured scene is open. Unsupported live
material/label changes fail without closing or restarting the editor. Offline
XML editing remains available only after verifying that the editor is stopped.
Its bounded JSON plan lives under bridge output; the Grassland layer may
bind official grass kinds and verified ground cover, including the shrubs,
leaves and stones from the treeless Battanian forest preset; tree kinds remain
rejected. Seed zero is supported to preserve official preset parameters.
`terrain_paint_plan` applies
bounded native material brush strokes from an output-directory JSON plan;
`terrain_layer_stats` reads weight coverage and flora counts. Neither operation
imports a new heightmap. Layout positions support the full 200 metre terrain;
layout rotation inputs are degrees.

### Live vegetation properties

The private probe also supports scoped reference studies. `bind_scene` can
switch from the currently bound authored scene with `saveCurrent:true`; it
uses the native synchronous save followed by the normal editor OpenScene
handler. The target must already be indexed and belong to a user module.
`discardReference:true` is limited to `bannersage_reference_*` work copies with
the `.bannersage-reference.json` marker, so it cannot discard an authored map.
After a successful binding, update the local session identity from the returned
directory and wait for the new scene status before editing. Do not automatically
retry an accepted scene-open request that subsequently fails.

For navigation inspection, the private `terrain_flora_info` diagnostic accepts
`includeNavigation:true`, reporting actual face counts and face ID distributions.
With `navigationVisible:true` it invokes only the verified editor navigation-mode
action; false selects the normal selection mode. Verify the actual overlay:
the toolbar QAction wrappers report unchecked even when navigation is visible.
These in-process Qt calls do not send desktop input. The companion references
TaleWorlds.DotNet as well as Engine and Library. A secondary Scene.Read preview
attempt stalled this application-tick configuration and was removed; reference
studies use normal editor opens of temporary copies instead.

Build `EditorProbe.csproj` and `LiveTerrainLayers.csproj` against the same local
`GameDir` and .NET Framework references. Deploy the main module normally and copy
`BannerlordSage.EditorProbe.Live.dll` from the companion build into the session's
`outputDir`. Installing the new main bridge requires one editor restart. The
fixed local companion is loaded from bytes and can subsequently be rebuilt in
place without restarting the editor. Requests cannot select an assembly path,
class, method or executable.

Read `bannerlord_editor_terrain_flora_info` first. Pass its `revision` as
`expectedRevision`, its layer names in `expectedLayers`, and the desired complete
flora lists for the selected layers/seasons to
`bannerlord_editor_configure_terrain_flora`. An empty list removes that season's
bindings. Other layers, heightmaps, material weights, prefabs and environment
settings are retained. A changed revision, invalid kind, out-of-range value or
duplicate row is rejected before mutation. Each applied plan produces a local
metadata backup; native errors must not be retried automatically.

The version-pinned implementation updates the active native material definitions
and runtime flora records on the inspector's Qt thread. It runs both procedural
generation and the renderer's final aggregation step, clearing old terrain draw
entries to avoid duplication while retaining free flora. Main scene files are
written only by the regular native save command. The private actions are
`terrain_flora_info`, `terrain_flora_plan` and `terrain_flora_refresh`.
The local v1.4.8 companion also repairs save-time square material-cache artifacts.
`terrain_flora_info {includeSurfaceState:true}` hashes the editable height and
weight buffers (at most sixteen nodes). `terrain_flora_refresh
{surfaceRefresh:true}` prechecks an entirely zero material-brush footprint in every
node and invokes the complete native brush/finalization path there, restoring UI
settings and proving raw-buffer equality. It refuses when no safe footprint exists.
The earlier direct texture/palette/delayed-refresh implementation failed visually
after expanding from eight to thirteen layers; a forced height dispatcher also
failed. Native history handling is delegated to the engine. The bound-scene save
guard watches the three scene/terrain save files on the UI thread, waits for a
one-second stable file signature, then performs that repair. The updated core
installs it automatically; an older running core can enable the companion through
`terrain_flora_refresh {surfaceSaveGuard:true}` without restarting. Inspect
`terrain_flora_info {includeSurfaceSaveGuard:true}` for refresh count/errors.
Three consecutive local thirteen-layer saves retained all 56 raw buffers and
correct ground blending. This is a version-pinned compatibility repair, not a general
engine serialization fix.

The private material diagnostic `terrain_flora_info
{includeSurfaceMaterials:true}` reads native blend/elevation/parallax/slope values.
`terrain_flora_plan` also accepts a bounded `surface_palette_v1` plan containing
`operation`, `expectedLayers` and `expectedFloraRevision`. Its fixed registered
Native Captain 008 profile supports `inspect`, `register` and `create`, appending
five reference templates/layers with no flora bindings. It uses the native XML
descriptor reader and map insertion functions without reloading the library;
template hash ownership persists across companion generations. `soften_existing`
is a separate narrow update for the example scene's named soil/path parameters.
All native ABI calls remain pinned to the examined v1.4.8 DLL fingerprint.
The private MCP material, height-plan and smoothing actions also refresh flora
after successful brush edits when this capability is installed. A refresh
failure reports that the brush was already applied and forbids automatic retry.

Each material stroke supports `hardness` (0..1), an optional official `pattern`
filename from the editor's loaded brush browser, `angle` (0..360 degrees), and
native modes 0 paint, 1 erase, 2 smooth weights, or 3 flatten to `target` (0..1).
The full plan is checked before painting. Patterned strokes sample the native
brush bitmap; they do not replace the terrain with a generated coverage image.
Brush/UI settings are restored after each stroke, and upper layers remain
available for overlap. The private `remove_layout` action takes 1..64 unique
`ids`, checks every selected managed root before removing any, and refuses
foreign or child entities. Save explicitly after landscape changes.

`terrain_height_brush_plan` is a separate private native elevation action. Its
bounded output-directory JSON contains explicit `x`, `y`, `radius`, `strength`
and `mode` per stroke: 1 lowers elevation, 2 smooths it. Radius is 1..8 metres,
strength 0.01..0.25, passes 1..4 and hardness 0..0.6. It rejects material-layer,
pattern and flatten-target fields before applying any stroke, and repairs
height encoding bounds after the batch. Calibrate and measure a small segment
before applying a longer elevation plan; material-paint plans cannot edit height.

The original local v1.4.8 validation covered prefab availability, idempotence,
native save/reopen, GUID/full-transform persistence and real MCP images for the
camp and harbor. This isolated public branch additionally checks the existing
GitHub toolset plus twelve editor tools (default 36/full 49), shared transport/schema
tests, C# build and release smoke. No unrelated local dataset/profile/features
are included in this preview.

```powershell
bun test src/utils/bannerlord-editor-client.test.ts
bun test src/utils/bannerlord-editor-registry.test.ts
bun test src/utils/bannerlord-editor-module.test.ts
bun test src/utils/bannerlord-editor-terrain-material.test.ts
bun run smoke:release
```

- Validated: aligned v1.4.8, dedicated module scenes and existing prefabs.
  Native entity enumeration also includes invisible engine helpers.
- Also tested locally: a 200 by 200 metre flat terrain generated from the
  official startup scene, native Save As into a user module, and replacing the
  single base layer with the official `soil_b` template while stopped. Native
  reload/capture/save confirmed the soil appearance and unchanged dimensions,
  resolution and elevation. This does not validate general terrain painting.
- Native map Mod creation has also been validated on v1.4.8 through the full
  MCP server, including real game directory placement, an isolated runtime
  junction, unchanged startup scene and duplicate-name rejection. This does
  not validate loading the generated Mod into a campaign or binding a new scene.
- **Not validated:** general terrain sculpting/painting, GI baking, headless rendering,
  playable Mission/player entry, navigation, deck collision traversal, sailing,
  custom TPAC/FBX ingestion, generic scene creation, undo and job cancellation.
  Ships in the sample are static props. The scope guard does not automatically
  reject every possible terrain configuration; use the tested no-terrain fixture.
- A native screenshot once returned a valid but black PNG; raising the existing
  editor window restored a rendered image. Renderer-state cause is not isolated.
  Keep the viewport rendering and inspect content; PNG validity/size/byte equality
  alone do not prove an informative capture. Public capture does not raise UI.
- Editor preview/helper graphics can remain visible; these are not final artwork.
  A stalled native action may outlive a client timeout. Set an MCP call timeout
  of at least 90 seconds if needed and inspect state before retrying writes.

Please contribute narrowly scoped PRs with real-engine validation and clear
version/claim boundaries. Terrain, Mission entry, navigation, reliable preview
diagnostics and ship physics are useful next areas.
