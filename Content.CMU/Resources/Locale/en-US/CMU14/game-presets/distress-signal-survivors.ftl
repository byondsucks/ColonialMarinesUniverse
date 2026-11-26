cmu-distress-signal-survivors-spawning =
    {$count ->
        [one] {$count} survivor is scheduled to spawn at round start.
       *[other] {$count} survivors are scheduled to spawn at round start.
    }

cmu-distress-signal-no-survivors = No survivors are scheduled to spawn at round start.

cmu-survivor-no-body-return-to-lobby = No survivor slot was available for you. You have been returned to the lobby.

# Civilian survivors: a separate population with its own allocation (1-4), never part of the faction group.
# Civilian survivor briefing. { $colony } is the colony the round landed on.