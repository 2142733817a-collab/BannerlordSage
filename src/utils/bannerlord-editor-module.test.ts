import { afterEach, expect, test } from 'bun:test'
import { createServer, type Server, type Socket } from 'node:net'
import { mkdtemp, mkdir, writeFile, readFile, realpath, rm } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join, resolve, sep } from 'node:path'
import { createNativeModule, moduleCreationInfo } from './bannerlord-editor-module'

const fixtures: Array<{ root: string; server: Server; sockets: Set<Socket> }> = []
afterEach(async () => {
  for (const f of fixtures.splice(0)) {
    for (const socket of f.sockets) socket.destroy()
    await new Promise<void>(done => f.server.close(() => done()))
    if (!resolve(f.root).startsWith(resolve(tmpdir()) + sep)) throw new Error('Unexpected cleanup path')
    await rm(f.root, { recursive: true, force: true })
  }
})
async function fixture(options: { capability?: boolean; wrongId?: boolean; incomplete?: boolean; isolated?: boolean } = {}) {
  const root = await mkdtemp(join(tmpdir(), 'bannersage-native-module-'))
  const gameDir = join(root, 'game'), modules = join(gameDir, 'Modules')
  const moduleDir = join(modules, 'BannerlordSage.EditorProbe')
  const sceneDir = join(moduleDir, 'SceneObj', 'probe')
  const outputDir = join(root, 'output')
  await mkdir(sceneDir, { recursive: true }); await mkdir(outputDir)
  await mkdir(join(modules, 'Native'))
  for (const variant of ['Win64_Shipping_Client', 'Win64_Shipping_wEditor']) {
    const bin = join(gameDir, 'bin', variant); await mkdir(bin, { recursive: true })
    await writeFile(join(bin, 'Version.xml'), '<Version><Singleplayer Value="v1.4.8"/></Version>')
  }
  let nativeModulesRoot = modules
  if (options.isolated) {
    nativeModulesRoot = join(root, 'runtime', 'Modules'); await mkdir(nativeModulesRoot, { recursive: true })
    const { symlink } = await import('node:fs/promises')
    for (const name of ['Native', 'BannerlordSage.EditorProbe']) await symlink(join(modules, name), join(nativeModulesRoot, name), 'junction')
  }
  const requests: any[] = [], sockets = new Set<Socket>()
  const server = createServer(socket => {
    sockets.add(socket); socket.on('close', () => sockets.delete(socket))
    let buffer = ''
    socket.on('data', async chunk => {
      buffer += chunk.toString('utf8'); if (!buffer.includes('\n')) return
      const request = JSON.parse(buffer.slice(0, buffer.indexOf('\n'))); requests.push(request)
      let result: any
      if (request.action === 'status') result = { editMode: true, scene: '__default_new_editor_scene_', bridgeCapabilities: options.capability === false ? [] : ['native_module_create_v1'] }
      else if (request.action === 'module_creation_info') result = { editMode: true, nativeModulesRoot }
      else {
        const nativePath = join(nativeModulesRoot, request.args.moduleName)
        await mkdir(nativePath)
        if (!options.incomplete) await writeFile(join(nativePath, 'SubModule.xml'), `<Module><Id value="${options.wrongId ? 'wrong' : request.args.moduleName}"/></Module>`)
        result = { created: true, nativePath, command: `editor.create_new_module ${request.args.moduleName}`, output: 'Done' }
      }
      socket.end(JSON.stringify({ ok: true, result, mainThread: 7, executedThread: 7 }) + '\n')
    })
  })
  await new Promise<void>(done => server.listen(0, '127.0.0.1', done))
  fixtures.push({ root, server, sockets })
  const sessionPath = join(root, 'session.json')
  await writeFile(sessionPath, JSON.stringify({ gameDir, sceneDir, outputDir, token: 'fixture-local-token', port: (server.address() as any).port }))
  return { root, modules, nativeModulesRoot, sessionPath, requests }
}
test('invalid or command-injecting names fail before connecting', async () => {
  for (const name of ['../Native', 'Native;quit', 'a b', 'a\nquit', 'CON', 'LPT1', '', 'a'.repeat(65)])
    await expect(createNativeModule(name, 'missing')).rejects.toThrow('folder')
})
test('old bridge is rejected without sending a create request', async () => {
  const f = await fixture({ capability: false })
  await expect(createNativeModule('newmod', f.sessionPath)).rejects.toThrow('lacks')
  expect(f.requests.map(r => r.action)).toEqual(['status'])
})
test('existing module is preserved without a native write', async () => {
  const f = await fixture(); await mkdir(join(f.modules, 'newmod'))
  await writeFile(join(f.modules, 'newmod', 'keep.txt'), 'existing work')
  await expect(createNativeModule('newmod', f.sessionPath)).rejects.toThrow('already exists')
  expect(await readFile(join(f.modules, 'newmod', 'keep.txt'), 'utf8')).toBe('existing work')
  expect(f.requests.some(r => r.action === 'create_module')).toBe(false)
})
test('native game-root creation verifies the manifest and preserves the scene', async () => {
  const f = await fixture(); const r = await createNativeModule('newmod', f.sessionPath)
  expect(r.created).toBe(true); expect(r.sceneUnchanged).toBe(true)
  expect(r.modulePath).toBe(join(f.modules, 'newmod'))
  expect(f.requests.filter(r => r.action === 'create_module')).toHaveLength(1)
})
test('isolated output is moved to game Modules and linked back', async () => {
  const f = await fixture({ isolated: true }); const r = await createNativeModule('newmod', f.sessionPath)
  expect(await realpath(r.runtimePath)).toBe(await realpath(join(f.modules, 'newmod')))
  expect(r.editorConnected).toBe(true)
})
test('wrong manifest ID is rejected before moving output', async () => {
  const f = await fixture({ isolated: true, wrongId: true })
  await expect(createNativeModule('newmod', f.sessionPath)).rejects.toThrow('manifest ID')
  expect(f.requests.filter(r => r.action === 'create_module')).toHaveLength(1)
})
test('incomplete native output is not treated as success or retried', async () => {
  const f = await fixture({ incomplete: true })
  await expect(createNativeModule('newmod', f.sessionPath)).rejects.toThrow()
  expect(f.requests.filter(r => r.action === 'create_module')).toHaveLength(1)
})
