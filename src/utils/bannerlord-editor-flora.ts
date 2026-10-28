import { writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { z } from 'zod'
import { editorStatus, editorToolResult, loadEditorSession, requestEditor, type EditorAction } from './bannerlord-editor-client'

const label = z.string().regex(/^[A-Za-z][A-Za-z0-9 _-]{0,63}$/)
const vector = (minimum: number, maximum: number) => z.tuple([
  z.number().finite().min(minimum).max(maximum), z.number().finite().min(minimum).max(maximum), z.number().finite().min(minimum).max(maximum),
])
export const terrainFloraBindingSchema = z.object({
  name: z.string().regex(/^[A-Za-z][A-Za-z0-9_]{0,127}$/),
  density: z.number().finite().min(0).max(60),
  seedIndex: z.number().int().min(0).max(1000000).optional(),
  colonyRadius: z.number().finite().min(0).max(200).optional(),
  colonyThreshold: z.number().finite().min(0).max(1).optional(),
  weightOffset: z.number().finite().min(0).max(1).optional(),
  sizeMin: vector(0.05, 5).optional(), sizeMax: vector(0.05, 5).optional(),
  albedoMultiplier: vector(0, 4).optional(),
}).strict().superRefine((value, ctx) => {
  const low = value.sizeMin ?? [1, 1, 1], high = value.sizeMax ?? [1, 1, 1]
  if (low.some((v, i) => v > high[i])) ctx.addIssue({ code: 'custom', message: 'Minimum flora size exceeds maximum size.' })
})
const bindings = z.array(terrainFloraBindingSchema).max(64).superRefine((values, ctx) => {
  if (new Set(values.map(x => x.name)).size !== values.length) ctx.addIssue({ code: 'custom', message: 'Flora kinds must be unique within each seasonal list.' })
})
const seasons = z.object({ summer: bindings.optional(), fall: bindings.optional(), winter: bindings.optional(), spring: bindings.optional() })
  .strict().refine(value => Object.keys(value).length > 0, 'Choose at least one season.')
export const terrainFloraPlanSchema = z.object({
  expectedRevision: z.string().regex(/^[A-Fa-f0-9]{64}$/),
  expectedLayers: z.array(label).min(1).max(15),
  layers: z.array(z.object({ layer: label, seasons }).strict()).min(1).max(15),
}).strict().superRefine((value, ctx) => {
  if (new Set(value.expectedLayers).size !== value.expectedLayers.length || new Set(value.layers.map(x => x.layer)).size !== value.layers.length)
    ctx.addIssue({ code: 'custom', message: 'Terrain layer names must be unique.' })
  if (value.layers.some(x => !value.expectedLayers.includes(x.layer))) ctx.addIssue({ code: 'custom', message: 'Flora edit references a layer outside the expected order.' })
})

export async function applyTerrainFlora(parameters: z.input<typeof terrainFloraPlanSchema>, sessionPath?: string) {
  const plan = terrainFloraPlanSchema.parse(parameters), session = await loadEditorSession(sessionPath)
  const json = JSON.stringify(plan)
  if (Buffer.byteLength(json) > 262144) throw new Error('Flora plan exceeds the native bridge size limit.')
  const path = join(session.outputDir, 'live-flora-plan-' + crypto.randomUUID() + '.json')
  await writeFile(path, json, { flag: 'wx' })
  return editorToolResult('terrain_flora_plan', { planPath: path }, sessionPath, true)
}

export async function terrainBrushWithFloraRefresh(action: EditorAction, args: Record<string, unknown>, sessionPath?: string) {
  if (!['terrain_paint_plan', 'terrain_height_brush_plan', 'terrain_smooth_brush'].includes(action)) throw new Error('Unsupported terrain brush action.')
  const result = await editorToolResult(action, args, sessionPath, true)
  const first = result.content.find(x => x.type === 'text')
  if (!first || first.type !== 'text') return result
  const reply = JSON.parse(first.text)
  if (!reply.ok) return result
  try {
    const status = await editorStatus(sessionPath)
    if (!status.ok || !status.connected) throw new Error('Brush applied, but the editor connection was lost before flora refresh.')
    if (!status.result?.bridgeCapabilities?.includes('live_terrain_flora_v1')) return result
    const refresh = await requestEditor('terrain_flora_refresh', {}, sessionPath)
    if (!refresh.ok) throw new Error(refresh.error ?? 'Native flora refresh failed.')
    reply.result.floraRefreshComplete = true
    reply.result.processId = refresh.result.processId
    return { content: [{ type: 'text' as const, text: JSON.stringify(reply) }], isError: false }
  } catch (error: any) {
    return { isError: true, content: [{ type: 'text' as const, text: JSON.stringify({ ok: false, error: error.message,
      brushApplied: true, partialChangesPossible: true, retryAutomatically: false }) }] }
  }
}
