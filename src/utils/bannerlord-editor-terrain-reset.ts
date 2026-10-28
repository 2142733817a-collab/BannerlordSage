import { cp, lstat, mkdir, readFile, realpath, rename, writeFile } from 'node:fs/promises'
import { join, relative, sep, isAbsolute } from 'node:path'
import { XMLParser, XMLValidator } from 'fast-xml-parser'
import { loadEditorSession } from './bannerlord-editor-client'
import { requireEditorStopped } from './bannerlord-editor-terrain-material'

export function removeSceneTerrainDefinition(xml: string) {
  if (Buffer.byteLength(xml) > 16 * 1024 * 1024 || /<!DOCTYPE|<!ENTITY/i.test(xml) || XMLValidator.validate(xml) !== true)
    throw new Error('Invalid or oversized scene XML.')
  const terrain = new XMLParser({ ignoreAttributes: false }).parse(xml)?.scene?.terrain
  if (!terrain || Array.isArray(terrain) || terrain['@_enabled'] !== 'true') throw new Error('The saved scene has no single enabled terrain.')
  let count = 0
  const result = xml.replace(/<terrain\b[^>]*>[\s\S]*?<\/terrain\s*>/g, () => { count++; return '<terrain enabled="false"/>' })
  if (count !== 1 || XMLValidator.validate(result) !== true) throw new Error('Ambiguous terrain XML.')
  return result
}

export async function resetEditorTerrain(sessionPath?: string) {
  const session = await loadEditorSession(sessionPath)
  await requireEditorStopped()
  const sceneFile = join(session.sceneDir, 'scene.xscene')
  if ((await realpath(sceneFile)).toLowerCase() !== sceneFile.toLowerCase() || (await lstat(sceneFile)).size > 16 * 1024 * 1024)
    throw new Error('The scene XML must be a bounded physical file.')
  const original = await readFile(sceneFile, 'utf8')
  const updated = removeSceneTerrainDefinition(original)
  const editDir = join(session.moduleDir, 'SceneEditData', session.sceneName)
  const inside = relative(session.moduleDir, editDir)
  if (!inside || isAbsolute(inside) || inside === '..' || inside.startsWith('..' + sep)) throw new Error('Edit data escaped the configured module.')
  let editExists = false
  try {
    const stat = await lstat(editDir)
    if (!stat.isDirectory() || stat.isSymbolicLink() || (await realpath(editDir)).toLowerCase() !== editDir.toLowerCase())
      throw new Error('Scene edit data must be a physical directory inside the configured module.')
    editExists = true
  } catch (error: any) { if (error.code !== 'ENOENT') throw error }
  const binary = join(session.sceneDir, 'terrain.bin')
  const binStat = await lstat(binary)
  if (!binStat.isFile() || binStat.isSymbolicLink()) throw new Error('Terrain binary must be a physical file.')
  const backupDirectory = join(session.outputDir, 'terrain-reset-backup-' + crypto.randomUUID())
  await mkdir(backupDirectory)
  await cp(session.sceneDir, join(backupDirectory, 'SceneObj'), { recursive: true, errorOnExist: true, force: false })
  if (editExists) await cp(editDir, join(backupDirectory, 'SceneEditData'), { recursive: true, errorOnExist: true, force: false })
  try {
    await requireEditorStopped()
    if (await readFile(sceneFile, 'utf8') !== original) throw new Error('The scene changed before terrain removal.')
    const pending = join(session.sceneDir, 'scene-reset-' + crypto.randomUUID() + '.tmp')
    await writeFile(pending, updated, { flag: 'wx' })
    await rename(pending, sceneFile)
    await rename(binary, join(backupDirectory, 'removed-terrain.bin'))
    if (editExists) await rename(editDir, join(backupDirectory, 'removed-edit-data'))
    return { ok: true, result: { removed: true, sceneName: session.sceneName, backupDirectory, requiresEditorReload: true } }
  } catch (error: any) { return { ok: false, error: error.message, backupDirectory, partialChangesPossible: true, retryAutomatically: false } }
}
