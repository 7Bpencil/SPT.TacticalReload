#if UNITY_EDITOR

using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public class AnimatorIncomingTransitionsWindow : EditorWindow
{
    private AnimatorState selectedState;
    private Vector2 scrollPosition;

    [MenuItem("Custom Windows/7Bpencil/Incoming Transitions")]
    private static void Open()
    {
        GetWindow<AnimatorIncomingTransitionsWindow>(
            "Incoming Transitions"
        );
    }

    private void OnEnable()
    {
        Selection.selectionChanged += OnSelectionChanged;
        OnSelectionChanged();
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= OnSelectionChanged;
    }

    private void OnSelectionChanged()
    {
        selectedState = Selection.activeObject as AnimatorState;
        scrollPosition = Vector2.zero;
        Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField(
            "Selected Animator State",
            EditorStyles.boldLabel
        );

        if (selectedState == null)
        {
            EditorGUILayout.HelpBox(
                "Select a State in the Animator window.",
                MessageType.Info
            );

            return;
        }

        EditorGUILayout.LabelField(
            "State",
            selectedState.name
        );

        EditorGUILayout.Space(10);

        EditorGUILayout.LabelField(
            "Incoming Transitions",
            EditorStyles.boldLabel
        );

        EditorGUILayout.Space(4);

        // Everything below this point is scrollable.
        scrollPosition = EditorGUILayout.BeginScrollView(
            scrollPosition,
            GUILayout.ExpandHeight(true)
        );

        var controller = GetController(selectedState);

        if (controller == null)
        {
            EditorGUILayout.HelpBox(
                "Could not find the Animator Controller.",
                MessageType.Warning
            );

            EditorGUILayout.EndScrollView();
            return;
        }

        bool found = false;

        foreach (var layer in controller.layers)
        {
            FindIncomingTransitions(
                layer.stateMachine,
                selectedState,
                layer.name,
                ref found
            );
        }

        if (!found)
        {
            EditorGUILayout.HelpBox(
                "No states transition to this state.",
                MessageType.Info
            );
        }

        EditorGUILayout.EndScrollView();
    }

    private static AnimatorController GetController(
        AnimatorState state)
    {
        string path = AssetDatabase.GetAssetPath(state);

        if (string.IsNullOrEmpty(path))
            return null;

        return AssetDatabase.LoadAssetAtPath<AnimatorController>(
            path
        );
    }

    private void FindIncomingTransitions(
        AnimatorStateMachine stateMachine,
        AnimatorState target,
        string layerName,
        ref bool found)
    {
        foreach (var child in stateMachine.states)
        {
            AnimatorState source = child.state;

            foreach (var transition in source.transitions)
            {
                if (transition.destinationState != target)
                    continue;

                found = true;

                DrawTransition(
                    layerName,
                    source,
                    target,
                    transition
                );
            }
        }

        foreach (var child in stateMachine.stateMachines)
        {
            FindIncomingTransitions(
                child.stateMachine,
                target,
                layerName,
                ref found
            );
        }
    }

    private void DrawTransition(
        string layerName,
        AnimatorState source,
        AnimatorState target,
        AnimatorStateTransition transition)
    {
        EditorGUILayout.BeginVertical(
            EditorStyles.helpBox
        );

        EditorGUILayout.LabelField(
            $"{layerName}: {source.name} → {target.name}",
            EditorStyles.boldLabel
        );

        if (transition.hasExitTime)
        {
            EditorGUILayout.LabelField(
                $"Exit Time: {transition.exitTime}"
            );
        }

        EditorGUILayout.LabelField(
            $"Duration: {transition.duration}"
        );

        if (transition.conditions != null &&
            transition.conditions.Length > 0)
        {
            EditorGUILayout.Space(2);

            EditorGUILayout.LabelField("Conditions:");

            foreach (var condition in transition.conditions)
            {
                EditorGUILayout.LabelField(
                    $"  {condition.parameter} " +
                    $"{GetConditionOperator(condition.mode)} " +
                    $"{condition.threshold}"
                );
            }
        }

        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(4);
    }

    private static string GetConditionOperator(
        AnimatorConditionMode mode)
    {
        switch (mode)
        {
            case AnimatorConditionMode.If:
                return "If";

            case AnimatorConditionMode.IfNot:
                return "If Not";

            case AnimatorConditionMode.Greater:
                return ">";

            case AnimatorConditionMode.Less:
                return "<";

            case AnimatorConditionMode.Equals:
                return "==";

            case AnimatorConditionMode.NotEqual:
                return "!=";

            default:
                return mode.ToString();
        }
    }
}

#endif
