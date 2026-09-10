namespace Vxp.Input;

/// <summary>
/// Decides which single action a control means, given that one control may be bound to
/// several.
/// </summary>
/// <remarks>
/// <para>
/// Binding a control twice is deliberate rather than a mistake. The arrow keys work the
/// transport during playback and move the highlight once a menu is open; Escape opens the
/// menu and then backs out of it. Firing every matching action at once would let Escape
/// open and immediately close the menu, so exactly one action wins per press and which
/// one depends on whether a menu is up.
/// </para>
/// </remarks>
public static class InputRouter
{
    /// <summary>Actions that only mean anything while a menu is open.</summary>
    public static bool IsMenuNavigation(InputAction action) => action
        is InputAction.MenuUp or InputAction.MenuDown
        or InputAction.MenuLeft or InputAction.MenuRight
        or InputAction.MenuSelect or InputAction.MenuBack
        or InputAction.MenuPageUp or InputAction.MenuPageDown
        or InputAction.MenuResetItem;

    /// <summary>
    /// Picks the action to run, or null if the control does nothing in this state.
    /// </summary>
    /// <param name="candidates">Every action the control is bound to.</param>
    /// <param name="menuOpen">Whether a menu is currently open.</param>
    public static InputAction? Resolve(IReadOnlyList<InputAction> candidates, bool menuOpen)
    {
        if (menuOpen)
        {
            foreach (var candidate in candidates)
            {
                // While a menu is open, the menu key means "go back" rather than "open".
                var action = candidate == InputAction.ToggleMenu ? InputAction.MenuBack : candidate;
                if (IsMenuNavigation(action)) return action;
            }

            // Anything else is swallowed, so playback controls do not fire behind a menu.
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (!IsMenuNavigation(candidate)) return candidate;
        }

        return null;
    }
}
