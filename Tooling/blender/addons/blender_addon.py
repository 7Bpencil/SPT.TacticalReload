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


class MV_Settings(PropertyGroup):
    vest: PointerProperty(type=bpy.types.Object)
    armature: PointerProperty(type=bpy.types.Object)


def find_armature(obj):
    if not obj or obj.type != 'MESH':
        return None

    for m in obj.modifiers:
        if m.type == 'ARMATURE' and m.object and m.object.type == 'ARMATURE':
            return m.object

    return None


class MV_SelectVest(Operator):
    bl_idname = "mv.select_vest"
    bl_label = "Select Vest"

    def execute(self, context):
        vest_armature = find_armature(context.active_object)
        if vest_armature:
            settings = context.scene.mv_settings
            settings.vest = context.active_object
            settings.armature = vest_armature
            print("found vest and armature")
            print(settings.vest)
            print(settings.armature)
        else:
            print("not found vest and armature")

        return {'FINISHED'}


CLASSES = (
    MV_Settings,
    MV_SelectVest,
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
