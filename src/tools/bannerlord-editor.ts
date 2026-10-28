import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js'
import { z } from 'zod'
import { editorStatus, editorToolResult } from '../utils/bannerlord-editor-client'
import { createNativeModule, moduleCreationInfo } from '../utils/bannerlord-editor-module'
import { saveEditorSceneAs } from '../utils/bannerlord-editor-scene'
import { setEditorTerrainMaterial } from '../utils/bannerlord-editor-terrain-material'
import { resetEditorTerrain } from '../utils/bannerlord-editor-terrain-reset'
import { applyTerrainFlora, terrainFloraPlanSchema } from '../utils/bannerlord-editor-flora'

const sessionPath = z.string().min(1).max(4096).optional().describe('Optional absolute path to the local editor bridge session JSON; defaults to BANNERSAGE_EDITOR_PROBE_SESSION.')
export const bannerlordEditorLayoutEntitySchema = z.object({
  id: z.string().regex(/^[a-zA-Z][a-zA-Z0-9_-]{0,63}$/),
  prefab: z.string().min(1).max(128),
  x: z.number().finite().min(-200).max(200),
  y: z.number().finite().min(-200).max(200),
  z: z.number().finite().min(-100).max(100),
  rx: z.number().finite().min(-3600).max(3600).optional(),
  ry: z.number().finite().min(-3600).max(3600).optional(),
  rz: z.number().finite().min(-3600).max(3600).optional(),
  sx: z.number().finite().min(0.05).max(20).optional(),
  sy: z.number().finite().min(0.05).max(20).optional(),
  sz: z.number().finite().min(0.05).max(20).optional(),
}).strict()
export const bannerlordEditorLayoutSchema = z.array(bannerlordEditorLayoutEntitySchema).min(1).max(64).superRefine((entities, ctx) => {
  if (new Set(entities.map(entity => entity.id)).size !== entities.length)
    ctx.addIssue({ code: 'custom', message: 'Layout entity IDs must be unique.' })
})

export const bannerlordEditorTerrainSchema = z.object({
  nodesX: z.number().int().min(1).max(16),
  nodesY: z.number().int().min(1).max(16),
  nodeSize: z.number().int().min(2).max(1000).optional().describe('Override the official node size only when requested; otherwise keep the current native value.'),
  nodeDimension: z.union([z.literal(32), z.literal(64), z.literal(128), z.literal(256)]).optional().describe('Override node resolution only when requested; otherwise keep the current native value.'),
  layerTemplate: z.string().regex(/^[A-Za-z][A-Za-z0-9_]{0,127}$/).optional().describe('Official terrain layer template, such as soil_b; omitted uses default.'),
}).strict().refine(p => p.nodeDimension === undefined || p.nodesX * p.nodesY * (p.nodeDimension + 1) ** 2 <= 4000000, 'Terrain exceeds the vertex budget.')

export function registerBannerlordEditorTools(server: McpServer, mode: 'query-first' | 'full') {
  server.registerTool('bannerlord_editor_status', {
    title: 'Bannerlord Editor Status',
    description: 'Inspect the configured local editor bridge, version alignment and active scene. Returns a diagnostic when the bridge is unconfigured or disconnected.',
    inputSchema: { sessionPath }, annotations: { readOnlyHint: true, openWorldHint: false },
  }, async ({ sessionPath }) => ({ content: [{ type: 'text' as const, text: JSON.stringify(await editorStatus(sessionPath)) }] }))
  if (mode !== 'full') return
  const moduleResult = async (call: () => Promise<unknown>) => {
    try { return { content: [{ type: 'text' as const, text: JSON.stringify(await call()) }] } }
    catch (error: any) { return { isError: true, content: [{ type: 'text' as const, text: JSON.stringify({ ok: false, error: error.message, retryAutomatically: false }) }] } }
  }
  server.registerTool('bannerlord_editor_module_creation_info', {
    title: 'Native Editor Module Creation Info',
    description: 'Inspect native map module creation support and the editor-runtime and real game Modules paths. Does not open or create a scene.',
    inputSchema: { sessionPath }, annotations: { readOnlyHint: true, openWorldHint: false },
  }, async ({ sessionPath }) => moduleResult(() => moduleCreationInfo(sessionPath)))
  server.registerTool('bannerlord_editor_create_module', {
    title: 'Create Native Editor Map Module',
    description: 'Create an asset/map Mod using editor.create_new_module, the native function behind Create New Module. Place it in the real game Modules folder and link isolated editor runtimes. Refuses existing folders. Does not open, create or save a scene. Distinct from the C# scaffold create_mod_workspace.',
    inputSchema: { moduleName: z.string().regex(/^[A-Za-z][A-Za-z0-9_]{0,63}$/), sessionPath },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  }, async ({ moduleName, sessionPath }) => moduleResult(() => createNativeModule(moduleName, sessionPath)))
  server.registerTool('bannerlord_editor_save_scene_as', {
    title: 'Native Editor Save Scene As',
    description: 'Save the official default scene or the configured authored scene into an existing user module through the native Save As function. Preserves scene contents and environment. Existing destinations require explicit overwriteExisting and are backed up to the bridge output folder first.',
    inputSchema: { moduleName: z.string().regex(/^[A-Za-z][A-Za-z0-9_]{0,63}$/), sceneName: z.string().regex(/^[A-Za-z][A-Za-z0-9_]{0,79}$/), overwriteExisting: z.boolean().optional().default(false), sessionPath },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  }, async ({ moduleName, sceneName, overwriteExisting, sessionPath }) => moduleResult(() => saveEditorSceneAs(moduleName, sceneName, overwriteExisting, sessionPath)))
  server.registerTool('bannerlord_editor_terrain_info', {
    title: 'Editor Terrain Info',
    description: 'Read the active configured scene terrain size, node counts and resolution, and inspect native terrain generation support.',
    inputSchema: { sessionPath, useDefaultScene: z.boolean().optional().default(false) }, annotations: { readOnlyHint: true, openWorldHint: false },
  }, async ({ sessionPath, useDefaultScene }) => editorToolResult('terrain_info', { useDefaultScene }, sessionPath, !useDefaultScene))
  server.registerTool('bannerlord_editor_terrain_flora_info', {
    title: 'Read Live Terrain Flora',
    description: 'Read active terrain layers, all seasonal vegetation bindings, density, scale, seed and color, plus an optimistic revision and editor process ID. Reads live memory without saving, reloading or restarting.',
    inputSchema: { sessionPath }, annotations: { readOnlyHint: true, openWorldHint: false },
  }, async ({ sessionPath }) => editorToolResult('terrain_flora_info', {}, sessionPath, true))
  server.registerTool('bannerlord_editor_configure_terrain_flora', {
    title: 'Edit Live Terrain Flora',
    description: 'Replace the selected layers seasonal flora lists in the running editor: add, remove or replace kinds, change density, seed, scale and color. Read terrain_flora_info first and pass its revision and layer order. Empty lists remove seasonal bindings. All edits are validated before mutation, backed up and refreshed through the native editor renderer. No scene reload or restart; save_scene persists the result. Never retry automatically after failure.',
    inputSchema: { parameters: terrainFloraPlanSchema, sessionPath },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  }, async ({ parameters, sessionPath }) => {
    try { return await applyTerrainFlora(parameters, sessionPath) }
    catch (error: any) { return { isError: true, content: [{ type: 'text' as const, text: JSON.stringify({ ok: false, error: error.message, retryAutomatically: false }) }] } }
  })
  server.registerTool('bannerlord_editor_generate_terrain', {
    title: 'Generate Editor Terrain',
    description: 'Generate flat terrain through the native Generate Terrain handler in the active configured scene. width=nodesX*nodeSize, length=nodesY*nodeSize in metres; 2 by 2 nodes of size 100 creates a 200 by 200 metre horizontal surface. Refuses existing terrain and unsupported native binary profiles. Changes memory; save_scene persists the result. Never retry automatically after a failure.',
    inputSchema: { parameters: bannerlordEditorTerrainSchema, useDefaultScene: z.boolean().optional().default(false), sessionPath },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  }, async ({ parameters, sessionPath, useDefaultScene }) => editorToolResult('generate_terrain', { ...parameters, useDefaultScene }, sessionPath, !useDefaultScene))
  server.registerTool('bannerlord_editor_set_terrain_material', {
    title: 'Set Whole Terrain Surface Material',
    description: 'Apply an official terrain library material to the configured saved single-layer terrain while the editor is stopped. First save and quit through MCP; Windows process verification refuses live editors. Backs up XML and replaces only the base material and plain seasonal references. Reopen through MCP to verify rendering. Refuses multiple layers or seasonal overrides. Never retry automatically after failure.',
    inputSchema: { materialName: z.string().regex(/^[A-Za-z][A-Za-z0-9_]{0,127}$/), sessionPath },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  }, async ({ materialName, sessionPath }) => {
    try {
      const reply = await setEditorTerrainMaterial(materialName, sessionPath)
      return { isError: !reply.ok, content: [{ type: 'text' as const, text: JSON.stringify(reply) }] }
    } catch (error: any) { return { isError: true, content: [{ type: 'text' as const, text: JSON.stringify({ ok: false, error: error.message, retryAutomatically: false }) }] } }
  })
  server.registerTool('bannerlord_editor_reset_terrain', {
    title: 'Remove Saved Terrain With Backup',
    description: 'Remove only terrain from the configured saved scene while the editor is stopped. Requires explicit removeExisting=true. Backs up all scene and terrain edit data before archiving terrain binaries and disabling the saved terrain definition; preserves other scene settings and entities. Reopen through MCP and generate new terrain afterwards.',
    inputSchema: { removeExisting: z.literal(true), sessionPath },
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
  }, async ({ sessionPath }) => {
    try { const reply = await resetEditorTerrain(sessionPath); return { isError: !reply.ok, content: [{ type: 'text' as const, text: JSON.stringify(reply) }] } }
    catch (error: any) { return { isError: true, content: [{ type: 'text' as const, text: JSON.stringify({ ok: false, error: error.message, retryAutomatically: false }) }] } }
  })
  server.registerTool('bannerlord_editor_import_heightmap', {
    title: 'Import Terrain Grayscale Heightmap',
    description: 'Import a 16-bit single-channel grayscale PNG through the native terrain heightmap backend across all existing terrain nodes. File must be in the configured bridge output directory. Gray 0 maps to heightOffset; gray 65535 maps to heightOffset+heightScale metres. Changes memory; save_scene persists. Never retry automatically after failure.',
    inputSchema: { heightmapPath: z.string().min(1).max(4096), heightScale: z.number().finite().positive().max(1000), heightOffset: z.number().finite().min(-1000).max(1000).optional().default(0), sessionPath },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false, openWorldHint: false },
  }, async ({ sessionPath, ...parameters }) => editorToolResult('import_heightmap', parameters, sessionPath, true))
  server.registerTool('bannerlord_editor_entities', {
    title: 'Bannerlord Editor Managed Entities',
    description: 'Read the managed layout objects in the active configured scene, including stable IDs, prefabs, GUIDs and transforms.',
    inputSchema: { sessionPath }, annotations: { readOnlyHint: true, openWorldHint: false },
  }, async ({ sessionPath }) => editorToolResult('layout_entities', {}, sessionPath, true))
  server.registerTool('bannerlord_editor_prefab_info', {
    title: 'Bannerlord Editor Prefab Info',
    description: 'Check a Native prefab in the active configured scene and read mesh bounds when available. Does not instantiate objects.',
    inputSchema: { sessionPath, prefab: z.string().min(1).max(128), mesh: z.string().min(1).max(128).optional() },
    annotations: { readOnlyHint: true, openWorldHint: false },
  }, async ({ sessionPath, prefab, mesh }) => editorToolResult('prefab_info', { prefab, ...(mesh ? { mesh } : {}) }, sessionPath, true))
  server.registerTool('bannerlord_editor_apply_layout', {
    title: 'Bannerlord Editor Apply Layout',
    description: 'Create or update up to 64 managed prefab roots in the active configured scene. Repeated IDs preserve objects and GUIDs; prefab conflicts are rejected. This changes the scene in memory; call save_scene explicitly to persist it.',
    inputSchema: { sessionPath, entities: bannerlordEditorLayoutSchema },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  }, async ({ sessionPath, entities }) => editorToolResult('apply_layout', { entities }, sessionPath, true))
  server.registerTool('bannerlord_editor_save_scene', {
    title: 'Bannerlord Editor Save Scene',
    description: 'Save the active configured dedicated scene. Does not close, replace or reload the scene.',
    inputSchema: { sessionPath }, annotations: { readOnlyHint: false, idempotentHint: true, openWorldHint: false },
  }, async ({ sessionPath }) => editorToolResult('save_scene', {}, sessionPath, true))
  server.registerTool('bannerlord_editor_capture', {
    title: 'Bannerlord Editor Capture',
    description: 'Return a bounded screenshot preview (PNG or JPEG, at most 192 KiB) plus metadata and the original full-resolution PNG path. Captures the current view without moving the camera, changing scene metadata or reloading. Use the inline preview for routine inspection; reading every original PNG back into chat defeats the preview size limit.',
    inputSchema: { sessionPath }, annotations: { readOnlyHint: true, openWorldHint: false },
  }, async ({ sessionPath }) => editorToolResult('capture', {}, sessionPath, true))
}
