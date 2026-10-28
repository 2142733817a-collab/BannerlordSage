import { spawn } from 'node:child_process'
import { readFile, unlink } from 'node:fs/promises'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { randomUUID } from 'node:crypto'

// Bound inline screenshots separately from the full-resolution file on disk.
export const EDITOR_MAX_PREVIEW_BYTES = 192 * 1024
const script = fileURLToPath(new URL('../../tools/editor-probe/capture-preview.ps1', import.meta.url))

export async function prepareEditorPreview(pngPath: string, png: Buffer) {
  if (png.length <= EDITOR_MAX_PREVIEW_BYTES) return { data: png, mimeType: 'image/png' }
  const outputPath = join(dirname(pngPath), `capture-preview-${randomUUID()}.jpg`)
  try {
    const command = await readFile(script, 'utf8')
    await new Promise<void>((resolve, reject) => {
      const child = spawn(join(process.env.SystemRoot || 'C:\\Windows', 'System32/WindowsPowerShell/v1.0/powershell.exe'),
        ['-NoProfile', '-NonInteractive', '-Command', command], { windowsHide: true, stdio: 'ignore',
          env: { ...process.env, BANNERSAGE_PREVIEW_INPUT: pngPath, BANNERSAGE_PREVIEW_OUTPUT: outputPath } })
      const timer = setTimeout(() => { child.kill(); reject(new Error('Capture preview timed out')) }, 20000)
      child.once('error', error => { clearTimeout(timer); reject(error) })
      child.once('close', code => { clearTimeout(timer); code === 0 ? resolve() : reject(new Error('Capture preview failed')) })
    })
    const data = await readFile(outputPath)
    if (!data.length || data.length > EDITOR_MAX_PREVIEW_BYTES || data[0] !== 0xff || data[1] !== 0xd8)
      throw new Error('Invalid capture preview')
    return { data, mimeType: 'image/jpeg' }
  } finally { await unlink(outputPath).catch(() => {}) }
}
