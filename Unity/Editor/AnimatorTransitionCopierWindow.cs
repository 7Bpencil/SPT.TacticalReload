#if UNITY_EDITOR

using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

public class AnimatorTransitionCopierWindow : EditorWindow
{
    private AnimatorState sourceState;
    private AnimatorState destinationState;

    [MenuItem("Custom Windows/7Bpencil/Animator Transition Copier")]
    private static void Open()
    {
        GetWindow<AnimatorTransitionCopierWindow>(
            "Transition Copier"
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
        Repaint();
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(8);

        EditorGUILayout.LabelField(
            "Animator Transition Copier",
            EditorStyles.boldLabel
        );

        EditorGUILayout.Space(8);

        DrawSourceSection();

        EditorGUILayout.Space(12);

        DrawDestinationSection();

        EditorGUILayout.Space(16);

        DrawPasteButton();

        EditorGUILayout.Space(12);

        DrawSelectionHelper();
    }

    // ---------------------------------------------------------------------
    // Source
    // ---------------------------------------------------------------------

    private void DrawSourceSection()
    {
        EditorGUILayout.LabelField(
            "Source State",
            EditorStyles.boldLabel
        );

        EditorGUILayout.Space(3);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        if (sourceState != null)
        {
            EditorGUILayout.LabelField(
                sourceState.name,
                EditorStyles.boldLabel
            );

            EditorGUILayout.LabelField(
                $"Outgoing transitions: {sourceState.transitions.Length}",
                EditorStyles.miniLabel
            );
        }
        else
        {
            EditorGUILayout.LabelField(
                "No source state selected.",
                EditorStyles.miniLabel
            );
        }

        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(4);

        if (GUILayout.Button("Use Currently Selected State"))
        {
            AnimatorState state = GetSelectedAnimatorState();

            if (state != null)
            {
                sourceState = state;
            }
            else
            {
                ShowNoStateSelectedMessage();
            }
        }

        if (sourceState != null)
        {
            if (GUILayout.Button("Clear Source"))
            {
                sourceState = null;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Destination
    // ---------------------------------------------------------------------

    private void DrawDestinationSection()
    {
        EditorGUILayout.LabelField(
            "Destination State",
            EditorStyles.boldLabel
        );

        EditorGUILayout.Space(3);

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        if (destinationState != null)
        {
            EditorGUILayout.LabelField(
                destinationState.name,
                EditorStyles.boldLabel
            );

            EditorGUILayout.LabelField(
                $"Existing outgoing transitions: " +
                $"{destinationState.transitions.Length}",
                EditorStyles.miniLabel
            );
        }
        else
        {
            EditorGUILayout.LabelField(
                "No destination state selected.",
                EditorStyles.miniLabel
            );
        }

        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(4);

        if (GUILayout.Button("Use Currently Selected State"))
        {
            AnimatorState state = GetSelectedAnimatorState();

            if (state != null)
            {
                destinationState = state;
            }
            else
            {
                ShowNoStateSelectedMessage();
            }
        }

        if (destinationState != null)
        {
            if (GUILayout.Button("Clear Destination"))
            {
                destinationState = null;
            }
        }
    }

    // ---------------------------------------------------------------------
    // Paste
    // ---------------------------------------------------------------------

    private void DrawPasteButton()
    {
        EditorGUILayout.LabelField(
            "Paste",
            EditorStyles.boldLabel
        );

        GUI.enabled =
            sourceState != null &&
            destinationState != null;

        if (GUILayout.Button(
            "Paste Transitions",
            GUILayout.Height(34)
        ))
        {
            PasteTransitions();
        }

        GUI.enabled = true;
    }

    // ---------------------------------------------------------------------
    // Selection helper
    // ---------------------------------------------------------------------

    private void DrawSelectionHelper()
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.LabelField(
            "Quick workflow",
            EditorStyles.boldLabel
        );

        EditorGUILayout.LabelField(
            "1. Select source state and click " +
            "\"Use Currently Selected State\".",
            EditorStyles.miniLabel
        );

        EditorGUILayout.LabelField(
            "2. Select destination state and click " +
            "\"Use Currently Selected State\".",
            EditorStyles.miniLabel
        );

        EditorGUILayout.LabelField(
            "3. Click \"Paste Transitions\".",
            EditorStyles.miniLabel
        );

        EditorGUILayout.EndVertical();
    }

    // ---------------------------------------------------------------------
    // Paste transitions
    // ---------------------------------------------------------------------

    private void PasteTransitions()
    {
        if (sourceState == null)
        {
            Debug.LogWarning(
                "No source state selected."
            );

            return;
        }

        if (destinationState == null)
        {
            Debug.LogWarning(
                "No destination state selected."
            );

            return;
        }

        if (sourceState == destinationState)
        {
            if (!EditorUtility.DisplayDialog(
                "Paste Transitions",
                "Source and destination are the same state.\n\n" +
                "This will create duplicate transitions. Continue?",
                "Paste",
                "Cancel"
            ))
            {
                return;
            }
        }

        AnimatorStateTransition[] sourceTransitions =
            sourceState.transitions;

        if (sourceTransitions == null ||
            sourceTransitions.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "Paste Transitions",
                $"State '{sourceState.name}' has no outgoing transitions.",
                "OK"
            );

            return;
        }

        Undo.RegisterCompleteObjectUndo(
            destinationState,
            "Paste Animator Transitions"
        );

        int pastedCount = 0;

        foreach (AnimatorStateTransition sourceTransition
                 in sourceTransitions)
        {
            if (sourceTransition == null)
                continue;

            AnimatorStateTransition newTransition =
                CreateTransition(
                    destinationState,
                    sourceTransition
                );

            if (newTransition == null)
                continue;

            CopyTransitionProperties(
                sourceTransition,
                newTransition
            );

            CopyConditions(
                sourceTransition,
                newTransition
            );

            pastedCount++;
        }

        EditorUtility.SetDirty(destinationState);

        AssetDatabase.SaveAssets();

        Debug.Log(
            $"Pasted {pastedCount} transition(s) " +
            $"from '{sourceState.name}' " +
            $"to '{destinationState.name}'."
        );

        Repaint();
    }

    // ---------------------------------------------------------------------
    // Create transition
    // ---------------------------------------------------------------------

    private static AnimatorStateTransition CreateTransition(
        AnimatorState destinationSource,
        AnimatorStateTransition sourceTransition
    )
    {
        if (destinationSource == null)
            return null;

        AnimatorState destination =
            sourceTransition.destinationState;

        AnimatorStateMachine destinationStateMachine =
            sourceTransition.destinationStateMachine;

        // State -> State
        if (destination != null)
        {
            return destinationSource.AddTransition(
                destination
            );
        }

        // State -> State Machine
        if (destinationStateMachine != null)
        {
            return destinationSource.AddTransition(
                destinationStateMachine
            );
        }

        // State -> Exit
        if (sourceTransition.isExit)
        {
            return destinationSource.AddExitTransition();
        }

        Debug.LogWarning(
            "Could not determine transition destination."
        );

        return null;
    }

    // ---------------------------------------------------------------------
    // Copy transition properties
    // ---------------------------------------------------------------------

    private static void CopyTransitionProperties(
        AnimatorStateTransition source,
        AnimatorStateTransition destination
    )
    {
        destination.hasExitTime =
            source.hasExitTime;

        destination.exitTime =
            source.exitTime;

        destination.hasFixedDuration =
            source.hasFixedDuration;

        destination.duration =
            source.duration;

        destination.offset =
            source.offset;

        destination.interruptionSource =
            source.interruptionSource;

        destination.orderedInterruption =
            source.orderedInterruption;

        destination.canTransitionToSelf =
            source.canTransitionToSelf;

        destination.solo =
            source.solo;

        destination.mute =
            source.mute;
    }

    // ---------------------------------------------------------------------
    // Copy conditions
    // ---------------------------------------------------------------------

    private static void CopyConditions(
        AnimatorStateTransition source,
        AnimatorStateTransition destination
    )
    {
        AnimatorCondition[] conditions =
            source.conditions;

        if (conditions == null)
            return;

        foreach (AnimatorCondition condition in conditions)
        {
            destination.AddCondition(
                condition.mode,
                condition.threshold,
                condition.parameter
            );
        }
    }

    // ---------------------------------------------------------------------
    // Current Animator selection
    // ---------------------------------------------------------------------

    private static AnimatorState GetSelectedAnimatorState()
    {
        return Selection.activeObject as AnimatorState;
    }

    // ---------------------------------------------------------------------
    // Utility
    // ---------------------------------------------------------------------

    private static void ShowNoStateSelectedMessage()
    {
        EditorUtility.DisplayDialog(
            "Animator Transition Copier",
            "Select an Animator State in the Animator graph first.",
            "OK"
        );
    }
}

#endif
