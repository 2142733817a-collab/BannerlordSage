import { copyFile, lstat, mkdir, readFile, rename, writeFile } from 'node:fs/promises'
import { isAbsolute, join, relative, resolve } from 'node:path'
import { XMLParser, XMLValidator } from 'fast-xml-parser'
import { editorStatus, loadEditorSession, requestEditor } from '../../src/utils/bannerlord-editor-client'
import { terrainFloraPlanSchema } from '../../src/utils/bannerlord-editor-flora'
import { requireEditorStopped } from '../../src/utils/bannerlord-editor-terrain-material'

const parser = new XMLParser({ ignoreAttributes: false })
const list = (value: any): any[] => value === undefined ? [] : Array.isArray(value) ? value : [value]
// Native uses is_grass as a rendering flag. Verified ground cover also includes
// the shrubs, leaves and stones in the treeless Battanian forest preset.
const grasslandGroundCover = new Set(['flora_terrain_drygrass', 'flora_dry_plant__mix_grass',
  'flora_plant__mix', 'flora_green__mix_flower', 'free_valley_flower__mix', 'shrub__mix',
  'wheat_group_summer', 'rock_new_green', 'flora_green__mix_big_leafs', 'ground_rocks_group'])
function parse(xml: string) {
  if (Buffer.byteLength(xml) > 2 * 1024 * 1024 || /<!DOCTYPE|<!ENTITY/i.test(xml) || XMLValidator.validate(xml) !== true)
    throw new Error('Invalid or oversized terrain layer XML.')
  return parser.parse(xml)
}

export function configureLayerXml(sceneXml: string, layersXml: string, expectedNames: string[], floraXml: string) {
  const scene = parse(sceneXml).scene
  const old = list(scene?.terrain?.layers?.layer)
  const next = list(parse(layersXml).layers?.layer)
  if (scene?.terrain?.['@_enabled'] !== 'true' || old.length !== expectedNames.length || next.length !== old.length || old.length < 2 || old.length > 15 ||
      old.some((x, i) => x['@_name'] !== expectedNames[i]))
    throw new Error('The existing native layer order does not match the configuration plan.')
  const kinds = new Map(list(parse(floraXml).flora_kinds?.flora_kind).map(x => [x['@_name'], x]))
  const names = next.map(x => x['@_name'])
  if (new Set(names).size !== names.length || names.some(n => typeof n !== 'string' || !/^[A-Za-z][A-Za-z0-9 _-]{0,63}$/.test(n)))
    throw new Error('Terrain layer names must be unique English labels.')
  for (const layer of next) {
    if (!layer.summer || layer.summer['@_is_enabled'] !== 'true') throw new Error('Each layer requires an enabled summer definition.')
    for (const season of ['summer', 'fall', 'winter', 'spring']) {
      for (const mesh of list(layer[season]?.meshes?.mesh)) {
        const kind: any = kinds.get(mesh['@_name'])
        if (!kind) throw new Error('Layer references an unknown official flora kind.')
        if (layer['@_name'] === 'Grassland' && !grasslandGroundCover.has(mesh['@_name']) &&
            !list(kind.flags?.flag).some(f => f['@_name'] === 'is_grass' && f['@_value'] === 'true'))
          throw new Error('Grassland may bind verified non-tree ground cover only.')
        const density = Number(mesh['@_density'])
        if (!Number.isFinite(density) || density < 0 || density > 60) throw new Error('Flora density must be between 0 and 60.')
      }
    }
  }
  let replaced = 0
  const result = sceneXml.replace(/<terrain\b[^>]*>[\s\S]*?<\/terrain\s*>/g, terrain =>
    terrain.replace(/<layers\b[^>]*>[\s\S]*?<\/layers\s*>/g, () => { replaced++; return layersXml }))
  if (replaced !== 1) throw new Error('Ambiguous scene layer configuration.')
  parse(result)
  return result
}

async function bounded(path: string, maximum = 2 * 1024 * 1024) {
  const stat = await lstat(path)
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > maximum) throw new Error('Invalid configuration file.')
  return readFile(path, 'utf8')
}

function canonical(value: any): string {
  if (Array.isArray(value)) return '[' + value.map(canonical).join(',') + ']'
  if (value && typeof value === 'object') return '{' + Object.keys(value).sort().map(k => JSON.stringify(k) + ':' + canonical(value[k])).join(',') + '}'
  return JSON.stringify(value)
}

export function liveFloraPlan(sceneXml: string, layersXml: string, expectedNames: string[], floraXml: string, current: any) {
  configureLayerXml(sceneXml, layersXml, expectedNames, floraXml)
  const before = parse(sceneXml).scene.terrain.layers, after = parse(layersXml).layers
  const stripFlora = (value: any) => {
    const copy = structuredClone(value)
    for (const layer of list(copy.layer)) for (const season of ['summer', 'fall', 'winter', 'spring'])
      if (layer[season] && typeof layer[season] === 'object') delete layer[season].meshes
    return copy
  }
  if (canonical(stripFlora(before)) !== canonical(stripFlora(after)))
    throw new Error('This live plan changes surface materials, flags or layer labels. The live flora interface only edits vegetation; it will not close the editor or overwrite active scene files.')
  if (!Array.isArray(current?.layers) || current.layers.map((x: any) => x.name).join('\0') !== expectedNames.join('\0'))
    throw new Error('The active native layer order changed.')
  const triple = (text: string) => text.split(',').map(x => Number(x.trim()))
  const fields: Record<string, string> = { density: 'density', seed_index: 'seedIndex', colony_radius: 'colonyRadius',
    colony_threshold: 'colonyThreshold', weight_offset: 'weightOffset' }
  const vectors: Record<string, string> = { size_min: 'sizeMin', size_max: 'sizeMax', albedo_multiplier: 'albedoMultiplier' }
  const layers = list(after.layer).map(layer => {
    const seasons: Record<string, any[]> = {}
    for (const season of ['summer', 'fall', 'winter', 'spring']) {
      if (!layer[season]) continue
      if (layer[season].meshes === undefined) {
        seasons[season] = current.layers.find((x: any) => x.name === layer['@_name']).seasons[season]
        continue
      }
      seasons[season] = list(layer[season].meshes?.mesh).map(mesh => {
        const allowed = ['name', 'index', ...Object.keys(fields), ...Object.keys(vectors)].map(x => '@_' + x)
        if (Object.keys(mesh).some(k => !allowed.includes(k))) throw new Error('Unsupported vegetation XML field; nothing was modified.')
        const value: Record<string, unknown> = { name: mesh['@_name'] }
        for (const [xml, json] of Object.entries(fields)) if (mesh['@_' + xml] !== undefined) value[json] = Number(mesh['@_' + xml])
        for (const [xml, json] of Object.entries(vectors)) if (mesh['@_' + xml] !== undefined) value[json] = triple(String(mesh['@_' + xml]))
        return value
      })
    }
    return { layer: layer['@_name'], seasons }
  })
  return terrainFloraPlanSchema.parse({ expectedLayers: expectedNames, expectedRevision: current.revision, layers })
}

export async function configureTerrainLayers(planPath: string, sessionPath: string) {
  const session = await loadEditorSession(sessionPath)
  const planFile = resolve(planPath), within = relative(session.outputDir, planFile)
  if (within.startsWith('..') || isAbsolute(within) || !planFile.endsWith('.json')) throw new Error('Layer plan must stay within the bridge output directory.')
  const plan = JSON.parse(await bounded(planFile, 262144))
  if (!Array.isArray(plan.expectedLayers) || !plan.expectedLayers.every((x: unknown) => typeof x === 'string') || typeof plan.layersXml !== 'string')
    throw new Error('Layer plan requires the expected native layer order and replacement definitions.')
  const sceneFile = join(session.sceneDir, 'scene.xscene')
  const original = await bounded(sceneFile)
  const flora = await bounded(join(session.gameDir, 'Modules', 'Native', 'ModuleData', 'flora_kinds.xml'), 16 * 1024 * 1024)
  const updated = configureLayerXml(original, plan.layersXml, plan.expectedLayers, flora)
  const status = await editorStatus(sessionPath)
  if (status.connected) {
    if (!status.activeSceneMatches) throw new Error('The configured scene must be active for live flora editing.')
    if (!status.result?.bridgeCapabilities?.includes('live_terrain_flora_v1'))
      throw new Error('The installed bridge does not support live flora yet. Install the live-layer companion; do not restart for each ordinary edit.')
    const current = await requestEditor('terrain_flora_info', {}, sessionPath)
    if (!current.ok) return current
    const live = liveFloraPlan(original, plan.layersXml, plan.expectedLayers, flora, current.result)
    const path = join(session.outputDir, 'live-flora-plan-' + crypto.randomUUID() + '.json')
    await writeFile(path, JSON.stringify(live), { flag: 'wx' })
    const reply = await requestEditor('terrain_flora_plan', { planPath: path }, sessionPath)
    if (!reply.ok) { reply.retryAutomatically = false; reply.partialChangesPossible = true }
    return reply
  }
  // Offline authoring remains available only after process verification. A
  // disconnected or loading editor is never treated as permission to edit files.
  await requireEditorStopped()
  const backupDirectory = join(session.outputDir, 'layer-configuration-backup-' + crypto.randomUUID())
  await mkdir(backupDirectory)
  await copyFile(sceneFile, join(backupDirectory, 'scene.xscene'))
  await requireEditorStopped()
  if (await bounded(sceneFile) !== original) throw new Error('Saved scene changed while preparing layer definitions.')
  const pending = join(session.sceneDir, 'scene-layers-' + crypto.randomUUID() + '.tmp')
  await writeFile(pending, updated, { flag: 'wx' })
  await rename(pending, sceneFile)
  const layers = list(parse(updated).scene.terrain.layers.layer)
  return { ok: true, result: { configured: true, requiresEditorReload: true, backupDirectory,
    layers: layers.map(x => ({ name: x['@_name'], flora: list(x.summer?.meshes?.mesh).map(m => m['@_name']) })) } }
}
