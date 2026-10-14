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
| 8 | Press browser Back. | The board is shown again at its previous URL. |
| 9 | Open `/stories/00000000-0000-0000-0000-000000000001`. | The app loads and shows **Story not found** / "There is no story with this id", with a **Back to board** button. |
| 10 | Click **Back to board**. | The board is shown at `/?project=…`. |
| 11 | Open `/stories/not-a-guid`, `/api/does-not-exist` and `/styles.css`. | The first two are plain 404s (no app HTML); `/styles.css` is served as CSS. |
