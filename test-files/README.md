# Ignore rule fixtures

A small tree for checking `.protonignore` and per folder `.gitignore` by hand, the way a user would
hit them. Copy the contents of this folder into a sync root and see what turns up in the cloud.
The syntax itself is documented in [PROTONIGNORE.md](../PROTONIGNORE.md).

## How to run it

Rules only apply to items that are not yet indexed. A file that is already in sync stays in sync,
even if a rule would exclude it now. So the order matters:

1. Start from a sync root that does not contain any of these files yet.
2. Copy the whole tree in, `.protonignore` included, before the client indexes it.
3. Let it settle, then compare against the table below in the web client.

Adding the rules afterwards to an already synced tree is a different test, and a valid one, it just
has a different expected result: nothing disappears.

## The rules under test

`.protonignore` on the sync root:

| Rule | What it covers |
| --- | --- |
| `*.log` | extension, at any depth |
| `!keep.log` | an exception to the rule above |
| `node_modules/` | directory only, at any depth |
| `/build` | anchored, the sync root only |
| `!dist/` | brings back what a deeper `.gitignore` excludes |

`myrepo/.gitignore` excludes `dist/` and `*.tmp` for that subtree.

## Expected result

| File | Expected | Why |
| --- | --- | --- |
| `sync-me.txt` | synced | no rule matches |
| `keep.log` | synced | `*.log` matches, `!keep.log` takes it back |
| `skip-me.log` | excluded | `*.log` |
| `build/output.bin` | excluded | `/build` is anchored and this is the root `build` |
| `sub/build/keeper.txt` | synced | the same anchor does not reach a nested `build` |
| `node_modules/react/index.js` | excluded | `node_modules/` matches at any depth |
| `myrepo/src/app.ts` | synced | no rule matches |
| `myrepo/scratch.tmp` | excluded | `*.tmp` from `myrepo/.gitignore` |
| `myrepo/dist/bundle.js` | synced | `.gitignore` excludes `dist/`, `.protonignore` outranks it with `!dist/` |
| `.protonignore`, `myrepo/.gitignore` | synced | ignore files are ordinary files unless they list themselves |

The last two rows are the interesting ones. `myrepo/dist/bundle.js` is the case for the precedence
order, a repository excludes its build output but you still want it in the cloud. And an ignore file
that is not listed in its own rules gets synced like anything else, which surprises people.

## A note on committing to this repository

Five of these files match patterns in the repository's own `.gitignore`, so git drops them unless
something is done about it. `!/test-files/**` at the end of that file handles most of them.
`myrepo/dist/bundle.js` and `myrepo/scratch.tmp` are not covered by it, because a `.gitignore` in a
subdirectory outranks the root one, and weakening `myrepo/.gitignore` would destroy the very case it
is here to test. Those two were added with `git add -f`.

They are tracked now, so edits to them show up normally. Only a new file with such a name would need
the same treatment again. Which is, more or less, the bug this whole fork started with.
