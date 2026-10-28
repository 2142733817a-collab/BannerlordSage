import { lstat, realpath, rename, symlink, readFile, readdir } from 'node:fs/promises'
import { join, resolve } from 'node:path'
import { loadEditorSession, editorStatus, requestEditor } from './bannerlord-editor-client'

import { XMLParser } from 'fast-xml-parser'
const samePath = (a: string, b: string) => resolve(a).toLowerCase() === resolve(b).toLowerCase()
export function validateModuleName(name: unknown): asserts name is string {
  if (typeof name !== 'string' || !/^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(name) || /^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$/i.test(name))
    throw new Error('Invalid module folder name. Use 1–64 letters, numbers or underscores, starting with a letter; device names are forbidden.')
}
async function exists(path: string) {
  try { await lstat(path); return true } catch (e: any) { if (e.code === 'ENOENT') return false; throw e }
}
async function nativeRequest(action: 'module_creation_info' | 'create_module', args: Record<string, unknown>, sessionPath?: string) {
  const reply = await requestEditor(action, args, sessionPath)
  if (!reply.ok) throw new Error(reply.error || 'Bridge rejected operation; inspect the target before retrying.')
  if (reply.mainThread !== reply.executedThread) throw new Error('Operation did not execute on the editor main thread.')
  return reply
}
export async function moduleCreationInfo(sessionPath?: string) {
  const status = await editorStatus(sessionPath)
  if (!status.ok || !status.connected) throw new Error('Editor bridge is not connected.')
  if (!status.result?.bridgeCapabilities?.includes('native_module_create_v1')) throw new Error('Running bridge lacks the native module extension; restart with the extended DLL.')
  const reply = await nativeRequest('module_creation_info', {}, sessionPath)
  const session = await loadEditorSession(sessionPath)
  const nativeModulesRoot = await realpath(reply.result.nativeModulesRoot)
  const gameModulesRoot = await realpath(join(session.gameDir, 'Modules'))
  for (const module of ['Native', 'BannerlordSage.EditorProbe']) {
    if (!samePath(await realpath(join(nativeModulesRoot, module)), await realpath(join(gameModulesRoot, module))))
      throw new Error('Editor runtime module links do not match the configured game.')
  }
  return { ...reply.result, nativeModulesRoot, gameModulesRoot, status }
}
export async function createNativeModule(moduleName: string, sessionPath?: string) {
  validateModuleName(moduleName)
  const info = await moduleCreationInfo(sessionPath)
  if (!info.editMode) throw new Error('The editor must be in edit mode.')
  const destination = join(info.gameModulesRoot, moduleName)
  const nativePath = join(info.nativeModulesRoot, moduleName)
  if (await exists(destination) || await exists(nativePath)) throw new Error('Module folder already exists; refusing to overwrite it.')
  const beforeScene = info.status.result.scene
  const reply = await nativeRequest('create_module', { moduleName }, sessionPath)
  if (!reply.result?.created || !samePath(reply.result.nativePath, nativePath)) throw new Error('Unexpected native output path; inspect before retrying.')
  const manifestPath = join(nativePath, 'SubModule.xml')
  const stat = await lstat(manifestPath)
  if (!stat.isFile() || stat.size > 256 * 1024) throw new Error('Invalid native module manifest.')
  const xml = new XMLParser({ ignoreAttributes: false, attributeNamePrefix: '' }).parse(await readFile(manifestPath, 'utf8'))
  if (xml?.Module?.Id?.value !== moduleName) throw new Error('Native manifest ID does not match requested module.')
  if (!(await lstat(nativePath)).isDirectory() || (await lstat(nativePath)).isSymbolicLink()) throw new Error('Native output is not a physical directory.')
  let moved = false
  try {
    if (!samePath(nativePath, destination)) {
      if (await exists(destination)) throw new Error('Destination appeared during creation; no overwrite performed.')
      await rename(nativePath, destination); moved = true
      await symlink(destination, nativePath, 'junction')
    }
    if (!samePath(await realpath(nativePath), destination)) throw new Error('Runtime alias verification failed.')
    const after = await editorStatus(sessionPath)
    return { ok: true, created: true, moduleName, modulePath: destination, runtimePath: nativePath,
      command: reply.result.command, nativeOutput: reply.result.output, mainThread: reply.mainThread,
      executedThread: reply.executedThread, sceneUnchanged: after.ok && after.result?.scene === beforeScene,
      entries: await readdir(destination), editorConnected: after.connected }
  } catch (e: any) {
    throw new Error(`${e.message}; native creation already happened. Inspect ${moved ? destination : nativePath} before retrying.`)
  }
}
