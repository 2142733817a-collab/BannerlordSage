import { describe, expect, test } from 'bun:test'
import { terrainFloraPlanSchema } from './bannerlord-editor-flora'

const make = () => ({ expectedRevision: 'A'.repeat(64), expectedLayers: ['Base Soil', 'Grassland'],
  layers: [{ layer: 'Grassland', seasons: { summer: [{ name: 'flora_grass_a', density: 24 }] } }] })

describe('live terrain flora plans', () => {
  test('accepts edits and an empty list for removal without any lifecycle action', () => {
    expect(terrainFloraPlanSchema.parse(make()).layers[0].seasons.summer?.[0].density).toBe(24)
    const plan = make(); plan.layers[0].seasons.summer = []
    expect(terrainFloraPlanSchema.parse(plan).layers[0].seasons.summer).toEqual([])
  })
  test('requires the revision and known unique layer names', () => {
    expect(terrainFloraPlanSchema.safeParse({ ...make(), expectedRevision: '' }).success).toBe(false)
    const plan = make(); plan.layers[0].layer = 'Trees'
    expect(terrainFloraPlanSchema.safeParse(plan).success).toBe(false)
    expect(terrainFloraPlanSchema.safeParse({ ...make(), layers: [...make().layers, ...make().layers] }).success).toBe(false)
  })
  test('rejects nonfinite and excessive density before any native call', () => {
    for (const density of [NaN, Infinity, -1, 61]) {
      const plan = make(); plan.layers[0].seasons.summer[0].density = density
      expect(terrainFloraPlanSchema.safeParse(plan).success).toBe(false)
    }
  })
  test('preserves the zero seed used by official Battanian flora presets', () => {
    const plan = make(); Object.assign(plan.layers[0].seasons.summer[0], { seedIndex: 0 })
    expect(terrainFloraPlanSchema.parse(plan).layers[0].seasons.summer?.[0].seedIndex).toBe(0)
    for (const seedIndex of [-1, 0.5, 1000001]) {
      Object.assign(plan.layers[0].seasons.summer[0], { seedIndex })
      expect(terrainFloraPlanSchema.safeParse(plan).success).toBe(false)
    }
  })
  test('rejects duplicates, unknown fields and inverted size ranges', () => {
    const plan = make(); plan.layers[0].seasons.summer.push({ name: 'flora_grass_a', density: 2 })
    expect(terrainFloraPlanSchema.safeParse(plan).success).toBe(false)
    const size = make(); Object.assign(size.layers[0].seasons.summer[0], { sizeMin: [2, 1, 1], sizeMax: [1, 1, 1] })
    expect(terrainFloraPlanSchema.safeParse(size).success).toBe(false)
    expect(terrainFloraPlanSchema.safeParse({ ...make(), quit: true }).success).toBe(false)
  })
})
