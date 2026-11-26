using System;
using System.Collections.Generic;
using Content.Shared.Eui;
using Content.Shared.Preferences;
using Robust.Shared.Serialization;

namespace Content.Shared.CMU14.Survivors.Setup;

/// <summary>
///     Which stage of the survivor setup the held group is in. Both stages happen before anyone is spawned.
/// </summary>
[Serializable, NetSerializable]
public enum SurvivorSetupPhase : byte
{
    /// <summary>Players vote on the faction their survivor group will spawn as.</summary>
    Vote,

    /// <summary>The faction is decided; players claim a role and pick the character they spawn as.</summary>
    Pick,
}

/// <summary>One faction the group can vote for, with the votes it currently has.</summary>
[Serializable, NetSerializable]
public sealed class SurvivorPartyOption
{
    public readonly string Id;
    public readonly string Name;
    public readonly int Votes;

    public SurvivorPartyOption(string id, string name, int votes)
    {
        Id = id;
        Name = name;
        Votes = votes;
    }
}

/// <summary>One spawnable role slot in the winning faction's party spawn.</summary>
[Serializable, NetSerializable]
public sealed class SurvivorRoleSlot
{
    public readonly int Index;
    public readonly string Name;
    public readonly bool Leader;

    /// <summary>Name of the player who won this slot, or null while it is free or being raffled.</summary>
    public readonly string? ClaimedBy;

    /// <summary>Names of the players in the raffle for this slot, empty when there is no contest.</summary>
    public readonly List<string> Contestants;

    /// <summary>Seconds until the raffle for this slot resolves, or null when no raffle is running.</summary>
    public readonly int? RaffleSecondsLeft;

    /// <summary>Composition category of the role, used for its accent and as the icon fallback.</summary>
    public readonly SurvivorRoleCategory? Category;

    /// <summary>
    ///     Sprite path and state of the faction's own job icon for this role, or null to fall back to the
    ///     generic category icon. Picked by the server from the winning faction's icon table.
    /// </summary>
    public readonly string? IconSprite;

    public readonly string? IconState;

    /// <summary>True when only a synthetic character may take this role.</summary>
    public readonly bool RequiresSynthetic;

    public SurvivorRoleSlot(int index, string name, bool leader, string? claimedBy,
        List<string> contestants, int? raffleSecondsLeft, SurvivorRoleCategory? category = null,
        string? iconSprite = null, string? iconState = null, bool requiresSynthetic = false)
    {
        RequiresSynthetic = requiresSynthetic;
        IconSprite = iconSprite;
        IconState = iconState;
        Category = category;
        Index = index;
        Name = name;
        Leader = leader;
        ClaimedBy = claimedBy;
        Contestants = contestants;
        RaffleSecondsLeft = raffleSecondsLeft;
    }
}

/// <summary>One saved character this player can spawn as.</summary>
[Serializable, NetSerializable]
public sealed class SurvivorCharacterOption
{
    public readonly int Slot;
    public readonly string Name;

    public SurvivorCharacterOption(int slot, string name)
    {
        Slot = slot;
        Name = name;
    }
}

/// <summary>
///     One member of this player's survivor group, shown to everyone before the group spawns.
///     Only information the group is entitled to at this stage: display name, claimed role, the
///     character they will spawn as, and their faction vote.
/// </summary>
/// <summary>
///     State of the survivor setup window. Sent to every held player, so each one sees the same group
///     roster and the same vote tally.
/// </summary>
[Serializable, NetSerializable]
public sealed class SurvivorSetupEuiState : EuiStateBase
{
    public SurvivorSetupPhase Phase;

    /// <summary>CurTime at which the current phase ends.</summary>
    public TimeSpan PhaseEnd;

    public List<SurvivorPartyOption> Parties = new();

    /// <summary>
    ///     Every faction this round's group could be drawn as, so players can see the whole pool even
    ///     though only two of them are being voted on.
    /// </summary>
    public List<string> AvailableFactions = new();

    /// <summary>Faction this player voted for, or null.</summary>
    public string? MyVote;

    public List<SurvivorRoleSlot> Slots = new();

    /// <summary>Index of the role slot this player claimed, or null.</summary>
    public int? MySlot;

    /// <summary>
    ///     True once this player won a role's raffle: the server has locked them into that role, so no other
    ///     role may be claimed for the rest of the selection.
    /// </summary>
    public bool MySlotLocked;

    public List<SurvivorCharacterOption> Characters = new();

    /// <summary>Saved character slot this player picked; null means a random character.</summary>
    public int? MyCharacter;

    /// <summary>
    ///     True when this player's currently selected character is a synthetic, which is what a synthetic-only
    ///     survivor role requires.
    /// </summary>
    public bool MyCharacterIsSynthetic;

    /// <summary>The whole survivor group, including this player. Available before anyone is spawned.</summary>
    /// <summary>
    ///     One line per teammate, already formatted by the server. Deliberately plain strings: a custom member
    ///     type in this state did not survive serialisation, which left the group panel empty.
    /// </summary>
    public List<string> Roster = new();

    /// <summary>Name of the faction the group will spawn as once the vote resolved, else null.</summary>
    public string? ResolvedFaction;

    public bool HasLoadout;

    /// <summary>Armor entity prototype ids this faction offers.</summary>
    public List<string> ArmorOptions = new();

    public List<string> HelmetOptions = new();

    public List<string> FirearmOptions = new();

    /// <summary>This player's current armor selection (entity prototype id), or null.</summary>
    public string? MyArmor;

    public string? MyHelmet;

    public string? MyFirearm;

    /// <summary>True once this player confirmed their loadout; the selection is final after that.</summary>
    public bool MyLoadoutConfirmed;

    /// <summary>Character selection unlocks after the loadout is confirmed.</summary>
    public bool CanPickCharacter = true;

    /// <summary>Length of the current phase in seconds, so the client can draw an accurate progress bar.</summary>
    public float PhaseSeconds = 1f;

    /// <summary>
    ///     True when the winning faction requires a Predator round but it could not be started. The UI warns
    ///     the group instead of pretending the hunt exists.
    /// </summary>
    public bool PredatorRoundFailed;
}

[Serializable, NetSerializable]
public sealed class SurvivorVoteMsg : EuiMessageBase
{
    public readonly string PartyId;

    public SurvivorVoteMsg(string partyId)
    {
        PartyId = partyId;
    }
}

[Serializable, NetSerializable]
public sealed class SurvivorClaimSlotMsg : EuiMessageBase
{
    public readonly int Slot;

    public SurvivorClaimSlotMsg(int slot)
    {
        Slot = slot;
    }
}

[Serializable, NetSerializable]
public sealed class SurvivorPickCharacterMsg : EuiMessageBase
{
    /// <summary>Saved character slot, or null for a random character.</summary>
    public readonly int? Slot;

    public SurvivorPickCharacterMsg(int? slot)
    {
        Slot = slot;
    }
}

/// <summary>Categories a survivor can change in their faction loadout.</summary>
[Serializable, NetSerializable]
public enum SurvivorLoadoutCategory : byte
{
    Armor,
    Helmet,
    Firearm,
}

/// <summary>Selects one option in a loadout category. The server ignores anything not in the faction's list.</summary>
[Serializable, NetSerializable]
public sealed class SurvivorSelectLoadoutMsg : EuiMessageBase
{
    public readonly SurvivorLoadoutCategory Category;
    public readonly string ProtoId;

    public SurvivorSelectLoadoutMsg(SurvivorLoadoutCategory category, string protoId)
    {
        Category = category;
        ProtoId = protoId;
    }
}

/// <summary>Locks the current selection; after this the loadout cannot be changed.</summary>
[Serializable, NetSerializable]
public sealed class SurvivorConfirmLoadoutMsg : EuiMessageBase
{
}
