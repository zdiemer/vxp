using Vxp.Input;
using Xunit;

namespace Vxp.Tests;

public class KeyNameTests
{
    [Theory]
    [InlineData("Space")]
    [InlineData("Escape")]
    [InlineData("Return")]
    [InlineData("Backspace")]
    [InlineData("Tab")]
    [InlineData("Delete")]
    [InlineData("Left")]
    [InlineData("Right")]
    [InlineData("Up")]
    [InlineData("Down")]
    [InlineData("PageUp")]
    [InlineData("PageDown")]
    [InlineData("F1")]
    [InlineData("F11")]
    [InlineData("F12")]
    [InlineData("A")]
    [InlineData("Z")]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("Minus")]
    [InlineData("Equals")]
    [InlineData("Comma")]
    [InlineData("Period")]
    [InlineData("NumpadPlus")]
    public void NamesRoundTripThroughKeycodes(string name)
    {
        Assert.True(KeyNames.TryParse(name, out var keycode));
        Assert.Equal(name, KeyNames.Name(keycode));
    }

    [Fact]
    public void EnterIsAcceptedAsAnAliasForReturn()
    {
        Assert.True(KeyNames.TryParse("Enter", out var enter));
        Assert.True(KeyNames.TryParse("Return", out var @return));
        Assert.Equal(@return, enter);
    }

    [Fact]
    public void LetterNamesAreCaseInsensitive()
    {
        Assert.True(KeyNames.TryParse("a", out var lower));
        Assert.True(KeyNames.TryParse("A", out var upper));
        Assert.Equal(upper, lower);
    }

    [Fact]
    public void UnknownNamesAreRejected()
        => Assert.False(KeyNames.TryParse("NotAKey", out _));

    [Fact]
    public void EveryAdvertisedNameParses()
    {
        foreach (var name in KeyNames.KnownNames)
            Assert.True(KeyNames.TryParse(name, out _), $"'{name}' is listed but does not parse.");
    }
}

public class BindingTests
{
    [Theory]
    [InlineData("Space")]
    [InlineData("Escape")]
    [InlineData("F12")]
    [InlineData("Ctrl+Q")]
    [InlineData("Shift+Left")]
    [InlineData("Alt+Return")]
    [InlineData("Ctrl+Alt+Shift+A")]
    [InlineData("Pad:A")]
    [InlineData("Pad:DPadUp")]
    [InlineData("Pad:Start")]
    [InlineData("Pad:LeftTrigger+")]
    [InlineData("Pad:RightTrigger-")]
    public void BindingsRoundTripThroughText(string text)
    {
        Assert.True(Binding.TryParse(text, out var binding));
        Assert.Equal(text, binding.ToString());
    }

    [Fact]
    public void ModifiersAreRecognisedInAnyOrder()
    {
        Assert.True(Binding.TryParse("Shift+Ctrl+A", out var a));
        Assert.True(Binding.TryParse("Ctrl+Shift+A", out var b));
        Assert.Equal(a, b);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, a.Modifiers);
    }

    [Fact]
    public void ControllerAxisDirectionIsPreserved()
    {
        Assert.True(Binding.TryParse("Pad:LeftTrigger+", out var positive));
        Assert.True(Binding.TryParse("Pad:LeftTrigger-", out var negative));

        Assert.True(positive.Positive);
        Assert.False(negative.Positive);
        Assert.NotEqual(positive, negative);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Pad:NotAButton")]
    [InlineData("Hyper+A")]
    [InlineData("NotAKey")]
    public void MalformedBindingsAreRejected(string text)
        => Assert.False(Binding.TryParse(text, out _));

    [Fact]
    public void NullIsRejected()
        => Assert.False(Binding.TryParse(null, out _));
}

public class InputMapTests
{
    [Fact]
    public void DefaultsBindEveryActionExceptNone()
    {
        var map = InputMap.CreateDefault();

        foreach (var action in InputActions.All)
            Assert.NotEmpty(map.BindingsFor(action));
    }

    [Fact]
    public void SettingsRoundTripPreservesEveryBinding()
    {
        var original = InputMap.CreateDefault();
        var restored = InputMap.FromSettings(original.ToSettings());

        foreach (var action in InputActions.All)
            Assert.Equal(original.BindingsFor(action), restored.BindingsFor(action));
    }

    [Fact]
    public void ActionsMissingFromSettingsKeepTheirDefaults()
    {
        var map = InputMap.FromSettings(new Dictionary<string, List<string>>
        {
            ["TogglePause"] = ["Pad:A"],
        });

        Assert.Equal([Binding.Button(ControllerButton.A)], map.BindingsFor(InputAction.TogglePause));
        Assert.NotEmpty(map.BindingsFor(InputAction.NextTrack));
    }

    [Fact]
    public void AnEmptyBindingListLeavesAnActionUnbound()
    {
        var map = InputMap.FromSettings(new Dictionary<string, List<string>>
        {
            ["Screenshot"] = [],
        });

        Assert.Empty(map.BindingsFor(InputAction.Screenshot));
        Assert.Equal("unbound", map.Describe(InputAction.Screenshot));
    }

    [Fact]
    public void UnparseableBindingsAreDroppedRatherThanCrashing()
    {
        var map = InputMap.FromSettings(new Dictionary<string, List<string>>
        {
            ["Screenshot"] = ["F12", "TotalNonsense"],
        });

        Assert.Single(map.BindingsFor(InputAction.Screenshot));
    }

    [Fact]
    public void RebindingReplacesEveryExistingControl()
    {
        var map = InputMap.CreateDefault();
        map.Rebind(InputAction.TogglePause, Binding.Key(KeyOf("P")));

        Assert.Equal([Binding.Key(KeyOf("P"))], map.BindingsFor(InputAction.TogglePause));
    }

    [Fact]
    public void AddingABindingKeepsTheExistingOnes()
    {
        var map = InputMap.CreateDefault();
        var before = map.BindingsFor(InputAction.TogglePause).Count;

        map.AddBinding(InputAction.TogglePause, Binding.Key(KeyOf("P")));
        Assert.Equal(before + 1, map.BindingsFor(InputAction.TogglePause).Count);

        // Adding the same control twice should not duplicate it.
        map.AddBinding(InputAction.TogglePause, Binding.Key(KeyOf("P")));
        Assert.Equal(before + 1, map.BindingsFor(InputAction.TogglePause).Count);
    }

    [Fact]
    public void ResettingRestoresTheShippedBinding()
    {
        var map = InputMap.CreateDefault();
        var original = map.BindingsFor(InputAction.NextTrack).ToArray();

        map.Rebind(InputAction.NextTrack, Binding.Key(KeyOf("P")));
        map.ResetToDefault(InputAction.NextTrack);

        Assert.Equal(original, map.BindingsFor(InputAction.NextTrack));
    }

    [Fact]
    public void ConflictsReportOtherActionsSharingAControl()
    {
        var map = InputMap.CreateDefault();
        var escape = Binding.Key(KeyOf("Escape"));

        var conflicts = map.Conflicts(escape, InputAction.ToggleMenu);

        Assert.Contains(InputAction.MenuBack, conflicts);
        Assert.DoesNotContain(InputAction.ToggleMenu, conflicts);
    }

    [Fact]
    public void MatchingPrefersTheBindingWithModifiers()
    {
        var map = InputMap.CreateDefault();

        // Right alone skips a track; Shift+Right seeks.
        var plain = map.MatchKey(KeyOf("Right"), KeyModifiers.None);
        var shifted = map.MatchKey(KeyOf("Right"), KeyModifiers.Shift);

        Assert.Contains(InputAction.NextTrack, plain);
        Assert.DoesNotContain(InputAction.SeekForward, plain);

        Assert.Contains(InputAction.SeekForward, shifted);
        Assert.DoesNotContain(InputAction.NextTrack, shifted);
    }

    [Fact]
    public void AControllerButtonMatchesEveryActionBoundToIt()
    {
        var map = InputMap.CreateDefault();
        var actions = map.MatchButton((int)ControllerButton.A);

        Assert.Contains(InputAction.TogglePause, actions);
        Assert.Contains(InputAction.MenuSelect, actions);
    }

    [Fact]
    public void AxisMatchingRespectsDirection()
    {
        var map = InputMap.CreateDefault();

        Assert.Contains(InputAction.FastForward, map.MatchAxis((int)ControllerAxis.RightTrigger, positive: true));
        Assert.Empty(map.MatchAxis((int)ControllerAxis.RightTrigger, positive: false));
    }

    private static int KeyOf(string name)
    {
        Assert.True(KeyNames.TryParse(name, out var code));
        return code;
    }
}

public class InputRouterTests
{
    /// <summary>
    /// Escape is bound to both ToggleMenu and MenuBack. Firing both from one press would
    /// open the menu and immediately close it again.
    /// </summary>
    [Fact]
    public void EscapeOpensTheMenuWhenNoneIsOpen()
    {
        var candidates = new[] { InputAction.ToggleMenu, InputAction.MenuBack };

        Assert.Equal(InputAction.ToggleMenu, InputRouter.Resolve(candidates, menuOpen: false));
    }

    [Fact]
    public void EscapeGoesBackWhenAMenuIsOpen()
    {
        var candidates = new[] { InputAction.ToggleMenu, InputAction.MenuBack };

        Assert.Equal(InputAction.MenuBack, InputRouter.Resolve(candidates, menuOpen: true));
    }

    [Fact]
    public void EscapeStillGoesBackWhenOnlyToggleMenuIsBound()
    {
        Assert.Equal(InputAction.MenuBack, InputRouter.Resolve([InputAction.ToggleMenu], menuOpen: true));
    }

    [Fact]
    public void ArrowKeysWorkTheTransportDuringPlayback()
    {
        var candidates = new[] { InputAction.MenuUp, InputAction.VolumeUp };

        Assert.Equal(InputAction.VolumeUp, InputRouter.Resolve(candidates, menuOpen: false));
    }

    [Fact]
    public void ArrowKeysMoveTheHighlightInsideAMenu()
    {
        var candidates = new[] { InputAction.MenuUp, InputAction.VolumeUp };

        Assert.Equal(InputAction.MenuUp, InputRouter.Resolve(candidates, menuOpen: true));
    }

    [Fact]
    public void PlaybackControlsDoNotFireBehindAnOpenMenu()
        => Assert.Null(InputRouter.Resolve([InputAction.NextTrack], menuOpen: true));

    [Fact]
    public void MenuOnlyControlsDoNothingDuringPlayback()
        => Assert.Null(InputRouter.Resolve([InputAction.MenuPageDown], menuOpen: false));

    [Fact]
    public void AnUnboundControlResolvesToNothing()
    {
        Assert.Null(InputRouter.Resolve([], menuOpen: false));
        Assert.Null(InputRouter.Resolve([], menuOpen: true));
    }

    [Fact]
    public void EveryDefaultBindingResolvesToSomethingInAtLeastOneState()
    {
        var map = InputMap.CreateDefault();

        foreach (var action in InputActions.All)
        {
            foreach (var binding in map.BindingsFor(action))
            {
                var candidates = binding.Kind == BindingKind.Key
                    ? map.MatchKey(binding.Code, binding.Modifiers)
                    : map.MatchButton(binding.Code);

                if (candidates.Count == 0) continue;

                var resolved = InputRouter.Resolve(candidates, menuOpen: false)
                               ?? InputRouter.Resolve(candidates, menuOpen: true);

                Assert.NotNull(resolved);
            }
        }
    }
}
