# Story page browser check

The scripted browser check for the story-permalinks epic (`/stories/{storyId}`). Run it in a browser (or the in-app
browser) against a running API before an epic story is closed. Each later story of the epic appends its steps here.

## Setup

1. Run the API with a database, for example with `Auth:Enabled=false` on `http://localhost:5085`.
2. Create test data through the API or the board:
   - project **Demo**, and a second project **Other** (so the app's first project is not the story's);
   - epic **Permalinks** in Demo;
   - story **Story page** in Demo, epic Permalinks, priority High, estimate 3, labels `ui, web`, a description, and
     one comment.
3. Note the story's id (`GET /api/projects/{demoId}` lists it).

## Steps

| # | Step | Expected |
|---|------|----------|
| 1 | Open `/stories/{storyId}` directly in a new tab. | The app loads (no 404, styles applied). The page shows the breadcrumb `Demo › Permalinks › Story page`, the title, Type, Status, Priority High, Epic Permalinks, Estimate 3, Labels `ui, web`, the description and the comment. The sidebar highlights Demo. |
| 2 | Change the title and set Priority to Critical. Wait a second. | The eyebrow briefly shows `· saving…`. `GET /api/items/{storyId}` returns the new title and `Critical`. |
| 3 | Add a comment (author + body) and press **Add Comment**. | The comment appears under the existing one; the draft clears. |
| 4 | Click **Permalinks** in the breadcrumb. | The URL is `/?project={demoId}&epic={permalinksId}` and the board shows the Permalinks epic with the story's card. |
| 5 | Press browser Back, then Forward. | Back shows the story page again at `/stories/{storyId}`; Forward returns to the epic-scoped board. |
| 6 | On the story page click **Demo** in the breadcrumb. | The URL is `/?project={demoId}` and the board shows all of Demo (no epic selected). |
| 7 | On the board, open the story's card. In the slideout, type into the title and **immediately** click the open-as-page button (the expand icon left of Delete). | The slideout closes and the URL is `/stories/{storyId}`. The page shows the title just typed (the pending edit was saved first). |
| 8 | Press browser Back. | The board is shown again at its previous URL, with the story's slideout open (`…&story={storyId}`, since sc-26159). |
| 9 | Open `/stories/00000000-0000-0000-0000-000000000001`. | The app loads and shows **Story not found** / "There is no story with this id", with a **Back to board** button. |
| 10 | Click **Back to board**. | The board is shown at `/?project=…`. |
| 11 | Open `/stories/not-a-guid`, `/stories/{storyId without its hyphens}`, `/api/does-not-exist` and `/styles.css`. | The first three are plain 404s (no app HTML); `/styles.css` is served as CSS. |
| 12 | With the API running with `Auth:Enabled=true` against your identity provider, sign out (or use a private window) and open `/stories/{storyId}#comment-{commentId}` (any comment id of the story). | A blank "Signing in…" page flashes and the browser goes to `/auth/login?returnUrl=%2Fstories%2F{storyId}%23comment-{commentId}`, then to the identity provider. After signing in the address bar shows `/stories/{storyId}#comment-{commentId}` (fragment kept). |
| 13 | Signed out, open `/auth/login?returnUrl=%2F%2Fevil.example%23x`, `/auth/login?returnUrl=https%3A%2F%2Fevil.example` and `/auth/login?returnUrl=%2F%09%2Fevil.example` (a tab, which browsers strip from a `Location`). | Each signs in and lands on `/` of the app, never on `evil.example`. |

### Links, dependencies, claim and change history (sc-26157)

Extra setup: in Demo create stories **Blocker story** (In Progress) and **Dependent story** (Ready); make Story page
blocked by Blocker story and Dependent story blocked by Story page (`POST /api/items/{id}/dependencies`); add a `pr`
link `https://github.com/acme/app/pull/42` to Story page; claim Story page through the MCP server
(`claim_work_item` with a token).

| # | Step | Expected |
|---|------|----------|
| 14 | Open `/stories/{storyId}`. | Under the fields: **Claim** "Claimed by {token identity} · expires {time}"; **Links** `PR https://github.com/acme/app/pull/42`; **Blocked by** Blocker story · In Progress; **Blocks** Dependent story · Ready. |
| 15 | Click the link URL. | It opens in a new tab; the story page stays. |
| 16 | Look at **History** under the comments. | Entries newest first, each with kind, actor (when there is one) and time: Claimed, Blocker added (`blocked_by — → Blocker story`), Link added, Comment added, Created at the bottom. `version` is not listed. |
| 17 | Set Priority to Critical and wait a second. | A new **Updated** entry `priority High → Critical` appears at the top of History. Adding a comment adds a **Comment added** entry the same way. |
| 18 | Click **Blocker story** under Blocked by. | The URL is `/stories/{blockerId}` and the page shows Blocker story, with Story page under **Blocks** and "Not claimed". |
| 19 | Press browser Back. | Story page is shown again at `/stories/{storyId}`. |
| 20 | Open a story with no claim, link or dependency. | Claim shows "Not claimed", Links "No links", Blocked by and Blocks "Nothing". |
| 21 | `GET /api/items/changes?fromStart=true&itemId={storyId}&projectId={otherId}`. | HTTP 400 `invalid_argument` naming `item_id`; with `projectId={demoId}` or without `projectId` it returns only Story page's entries. |

### Live updates (sc-26158)

Open `/stories/{storyId}` in two tabs, A and B. Changes "from elsewhere" are made in B, or with
`PATCH /api/items/{storyId}` / `POST /api/items/{storyId}/comments` (or the MCP server).

| # | Step | Expected |
|---|------|----------|
| 22 | In B, change the title and set Status to Done; add a comment. | Within a second A shows the new title (field and breadcrumb), Done, the comment and new **Updated** / **Comment added** history entries, without a reload. |
| 23 | Add a link and a blocker to the story through the API. | A shows them under **Links** and **Blocked by**, and the history entries, without a reload. |
| 24 | In A, click into the description and type a few words; leave the cursor there. In B, type into the description and set Priority to Critical. | A shows Critical; its description still shows what was typed in A (not B's text). |
| 25 | In A, click outside the description. | A's description now shows the text saved last (B's). |
| 26 | In A, type into the title and, within 400 ms, `PATCH` `{"title":"Remote title","priority":"Low"}`. Wait two seconds. | A's title never shows "Remote title"; A shows Low. `GET /api/items/{storyId}` returns A's title and priority Low (A's save wrote only the title). |
| 27 | In A, type into the title in bursts (pause about half a second between them). | The title never jumps back to an earlier value while the saves come back through the change feed. |
| 28 | In A, start a comment draft; change the status elsewhere. | The status updates; the draft stays. |
| 29 | In A, open DevTools → Network and filter `changes`. Click the breadcrumb back to the board, then change the story elsewhere. | One `/api/changes` stream per tab, the board's. After leaving the page the change fetches the board, not `/api/items/{storyId}`. |
| 30 | Delete the story elsewhere while A shows it. | A shows **Story deleted** / "This story was deleted" with **Back to board**; leaving shows no "Save failed" message. |
| 31 | Stop the API, change nothing, start it again. | Within a few seconds the board (and an open story page) is read again. |
| 32 | Claim the story through the MCP server (`claim_work_item`), then renew and release it. | A's **Claim** shows "Claimed by {token identity} · expires {time}", then the new expiry, then "Not claimed", each with a **Claimed** / **Claim renewed** / **Claim released** history entry, without a reload. |
| 33 | In A, open DevTools → Network and filter `changes`. Create or change a story in another project. | A fetches `/api/items/{storyId}` once but does not read `/api/items/changes?itemId=…` (its history) again. |
| 34 | In A, type into the title and, within 400 ms, delete the story elsewhere. | A shows **Story deleted**; no "Save failed" message. On the board, the same with the story's slideout open closes the slideout, takes the card off and shows "This story was deleted elsewhere; the edit was not saved." |
| 35 | Open a story page and, while it shows "Loading story…" (throttle the network in DevTools), change its title elsewhere. | Once loaded, A shows the new title without another change. |

### Slideout URL state and Copy link (sc-26159)

Open the board at `/?project={demoId}` in a fresh tab. Story page and a second Demo story are on the board.

| # | Step | Expected |
|---|------|----------|
| 36 | Click Story page's card. | The slideout opens and the URL becomes `/?project={demoId}&story={storyId}` (a new history entry). |
| 37 | Reload the page. | The board loads with Story page's slideout open again. |
| 38 | Type into the slideout's title and **immediately** press browser Back. | The slideout closes, the URL is `/?project={demoId}` and the card shows the new title (`GET /api/items/{storyId}` returns it): the pending edit was saved. Forward reopens the slideout. |
| 39 | With the slideout open, press **Close** (and, again, click the dimmed overlay). | The slideout closes and `?story=` leaves the URL; pressing Back afterwards does not reopen the slideout (it leaves the board URL, like Back from the board would). |
| 40 | Open a card, then open the other Demo story from the command palette (Cmd+K) while the slideout is open; press Back. | The URL's `story=` changes to the second story without a new history entry; Back closes the slideout (it does not step back to the first story). |
| 41 | Open `/?project={otherId}&story={storyId}` (the story is in Demo). | The board switches to Demo (sidebar highlights Demo, URL `/?project={demoId}&epic={permalinksId}&story={storyId}`, replacing the entry) and shows Story page's slideout. |
| 42 | Open `/?project={demoId}&story=00000000-0000-0000-0000-000000000001`, then `/?story=not-a-guid`. | The board loads without a slideout, `story=` is dropped from the URL and the feedback bar says "There is no story with this id; it may have been deleted." No script error in the console. |
| 43 | Open the slideout and delete the story elsewhere (`DELETE /api/items/{id}`). | The slideout closes and `story=` leaves the URL. |
| 44 | In the slideout click **Copy link**. | The feedback bar shows "Link copied to the clipboard"; pasting gives `{origin}/stories/{storyId}`, which opens the story page. |
| 45 | Open the app over plain http on a non-localhost address (not a secure context), or deny clipboard access, and click **Copy link**. | The link is still copied through the fallback; if the browser refuses that too, the feedback bar shows "Couldn't copy the link (…): {origin}/stories/{storyId}" so it can be copied by hand. |
| 46 | Open `/?project={demoId}&story=..%2F..%2Fapi%2Fprojects%2F{otherId}` (a crafted, non-id `story=`) with DevTools → Network open. | No request to `/api/projects/{otherId}` (nor any `/api/items/…` for it); no slideout; `story=` is dropped from the URL and the feedback bar says "There is no story with this id; it may have been deleted." |
| 47 | Open `/styles.css`, then `/?project={demoId}` in the same tab (so the board is the tab's first app entry). Open a card and **double-click Close** (or click Close and then the overlay at once). | The slideout closes and the tab stays on `/?project={demoId}`; it does not step back to `/styles.css`. |
| 48 | Open a card, reload, type into the slideout's title and **immediately** press Close (and, again, the overlay). | `GET /api/items/{storyId}` returns the new title: the edit is saved before the slideout steps back. |
| 49 | Open `/styles.css`, then `/?project={demoId}`; open a card and delete that story elsewhere (`DELETE /api/items/{id}`). After the slideout closes, press Back. | Back goes to `/styles.css` (the slideout's entry was stepped back over, not left as an identical duplicate). |
