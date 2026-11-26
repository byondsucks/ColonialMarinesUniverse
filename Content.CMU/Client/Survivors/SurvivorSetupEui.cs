using Content.Client.Eui;
using Content.Shared.CMU14.Survivors.Setup;
using Content.Shared.Eui;
using JetBrains.Annotations;

namespace Content.Client.CMU14.Survivors;

/// <summary>
///     Client side of the survivor setup window. Shares its type name with the server EUI so the EUI
///     manager pairs them. The server owns the vote and the roster; this only forwards picks.
/// </summary>
[UsedImplicitly]
public sealed class SurvivorSetupEui : BaseEui
{
    private readonly SurvivorSetupWindow _window;
    private bool _shown;

    public SurvivorSetupEui()
    {
        _window = new SurvivorSetupWindow();
        _window.OnVote += party => SendMessage(new SurvivorVoteMsg(party));
        _window.OnClaim += slot => SendMessage(new SurvivorClaimSlotMsg(slot));
        _window.OnCharacter += slot => SendMessage(new SurvivorPickCharacterMsg(slot));
        _window.OnSelectLoadout += (category, protoId) => SendMessage(new SurvivorSelectLoadoutMsg(category, protoId));
        _window.OnConfirmLoadout += () => SendMessage(new SurvivorConfirmLoadoutMsg());
    }

    public override void Opened()
    {
        base.Opened();

        // The window is opened by the first state instead of here: opening an unpopulated window showed a
        // blank frame for a moment before the vote/role data arrived.
    }

    public override void Closed()
    {
        base.Closed();

        if (!_shown)
            return;

        _shown = false;
        _window.Close();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is not SurvivorSetupEuiState cast)
            return;

        _window.UpdateState(cast);

        if (_shown)
            return;

        // First populated state: put the window on screen already filled in.
        _shown = true;
        _window.OpenCentered();
    }
}
