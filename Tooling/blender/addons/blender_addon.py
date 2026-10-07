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


def find_armature(obj):
    if not obj or obj.type != 'MESH':
        return None

    for m in obj.modifiers:
        if m.type == 'ARMATURE' and m.object and m.object.type == 'ARMATURE':
            return m.object

    return None


class MV_CreatePoint(Operator):
    bl_idname = "mv.create_point"
    bl_label = "Create Point"

    def execute(self, context):
        active = context.active_object
        if active is None:
            print("MV_CreatePoint: no active object")
            return {"CANCELLED"}

        armature = find_armature(active)
        if armature is None:
            print("MV_CreatePoint: no armature on active object")
            return {"CANCELLED"}

        center = sum((Vector(corner) for corner in active.bound_box), Vector()) / 8

        bpy.ops.mesh.primitive_uv_sphere_add(
            location=center,
            radius=0.01,
        )

        point = context.object
        point.parent = active
        point.name = "7b_point"

        print("MV_CreatePoint: created point")

        return {"FINISHED"}


CLASSES = (
    MV_CreatePoint,
)


def register():
    for cls in CLASSES:
        bpy.utils.register_class(cls)


def unregister():
    for cls in reversed(CLASSES):
        bpy.utils.unregister_class(cls)


if __name__ == "__main__":
    register()
