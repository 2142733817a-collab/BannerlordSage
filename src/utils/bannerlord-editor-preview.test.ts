import { afterEach, expect, test } from 'bun:test'
import { randomBytes, createHash } from 'node:crypto'
import { deflateSync } from 'node:zlib'
import { mkdtemp, readFile, writeFile, rm, readdir } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { EDITOR_MAX_PREVIEW_BYTES, prepareEditorPreview } from './bannerlord-editor-preview'

const roots: string[] = []
afterEach(async () => { for (const root of roots.splice(0)) await rm(root, { recursive: true, force: true }) })

function chunk(type: string, data: Buffer) {
  const body = Buffer.concat([Buffer.from(type), data])
  let crc = 0xffffffff
  for (const byte of body) {
    crc ^= byte
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0)
  }
  const length = Buffer.alloc(4); length.writeUInt32BE(data.length)
  const checksum = Buffer.alloc(4); checksum.writeUInt32BE((crc ^ 0xffffffff) >>> 0)
  return Buffer.concat([length, body, checksum])
}

test('small captures retain their original bytes and PNG MIME type', async () => {
  const bytes = Buffer.from('small already-validated capture')
  const result = await prepareEditorPreview('unused.png', bytes)
  expect(result.data).toBe(bytes)
  expect(result.mimeType).toBe('image/png')
})

test.skipIf(process.platform !== 'win32')('noisy large screenshots are bounded without modifying the original', async () => {
  const root = await mkdtemp(join(tmpdir(), 'bannersage-preview-')); roots.push(root)
  const width = 1024, height = 768
  const header = Buffer.alloc(13); header.writeUInt32BE(width); header.writeUInt32BE(height, 4)
  header[8] = 8; header[9] = 2
  const pixels = Buffer.alloc((width * 3 + 1) * height)
  for (let y = 0; y < height; y++) randomBytes(width * 3).copy(pixels, y * (width * 3 + 1) + 1)
  const png = Buffer.concat([Buffer.from('89504e470d0a1a0a', 'hex'), chunk('IHDR', header),
    chunk('IDAT', deflateSync(pixels)), chunk('IEND', Buffer.alloc(0))])
  const path = join(root, 'capture with spaces.png'); await writeFile(path, png)
  expect(png.length).toBeGreaterThan(EDITOR_MAX_PREVIEW_BYTES)
  const result = await prepareEditorPreview(path, png)
  expect(result.mimeType).toBe('image/jpeg')
  expect(result.data.subarray(0, 2).toString('hex')).toBe('ffd8')
  expect(result.data.length).toBeLessThanOrEqual(EDITOR_MAX_PREVIEW_BYTES)
  let dimensions: { width: number; height: number } | undefined
  for (let i = 0; i < result.data.length - 9; i++) {
    if (result.data[i] === 0xff && [0xc0, 0xc2].includes(result.data[i + 1])) {
      dimensions = { width: result.data.readUInt16BE(i + 7), height: result.data.readUInt16BE(i + 5) }
      break
    }
  }
  expect(dimensions).toBeDefined()
  expect(dimensions!.width).toBeGreaterThanOrEqual(512)
  expect(dimensions!.width).toBeLessThanOrEqual(1280)
  expect(dimensions!.height / dimensions!.width).toBeCloseTo(height / width, 2)
  expect(createHash('sha256').update(await readFile(path)).digest('hex'))
    .toBe(createHash('sha256').update(png).digest('hex'))
  expect(await readdir(root)).toEqual(['capture with spaces.png'])
})
