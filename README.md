# Aion 2 Pet Tracker

A passive pet tracker for Aion 2 (Global). It only reads server-to-client network traffic; it never touches the game
process. See `docs/PROTOCOL.md` for what is known about the protocol.

## Data credits

Pet ids, names and genera in `data/pets.json` were imported once from the community database **TH.GL**
(<https://aion2.th.gl/db/pets>), which lists the same pet ids the game server sends. See `data/pets.source.json` for the
source, fetch date and credit line.

Data: TH.GL (aion2.th.gl), game data © NCSOFT.

The import is a one-time, polite fetch of a single list page (`pt-research import-names`, descriptive User-Agent, at most
one request per second, hard cap of 220 requests). The tracker app itself makes no network calls; the per-pet link
button only opens `https://aion2.th.gl/db/pets/pet_<id>` in your browser. Your own names in
`%AppData%\Aion2PetTracker\pet-names.json` always win over the shipped data.
