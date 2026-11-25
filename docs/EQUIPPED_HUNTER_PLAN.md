# Equipped Treasure Hunter finds rares more often

STATUS: PLAN. The path was decided by the owner on 2026-11-24. Nothing here is built and
nothing here has been watched in the game. Ledger rows: TM-24 (step zero), then TM-23 (the
lucky turn), both in [TODO.md](TODO.md).

## In plain words

A forum player asked for this: when the All units setting lets everybody pick up tile
treasure, a unit who spends its movement slot on Treasure Hunter should find the rare item
more often than a unit who only has the ability for free. Today equipping it is worth
nothing, so the slot is wasted.

It is possible. The work goes in two steps, in this order:

1. **Step zero (TM-24).** Take Chemists out of the All units grant, then check in the game
   that the Chemist bonus works the way the research says.
2. **The lucky turn (TM-23).** Give anyone who equips Treasure Hunter a chance at a lucky
   turn, where any treasure found is the rare one.

## What the research found

One read of the game's code on 2026-11-24, on the Steam build current that day. None of it
has been seen live. The game facts behind these lines are leads owed to the fact keeper,
so this page keeps only the plain outline the plan needs and no addresses.

- The rare chance appears to be 100 minus the unit's Brave. A Brave 97 unit sees the rare
  item 3 times in 100.
- A Chemist picks up tile treasure with nothing equipped. Every other job needs Treasure
  Hunter to pick up at all.
- The game appears to add 25 points of rare chance, capped at 100, when two things are
  true at once: the unit is a Chemist and the unit has Treasure Hunter. Either one alone
  adds nothing, and no other job ever gets the bonus.
- The All units grant appears to count as having Treasure Hunter. So with All units on,
  every Chemist gets the 25 points with nothing equipped, and equipping the ability adds
  nothing for anybody.
- The mod can tell an equipped Treasure Hunter from a granted one. They are kept in
  different places on the unit.

Today, at Brave 70, where the plain rare chance is 30 in 100:

| Unit | All units off | All units on |
|---|---|---|
| Chemist, nothing equipped | picks up, 30 | picks up, 55, the bonus for free |
| Chemist, Treasure Hunter equipped | picks up, 55 | picks up, 55, nothing gained |
| Other job, nothing equipped | cannot pick up | picks up, 30 |
| Other job, Treasure Hunter equipped | picks up, 30 | picks up, 30, nothing gained |

## The path the owner chose

Choice A, after step zero. Decided 2026-11-24.

### Step zero (TM-24)

The Chemist row leaves the list of job rows that All units grants Treasure Hunter to.
Chemists still pick up treasure, because the game lets them with nothing equipped. What
changes is one cell of the table above: a Chemist with nothing equipped goes back to the
plain chance, so equipping Treasure Hunter is worth 25 points to a Chemist again. That is
the game's own rule, working as it does with the mod off. No new machinery.

Then the check in the game. Odds are a dice matter, so the check uses the two ends where
the dice have no say:

- At Brave 25 or lower the bonus makes the rare certain. A Chemist with Treasure Hunter
  equipped must get the rare on every claim; one common item refutes the research.
- At Brave 100 the plain chance is nothing. A Chemist with nothing equipped must get the
  common item on every claim; one rare item means the free bonus is still there.

Claiming and then resetting the battle spends no treasure, so the same four tiles serve
for as many claims as the check needs. How the Brave values are reached is settled when
the row is promoted.

If the check disagrees with the research, stop. The lucky turn below leans on the same
reading of the game, so it is re-planned before any of it is built.

### The lucky turn (TM-23)

One new number in the mod's settings, 0 to 100, 0 by default, which is off. It is how
often an equipped hunter's turn is a lucky one.

- The mod leaves the game's dice alone and watches whose turn it is.
- When that unit is one of the player's and has Treasure Hunter in its equipped movement
  slot, the mod rolls its own dice once for the turn.
- On a lucky turn the mod holds the rare item on both sides of this battle's treasure
  tiles, so the game's roll gives the rare either way. When the turn ends, or the unit
  taking its turn is not an equipped hunter, the mod holds the tiles' own two items.
- The hold is stateless. Every tick the mod works out what the tiles should hold and
  writes only where they differ, so a Retry or a resumed battle cannot leave a lucky turn
  behind.
- If anything the mod needs cannot be read, it writes nothing new and puts the tiles' own
  items back.

A granted Treasure Hunter never makes a turn lucky. The setting does nothing while Rare
treasure only is on, and it does not need All units to be on.

What a player gets, as chances in 100, with step zero done:

| Unit, Brave 70 | Setting 0 | Setting 25 | Setting 100 |
|---|---|---|---|
| Other job, granted only | 30 | 30 | 30 |
| Other job, equipped | 30 | 48 | 100 |
| Chemist, nothing equipped | 30 | 30 | 30 |
| Chemist, equipped | 55 | 66 | 100 |

The lucky turn stacks on the game's own roll: the total is the game's chance, plus the
setting's share of what is left. A Chemist keeps the game's 25 points on top, since the mod
does not touch the game's roll.

Gates before any code, all live and all on the owner's go:

1. A write to the battle's treasure tiles in the middle of a battle changes what a claim
   gives.
2. The mod can tell whose turn it is from the moment the turn opens, well before a move
   ends.
3. A granted Treasure Hunter does not read as an equipped one.
4. What the battle's treasure tiles hold after a Retry, and after a suspend and a resume.

The build is checked live with the setting at 100, where two claims settle it and no
counting of dice is needed.

### What was not chosen

- **A small patch on the game's rare roll.** It gives exact odds on the game's own dice and
  is the smaller change, but it would be the first time this mod alters game code. The
  mod's worst case today is a wrong highlight or wrong odds; a bad patch can crash the
  game on a treasure tile. It stays the fallback if gate 2 fails.
- **Lowering the unit's Brave for a moment.** Brave also feeds reaction abilities.
- **Marking every job as a Chemist.** The game reads that from a job's kind, which other
  code leans on, and a shipped data file cannot sit behind a setting.

## Owed before any code

- The leads from the research go to the fact keeper, marked as one code read and not yet
  seen live. Step zero's check is the first live proof for the bonus.
- TM-24 is promoted to Now with its Done means and Verify, then built and checked, before
  TM-23 is promoted.
