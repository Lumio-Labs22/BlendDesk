import os
from mathutils import Vector

import bpy

scene = bpy.context.scene
render = scene.render
render.engine = "BLENDER_WORKBENCH"
render.use_compositing = False
render.use_sequencer = False
render.use_border = False
render.film_transparent = False
render.image_settings.file_format = "PNG"
render.image_settings.color_mode = "RGB"
render.filepath = os.environ["BLENDDESK_PREVIEW_OUTPUT"]

shading = scene.display.shading
shading.light = "STUDIO"
shading.color_type = "MATERIAL"
shading.show_shadows = True
shading.show_cavity = True
shading.cavity_type = "BOTH"
shading.background_type = "VIEWPORT"
shading.background_color = (0.075, 0.085, 0.105)

if scene.camera is None:
    # Frame visible geometry when the saved scene has no camera.
    rotation = Vector((4, -6, 4)).to_track_quat("Z", "Y")
    to_camera = rotation.inverted()
    low = Vector((float("inf"),) * 3)
    high = Vector((float("-inf"),) * 3)
    for instance in bpy.context.evaluated_depsgraph_get().object_instances:
        obj = instance.object
        if obj.hide_render or obj.type not in {
            "MESH", "CURVE", "SURFACE", "META", "FONT", "VOLUME", "CURVES", "POINTCLOUD",
            "GPENCIL", "GREASEPENCIL",
        }:
            continue
        for corner in obj.bound_box:
            point = to_camera @ (instance.matrix_world @ Vector(corner))
            for axis in range(3):
                low[axis] = min(low[axis], point[axis])
                high[axis] = max(high[axis], point[axis])
    if low.x == float("inf"):
        raise RuntimeError("The active scene has no visible geometry to frame.")

    center = (low + high) / 2
    extent = high - low
    camera_data = bpy.data.cameras.new("BlendDesk Preview Camera")
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = 1.25 * max(extent.x, extent.y * 1.6, 0.01)
    distance = max(extent.z, extent.x, extent.y, 1)
    camera_data.clip_end = distance * 4
    camera = bpy.data.objects.new(camera_data.name, camera_data)
    scene.collection.objects.link(camera)
    camera.location = rotation @ Vector((center.x, center.y, high.z + distance))
    camera.rotation_euler = rotation.to_euler()
    scene.camera = camera
    render.resolution_x, render.resolution_y = 400, 250
    render.pixel_aspect_x = render.pixel_aspect_y = 1
else:
    width = render.resolution_x * render.pixel_aspect_x
    height = render.resolution_y * render.pixel_aspect_y
    scale = 400 / max(width, height)
    render.resolution_x = max(16, round(width * scale))
    render.resolution_y = max(16, round(height * scale))
    render.pixel_aspect_x = render.pixel_aspect_y = 1

render.resolution_percentage = 100
bpy.ops.render.render(write_still=True)
