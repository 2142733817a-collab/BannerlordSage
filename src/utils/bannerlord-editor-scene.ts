import { lstat, readFile, realpath, writeFile } from 'node:fs/promises'
import { dirname, join, resolve } from 'node:path'
import { XMLParser } from 'fast-xml-parser'
import { loadEditorSession, requestEditor } from './bannerlord-editor-client'
import { validateModuleName } from './bannerlord-editor-module'

const samePath = (a: string, b: string) => resolve(a).toLowerCase() === resolve(b).toLowerCase()

export async function saveEditorSceneAs(moduleName: string, sceneName: string, overwriteExisting = false, sessionPath?: string) {
  validateModuleName(moduleName)
  if (/^(Native|SandBox|SandBoxCore|StoryMode|CustomBattle|Multiplayer|BirthAndDeath|FastMode)$/i.test(moduleName))
    throw new Error('Choose a user authoring module, not an official module.')
  if (!/^[A-Za-z][A-Za-z0-9_]{0,79}$/.test(sceneName) || /^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$/i.test(sceneName))
    throw new Error('Invalid scene folder name.')
  const selectedSession = sessionPath ?? process.env.BANNERSAGE_EDITOR_PROBE_SESSION
  const session = await loadEditorSession(selectedSession)
  const status = await requestEditor('status', {}, selectedSession)
  if (!status.ok || !status.result?.editMode || !status.result.bridgeCapabilities?.includes('native_scene_lifecycle_v1'))
    throw new Error('A running editor with native scene lifecycle support is required.')
  const moduleDir = await realpath(join(session.gameDir, 'Modules', moduleName))
  if (!samePath(dirname(moduleDir), join(session.gameDir, 'Modules'))) throw new Error('Module escaped game Modules.')
  const manifest = join(moduleDir, 'SubModule.xml')
  if ((await lstat(manifest)).size > 256 * 1024) throw new Error('Oversized module manifest.')
  const xml = new XMLParser({ ignoreAttributes: false, attributeNamePrefix: '' }).parse(await readFile(manifest, 'utf8'))
  if (xml?.Module?.Id?.value !== moduleName) throw new Error('Module manifest ID differs from its folder.')
  const sceneRoot = join(moduleDir, 'SceneObj')
  if (!samePath(await realpath(sceneRoot), sceneRoot)) throw new Error('SceneObj must be a physical module directory.')
  const reply = await requestEditor('save_scene_as', { moduleName, sceneName, overwriteExisting }, selectedSession)
  if (!reply.ok || !reply.result?.saved || reply.mainThread === undefined || reply.mainThread !== reply.executedThread)
    throw new Error(reply.error ?? 'Native Save As did not confirm a main-thread save. Inspect before retrying.')
  const expected = join(sceneRoot, sceneName)
  if (!samePath(await realpath(reply.result.sceneDirectory), expected)) throw new Error('Native Save As returned another scene path.')
  const config = JSON.parse((await readFile(selectedSession!, 'utf8')).replace(/^\uFEFF/, ''))
  config.moduleName = moduleName
  config.sceneName = sceneName
  config.sceneDir = reply.result.sceneDirectory
  await writeFile(selectedSession!, JSON.stringify(config, null, 2))
  return reply
}
