import { expect, test } from 'bun:test'
import { removeSceneTerrainDefinition } from './bannerlord-editor-terrain-reset'

test('terrain removal preserves all entities, environment and camera bytes', () => {
  const prefix = '<?xml version="1.0"?><scene name="newmap"><environment_properties water="original"/><entities><entity name="ground_plane"/></entities>'
  const terrain = '<terrain enabled="true" node_dimension_x="2" node_size="100"><layers><layer name="soil_b"/></layers><nodes><node idx="0"/></nodes></terrain>'
  const suffix = '<editor_data camera="original"/></scene>'
  expect(removeSceneTerrainDefinition(prefix + terrain + suffix)).toBe(prefix + '<terrain enabled="false"/>' + suffix)
})
test('terrain removal rejects missing, duplicate, disabled or malformed definitions', () => {
  for (const xml of ['<scene/>', '<scene><terrain enabled="false"/></scene>', '<scene><terrain enabled="true"></terrain><terrain enabled="true"></terrain></scene>', '<scene><terrain>'])
    expect(() => removeSceneTerrainDefinition(xml)).toThrow()
})
