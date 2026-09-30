# Slippi patch

`0001-spire-duel-control.patch` targets project-slippi/Ishiiruka commit
`60f7b63496fb6ec7b9180a04f16f3edc0ad89fe2` on its `slippi` branch.

It is the first half of the automatic-duel control path. The patch:

- accepts `--spire-duel <duel.json>` without changing normal launches;
- validates the bridge's duel ID, Direct connect code, characters, legal stage,
  and one-stock rule;
- replaces the in-game matchmaking request with the supplied Direct code;
- applies both character selections, the selected stage, and one stock.

It does not yet make the injected Melee menu issue its first find-opponent
request. A follow-up Gecko/EXI change must jump directly into the online scene
and initiate that request before this is hands-free.

Apply it from the root of the matching Ishiiruka checkout:

```bash
git apply /path/to/spire-showdown/slippi-patch/0001-spire-duel-control.patch
```

This patch is experimental until both Bazzite and Windows Slippi builds pass an
actual mixed-platform Direct match.
