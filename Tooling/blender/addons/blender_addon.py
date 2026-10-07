bl_info = {
    "name": "Modular Vests",
    "author": "7Bpencil",
    "blender": (2, 80, 0),
    "category": "Object",
}


import bpy
import json

from mathutils import Vector
from bpy_extras import view3d_utils
from bpy.props import StringProperty, IntProperty, FloatProperty, PointerProperty, CollectionProperty
from bpy.types import Operator, Panel, PropertyGroup, UIList


class MV_Settings(bpy.types.PropertyGroup):
    columns: bpy.props.IntProperty(name="Columns", default=6, min=1, max=12)
    rows: bpy.props.IntProperty(name="Rows", default=6, min=1, max=12)


def find_armature(obj):
    if not obj or obj.type != 'MESH':
        return None

    for m in obj.modifiers:
        if m.type == 'ARMATURE' and m.object and m.object.type == 'ARMATURE':
            return m.object

    return None


class OT_CreatePoints(Operator):
    bl_idname = "mv.create_points"
    bl_label = "Create Points"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        active = context.active_object
        if active is None:
            print("OT_CreatePoints: no active object")
            return {"CANCELLED"}

        armature = find_armature(active)
        if armature is None:
            print("OT_CreatePoints: no armature on active object")
            return {"CANCELLED"}

        bpy.ops.object.select_all(action='DESELECT')

        settings = context.scene.mv_settings

        center = sum((Vector(corner) for corner in active.bound_box), Vector()) / 8

        cell_x = 0.04
        cell_z = 0.026

        points = []

        for z in range(settings.rows):
            for x in range(settings.columns):
                offset = Vector((x * cell_x, 0, z * cell_z))
                location = center + offset

                bpy.ops.mesh.primitive_uv_sphere_add(
                    location=location,
                    radius=0.01,
                )

                point = context.object
                point.parent = active
                point.name = "7b_point"

                points.append(point)

        for point in points:
            point.select_set(True)

        context.view_layer.objects.active = points[0]

        print("OT_CreatePoints: created points")

        return {"FINISHED"}


class PT_CreatePoints(Panel):
    bl_idname = "mv.create_points.panel"
    bl_label = "Create Points"
    bl_space_type = 'VIEW_3D'
    bl_region_type = 'UI'
    bl_category = "Tool"

    def draw(self, context):
        layout = self.layout
        settings = context.scene.mv_settings
        layout.prop(settings, "columns")
        layout.prop(settings, "rows")
        layout.separator()
        layout.operator("mv.create_points", text="Create Points")


CLASSES = (
    MV_Settings,
    OT_CreatePoints,
    PT_CreatePoints,
)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)

    bpy.types.Scene.mv_settings = PointerProperty(type=MV_Settings)


def unregister():
    if hasattr(bpy.types.Scene, "mv_settings"):
        del bpy.types.Scene.mv_settings

    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
