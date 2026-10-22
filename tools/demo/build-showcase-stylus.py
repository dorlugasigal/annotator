"""Create the original website prop in an isolated Blender scene; preserve the open scene."""
import bpy
import math
from pathlib import Path

root = Path(__file__).resolve().parents[2]
output = root / "website/public/models/annotator-stylus.glb"
output.parent.mkdir(parents=True, exist_ok=True)
original = bpy.context.window.scene
scene = bpy.data.scenes.new("Annotator - website stylus")


def material(name, color, metallic, roughness):
    value = bpy.data.materials.new(name)
    value.diffuse_color = (*color, 1)
    value.use_nodes = True
    shader = value.node_tree.nodes.get("Principled BSDF")
    shader.inputs["Base Color"].default_value = (*color, 1)
    shader.inputs["Metallic"].default_value = metallic
    shader.inputs["Roughness"].default_value = roughness
    return value


def lathe(name, profile, surface, segments=64):
    vertices = [
        (radius * math.cos(i * math.tau / segments),
         radius * math.sin(i * math.tau / segments), z)
        for z, radius in profile for i in range(segments)
    ]
    faces = [
        (row * segments + i, row * segments + (i + 1) % segments,
         (row + 1) * segments + (i + 1) % segments, (row + 1) * segments + i)
        for row in range(len(profile) - 1) for i in range(segments)
    ]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], faces)
    mesh.materials.append(surface)
    obj = bpy.data.objects.new(name, mesh)
    scene.collection.objects.link(obj)
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    return obj


try:
    bpy.context.window.scene = scene
    graphite = material("Anodized graphite", (.033, .038, .042), .75, .27)
    alloy = material("Machined aluminum", (.68, .71, .74), 1, .22)
    coral = material("Vermilion ceramic", (.82, .056, .018), .28, .24)
    nib = material("Graphite tip", (.008, .009, .01), .25, .38)
    lathe("Body", [(-1.57, 0), (-1.57, .214), (-1.52, .235),
                   (1.94, .235), (2.03, .226), (2.09, .18), (2.12, 0)], graphite)
    lathe("Ceramic grip", [(-1.6, .214), (-1.56, .248), (-.64, .248),
                           (-.60, .23)], coral)
    for i in range(17):
        z = -1.48 + i * .047
        lathe(f"Grip groove {i:02}", [(z, .247), (z + .006, .252),
                                    (z + .015, .252), (z + .021, .247)], coral, 48)
    lathe("Aluminum cone", [(-2.22, .039), (-2.18, .05), (-1.70, .199),
                            (-1.62, .214), (-1.60, .214)], alloy)
    lathe("Drawing nib", [(-2.40, 0), (-2.38, .022), (-2.22, .039)], nib)
    for z in [-.58, 1.66, 1.75]:
        lathe("Polished ring", [(z, .234), (z + .015, .24),
                                (z + .05, .24), (z + .065, .234)], alloy)
    lathe("Cap", [(2.02, .19), (2.08, .20), (2.13, .15), (2.14, 0)], coral)
    # All parts have identity transforms. Bake same-material parts into one draw call.
    for surface in [graphite, alloy, coral, nib]:
        parts = [obj for obj in scene.objects if obj.data.materials[0] == surface]
        vertices, faces = [], []
        for part in parts:
            offset = len(vertices)
            vertices.extend(tuple(vertex.co) for vertex in part.data.vertices)
            faces.extend(tuple(index + offset for index in polygon.vertices) for polygon in part.data.polygons)
        mesh = bpy.data.meshes.new(surface.name)
        mesh.from_pydata(vertices, [], faces)
        mesh.materials.append(surface)
        combined = bpy.data.objects.new(surface.name, mesh)
        scene.collection.objects.link(combined)
        for polygon in mesh.polygons:
            polygon.use_smooth = True
        for part in parts:
            old_mesh = part.data
            bpy.data.objects.remove(part, do_unlink=True)
            bpy.data.meshes.remove(old_mesh)
    bpy.ops.export_scene.gltf(
        filepath=str(output), export_format="GLB", use_active_scene=True,
        export_animations=False, export_cameras=False, export_lights=False,
        export_extras=False, export_materials="EXPORT",
    )
    print(f"Exported original Annotator stylus: {output.stat().st_size} bytes")
finally:
    bpy.context.window.scene = original
