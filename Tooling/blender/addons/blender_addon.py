import bpy


bl_info = {
    "name": "My Test Add-on",
    "blender": (2, 80, 0),
    "category": "Object",
}


class ObjectTest(bpy.types.Operator):
    """My Object Test Script"""

    bl_idname = "object.test"
    bl_label = "7b test"
    bl_options = {'REGISTER', 'UNDO'}

    def execute(self, context):
        scene = context.scene
        print("hello world")
        return {'FINISHED'}


def menu_func(self, context):
    self.layout.operator(ObjectTest.bl_idname)


def register():
    bpy.utils.register_class(ObjectTest)
    bpy.types.VIEW3D_MT_object.append(menu_func)


def unregister():
    bpy.types.VIEW3D_MT_object.remove(menu_func)
    bpy.utils.unregister_class(ObjectTest)


if __name__ == "__main__":
    register()
