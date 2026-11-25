# Treasure Hunter for the player's units, and worth equipping

STATUS: PLAN. Part 1 is being built (TM-25, go from Duet on 2026-11-25); part 2 is not
started, and nothing here has been watched in the game beyond part 1's gates. Two parts,
both decided, worked in this order: first keep the free ability away from enemies (TM-25,
chosen 2026-11-25 on Duet's review), then make an equipped Treasure Hunter find more rares
(TM-24, then TM-23, chosen by the owner on 2026-11-24). Ledger rows are in [TODO.md](TODO.md).

## Part 1: enemies get none (TM-25)

### In plain words

A player reported this and the owner saw it in the game: with All units on, enemies of the
same jobs get Treasure Hunter too. The mod adds the ability to the game's job list, and the
game builds enemies from that same list, so an enemy that ends a move on a hidden treasure
takes it before the player can.

The fix: the All units setting stops changing the job list. In each battle the mod gives the
ability to the units the player deployed, and to nobody else.

### What the research found

One read of the game's code, plus the original PlayStation version, which works the same
way. Nothing below about treasure has been seen live yet, and the facts behind it are owed to
the fact keeper, so this part keeps no addresses either.

- Whether a unit picks up treasure is decided when it stops moving, from one switch on that
  unit. The job list is not looked at then; it matters only because the game copies it onto
  every unit when the battle starts.
- The player's deployed units sit in their own block of seats in the battle, and the game
  builds enemies only into the seats below it. Every unit also carries a team marker.
- Turning a movement switch on in the middle of a battle has been seen to work: Living
  Weapons once granted Teleport that way, on an older game build. Turning one off has a
  warning sign: a monster kept floating after its movement switches were zeroed.

### The solution

Chosen 2026-11-25, on Duet's review of the proposal.

- The All units setting stops changing the job list.
- In each battle the mod turns the switch on for every unit the player deployed, whatever
  its job and recruited monsters included, and holds it on until the battle ends. A unit
  qualifies only when it sits in a player seat, its team marker says it fights for the
  player right now, and it is on the field.
- Chemists are skipped. They pick up treasure with nothing equipped anyway, and skipping
  them leaves an equipped Treasure Hunter worth the game's Chemist bonus, which is what
  step zero in part 2 wants.
- A charmed player unit fights for the enemy, so it has no free ability while charmed and
  gets it back when the charm ends.
- Guests, the computer-run allies such as Agrias early on, stay out for now. Gate 4 tells
  whether a guest's find reaches the player's bag; if it does, guests can come in later.
- If anything cannot be read, or a unit does not look right, the mod writes nothing and
  that unit plays as in the unmodded game.
- The setting's description is rewritten. The Treasure Hunter tag leaves the Formation
  screen, because it comes from the job list, and the warning about enemies goes. Units
  still pick up treasure in battle.
- The jobs left out today, for having no free slot in the job list or for sharing a row
  with enemies, get it too, since every deployed unit qualifies.
- The setting no longer needs the Mod Loader; Rare treasure only still does. It also
  follows Enable Treasure Master, and a guest marked unit is skipped and named in the log.

What it does not change:

- Enemy Chemists. The same code read says the unmodded game lets every Chemist pick up
  treasure with nothing equipped, enemies included. Only a patch on the game's code could
  stop that.
- An enemy that really has Treasure Hunter equipped still picks up treasure, as in the
  unmodded game.

### Gates

All live and all on the go, and only while no other session is driving the game and no
other copy of it is open.

1. The switch alone decides it. A throwaway test with no mod build, both ways:
   - With the setting off, turn the switch on for one of the player's units that is not a
     Chemist and has another movement ability equipped. It picks up a treasure, and its
     own movement ability still works.
   - With the setting on, turn the switch off for one of the player's units of a granted
     job. It walks onto a treasure and does not pick it up. If it does, the game reads
     something besides the switch; write down what.
2. The switch stays put. Watched without writing: right after the battle starts, when the
   game copies the job list onto every unit; through an enemy turn, since the enemy's
   thinking puts saved unit records back while it tries moves out; and through a Retry and
   a suspend and resume. If the switch does not hold, stop there and report.
3. After the build, an enemy of a standard job walked onto a lit tile leaves the treasure
   for the player; Battle Brain's hands mode lets the owner play the enemy side. A charmed
   player unit is checked once, so its missing ability is not filed as a new bug.
4. For information: with today's build, when an enemy or a guest picks up a treasure, does
   the item land in the player's bag? It decides whether guests come in later.

Results so far, 2026-11-25, on Grogh Heights with the Mod Loader off:

- Gate 1's first half passed. With the switch turned on and nothing else granting it, Ramza,
  with Move +3 equipped, picked up a Potion the moment he stopped on a treasure tile.
- Gate 2 held through the battle start and five enemy turns. The game wrote each player
  unit's movement switches once, about seven seconds after the map loaded and before any
  turn, and never touched Ramza's switch again.
- A seat the game uses while it builds the party reads as empty, so the mod picks units by
  their on field and seat filled markers, never by seat number alone.
- A battle restart rebuilds every unit about half a second later, once, and the switch comes
  back off with it, as expected. The mod's hold puts it back long before anyone moves.
- Still open: gate 1's second half and gate 4, which need the Mod Loader on, and gate 2's
  suspend and resume.

### What was not chosen

- Taking the ability back from enemies each battle. When it misses, it falls back to the
  very bug being fixed, and turning a switch off has the warning sign above.
- A small patch on the game's pick-up check. It would be the mod's first change to game
  code, so it stays the fallback if gate 1 or 2 fails, as with the lucky turn.
- Dropping the standard jobs from the grant. It removes the problem and most of what the
  setting is for.
- Putting a treasure back after an enemy takes it. It fights the game's own record of what
  has been found.

### Owed before any code

- The research leads go to the fact keeper, together with part 2's.
- Gates 1 and 2 pass.

## Part 2: equipped hunters find more rares (TM-24, TM-23)

### In plain words

A forum player asked for this: when the All units setting lets everybody pick up tile
treasure, a unit who spends its movement slot on Treasure Hunter should find the rare item
more often than a unit who only has the ability for free. Today equipping it is worth
nothing, so the slot is wasted.

It is possible. The work goes in two steps, in this order:

1. **Step zero (TM-24).** Take Chemists out of the All units grant, then check in the game
   that the Chemist bonus works the way the research says.
2. **The lucky turn (TM-23).** Give anyone who equips Treasure Hunter a chance at a lucky
   turn, where any treasure found is the rare one.

### What the research found

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

### The path the owner chose

Choice A, after step zero. Decided 2026-11-24.

#### Step zero (TM-24)

Part 1 goes first, and its per-battle grant already skips Chemists, so by the time this step
starts the change below is made and only the check in the game is left.

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

#### The lucky turn (TM-23)

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

#### What was not chosen

- **A small patch on the game's rare roll.** It gives exact odds on the game's own dice and
  is the smaller change, but it would be the first time this mod alters game code. The
  mod's worst case today is a wrong highlight or wrong odds; a bad patch can crash the
  game on a treasure tile. It stays the fallback if gate 2 fails.
- **Lowering the unit's Brave for a moment.** Brave also feeds reaction abilities.
- **Marking every job as a Chemist.** The game reads that from a job's kind, which other
  code leans on, and a shipped data file cannot sit behind a setting.

### Owed before any code

- Part 1 (TM-25) lands first.
- The leads from the research go to the fact keeper, marked as one code read and not yet
  seen live. Step zero's check is the first live proof for the bonus.
- TM-24 is promoted to Now with its Done means and Verify, then built and checked, before
  TM-23 is promoted.
