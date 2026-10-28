import { copyFile, lstat, mkdir, readFile, realpath, rename, writeFile } from 'node:fs/promises'
import { constants } from 'node:fs'
import { join } from 'node:path'
import { execFile } from 'node:child_process'
import { promisify } from 'node:util'
import { XMLParser, XMLValidator } from 'fast-xml-parser'
import { loadEditorSession } from './bannerlord-editor-client'

const parser = new XMLParser({ ignoreAttributes: false })
const MAX_XML = 16 * 1024 * 1024
function parse(xml: string) {
  if (Buffer.byteLength(xml) > MAX_XML || /<!DOCTYPE|<!ENTITY/i.test(xml) || XMLValidator.validate(xml) !== true)
    throw new Error('Invalid or oversized terrain XML.')
  return parser.parse(xml)
}

// Keep bytes outside the base material and its plain seasonal references unchanged.
// The official template provides its complete textures, flags and parameters.
export function replaceTerrainBaseMaterial(sceneXml: string, libraryXml: string, materialName: string): string {
  if (!/^[A-Za-z][A-Za-z0-9_]{0,127}$/.test(materialName)) throw new Error('Invalid terrain material name.')
  const scene = parse(sceneXml)?.scene
  const terrain = scene?.terrain
  if (terrain?.['@_enabled'] !== 'true') throw new Error('The scene has no enabled terrain.')
  const layers = terrain.layers?.layer
  if (!layers || Array.isArray(layers) || !layers.summer)
    throw new Error('Whole-surface replacement requires one base layer.')
  for (const season of ['fall', 'winter', 'spring']) {
    const reference = layers[season]
    if (!reference) continue
    if (Array.isArray(reference) || reference['@_name'] !== layers.summer['@_name'] ||
        Object.keys(reference).some(key => key !== 'meshes' && !['@_name', '@_is_enabled', '@_is_flora_layer', '@_is_mesh_blend_layer'].includes(key)) ||
        (reference.meshes !== undefined && reference.meshes !== '') ||
        ['@_is_enabled', '@_is_flora_layer', '@_is_mesh_blend_layer'].some(key => reference[key] !== undefined && reference[key] !== layers.summer[key]))
      throw new Error('Seasonal terrain overrides must be preserved; whole-surface replacement refused.')
  }
  const templates = parse(libraryXml)?.terrain_materials?.terrain_material
  const matches = (Array.isArray(templates) ? templates : [templates]).filter(t => t?.['@_name'] === materialName)
  if (matches.length !== 1 || matches[0]['@_is_enabled'] !== 'true' || matches[0]['@_is_flora_layer'] !== 'false' || matches[0]['@_is_mesh_blend_layer'] !== 'false')
    throw new Error('Choose one enabled surface terrain material from the official library.')
  const raw = [...libraryXml.matchAll(/<terrain_material\b[^>]*>[\s\S]*?<\/terrain_material\s*>/g)]
    .filter(m => parse(m[0]).terrain_material?.['@_name'] === materialName)
  if (raw.length !== 1) throw new Error('Cannot isolate the official terrain material definition.')
  const replacement = raw[0][0].replace(/^<terrain_material\b/, '<summer').replace(/<\/terrain_material\s*>$/, '</summer>')
  let terrainCount = 0, replaced = 0
  const result = sceneXml.replace(/<terrain\b[^>]*>[\s\S]*?<\/terrain\s*>/g, block => {
    terrainCount++
    return block.replace(/<layers\b[^>]*>[\s\S]*?<\/layers\s*>/g, layerBlock =>
      layerBlock.replace(/<summer\b[^>]*>[\s\S]*?<\/summer\s*>/g, () => { replaced++; return replacement })
        .replace(/<(?:fall|winter|spring)\b[^>]*>/g, opening =>
          opening.replace(/(\sname\s*=\s*)(["'])[^"']*\2/, `$1"${materialName}"`)))
  })
  if (terrainCount !== 1 || replaced !== 1) throw new Error('Ambiguous scene terrain definition.')
  parse(result)
  return result
}

async function boundedXml(path: string) {
  const stat = await lstat(path)
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size > MAX_XML) throw new Error('Invalid terrain XML file.')
  return readFile(path, 'utf8')
}

export async function setEditorTerrainMaterial(materialName: string, sessionPath?: string) {
  const session = await loadEditorSession(sessionPath)
  await requireEditorStopped()
  const requireStopped = requireEditorStopped
  const sceneFile = join(session.sceneDir, 'scene.xscene')
  if ((await realpath(sceneFile)).toLowerCase() !== sceneFile.toLowerCase()) throw new Error('The scene XML must be a physical file.')
  const library = await boundedXml(join(session.gameDir, 'Modules', 'Native', 'ModuleData', 'terrain_materials.xml'))
  const original = await boundedXml(sceneFile)
  const updated = replaceTerrainBaseMaterial(original, library, materialName)
  const backupDir = join(session.outputDir, 'terrain-material-backup-' + crypto.randomUUID())
  await mkdir(backupDir)
  const backupFile = join(backupDir, 'scene.xscene')
  await copyFile(sceneFile, backupFile, constants.COPYFILE_EXCL)
  try {
    await requireStopped()
    if (await boundedXml(sceneFile) !== original) throw new Error('The saved scene changed before the material update.')
    const pending = join(session.sceneDir, 'scene-material-' + crypto.randomUUID() + '.tmp')
    await writeFile(pending, updated, { flag: 'wx' })
    await rename(pending, sceneFile)
    const saved = parse(await boundedXml(sceneFile)).scene.terrain.layers.layer.summer
    if (saved?.['@_name'] !== materialName) throw new Error('Saved terrain XML did not retain the material.')
    return { ok: true, result: { saved: true, requiresEditorReload: true, sceneName: session.sceneName, materialName, coverage: 'entire_surface', backupFile } }
  } catch (error: any) {
    return { ok: false, error: error.message, backupFile, partialChangesPossible: true, retryAutomatically: false }
  }
}

export async function requireEditorStopped() {
    if (process.platform !== 'win32') throw new Error('The offline terrain material operation requires Windows process verification.')
    try {
      await promisify(execFile)('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
        "if(Get-Process -Name Bannerlord,Bannerlord.Native,TaleWorlds.MountAndBlade.Launcher -ErrorAction SilentlyContinue){exit 2}"], { windowsHide: true, timeout: 10000 })
    } catch { throw new Error('Save and quit the editor through MCP before changing the saved terrain layer. No files were changed.') }
}
