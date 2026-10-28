import { describe, expect, test } from 'bun:test'
import { replaceTerrainBaseMaterial } from './bannerlord-editor-terrain-material'

const library = `<terrain_materials><terrain_material name="soil_b" is_enabled="true" is_flora_layer="false" is_mesh_blend_layer="false" scale="5, 5" physics_material="soil"><textures><texture type="diffusemap" name="ground_soil_b_d"/><texture type="normalmap" name="ground_soil_b_n"/></textures><layer_flags><flag name="use_parallax" value="false"/></layer_flags><meshes/></terrain_material></terrain_materials>`
const layer = '<layer name="default"><summer name="default" is_enabled="true" is_flora_layer="false" is_mesh_blend_layer="false"><textures><texture type="diffusemap" name="editor_grid_8"/></textures></summer></layer>'
const scene = `<?xml version="1.0"?><scene name="newmap"><entities><game_entity name="ground_plane"/></entities><environment_properties water="untouched"/><terrain enabled="true" node_dimension_x="2" node_dimension_y="2" node_size="100"><layers version="1">${layer}</layers><nodes min_height="0" max_height="0"/></terrain><editor_data camera="unchanged"/></scene>`

describe('whole terrain surface material replacement', () => {
  test('copies the complete official template and preserves geometry, environment, entities and camera byte for byte', () => {
    const result = replaceTerrainBaseMaterial(scene, library, 'soil_b')
    expect(result).toContain('ground_soil_b_d')
    expect(result).toContain('ground_soil_b_n')
    expect(result).toContain('<layer name="default"><summer name="soil_b"')
    expect(result).not.toContain('editor_grid_8')
    const stripSummer = (xml: string) => xml.replace(/<summer\b[\s\S]*?<\/summer>/, '<summer/>')
    expect(stripSummer(result)).toBe(stripSummer(scene))
  })
  test('refuses unknown, disabled, flora and mesh blend templates', () => {
    expect(() => replaceTerrainBaseMaterial(scene, library, 'missing')).toThrow()
    for (const [field, value] of [['is_enabled', 'false'], ['is_flora_layer', 'true'], ['is_mesh_blend_layer', 'true']])
      expect(() => replaceTerrainBaseMaterial(scene, library.replace(`${field}="${value === 'true' ? 'false' : 'true'}"`, `${field}="${value}"`), 'soil_b')).toThrow()
  })
  test('refuses multiple layers and seasonal overrides instead of discarding painted content', () => {
    expect(() => replaceTerrainBaseMaterial(scene.replace(layer, layer + layer), library, 'soil_b')).toThrow()
    expect(() => replaceTerrainBaseMaterial(scene.replace('</layer>', '<winter name="snow"/></layer>'), library, 'soil_b')).toThrow()
    expect(() => replaceTerrainBaseMaterial(scene.replace('enabled="true"', 'enabled="false"'), library, 'soil_b')).toThrow()
  })
  test('updates official default seasonal inheritance without adding overrides', () => {
    const reference = '<winter is_enabled="true" name="default"><meshes/></winter>'
    const result = replaceTerrainBaseMaterial(scene.replace('</layer>', reference + '</layer>'), library, 'soil_b')
    expect(result).toContain('<winter is_enabled="true" name="soil_b"><meshes/></winter>')
    expect(() => replaceTerrainBaseMaterial(scene.replace('</layer>', '<winter name="default"><textures/></winter></layer>'), library, 'soil_b')).toThrow()
  })
  test('rejects invalid XML, entity declarations and unsafe names', () => {
    expect(() => replaceTerrainBaseMaterial(scene + '<broken>', library, 'soil_b')).toThrow()
    expect(() => replaceTerrainBaseMaterial('<!DOCTYPE scene>' + scene, library, 'soil_b')).toThrow()
    expect(() => replaceTerrainBaseMaterial(scene, library, '../soil_b')).toThrow()
  })
})
