#if UNITY_EDITOR

using AnimationEventSystem;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public static class GenerateAnimationStateFullNameHash
{
    private const string MenuPath =
        "Custom Windows/7Bpencil/Generate Random FullNameHash";

    [MenuItem(MenuPath)]
    private static void Generate()
    {
        AnimatorState state = GetSelectedAnimatorState();

        if (state == null)
        {
            Debug.LogWarning(
                "Generate Random FullNameHash: " +
                "Select an Animator state first.");
            return;
        }

        AnimationEventsStateBehaviour behaviour =
            FindBehaviour(state);

        if (behaviour == null)
        {
            Debug.LogWarning(
                $"Generate Random FullNameHash: " +
                $"State '{state.name}' does not have " +
                $"an AnimationEventsStateBehaviour.");
            return;
        }

        int oldHash = behaviour.FullNameHash;
        int newHash = GenerateUniqueHash();

        Undo.RecordObject(
            behaviour,
            "Generate Random FullNameHash");

        behaviour.FullNameHash = newHash;

        EditorUtility.SetDirty(behaviour);

        Debug.Log(
            $"Generated FullNameHash for '{state.name}': " +
            $"{oldHash} -> {newHash}");
    }

    [MenuItem(MenuPath, true)]
    private static bool ValidateGenerate()
    {
        return GetSelectedAnimatorState() != null &&
               FindBehaviour(GetSelectedAnimatorState()) != null;
    }

    private static AnimationEventsStateBehaviour FindBehaviour(
        AnimatorState state)
    {
        StateMachineBehaviour[] behaviours =
            state.behaviours;

        foreach (StateMachineBehaviour behaviour in behaviours)
        {
            if (behaviour is AnimationEventsStateBehaviour animationBehaviour)
                return animationBehaviour;
        }

        return null;
    }

    private static AnimatorState GetSelectedAnimatorState()
    {
        Object[] selectedObjects =
            Selection.objects;

        foreach (Object selectedObject in selectedObjects)
        {
            if (selectedObject is AnimatorState state)
                return state;
        }

        return null;
    }

    private static int GenerateUniqueHash()
    {
        int hash;

        do
        {
            hash = Random.Range(
                int.MinValue,
                int.MaxValue);
        }
        while (hash == 0);

        return hash;
    }
}

#endif
