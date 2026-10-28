import { describe, expect, test } from 'bun:test'
import { configureLayerXml, liveFloraPlan } from './configure-layers'

const flora = '<flora_kinds><flora_kind name="grass"><flags><flag name="is_grass" value="true"/></flags></flora_kind><flora_kind name="tree"><flags><flag name="tree" value="true"/></flags></flora_kind></flora_kinds>'
const scene = '<scene name="test"><environment keep="yes"/><terrain enabled="true"><layers version="1"><layer name="base"/><layer name="new"/></layers><nodes keep="yes"/></terrain><entities keep="yes"/></scene>'
const layers = (name = 'Grassland', mesh = 'grass') => `<layers version="1"><layer name="Base Soil"><summer is_enabled="true"><meshes/></summer></layer><layer name="${name}"><summer is_enabled="true"><meshes><mesh name="${mesh}" density="38"/></meshes></summer></layer></layers>`

describe('terrain layer authoring constraints', () => {
  test('retains scene environment, node data and entities while binding grass', () => {
    const actual = configureLayerXml(scene, layers(), ['base', 'new'], flora)
    expect(actual).toContain('<environment keep="yes"/>')
    expect(actual).toContain('<nodes keep="yes"/>')
    expect(actual).toContain('<entities keep="yes"/>')
    expect(actual).toContain('name="Grassland"')
    expect(actual).toContain('name="grass" density="38"')
  })
  test('rejects tree flora on the treeless layer', () => {
    expect(() => configureLayerXml(scene, layers('Grassland', 'tree'), ['base', 'new'], flora)).toThrow('non-tree ground cover')
  })
  test('allows verified Native herbaceous ground cover without the grass rendering flag', () => {
    const herbs = flora.replace('</flora_kinds>', '<flora_kind name="flora_plant__mix"/><flora_kind name="flora_green__mix_flower"/></flora_kinds>')
    for (const name of ['flora_plant__mix', 'flora_green__mix_flower'])
      expect(configureLayerXml(scene, layers('Grassland', name), ['base', 'new'], herbs)).toContain(`name="${name}"`)
  })
  test('still requires an allowlisted herb to exist in the installed Native definitions', () => {
    expect(() => configureLayerXml(scene, layers('Grassland', 'flora_plant__mix'), ['base', 'new'], flora)).toThrow('unknown official flora')
  })
  test('allows installed Battanian preset ground cover while keeping trees out', () => {
    const cover = ['shrub__mix', 'wheat_group_summer', 'rock_new_green', 'flora_green__mix_big_leafs', 'ground_rocks_group']
    const official = flora.replace('</flora_kinds>', cover.map(name => `<flora_kind name="${name}"/>`).join('') + '</flora_kinds>')
    for (const name of cover) {
      expect(configureLayerXml(scene, layers('Grassland', name), ['base', 'new'], official)).toContain(`name="${name}"`)
      expect(() => configureLayerXml(scene, layers('Grassland', name), ['base', 'new'], flora)).toThrow('unknown official flora')
    }
    expect(() => configureLayerXml(scene, layers('Grassland', 'tree'), ['base', 'new'], official)).toThrow('non-tree ground cover')
  })
  test('rejects non-English labels', () => {
    expect(() => configureLayerXml(scene, layers('草地'), ['base', 'new'], flora)).toThrow('English')
  })
  test('rejects a different native layer order before replacing XML', () => {
    expect(() => configureLayerXml(scene, layers(), ['new', 'base'], flora)).toThrow('layer order')
  })
  test('converts vegetation-only XML to a revision-guarded live plan', () => {
    const oldLayers = layers().replace('density="38"', 'density="20"')
    const oldScene = `<scene><terrain enabled="true">${oldLayers}</terrain></scene>`
    const current = { revision: 'A'.repeat(64), layers: [{ name: 'Base Soil' }, { name: 'Grassland' }] }
    const plan = liveFloraPlan(oldScene, layers(), ['Base Soil', 'Grassland'], flora, current)
    expect(plan.expectedRevision).toBe(current.revision)
    expect(plan.layers[1].seasons.summer?.[0]).toEqual({ name: 'grass', density: 38 })
  })
  test('refuses to turn an unsupported material edit into a restart', () => {
    const oldScene = `<scene><terrain enabled="true">${layers()}</terrain></scene>`
    const current = { revision: 'A'.repeat(64), layers: [{ name: 'Base Soil' }, { name: 'Grassland' }] }
    expect(() => liveFloraPlan(oldScene, layers().replace('name="Grassland"', 'name="Meadow"'), ['Base Soil', 'Grassland'], flora, current)).toThrow('will not close the editor')
  })
})
